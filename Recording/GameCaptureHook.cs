using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSATelemetryOverlay;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Recording;

/// <summary>
/// Selective game capture hook for KSA.
/// Intercepts KSA's render pipeline before ImGui compositing to capture the pristine 3D scene
/// from Program._offscreenTarget, for the flight recorder and for external consumers (the OBS plugin).
/// Downscaling and Linear->sRGB conversion happen on the GPU via vkCmdBlitImage; readback goes
/// through a host-cached staging ring and a bounded queue to a dedicated writer thread.
/// </summary>
public static class GameCaptureHook
{
    private const string LogPrefix = "[GameCapture] ";
    private const string HarmonyId = "com.elevatorctln.ksatelemetryoverlay.capture";
    private const int QueueFamilyIgnored = -1;
    private const int CpuBufferCount = 4;

    private static Harmony? _harmony;
    private static bool _installed;
    private static FieldInfo? _offscreenField;
    private static AccessTools.FieldRef<Program, RenderTarget>? _offscreenRef;

    private static readonly Stopwatch _fpsTimer = Stopwatch.StartNew();
    private static double _nextCaptureTime;
    private static double _lastClientSeenTime = 0;
    private const double IdleGracePeriodSeconds = 3.0;

    // Staging ring in host-visible RAM. KSA waits on the fence of the frame MaxFramesInFlight
    // back before recording a new one, so a slot that old is safe to read without a stall.
    private static BufferEx[] _stagingBuffers = [];
    private static double[] _stagingTimestamps = [];
    private static ulong _stagingBufferSize;
    private static int _writeIndex = 0;
    private static int _framesRecorded = 0;

    // GPU resources replaced while earlier frames may still be using them, freed once those frames retire
    private static readonly List<(ulong RetireAtFrame, BufferEx[] Buffers, ImageEx Image)> _retired = [];

    public unsafe delegate void RawFrameCapturedHandler(byte* rgbaPtr, int width, int height);
    public static event RawFrameCapturedHandler? OnRawFrameCaptured;
    public static bool ExternalCaptureRequested { get; set; }

    private static FrameQueue? _encodeQueue;
    private static int _encodeWidth;
    private static int _encodeHeight;

    // GPU intermediate downscale image
    private static ImageEx _downscaleImage;
    private static int _downscaleWidth;
    private static int _downscaleHeight;
    private static VkFormat _downscaleFormat;
    private static bool _loggedCaptureInfo = false;

    public static TelemetrySnapshot? CurrentSnapshot { get; private set; }
    public static double CurrentDeltaTime { get; private set; }

    public static void SetCurrentSnapshot(TelemetrySnapshot snapshot, double dt)
    {
        CurrentSnapshot = snapshot;
        CurrentDeltaTime = dt;
    }

    private static RenderTarget? OffscreenTarget
    {
        get
        {
            if (_offscreenRef != null)
            {
                return _offscreenRef(Program.Instance);
            }

            return _offscreenField?.GetValue(Program.Instance) as RenderTarget;
        }
    }

    private static void DisposeStagingBuffers()
    {
        StopEncoderQueue();
        ReleaseRetired(force: true);

        for (int i = 0; i < _stagingBuffers.Length; i++)
        {
            if (!_stagingBuffers[i].IsNull())
            {
                _stagingBuffers[i].Dispose();
            }
        }

        _stagingBuffers = [];
        _stagingTimestamps = [];

        if (!_downscaleImage.VkImage.IsNull())
        {
            _downscaleImage.Dispose();
            _downscaleImage = default;
        }

        _stagingBufferSize = 0;
        _downscaleWidth = 0;
        _downscaleHeight = 0;
        _framesRecorded = 0;
        _writeIndex = 0;
        _lastClientSeenTime = 0;
    }

    /// <summary>Hands the current GPU resources to the retire list instead of freeing them mid-flight.</summary>
    private static void RetireStagingBuffers(Renderer renderer)
    {
        StopEncoderQueue();
        _retired.Add((renderer.FrameCount + (ulong)renderer.MaxFramesInFlight + 1, _stagingBuffers, _downscaleImage));
        _stagingBuffers = [];
        _stagingTimestamps = [];
        _downscaleImage = default;
        _stagingBufferSize = 0;
        _downscaleWidth = 0;
        _downscaleHeight = 0;
    }

