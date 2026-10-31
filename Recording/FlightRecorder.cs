using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using KSATelemetryOverlay;
using KSATelemetryOverlay.Rendering;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Recording;

public enum RecordingState
{
    Idle,
    Recording,
    Finalizing,
}

public sealed class TelemetryLogEntry
{
    public readonly double VideoTime;
    public readonly TelemetrySnapshot Snapshot;

    public TelemetryLogEntry(double videoTime, TelemetrySnapshot snapshot)
    {
        VideoTime = videoTime;
        Snapshot = snapshot;
    }
}

/// <summary>
/// Autonomous in-game flight recording system for KSA using a Two-Pass Architecture:
/// Pass 1 (In-Flight): Records the clean 3D scene from KSA at rock-solid 1:1 CFR pacing
///                     while logging timestamped telemetry data with zero extra GPU/VRAM overhead.
/// Pass 2 (Post-Flight): Retroactively synchronizes the SpaceX overlay, countdown clock,
///                       and intro scanning reveal to the exact frame of rocket liftoff,
///                       compositing the transparent overlay directly into the video container.
/// </summary>
public sealed class FlightRecorder : IDisposable
{
    private const string LogPrefix = "[FlightRecorder] ";

    public static FlightRecorder Instance { get; } = new();

    private readonly FfmpegEncoder _cleanEncoder = new();
    private readonly FfmpegEncoder _compositorEncoder = new();
    private readonly VulkanOverlayCompositor _compositor = new();

    private RecordingState _state = RecordingState.Idle;

    private readonly List<TelemetryLogEntry> _telemetryLog = new(1800);
    private double _liftoffVideoTimestamp = -1;
    private double _introStartTimestamp = 0;

    private int _finalizeTotalFrames;
    private int _finalizeRenderFrame;
    private int _finalizeReadFrame;
    private int _stagingWriteIndex = 0;
    private int _stagingReadIndex = 0;
    private int _framesInPipeline = 0;
    private string _lastFinalizeError = "";
    private int _logSearchIndex;

    // Dedicated non-blocking background finalizer worker
    private Thread? _finalizerThread;
    private readonly AutoResetEvent _finalizerSignal = new(false);
    private readonly AutoResetEvent _finalizerDrainWait = new(false);
    private volatile bool _finalizerRunning;
    private readonly object _finalizerLock = new();
    private readonly byte[]?[] _finalizerCpuBuffers = new byte[2][];
    private int _finalizerCpuWriteSlot = 0;
    private volatile int _pendingFinalizerReadSlot = -1;
    private volatile string? _finalizerWorkerError;

    private string? _cleanVideoPath;
    private string? _finalVideoPath;
    private int _targetWidth = 1920;
    private int _targetHeight = 1080;
    private int _targetFps = 30;
    private string? _activeFfmpegPath;
    private string? _activeCodec;

    private double _landingGraceRemaining;
    private double _toastRemaining;
    private string _toastText = "";
    private bool _toastIsError;

    private string? _lastSavedPath;
    private double _lastSavedSizeMb;
    private string _statusMessage = "Ready";

    // Dedicated independent overlay instance for the recording
    private OverlayConfig? _recorderConfig;
    private OverlayRenderer? _recorderRenderer;
    private readonly IntroAnimator _recorderIntro = new();
    private readonly List<MissionEvent> _recordedEvents = new(32);
    private readonly MissionEventLog _recorderEvents = new();
    private int _eventsReplayedCount;

    public RecordingState State => _state;
    public bool IsRecording => _state == RecordingState.Recording;
    public bool IsFinalizing => _state == RecordingState.Finalizing;
    public long FramesWritten => _cleanEncoder.FramesWritten;
    public double ElapsedSeconds => _cleanEncoder.ElapsedSeconds;
    public string ActiveCodec => _activeCodec ?? _cleanEncoder.ActiveCodec;
    public string? LastSavedPath => _lastSavedPath;
    public double LastSavedSizeMb => _lastSavedSizeMb;
    public string StatusMessage => _statusMessage;
    public OverlayRenderer? RecorderRenderer => _recorderRenderer;
    public IntroAnimator RecorderIntro => _recorderIntro;
    public int FinalizeProgressPercent => _finalizeTotalFrames > 0
        ? (int)Math.Clamp((_finalizeReadFrame * 100.0) / _finalizeTotalFrames, 0, 100)
        : 0;

