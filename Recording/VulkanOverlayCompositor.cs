using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using KSA.Rendering;
using KSATelemetryOverlay;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Recording;

/// <summary>
/// Native Vulkan offscreen compositor for the Flight Recorder.
/// Composites the SpaceX-style Dear ImGui telemetry overlay directly over the pristine
/// 3D scene or renders transparent overlay frames with alpha for high-speed hardware compositing.
/// Operates on an isolated ImGuiContext and dedicated FrameRenderBuffers to avoid any
/// contention or state corruption with KSA's main swapchain UI.
/// </summary>
public sealed unsafe class VulkanOverlayCompositor : IDisposable
{
    private const string LogPrefix = "[FlightRecorder:Vulkan] ";
    private const int QueueFamilyIgnored = -1;

    private delegate void RenderDrawDataFn(
        ImDrawDataPtr drawData,
        CommandBuffer commandBuffer,
        VkPipeline pipeline,
        VkRenderPass renderPass,
        VkSampleCountFlags sampleCount);

    private static readonly MethodInfo? _createPipelineMethod = typeof(ImGuiBackendVulkanImpl).GetMethod(
        "CreatePipeline",
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
        null,
        [typeof(VkRenderPass), typeof(int), typeof(VkSampleCountFlags), typeof(string)],
        null);

    private static readonly MethodInfo? _destroyBuffersMethod = typeof(ImGuiBackendVulkanImpl).GetMethod(
        "DestroyWindowRenderBuffers",
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    private static readonly MethodInfo? _renderDrawData5Method = typeof(ImGuiBackendVulkanImpl).GetMethod(
        "RenderDrawData",
        BindingFlags.NonPublic | BindingFlags.Instance,
        null,
        [typeof(ImDrawDataPtr), typeof(CommandBuffer), typeof(VkPipeline), typeof(VkRenderPass), typeof(VkSampleCountFlags)],
        null);

    private RenderDrawDataFn? _renderDrawDataDelegate;

    private VkRenderPass _renderPass;
    private VkImageView _imageView;
    private VkFramebuffer _framebuffer;
    private VkPipeline _pipeline;
    private ImGuiContextPtr _recorderContext;
    private byte* _pViewportData;
    private int _width;
    private int _height;
    private VkFormat _format;
    private bool _resourcesAllocated;

    // Dedicated triple-buffered staging ring (3 staging buffers total) for high-speed non-blocking GPU compositing
    public const int StagingCount = 3;
    private readonly BufferEx[] _stagingBuffers = new BufferEx[StagingCount];
    private ImageEx _overlayImage;
    private VkImageLayout _currentOverlayLayout = VkImageLayout.Undefined;
    private ulong _stagingBufferSize;

    public bool ResourcesReady => _resourcesAllocated;

    public BufferEx GetStagingBuffer(int index) => (index >= 0 && index < StagingCount) ? _stagingBuffers[index] : default;

    public bool EnsureFinalizerResources(Renderer renderer, int width, int height, VkFormat format)
    {
        ulong bufferSize = (ulong)(width * height * 4);
        if (_resourcesAllocated && !_overlayImage.VkImage.IsNull() && !_stagingBuffers[0].IsNull() &&
            _width == width && _height == height && _stagingBufferSize == bufferSize)
        {
            return true;
        }

        DisposeFinalizerResources(renderer);

        try
        {
            _width = width;
            _height = height;
            _format = format;
            _stagingBufferSize = bufferSize;

            ImageEx.CreateInfo imgInfo = new()
            {
                Name = "FlightRecorder_OverlayImage",
                ImageType = VkImageType._2D,
                ImageFormat = format,
                ImageExtent = new VkExtent3D(width, height, 1),
                ImageMipLevels = 1,
                ImageArrayLayers = 1,
                ImageSamples = VkSampleCountFlags._1Bit,
                ImageTiling = VkImageTiling.Optimal,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                ImageInitialLayout = VkImageLayout.Undefined,
                AllocPreference = MemoryPreference.PreferGpu,
            };
            _overlayImage = renderer.Device.CreateImage(imgInfo);

            BufferEx.CreateInfo bufInfo = new()
            {
                Name = "FlightRecorder_Staging",
                BufferSize = (Brutal.ByteSize)(long)bufferSize,
                BufferUsage = VkBufferUsageFlags.TransferDstBit,
                AllocPreference = MemoryPreference.PreferCpu,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                AllocPreferredProperties = VkMemoryPropertyFlags.HostCachedBit
            };

            for (int i = 0; i < StagingCount; i++)
            {
                _stagingBuffers[i] = renderer.Device.CreateBuffer(bufInfo);
            }

            return EnsureResources(renderer, _overlayImage.VkImage, width, height, format);
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"failed to build finalizer resources: {ex.Message}");
            DisposeFinalizerResources(renderer);
            return false;
        }
    }