    private static void ReleaseRetired(bool force, Renderer? renderer = null)
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            var entry = _retired[i];
            if (!force && (renderer == null || renderer.FrameCount < entry.RetireAtFrame))
            {
                continue;
            }

            foreach (BufferEx buffer in entry.Buffers)
            {
                if (!buffer.IsNull()) buffer.Dispose();
            }

            if (!entry.Image.VkImage.IsNull()) entry.Image.Dispose();
            _retired.RemoveAt(i);
        }
    }

    private static void StopEncoderQueue()
    {
        _encodeQueue?.Stop(500);
        _encodeQueue = null;
    }

    private static unsafe void EncodeFrame(byte[] buffer, double timestamp)
    {
        int w = _encodeWidth;
        int h = _encodeHeight;
        if (w <= 0 || h <= 0) return;

        ReadOnlySpan<byte> frame = new(buffer, 0, w * h * 4);
        if (!double.IsNaN(timestamp))
        {
            FlightRecorder.Instance.WriteCapturedFrame(frame, w, h, timestamp);
        }

        RawFrameCapturedHandler? handler = OnRawFrameCaptured;
        if (handler != null)
        {
            fixed (byte* ptr = buffer)
            {
                handler(ptr, w, h);
            }
        }
    }

    public static bool Installed => _installed;
    public static string? ErrorMessage { get; private set; }

    public static void Install()
    {
        if (_installed) return;

        try
        {
            _offscreenField = typeof(Program).GetField("_offscreenTarget", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_offscreenField is null)
            {
                ErrorMessage = "Could not find Program._offscreenTarget field";
                Console.WriteLine(LogPrefix + ErrorMessage);
                return;
            }

            if (_offscreenField.FieldType == typeof(RenderTarget))
            {
                _offscreenRef = AccessTools.FieldRefAccess<Program, RenderTarget>(_offscreenField);
            }

            _harmony = new Harmony(HarmonyId);

            // 1. Patch RenderTarget.CreateColorImage to include VK_IMAGE_USAGE_TRANSFER_SRC_BIT
            MethodInfo? createColorImage = typeof(RenderTarget).GetMethod(
                "CreateColorImage", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (createColorImage != null)
            {
                MethodInfo cciTranspiler = typeof(GameCaptureHook).GetMethod(
                    nameof(CreateColorImageTranspile), BindingFlags.Static | BindingFlags.NonPublic)!;
                _harmony.Patch(createColorImage, transpiler: new HarmonyMethod(cciTranspiler));
            }

            // 2. Patch Program.RenderGame before ImGui renderpass
            MethodInfo? targetMethod = typeof(Program).GetMethod(
                "RenderGame", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (targetMethod is null)
            {
                ErrorMessage = "Could not find Program.RenderGame method";
                Console.WriteLine(LogPrefix + ErrorMessage);
                return;
            }

            MethodInfo transpiler = typeof(GameCaptureHook).GetMethod(
                nameof(RenderGameTranspile), BindingFlags.Static | BindingFlags.NonPublic)!;
            _harmony.Patch(targetMethod, transpiler: new HarmonyMethod(transpiler));

            // 3. Patch PartSelectedRenderer.Run to capture pristine 3D scene before yellow hover highlight is drawn
            MethodInfo? partSelectedRun = typeof(PartSelectedRenderer).GetMethod(
                nameof(PartSelectedRenderer.Run), BindingFlags.Static | BindingFlags.Public);
            if (partSelectedRun != null)
            {
                MethodInfo preHook = typeof(GameCaptureHook).GetMethod(
                    nameof(PartSelectedRendererPreHook), BindingFlags.Static | BindingFlags.NonPublic)!;
                _harmony.Patch(partSelectedRun, prefix: new HarmonyMethod(preHook));
            }

            _installed = true;
            Console.WriteLine(LogPrefix + "installed hardware capture hook on Program.RenderGame");

            // If renderer is already built, schedule rebuild so _offscreenTarget gets TRANSFER_SRC_BIT
            try
            {
                Program.ScheduleRendererRebuild();
            }
            catch { }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Console.WriteLine(LogPrefix + $"failed to install: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        if (!_installed) return;

        try
        {
            _harmony?.UnpatchAll(HarmonyId);
            DisposeStagingBuffers();
        }
        catch { }

        _installed = false;
        Console.WriteLine(LogPrefix + "uninstalled capture hook");
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> CreateColorImageTranspile(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var ins in instructions)
        {
            if (ins.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(ins.operand) == 28)
            {
                // 28 = ColorAttachment | Storage | Sampled
                // 29 = TransferSrc | ColorAttachment | Storage | Sampled
                yield return new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)29);
            }
            else
            {
                yield return ins;
            }
        }
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> RenderGameTranspile(IEnumerable<CodeInstruction> instructions)
    {
        var matcher = new CodeMatcher(instructions);

        matcher.MatchStartForward(
            CodeMatch.Calls(() => default(ImGuiBackendVulkanImpl)!.RenderDrawData(default)));
        matcher.ThrowIfInvalid("could not find ImGuiBackendVulkanImpl.RenderDrawData call in Program.RenderGame");

        matcher.MatchStartBackwards(
            CodeMatch.Calls(() => default(CommandBuffer).BeginRenderPass(default, default)));
        matcher.ThrowIfInvalid("could not find CommandBuffer.BeginRenderPass call in Program.RenderGame");

        matcher.RemoveInstruction();
        matcher.InsertAndAdvance(CodeInstruction.Call(() => ImGuiPreRenderHook(default, default, default)));

        return matcher.Instructions();
    }

    [HarmonyPrefix]
    private static void PartSelectedRendererPreHook(CommandBuffer commandBuffer)
    {
        try
        {
            OverlayConfig? config = TelemetryOverlayApi.CurrentConfig;
            bool isRecording = FlightRecorder.Instance.IsRecording;
            bool wantsVideo = isRecording || ExternalCaptureRequested;

            if (wantsVideo && config != null)
            {
                _lastClientSeenTime = _fpsTimer.Elapsed.TotalSeconds;
                TryCaptureOffscreen(commandBuffer, config, isRecording);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"pre-highlight capture error: {ex.Message}");
        }
    }

    private static unsafe void ImGuiPreRenderHook(
        CommandBuffer commandBuffer, in VkRenderPassBeginInfo beginInfo, VkSubpassContents contents)
    {
        try
        {
            Renderer renderer = Program.GetRenderer();
            if (_retired.Count > 0)
            {
                ReleaseRetired(force: false, renderer);
            }

            if (FlightRecorder.Instance.IsFinalizing)
            {
                FlightRecorder.Instance.StepFinalize(commandBuffer, renderer);
            }

            bool wantsVideo = FlightRecorder.Instance.IsRecording || ExternalCaptureRequested;

            if (!wantsVideo && !FlightRecorder.Instance.IsFinalizing)
            {
                double now = _fpsTimer.Elapsed.TotalSeconds;
                if (_lastClientSeenTime > 0 && (now - _lastClientSeenTime) > IdleGracePeriodSeconds)
                {
                    DisposeStagingBuffers();
                    _lastClientSeenTime = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"capture error: {ex.Message}");
        }

        // Always proceed with the original ImGui renderpass
        commandBuffer.BeginRenderPass(in beginInfo, contents);
    }

    /// <summary>
    /// Size of the captured frames. While recording this is the size the encoder was started
    /// with, so a window resize mid-flight scales into the same frame instead of corrupting the stream.
    /// </summary>
    public static void GetCaptureDimensions(OverlayConfig config, bool isRecording, out int dstW, out int dstH)
    {
        if (isRecording && FlightRecorder.Instance.TryGetLockedCaptureSize(out dstW, out dstH))
        {
            return;
        }

        int maxHeight = isRecording ? Math.Clamp(config.RecordMaxHeight, 240, 2160) : Math.Clamp(config.VideoStreamMaxHeight, 240, 2160);
        if (OffscreenTarget is { } target && target.Extent.Width > 0 && target.Extent.Height > 0)
        {
            int srcW = target.Extent.Width;
            int srcH = target.Extent.Height;
            if (srcH > maxHeight)
            {
                float scale = (float)maxHeight / srcH;
                dstW = (int)(srcW * scale) & ~1;
                dstH = maxHeight & ~1;
            }
            else
            {
                dstW = srcW & ~1;
                dstH = srcH & ~1;
            }
            return;
        }

        dstH = maxHeight & ~1;
        dstW = (dstH * 16 / 9) & ~1;
    }

    private static unsafe void TryCaptureOffscreen(CommandBuffer commandBuffer, OverlayConfig config, bool isRecording)
    {
        // Deadline pacing: a fixed "now - last < interval" test drops to every other frame
        // whenever frame times jitter around the interval.
        double now = _fpsTimer.Elapsed.TotalSeconds;
        int targetFps = isRecording ? Math.Clamp(config.RecordFps, 15, 60) : Math.Clamp(config.VideoStreamFps, 5, 60);
        double interval = 1.0 / targetFps;
        if (now + interval * 0.25 < _nextCaptureTime)
        {
            return;
        }

        _nextCaptureTime = Math.Max(_nextCaptureTime + interval, now);

        if (OffscreenTarget is not { } target)
        {
            return;
        }

        RenderImage? colorImage = target.ColorImage;
        if (colorImage is null) return;

        int srcW = target.Extent.Width;
        int srcH = target.Extent.Height;
        if (srcW <= 0 || srcH <= 0) return;

        GetCaptureDimensions(config, isRecording, out int dstW, out int dstH);
        if (dstW <= 0 || dstH <= 0) return;

        ulong bufferSize = (ulong)(dstW * dstH * 4);
        Renderer renderer = Program.GetRenderer();
        int framesInFlight = Math.Max(1, renderer.MaxFramesInFlight);
        int stagingCount = framesInFlight + 1;

        // 1. Allocate GPU downscale target image and staging ring if needed
        if (_stagingBuffers.Length != stagingCount || _stagingBufferSize != bufferSize || _downscaleImage.VkImage.IsNull() || _downscaleWidth != dstW || _downscaleHeight != dstH)
        {
            if (_stagingBuffers.Length > 0 || !_downscaleImage.VkImage.IsNull())
            {
                RetireStagingBuffers(renderer);
            }

            _stagingBufferSize = bufferSize;

            VkFormat targetFormat = VkFormat.R8G8B8A8SRGB;
            try
            {
                _downscaleImage = renderer.Device.CreateImage(DownscaleImageInfo(targetFormat, dstW, dstH));
            }
            catch
            {
                // Fallback to UNorm if sRGB not supported for transfer/blit
                targetFormat = VkFormat.R8G8B8A8UNorm;
                _downscaleImage = renderer.Device.CreateImage(DownscaleImageInfo(targetFormat, dstW, dstH));
            }

            _downscaleFormat = targetFormat;
            _downscaleWidth = dstW;
            _downscaleHeight = dstH;

            BufferEx.CreateInfo bufInfo = new()
            {
                Name = "GameCapture_Staging",
                BufferSize = (Brutal.ByteSize)(long)bufferSize,
                BufferUsage = VkBufferUsageFlags.TransferDstBit,
                AllocPreference = MemoryPreference.PreferCpu,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                AllocPreferredProperties = VkMemoryPropertyFlags.HostCachedBit
            };

            _stagingBuffers = new BufferEx[stagingCount];
            _stagingTimestamps = new double[stagingCount];
            for (int i = 0; i < stagingCount; i++)
            {
                _stagingBuffers[i] = renderer.Device.CreateBuffer(bufInfo);
            }

            _writeIndex = 0;
            _framesRecorded = 0;
            _loggedCaptureInfo = false;
        }

        if (_encodeQueue == null || _encodeQueue.FrameBytes != (int)bufferSize)
        {
            StopEncoderQueue();
            _encodeQueue = new FrameQueue((int)bufferSize, CpuBufferCount, EncodeFrame, "KSAGameCaptureEncoder");
        }

        // 2. Read the oldest staging slot, written framesInFlight captures ago (its frame's fence has been waited on)
        if (_framesRecorded >= framesInFlight)
        {
            int readyIndex = (_writeIndex + 1) % stagingCount;
            BufferEx readyBuffer = _stagingBuffers[readyIndex];

            // A full queue means the encoder is behind: skip this frame rather than overwrite one it is reading.
            // The recorder's timestamp pacing repeats the previous frame to keep the timeline intact.
            if (!readyBuffer.IsNull() && _encodeQueue.TryRent(out byte[] cpuTarget))
            {
                try
                {
                    using MappedMemory mapped = readyBuffer.Map();
                    mapped.AsSpan().CopyTo(cpuTarget);
                }
                catch
                {
                    _encodeQueue.Return(cpuTarget);
                    throw;
                }

                _encodeWidth = dstW;
                _encodeHeight = dstH;
                _encodeQueue.Submit(cpuTarget, _stagingTimestamps[readyIndex]);
            }
        }

        if (!_loggedCaptureInfo)
        {
            _loggedCaptureInfo = true;
            Console.WriteLine(LogPrefix + $"hardware capture: {srcW}x{srcH} ({colorImage.Format}) -> blit {dstW}x{dstH} ({_downscaleFormat})");
        }

        // The recorder stamps the capture now (and logs the telemetry that matches it)
        _stagingTimestamps[_writeIndex] = isRecording
            ? FlightRecorder.Instance.OnCaptureSubmitted()
            : double.NaN;

        // 3. Transition colorImage to TransferSrcOptimal, preserving tracked state
        ImageBarrierInfo prevState = colorImage.GetState();
        commandBuffer.PipelineBarrier2(colorImage, ImageBarrierInfo.Presets.TransferSrc);

        VkImageSubresourceRange dstRange = new()
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        // 4. Transition _downscaleImage to TransferDstOptimal
        TransitionImage(commandBuffer, _downscaleImage.VkImage, dstRange, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal);

        // 5. Hardware Blit (bilinear downscaling & color space conversion)
        VkImageBlit blit = default;
        blit.SrcSubresource = new VkImageSubresourceLayers
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            MipLevel = 0,
            BaseArrayLayer = 0,
            LayerCount = 1
        };
        blit.DstSubresource = new VkImageSubresourceLayers
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            MipLevel = 0,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        VkImageBlit* pBlit = &blit;
        VkOffset3D* srcOffsets = (VkOffset3D*)&pBlit->SrcOffsets;
        srcOffsets[0] = new VkOffset3D(0, 0, 0);
        srcOffsets[1] = new VkOffset3D(srcW, srcH, 1);

        VkOffset3D* dstOffsets = (VkOffset3D*)&pBlit->DstOffsets;
        dstOffsets[0] = new VkOffset3D(0, 0, 0);
        dstOffsets[1] = new VkOffset3D(dstW, dstH, 1);

        ReadOnlySpan<VkImageBlit> blitSpan = new(pBlit, 1);
        commandBuffer.BlitImage(
            colorImage.Image,
            VkImageLayout.TransferSrcOptimal,
            _downscaleImage.VkImage,
            VkImageLayout.TransferDstOptimal,
            blitSpan,
            VkFilter.Linear);

        // 6. Transition _downscaleImage from TransferDstOptimal to TransferSrcOptimal for buffer copy
        TransitionImage(commandBuffer, _downscaleImage.VkImage, dstRange,
            VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal);

        // 7. Copy _downscaleImage to current staging buffer
        VkBufferImageCopy region = new()
        {
            BufferOffset = (Brutal.ByteSize64)0L,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new VkImageSubresourceLayers
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new VkOffset3D(0, 0, 0),
            ImageExtent = new VkExtent3D(dstW, dstH, 1)
        };

        ReadOnlySpan<VkBufferImageCopy> copySpan = new(&region, 1);
        commandBuffer.CopyImageToBuffer(
            _downscaleImage.VkImage,
            VkImageLayout.TransferSrcOptimal,
            _stagingBuffers[_writeIndex].VkBuffer,
            copySpan);

        // 8. Host memory visibility barrier for the staging buffer to prevent cache tearing
        VkBufferMemoryBarrier2 bufferBarrier = new()
        {
            SrcStageMask = VkPipelineStageFlags2.TransferBit,
            SrcAccessMask = VkAccessFlags2.TransferWriteBit,
            DstStageMask = VkPipelineStageFlags2.HostBit,
            DstAccessMask = VkAccessFlags2.HostReadBit,
            SrcQueueFamilyIndex = QueueFamilyIgnored,
            DstQueueFamilyIndex = QueueFamilyIgnored,
            Buffer = _stagingBuffers[_writeIndex].VkBuffer,
            Offset = new Brutal.ByteSize64(0UL),
            Size = new Brutal.ByteSize64((ulong)(dstW * dstH * 4))
        };

        ReadOnlySpan<VkBufferMemoryBarrier2> bufSpan = new(&bufferBarrier, 1);
        commandBuffer.PipelineBarrier2(
            ReadOnlySpan<VkMemoryBarrier2>.Empty,
            bufSpan,
            ReadOnlySpan<VkImageMemoryBarrier2>.Empty);

        // 9. Restore colorImage back to previous state for subsequent passes (e.g. PartSelectedRenderer)
        commandBuffer.PipelineBarrier2(colorImage, prevState);

        // 10. Advance write index in the staging ring
        _writeIndex = (_writeIndex + 1) % stagingCount;
        _framesRecorded++;
    }

    /// <summary>
    /// Waits for every captured frame already handed to the writer thread to be written.
    /// Called by the recorder before it closes the encoder.
    /// </summary>
    public static bool WaitForEncoderIdle(int timeoutMs) => _encodeQueue?.WaitIdle(timeoutMs) ?? true;

    private static ImageEx.CreateInfo DownscaleImageInfo(VkFormat format, int width, int height) => new()
    {
        Name = "GameCapture_Downscale",
        ImageType = VkImageType._2D,
        ImageFormat = format,
        ImageExtent = new VkExtent3D(width, height, 1),
        ImageMipLevels = 1,
        ImageArrayLayers = 1,
        ImageSamples = VkSampleCountFlags._1Bit,
        ImageTiling = VkImageTiling.Optimal,
        ImageUsage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.ColorAttachmentBit,
        ImageSharingMode = VkSharingMode.Exclusive,
        ImageInitialLayout = VkImageLayout.Undefined,
        AllocPreference = MemoryPreference.PreferGpu,
    };

    private static unsafe void TransitionImage(
        CommandBuffer commandBuffer,
        VkImage image,
        VkImageSubresourceRange range,
        VkImageLayout oldLayout,
        VkImageLayout newLayout)
    {
        if (oldLayout == newLayout) return;

        VkPipelineStageFlags2 srcStage;
        VkAccessFlags2 srcAccess;
        VkPipelineStageFlags2 dstStage;
        VkAccessFlags2 dstAccess;

        if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.TransferDstOptimal)
        {
            srcStage = VkPipelineStageFlags2.TopOfPipeBit;
            srcAccess = VkAccessFlags2.None;
            dstStage = VkPipelineStageFlags2.BlitBit | VkPipelineStageFlags2.TransferBit;
            dstAccess = VkAccessFlags2.TransferWriteBit;
        }
        else if (oldLayout == VkImageLayout.TransferDstOptimal && newLayout == VkImageLayout.TransferSrcOptimal)
        {
            srcStage = VkPipelineStageFlags2.BlitBit | VkPipelineStageFlags2.TransferBit;
            srcAccess = VkAccessFlags2.TransferWriteBit;
            dstStage = VkPipelineStageFlags2.TransferBit;
            dstAccess = VkAccessFlags2.TransferReadBit;
        }
        else if (oldLayout == VkImageLayout.TransferDstOptimal && newLayout == VkImageLayout.ColorAttachmentOptimal)
        {
            srcStage = VkPipelineStageFlags2.BlitBit | VkPipelineStageFlags2.TransferBit;
            srcAccess = VkAccessFlags2.TransferWriteBit;
            dstStage = VkPipelineStageFlags2.ColorAttachmentOutputBit;
            dstAccess = VkAccessFlags2.ColorAttachmentReadBit | VkAccessFlags2.ColorAttachmentWriteBit;
        }
        else if (oldLayout == VkImageLayout.ColorAttachmentOptimal && newLayout == VkImageLayout.TransferSrcOptimal)
        {
            srcStage = VkPipelineStageFlags2.ColorAttachmentOutputBit;
            srcAccess = VkAccessFlags2.ColorAttachmentWriteBit;
            dstStage = VkPipelineStageFlags2.TransferBit;
            dstAccess = VkAccessFlags2.TransferReadBit;
        }
        else
        {
            srcStage = VkPipelineStageFlags2.AllCommandsBit;
            srcAccess = VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit;
            dstStage = VkPipelineStageFlags2.AllCommandsBit;
            dstAccess = VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit;
        }

        VkImageMemoryBarrier2 barrier = new()
        {
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = QueueFamilyIgnored,
            DstQueueFamilyIndex = QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };

        ReadOnlySpan<VkImageMemoryBarrier2> barrierSpan = new(&barrier, 1);
        commandBuffer.PipelineBarrier2(
            ReadOnlySpan<VkMemoryBarrier2>.Empty,
            ReadOnlySpan<VkBufferMemoryBarrier2>.Empty,
            barrierSpan);
    }
}