    /// <summary>
    /// Gets the standard recordings directory under Documents/My Games/Kitten Space Agency/recordings.
    /// </summary>
    public static string GetDefaultRecordingsDirectory()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs) || !Directory.Exists(docs))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            docs = Path.Combine(home, "Documents");
        }

        string dir = Path.Combine(docs, "My Games", "Kitten Space Agency", "recordings");
        if (!Directory.Exists(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch { }
        }
        return dir;
    }

    /// <summary>
    /// Starts a flight recording session (clean 3D capture + telemetry logging).
    /// </summary>
    public bool StartRecording(OverlayConfig config, TelemetrySnapshot snapshot, string? modDirectory)
    {
        if (_state != RecordingState.Idle) return false;

        string? ffmpeg = FfmpegEncoder.FindFfmpeg(config.FfmpegPath, modDirectory);
        if (ffmpeg == null)
        {
            SetToast("FFmpeg not found! Install FFmpeg or drop into mod directory.", isError: true);
            _statusMessage = "Error: FFmpeg not found";
            Console.WriteLine(LogPrefix + _statusMessage);
            return false;
        }

        string recordingsDir = !string.IsNullOrWhiteSpace(config.RecordingsFolder) && Directory.Exists(config.RecordingsFolder)
            ? config.RecordingsFolder
            : GetDefaultRecordingsDirectory();

        string vehicleSafe = SanitizeFilename(snapshot.VehicleName);
        if (string.IsNullOrWhiteSpace(vehicleSafe)) vehicleSafe = "Vehicle";
        string timeStamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");

        _finalVideoPath = Path.Combine(recordingsDir, $"{vehicleSafe}_{timeStamp}.mp4");
        _cleanVideoPath = Path.Combine(recordingsDir, $".clean_{vehicleSafe}_{timeStamp}.mp4");

        GameCaptureHook.GetCaptureDimensions(config, isRecording: true, out _targetWidth, out _targetHeight);
        _targetFps = Math.Clamp(config.RecordFps, 15, 60);

        _activeFfmpegPath = ffmpeg;
        _activeCodec = FfmpegEncoder.DetectBestCodec(ffmpeg);

        // Build independent overlay state for the recorder, preserving all visual configuration
        _recorderConfig = config.CloneForRecording();
        _recorderRenderer = new OverlayRenderer(_recorderConfig);
        _recorderIntro.Restart();

        _telemetryLog.Clear();
        _recordedEvents.Clear();
        _eventsReplayedCount = 0;
        _liftoffVideoTimestamp = -1;
        _landingGraceRemaining = 0;

        // If recording begins while vehicle is already airborne, anchor liftoff relative to elapsed seconds
        if (snapshot.HasLiftoff || (snapshot.Thrust > 0 && snapshot.BurningEngineCount > 0 && !snapshot.HasSurfaceContact))
        {
            _liftoffVideoTimestamp = -Math.Max(0.0, snapshot.MissionElapsedSeconds);
        }

        if (snapshot.Events != null)
        {
            foreach (var ev in snapshot.Events.Recorded)
            {
                _recordedEvents.Add(ev);
            }
        }

        bool started = _cleanEncoder.Start(ffmpeg, _cleanVideoPath, _targetWidth, _targetHeight, _targetFps, _activeCodec);
        if (!started)
        {
            SetToast("Failed to launch FFmpeg encoder process", isError: true);
            _statusMessage = "Error: Failed to launch FFmpeg";
            return false;
        }

        _state = RecordingState.Recording;
        SetToast($"Recording flight ({FfmpegEncoder.CodecDisplayName(_activeCodec)})", isError: false);
        _statusMessage = "Recording clean flight...";

        Console.WriteLine(LogPrefix + $"recording session started: {_targetWidth}x{_targetHeight} @ {_targetFps}fps ({_activeCodec})");
        return true;
    }

    /// <summary>
    /// Writes a clean frame with constant-framerate (CFR) pacing based on elapsed recording time.
    /// </summary>
    public bool WriteFramePaced(ReadOnlySpan<byte> rgbaData)
    {
        if (_state != RecordingState.Recording) return false;
        return _cleanEncoder.WriteFramePaced(rgbaData);
    }

    /// <summary>
    /// Stops flight recording and launches the retroactive post-flight compositing pass.
    /// </summary>
    public void StopRecording()
    {
        if (_state != RecordingState.Recording) return;

        _statusMessage = "Finalizing clean video...";
        var cleanResult = _cleanEncoder.Stop();

        if (!cleanResult.Success || cleanResult.Frames <= 0)
        {
            _state = RecordingState.Idle;
            _statusMessage = "Recording failed (see console log)";
            SetToast("Recording failed or produced empty file", isError: true);
            try
            {
                if (!string.IsNullOrEmpty(_cleanVideoPath) && File.Exists(_cleanVideoPath))
                {
                    File.Delete(_cleanVideoPath);
                }
            }
            catch { }
            return;
        }

        // Determine liftoff timestamp if it wasn't detected during flight
        if (_liftoffVideoTimestamp < 0)
        {
            // Scan recorded telemetry log for liftoff event
            for (int i = 0; i < _telemetryLog.Count; i++)
            {
                var snap = _telemetryLog[i].Snapshot;
                if (snap.HasLiftoff || (snap.Thrust > 0 && snap.BurningEngineCount > 0 && !snap.HasSurfaceContact) ||
                    (snap.VerticalSpeed > 1.0 && snap.Altitude > 10.0))
                {
                    _liftoffVideoTimestamp = _telemetryLog[i].VideoTime;
                    Console.WriteLine(LogPrefix + $"retroactive liftoff detected from telemetry log at {_liftoffVideoTimestamp:F2}s");
                    break;
                }
            }

            if (_liftoffVideoTimestamp < 0)
            {
                _liftoffVideoTimestamp = 0.0; // Fallback to start
            }
        }

        // Calculate intro start timestamp relative to liftoff
        double countdownSeconds = _recorderConfig?.CountdownSeconds ?? 10.0;
        _introStartTimestamp = _liftoffVideoTimestamp >= 0
            ? Math.Max(0.0, _liftoffVideoTimestamp - countdownSeconds)
            : 0.0;

        // Ensure Liftoff event is present in _recordedEvents if liftoff occurred
        bool liftoffOccurred = _liftoffVideoTimestamp >= 0 || (_telemetryLog.Count > 0 && _telemetryLog.Any(t => t.Snapshot.HasLiftoff));
        if (liftoffOccurred && !_recordedEvents.Any(e => e.Kind == MissionEventKind.Liftoff))
        {
            _recordedEvents.Add(new MissionEvent(MissionEventKind.Liftoff, 0.0));
        }

        // If live event log captured few or no events, scan telemetry log to reconstruct all flight milestones
        if (_recordedEvents.Count <= 1 && _telemetryLog.Count > 0)
        {
            MissionEventLog scanner = new();
            bool liftoffPassed = false;
            for (int i = 0; i < _telemetryLog.Count; i++)
            {
                var entry = _telemetryLog[i];
                var s = entry.Snapshot.Clone();
                double missionSec = entry.VideoTime - _liftoffVideoTimestamp;
                s.MissionElapsedSeconds = missionSec;
                s.HasLiftoff = entry.VideoTime >= _liftoffVideoTimestamp;
                bool liftoffThisFrame = !liftoffPassed && s.HasLiftoff;
                if (liftoffThisFrame) liftoffPassed = true;

                scanner.Update(s, liftoffThisFrame, 1, false);
            }

            foreach (var ev in scanner.Recorded)
            {
                if (!_recordedEvents.Any(existing => existing.Kind == ev.Kind && Math.Abs(existing.MissionTime - ev.MissionTime) < 2.0))
                {
                    _recordedEvents.Add(ev);
                }
            }
        }

        _recordedEvents.Sort((a, b) => a.MissionTime.CompareTo(b.MissionTime));

        // Initialize replay event log for finalization
        _recorderEvents.Reset();
        _eventsReplayedCount = 0;

        // Pre-fill events that occurred before recording began (if recording started mid-flight)
        double initialMissionSeconds = -_liftoffVideoTimestamp;
        while (_eventsReplayedCount < _recordedEvents.Count && _recordedEvents[_eventsReplayedCount].MissionTime <= initialMissionSeconds)
        {
            _recorderEvents.Record(_recordedEvents[_eventsReplayedCount]);
            _eventsReplayedCount++;
        }

        // Pre-fill future predictions with all upcoming events so the timeline displays them ahead of time right from frame 0
        _recorderEvents.ClearPredictions();
        for (int i = _eventsReplayedCount; i < _recordedEvents.Count; i++)
        {
            _recorderEvents.AddPrediction(_recordedEvents[i]);
        }

        // Reset overlay renderer to fresh state for finalizer pass
        if (_recorderConfig != null)
        {
            _recorderRenderer = new OverlayRenderer(_recorderConfig);
        }

        _finalizeTotalFrames = (int)cleanResult.Frames;
        _finalizeRenderFrame = 0;
        _finalizeReadFrame = 0;
        _stagingWriteIndex = 0;
        _stagingReadIndex = 0;
        _framesInPipeline = 0;
        _lastFinalizeError = "";
        _logSearchIndex = 0;
        _recorderIntro.Restart();

        Console.WriteLine(LogPrefix + $"in-flight pass finished ({_finalizeTotalFrames} frames). Starting post-flight compositor...");
        Console.WriteLine(LogPrefix + $"liftoff at {_liftoffVideoTimestamp:F2}s, intro starts at {_introStartTimestamp:F2}s");

        // Prepare CPU double-buffers for non-blocking background finalizer
        int frameSize = _targetWidth * _targetHeight * 4;
        if (_finalizerCpuBuffers[0] == null || _finalizerCpuBuffers[0]!.Length != frameSize)
        {
            _finalizerCpuBuffers[0] = new byte[frameSize];
            _finalizerCpuBuffers[1] = new byte[frameSize];
        }
        _finalizerCpuWriteSlot = 0;
        _pendingFinalizerReadSlot = -1;
        _finalizerWorkerError = null;

        // Start FFmpeg hardware compositor: reads clean video from disk, blends transparent overlay from stdin
        bool compStarted = _compositorEncoder.StartCompositor(
            _activeFfmpegPath ?? "ffmpeg",
            _cleanVideoPath!,
            _finalVideoPath!,
            _targetWidth,
            _targetHeight,
            _targetFps,
            _activeCodec);

        if (!compStarted)
        {
            _state = RecordingState.Idle;
            SetToast("Failed to start hardware compositor process", isError: true);
            _statusMessage = "Error: Compositor failed to start";
            return;
        }

        EnsureFinalizerThreadStarted();
        _state = RecordingState.Finalizing;
        _statusMessage = $"Finalizing replay video... 0% (0/{_finalizeTotalFrames})";
    }

    private void EnsureFinalizerThreadStarted()
    {
        if (_finalizerRunning && _finalizerThread != null && _finalizerThread.IsAlive) return;

        lock (_finalizerLock)
        {
            if (_finalizerRunning && _finalizerThread != null && _finalizerThread.IsAlive) return;
            _finalizerRunning = true;
            _finalizerWorkerError = null;
            _finalizerThread = new Thread(FinalizerLoop)
            {
                Name = "KSAFlightRecorderFinalizer",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _finalizerThread.Start();
        }
    }

    private void StopFinalizerThread()
    {
        lock (_finalizerLock)
        {
            if (!_finalizerRunning) return;
            _finalizerRunning = false;
            _finalizerSignal.Set();
        }
        _finalizerThread?.Join(1000);
        _finalizerThread = null;
    }

    private void FinalizerLoop()
    {
        while (_finalizerRunning)
        {
            _finalizerSignal.WaitOne();
            if (!_finalizerRunning) break;

            int slot = _pendingFinalizerReadSlot;
            if (slot < 0) continue;
            _pendingFinalizerReadSlot = -1;
            _finalizerDrainWait.Set();

            byte[]? buf = _finalizerCpuBuffers[slot];
            if (buf == null) continue;

            try
            {
                bool ok = _compositorEncoder.WriteFrame(buf);
                if (!ok)
                {
                    _finalizerWorkerError = "FFmpeg compositor stdin closed unexpectedly";
                    break;
                }
            }
            catch (Exception ex)
            {
                _finalizerWorkerError = ex.Message;
                break;
            }
        }
    }

    /// <summary>
    /// Executes safe, non-blocking post-flight compositing paced by KSA's render loop (1 frame per game tick).
    /// Readbacks are guaranteed 2 frames behind submission so the GPU is completely idle for the staging slot.
    /// Memory is streamed to FFmpeg asynchronously on a dedicated worker thread, keeping KSA's render loop smooth.
    /// </summary>
    public void StepFinalize(CommandBuffer commandBuffer, Renderer renderer)
    {
        if (_state != RecordingState.Finalizing) return;

        if (_finalizerWorkerError != null)
        {
            AbortFinalize(_finalizerWorkerError);
            return;
        }

        if (!_compositor.EnsureFinalizerResources(renderer, _targetWidth, _targetHeight, VkFormat.R8G8B8A8UNorm))
        {
            AbortFinalize("Failed to allocate GPU compositor resources");
            return;
        }

        // 1. Read staging buffer that GPU finished executing (guaranteed 2 frames behind)
        if (_framesInPipeline >= 2 || (_finalizeRenderFrame >= _finalizeTotalFrames && _framesInPipeline > 0))
        {
            BufferEx readyBuffer = _compositor.GetStagingBuffer(_stagingReadIndex);

            if (!readyBuffer.IsNull())
            {
                try
                {
                    if (_pendingFinalizerReadSlot >= 0)
                    {
                        _finalizerDrainWait.WaitOne(10);
                    }

                    byte[]? targetBuf = _finalizerCpuBuffers[_finalizerCpuWriteSlot];
                    if (targetBuf != null)
                    {
                        using (MappedMemory mapped = readyBuffer.Map())
                        {
                            mapped.AsSpan().CopyTo(targetBuf);
                        }

                        _pendingFinalizerReadSlot = _finalizerCpuWriteSlot;
                        _finalizerCpuWriteSlot = 1 - _finalizerCpuWriteSlot;
                        _finalizerSignal.Set();
                    }

                    _stagingReadIndex = (_stagingReadIndex + 1) % VulkanOverlayCompositor.StagingCount;
                    _finalizeReadFrame++;
                    _framesInPipeline--;
                    _statusMessage = $"Finalizing replay video... {FinalizeProgressPercent}% ({_finalizeReadFrame}/{_finalizeTotalFrames})";
                }
                catch (Exception ex)
                {
                    AbortFinalize($"Error reading staging buffer: {ex.Message}");
                    return;
                }
            }
        }

        // 2. Check if all frames have been read and sent to encoder
        if (_finalizeReadFrame >= _finalizeTotalFrames)
        {
            int waits = 0;
            while (_pendingFinalizerReadSlot >= 0 && waits++ < 200)
            {
                Thread.Sleep(5);
            }
            StopFinalizerThread();
            CompleteFinalize();
            return;
        }

        // 3. Render next single frame into GPU command buffer
        if (_finalizeRenderFrame < _finalizeTotalFrames)
        {
            int frameIdx = _finalizeRenderFrame;
            double frameTime = frameIdx / (double)_targetFps;

            // Find closest telemetry snapshot in log
            while (_logSearchIndex + 1 < _telemetryLog.Count && _telemetryLog[_logSearchIndex + 1].VideoTime <= frameTime)
            {
                _logSearchIndex++;
            }

            TelemetrySnapshot? baseSnap = _telemetryLog.Count > 0 ? _telemetryLog[_logSearchIndex].Snapshot : null;
            TelemetrySnapshot? frameSnap = baseSnap?.Clone();

            bool drawHud = frameTime >= _introStartTimestamp;

            if (frameSnap != null)
            {
                double missionSeconds = frameTime - _liftoffVideoTimestamp;
                frameSnap.MissionElapsedSeconds = missionSeconds;
                frameSnap.HasLiftoff = frameTime >= _liftoffVideoTimestamp && (_recordedEvents.Any(e => e.Kind == MissionEventKind.Liftoff) || (baseSnap?.HasLiftoff ?? false));
                frameSnap.CountingDown = frameTime < _liftoffVideoTimestamp;

                // 1. Move any events whose mission timestamp has arrived into Recorded (triggers notification banners)
                while (_eventsReplayedCount < _recordedEvents.Count && _recordedEvents[_eventsReplayedCount].MissionTime <= missionSeconds)
                {
                    _recorderEvents.Record(_recordedEvents[_eventsReplayedCount]);
                    _eventsReplayedCount++;
                }

                // 2. Put all remaining future events into Predicted so the timeline displays them ahead of time
                _recorderEvents.ClearPredictions();
                for (int i = _eventsReplayedCount; i < _recordedEvents.Count; i++)
                {
                    _recorderEvents.AddPrediction(_recordedEvents[i]);
                }

                // Include any future planned maneuver burns that were in the original telemetry
                if (baseSnap?.Events != null)
                {
                    foreach (var pred in baseSnap.Events.Predicted)
                    {
                        if (pred.Kind == MissionEventKind.PlannedBurn && pred.MissionTime > missionSeconds)
                        {
                            _recorderEvents.AddPrediction(pred);
                        }
                    }
                }

                frameSnap.Events = _recorderEvents;
            }

            // Render transparent overlay pass into _overlayImage
            bool ok = _compositor.RenderTransparentOverlay(commandBuffer, _targetWidth, _targetHeight, frameSnap, 1.0 / _targetFps, drawHud);
            if (!ok)
            {
                AbortFinalize($"Overlay render pass failed at frame {frameIdx}");
                return;
            }

            // Copy to staging buffer for CPU readback 2 frames later
            _compositor.CopyOverlayToStaging(commandBuffer, _stagingWriteIndex, _targetWidth, _targetHeight);

            _finalizeRenderFrame++;
            _stagingWriteIndex = (_stagingWriteIndex + 1) % VulkanOverlayCompositor.StagingCount;
            _framesInPipeline++;
        }
    }

    private void CompleteFinalize()
    {
        StopFinalizerThread();
        var result = _compositorEncoder.Stop();
        _compositor.DisposeFinalizerResources(Program.GetRenderer());
        _state = RecordingState.Idle;

        // Clean up temporary clean video file
        try
        {
            if (!string.IsNullOrEmpty(_cleanVideoPath) && File.Exists(_cleanVideoPath))
            {
                File.Delete(_cleanVideoPath);
            }
        }
        catch { }

        if (result.Success)
        {
            _lastSavedPath = result.OutputPath;
            _lastSavedSizeMb = result.FileSizeBytes / (1024.0 * 1024.0);
            string baseName = Path.GetFileName(result.OutputPath);
            _statusMessage = $"Saved: {baseName} ({_lastSavedSizeMb:F1} MB)";
            SetToast($"Flight recorded: {baseName} ({_lastSavedSizeMb:F1} MB, {result.DurationSeconds:F0}s)", isError: false);
            Console.WriteLine(LogPrefix + $"replay completed successfully: {result.OutputPath} ({result.Frames} frames, {_lastSavedSizeMb:F1} MB)");
        }
        else
        {
            _statusMessage = "Compositing failed";
            SetToast("Hardware compositing failed (see console log)", isError: true);
            Console.WriteLine(LogPrefix + "compositor failed or produced empty file");
        }
    }

    private void AbortFinalize(string reason)
    {
        Console.WriteLine(LogPrefix + $"finalization aborted: {reason}");
        _lastFinalizeError = reason;
        StopFinalizerThread();
        _compositorEncoder.Stop();
        _compositor.DisposeFinalizerResources(Program.GetRenderer());
        _state = RecordingState.Idle;
        _statusMessage = $"Finalization aborted: {reason}";
        SetToast($"Replay finalization failed: {reason}", isError: true);
    }

    /// <summary>
    /// Per-frame update hook for hotkeys, auto-triggers, and telemetry logging.
    /// </summary>
    public void Update(double dt, TelemetrySnapshot snapshot, OverlayConfig config, string? modDirectory)
    {
        if (_toastRemaining > 0)
        {
            _toastRemaining = Math.Max(0, _toastRemaining - dt);
        }

        // 1. Hotkey check
        try
        {
            if (!ImGui.GetCurrentContext().IsNull() && !ImGui.GetIO().WantTextInput)
            {
                if (ImGui.IsKeyPressed(config.RecordKey, repeat: false))
                {
                    if (IsRecording)
                    {
                        StopRecording();
                    }
                    else if (_state == RecordingState.Idle)
                    {
                        StartRecording(config, snapshot, modDirectory);
                    }
                }
            }
        }
        catch
        {
            // Outside of active ImGui frame or context
        }

        // 2. Auto-record on Liftoff / Engine ignition
        if (config.AutoRecordOnLiftoff && _state == RecordingState.Idle && snapshot.HasVehicle && !snapshot.HasLiftoff)
        {
            if (snapshot.Thrust > 0f || snapshot.BurningEngineCount > 0)
            {
                StartRecording(config, snapshot, modDirectory);
            }
        }

        // 3. During recording, log telemetry and detect liftoff
        if (_state == RecordingState.Recording)
        {
            double elapsed = _cleanEncoder.ElapsedSeconds;

            if (_liftoffVideoTimestamp < 0)
            {
                if (snapshot.HasLiftoff || (snapshot.Thrust > 0f && snapshot.BurningEngineCount > 0 && !snapshot.HasSurfaceContact))
                {
                    _liftoffVideoTimestamp = elapsed;
                    Console.WriteLine(LogPrefix + $"liftoff detected at {elapsed:F2}s");
                }
            }

            if (snapshot.Events != null)
            {
                var liveRecorded = snapshot.Events.Recorded;
                while (_recordedEvents.Count < liveRecorded.Count)
                {
                    _recordedEvents.Add(liveRecorded[_recordedEvents.Count]);
                }
            }

            _telemetryLog.Add(new TelemetryLogEntry(elapsed, snapshot.Clone()));
            _statusMessage = $"Recording: {elapsed:F1}s ({_cleanEncoder.FramesWritten} frames)";

            // 4. Auto-stop on mission end (Landing or Vehicle lost)
            if (config.AutoStopOnMissionEnd)
            {
                if (!snapshot.HasVehicle)
                {
                    StopRecording();
                }
                else if (snapshot.HasLiftoff && snapshot.HasSurfaceContact && snapshot.SurfaceSpeed < 2.0)
                {
                    _landingGraceRemaining += dt;
                    if (_landingGraceRemaining >= 4.0) // 4 seconds after touchdown
                    {
                        StopRecording();
                    }
                }
                else
                {
                    _landingGraceRemaining = 0;
                }
            }
        }
    }

    private void SetToast(string text, bool isError)
    {
        _toastText = text;
        _toastIsError = isError;
        _toastRemaining = 4.5;
    }

    /// <summary>
    /// Renders an in-game toast banner and recording indicator in the corner of the screen.
    /// </summary>
    public void DrawToastNotification()
    {
        ImDrawListPtr drawList = ImGui.GetForegroundDrawList();
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();

        // 1. If actively recording, show a subtle pulsing REC indicator in the top-right
        if (IsRecording)
        {
            float recX = viewport.WorkPos.X + viewport.WorkSize.X - 160f;
            float recY = viewport.WorkPos.Y + 20f;
            float2 boxMin = new(recX, recY);
            float2 boxMax = new(recX + 140f, recY + 34f);

            drawList.AddRectFilled(boxMin, boxMax, 0xCC111111, 6f);
            drawList.AddRect(boxMin, boxMax, 0x66FFFFFF, 6f);

            float pulse = 0.6f + 0.4f * MathF.Sin((float)ElapsedSeconds * 5f);
            uint dotColor = ImGui.GetColorU32(new float4(1f, 0.2f, 0.2f, pulse));
            drawList.AddCircleFilled(new float2(recX + 16f, recY + 17f), 6f, dotColor);

            string timerText = FormatElapsed(ElapsedSeconds);
            drawList.AddText(new float2(recX + 32f, recY + 8f), 0xFFFFFFFF, timerText);
        }
        else if (IsFinalizing)
        {
            // Finalization progress indicator
            float barW = 220f;
            float barH = 36f;
            float fx = viewport.WorkPos.X + viewport.WorkSize.X - barW - 20f;
            float fy = viewport.WorkPos.Y + 20f;
            float2 fMin = new(fx, fy);
            float2 fMax = new(fx + barW, fy + barH);

            drawList.AddRectFilled(fMin, fMax, 0xEE111111, 6f);
            drawList.AddRect(fMin, fMax, 0x883399FF, 6f);

            // Inner progress fill
            float pct = FinalizeProgressPercent / 100f;
            float innerW = (barW - 8f) * pct;
            if (innerW > 0)
            {
                drawList.AddRectFilled(new float2(fx + 4f, fy + 4f), new float2(fx + 4f + innerW, fy + barH - 4f), 0x553399FF, 4f);
            }

            string finalText = $"FINALIZING REPLAY {FinalizeProgressPercent}%";
            drawList.AddText(new float2(fx + 14f, fy + 10f), 0xFFFFFFFF, finalText);
        }

        // 2. Toast banner for starts, saves, and errors
        if (_toastRemaining > 0 && !string.IsNullOrEmpty(_toastText))
        {
            float alpha = Math.Clamp((float)_toastRemaining, 0f, 1f);
            if (_toastRemaining > 4.0) alpha = Math.Clamp((float)(4.5 - _toastRemaining) * 2f, 0f, 1f);

            float bannerWidth = 480f;
            float bannerHeight = 40f;
            float centerX = viewport.WorkPos.X + viewport.WorkSize.X * 0.5f;
            float topY = viewport.WorkPos.Y + 30f;

            float2 bMin = new(centerX - bannerWidth * 0.5f, topY);
            float2 bMax = new(centerX + bannerWidth * 0.5f, topY + bannerHeight);

            uint bgColor = _toastIsError
                ? ImGui.GetColorU32(new float4(0.35f, 0.05f, 0.05f, 0.90f * alpha))
                : ImGui.GetColorU32(new float4(0.08f, 0.12f, 0.18f, 0.90f * alpha));

            uint borderColor = _toastIsError
                ? ImGui.GetColorU32(new float4(1.0f, 0.3f, 0.3f, alpha))
                : ImGui.GetColorU32(new float4(0.3f, 0.7f, 1.0f, alpha));

            uint textColor = ImGui.GetColorU32(new float4(1f, 1f, 1f, alpha));

            drawList.AddRectFilled(bMin, bMax, bgColor, 6f);
            drawList.AddRect(bMin, bMax, borderColor, 6f);
            drawList.AddText(new float2(bMin.X + 16f, bMin.Y + 11f), textColor, _toastText);
        }
    }

    private static string FormatElapsed(double seconds)
    {
        int totalSec = (int)Math.Max(0, seconds);
        int m = totalSec / 60;
        int s = totalSec % 60;
        return $"REC {m:00}:{s:00}";
    }

    public static void OpenRecordingsFolder(string? customFolder)
    {
        string dir = !string.IsNullOrWhiteSpace(customFolder) && Directory.Exists(customFolder)
            ? customFolder
            : GetDefaultRecordingsDirectory();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"could not open recordings folder: {ex.Message}");
        }
    }

    private static string SanitizeFilename(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Vehicle";
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] result = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            result[i] = Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c;
        }
        return new string(result);
    }

    public void Dispose()
    {
        StopFinalizerThread();
        if (IsRecording)
        {
            _cleanEncoder.Stop();
        }
        if (IsFinalizing)
        {
            _compositorEncoder.Stop();
        }
        _state = RecordingState.Idle;
        _cleanEncoder.Dispose();
        _compositorEncoder.Dispose();
        _compositor.Dispose();
    }
}