    public bool EnsureResources(Renderer renderer, VkImage targetImage, int width, int height, VkFormat format)
    {
        if (_resourcesAllocated && _width == width && _height == height && _format == format && !_imageView.IsNull())
        {
            return true;
        }

        DisposeResources(renderer);

        try
        {
            _width = width;
            _height = height;
            _format = format;

            // 1. Create VkImageView wrapping target image
            VkImageViewCreateInfo viewInfo = new()
            {
                Image = targetImage,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            _imageView = renderer.Device.CreateImageView(in viewInfo, default);

            // 2. Create VkRenderPass (LoadOp.Clear clears background to (0,0,0,0) transparent)
            VkAttachmentDescription attachment = new()
            {
                Format = format,
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.Store,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = VkImageLayout.ColorAttachmentOptimal,
                FinalLayout = VkImageLayout.ColorAttachmentOptimal
            };

            VkAttachmentReference colorRef = new()
            {
                Attachment = 0,
                Layout = VkImageLayout.ColorAttachmentOptimal
            };

            VkSubpassDescription subpass = new()
            {
                PipelineBindPoint = VkPipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                ColorAttachments = &colorRef,
                DepthStencilAttachment = null
            };

            VkRenderPassCreateInfo rpInfo = new()
            {
                AttachmentCount = 1,
                Attachments = &attachment,
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = 0,
                Dependencies = null
            };
            _renderPass = renderer.Device.CreateRenderPass(in rpInfo, default);

            // 3. Create VkFramebuffer
            VkImageView viewHandle = _imageView;
            VkFramebufferCreateInfo fbInfo = new()
            {
                RenderPass = _renderPass,
                AttachmentCount = 1,
                Attachments = &viewHandle,
                Width = width,
                Height = height,
                Layers = 1
            };
            _framebuffer = renderer.Device.CreateFramebuffer(in fbInfo, default);

            // 4. Create ImGui graphics pipeline matching our render pass
            if (_createPipelineMethod != null)
            {
                _pipeline = (VkPipeline)_createPipelineMethod.Invoke(
                    ImGuiBackend.Vulkan, [_renderPass, 0, VkSampleCountFlags._1Bit, "RecorderImGui"])!;
            }
            else
            {
                Console.WriteLine(LogPrefix + "could not find CreatePipeline method on ImGuiBackendVulkanImpl");
                return false;
            }

            // 5. Create delegate for 5-param RenderDrawData
            if (_renderDrawDataDelegate == null && _renderDrawData5Method != null)
            {
                _renderDrawDataDelegate = (RenderDrawDataFn)_renderDrawData5Method.CreateDelegate(
                    typeof(RenderDrawDataFn), ImGuiBackend.Vulkan);
            }

            // 6. Create independent Dear ImGui context sharing game font atlas
            if (_recorderContext.IsNull())
            {
                ImGuiIOPtr mainIo = ImGui.GetIO();
                _recorderContext = ImGui.CreateContext(mainIo.Fonts);

                ImGuiContextPtr prevCtx = ImGui.GetCurrentContext();
                ImGui.SetCurrentContext(_recorderContext);

                ImGuiIOPtr secIo = ImGui.GetIO();
                secIo.BackendFlags = mainIo.BackendFlags | (ImGuiBackendFlags)(8 | 16 | 4096);
                secIo.BackendRendererUserData = mainIo.BackendRendererUserData;
                secIo.BackendRendererName = mainIo.BackendRendererName;

                // 7. Allocate isolated ViewportData structure for vertex buffer uploads
                if (_pViewportData == null)
                {
                    Type vpType = typeof(ImGuiBackendVulkanImpl).Assembly.GetType("KSA.ImGuiBackendVulkanImpl+ViewportData")!;
                    int size = Marshal.SizeOf(vpType);
                    _pViewportData = (byte*)NativeMemory.AllocZeroed((nuint)size);
                }

                var vp = ImGui.GetMainViewport();
                vp.RendererUserData = (IntPtr)_pViewportData;

                ImGui.SetCurrentContext(prevCtx);
            }

            _resourcesAllocated = true;
            Console.WriteLine(LogPrefix + $"hardware overlay compositor built: {width}x{height} ({format})");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"failed to build compositor resources: {ex.Message}");
            DisposeResources(renderer);
            return false;
        }
    }



    /// <summary>
    /// Renders the transparent SpaceX telemetry overlay onto the dedicated _overlayImage within commandBuffer.
    /// Clears to 100% transparent black (0,0,0,0) and conditionally draws HUD elements.
    /// </summary>
    public bool RenderTransparentOverlay(
        CommandBuffer commandBuffer,
        int width,
        int height,
        TelemetrySnapshot? snapshot,
        double dt,
        bool drawHud)
    {
        if (!_resourcesAllocated || _pipeline.IsNull() || _framebuffer.IsNull() || _renderPass.IsNull() || _recorderContext.IsNull())
        {
            return false;
        }

        try
        {
            VkImageSubresourceRange range = new()
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            // 1. Transition _overlayImage from current layout to ColorAttachmentOptimal
            TransitionImage(commandBuffer, _overlayImage.VkImage, range,
                _currentOverlayLayout, VkImageLayout.ColorAttachmentOptimal);
            _currentOverlayLayout = VkImageLayout.ColorAttachmentOptimal;

            ImDrawDataPtr drawData = default;

            if (drawHud && snapshot != null)
            {
                ImGuiContextPtr prevCtx = ImGui.GetCurrentContext();
                ImGui.SetCurrentContext(_recorderContext);

                ImGuiIOPtr io = ImGui.GetIO();
                io.DisplaySize = new float2(width, height);
                io.DeltaTime = (float)Math.Clamp(dt, 0.001, 0.1);

                ImGui.NewFrame();
                FlightRecorder.Instance.RecorderRenderer?.Draw(snapshot, dt);
                ImGui.Render();

                drawData = ImGui.GetDrawData();
                if (_pViewportData != null)
                {
                    drawData.OwnerViewport.RendererUserData = (IntPtr)_pViewportData;
                }

                ImGui.SetCurrentContext(prevCtx);
            }

            // 2. Begin RenderPass with LoadOp.Clear -> clears entire image to (0,0,0,0) transparent
            VkClearValue clearVal = new()
            {
                Color = new VkClearColorValue { Float32 = new float4(0f, 0f, 0f, 0f) }
            };

            VkRenderPassBeginInfo beginInfo = new()
            {
                RenderPass = _renderPass,
                Framebuffer = _framebuffer,
                RenderArea = new VkRect2D(width, height),
                ClearValueCount = 1,
                ClearValues = &clearVal
            };

            commandBuffer.BeginRenderPass(in beginInfo, VkSubpassContents.Inline);

            // 3. Draw overlay if we have draw data
            if (!drawData.IsNull() && drawData.CmdListsCount > 0 && drawData.TotalVtxCount > 0)
            {
                _renderDrawDataDelegate?.Invoke(drawData, commandBuffer, _pipeline, _renderPass, VkSampleCountFlags._1Bit);
            }

            commandBuffer.EndRenderPass();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"RenderTransparentOverlay error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Copies rendered _overlayImage to the target staging buffer and inserts host visibility barrier.
    /// </summary>
    public void CopyOverlayToStaging(CommandBuffer commandBuffer, int stagingIndex, int width, int height)
    {
        if (_overlayImage.VkImage.IsNull() || stagingIndex < 0 || stagingIndex >= StagingCount || _stagingBuffers[stagingIndex].IsNull()) return;

        VkImageSubresourceRange range = new()
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        // Transition from ColorAttachmentOptimal to TransferSrcOptimal
        TransitionImage(commandBuffer, _overlayImage.VkImage, range,
            _currentOverlayLayout, VkImageLayout.TransferSrcOptimal);
        _currentOverlayLayout = VkImageLayout.TransferSrcOptimal;

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
            ImageExtent = new VkExtent3D(width, height, 1)
        };

        ReadOnlySpan<VkBufferImageCopy> copySpan = new(&region, 1);
        commandBuffer.CopyImageToBuffer(
            _overlayImage.VkImage,
            VkImageLayout.TransferSrcOptimal,
            _stagingBuffers[stagingIndex].VkBuffer,
            copySpan);

        // Memory barrier so host read sees the transfer write
        VkBufferMemoryBarrier2 bufferBarrier = new()
        {
            SrcStageMask = VkPipelineStageFlags2.TransferBit,
            SrcAccessMask = VkAccessFlags2.TransferWriteBit,
            DstStageMask = VkPipelineStageFlags2.HostBit,
            DstAccessMask = VkAccessFlags2.HostReadBit,
            SrcQueueFamilyIndex = QueueFamilyIgnored,
            DstQueueFamilyIndex = QueueFamilyIgnored,
            Buffer = _stagingBuffers[stagingIndex].VkBuffer,
            Offset = new Brutal.ByteSize64(0UL),
            Size = new Brutal.ByteSize64((ulong)(width * height * 4))
        };

        ReadOnlySpan<VkBufferMemoryBarrier2> bufSpan = new(&bufferBarrier, 1);
        commandBuffer.PipelineBarrier2(
            ReadOnlySpan<VkMemoryBarrier2>.Empty,
            bufSpan,
            ReadOnlySpan<VkImageMemoryBarrier2>.Empty);
    }

    public void DisposeFinalizerResources(Renderer? renderer)
    {
        for (int i = 0; i < StagingCount; i++)
        {
            if (!_stagingBuffers[i].IsNull())
            {
                _stagingBuffers[i].Dispose();
                _stagingBuffers[i] = default;
            }
        }

        if (!_overlayImage.VkImage.IsNull())
        {
            _overlayImage.Dispose();
            _overlayImage = default;
        }

        _stagingBufferSize = 0;
        _currentOverlayLayout = VkImageLayout.Undefined;
        DisposeResources(renderer);
    }

    public void DisposeResources(Renderer? renderer)
    {
        if (!_resourcesAllocated && _imageView.IsNull()) return;

        try
        {
            if (_pViewportData != null)
            {
                try
                {
                    Type? vpType = typeof(ImGuiBackendVulkanImpl).Assembly.GetType("KSA.ImGuiBackendVulkanImpl+ViewportData");
                    int renderBuffersOffset = vpType != null ? (int)Marshal.OffsetOf(vpType, "RenderBuffers") : 120;
                    byte* pBuffers = _pViewportData + renderBuffersOffset;
                    _destroyBuffersMethod?.Invoke(ImGuiBackend.Vulkan, [(IntPtr)pBuffers]);
                }
                catch { }

                NativeMemory.Free(_pViewportData);
                _pViewportData = null;
            }

            if (!_recorderContext.IsNull())
            {
                ImGui.DestroyContext(_recorderContext);
                _recorderContext = default;
            }

            if (renderer != null)
            {
                if (!_pipeline.IsNull())
                {
                    renderer.Device.DestroyPipeline(_pipeline, default);
                    _pipeline = default;
                }

                if (!_framebuffer.IsNull())
                {
                    renderer.Device.DestroyFramebuffer(_framebuffer, default);
                    _framebuffer = default;
                }

                if (!_renderPass.IsNull())
                {
                    renderer.Device.DestroyRenderPass(_renderPass, default);
                    _renderPass = default;
                }

                if (!_imageView.IsNull())
                {
                    renderer.Device.DestroyImageView(_imageView, default);
                    _imageView = default;
                }
            }
        }
        catch { }

        _resourcesAllocated = false;
        _width = 0;
        _height = 0;
    }

    private static void TransitionImage(
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

        if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.ColorAttachmentOptimal)
        {
            srcStage = VkPipelineStageFlags2.TopOfPipeBit;
            srcAccess = VkAccessFlags2.None;
            dstStage = VkPipelineStageFlags2.ColorAttachmentOutputBit;
            dstAccess = VkAccessFlags2.ColorAttachmentWriteBit;
        }
        else if (oldLayout == VkImageLayout.ColorAttachmentOptimal && newLayout == VkImageLayout.TransferSrcOptimal)
        {
            srcStage = VkPipelineStageFlags2.ColorAttachmentOutputBit;
            srcAccess = VkAccessFlags2.ColorAttachmentWriteBit;
            dstStage = VkPipelineStageFlags2.TransferBit;
            dstAccess = VkAccessFlags2.TransferReadBit;
        }
        else if (oldLayout == VkImageLayout.TransferSrcOptimal && newLayout == VkImageLayout.ColorAttachmentOptimal)
        {
            srcStage = VkPipelineStageFlags2.TransferBit;
            srcAccess = VkAccessFlags2.TransferReadBit;
            dstStage = VkPipelineStageFlags2.ColorAttachmentOutputBit;
            dstAccess = VkAccessFlags2.ColorAttachmentWriteBit;
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

    public void Dispose()
    {
        try
        {
            Renderer renderer = Program.GetRenderer();
            DisposeFinalizerResources(renderer);
        }
        catch
        {
            DisposeFinalizerResources(null);
        }
    }
}
