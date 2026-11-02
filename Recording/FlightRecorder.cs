using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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
    /// <summary>Background work: closing the clean video, muxing, or finishing the compositor.</summary>
    Saving,
    /// <summary>The overlay pass is rendering, one or more frames per game frame.</summary>
    Finalizing,
}

/// <summary>
/// Telemetry captured at the same moment as one video frame. <see cref="VideoTime"/> is on the
/// same clock as the frame, so the replay can look up exactly what the player saw.
/// </summary>
public sealed class TelemetryLogEntry
{
    public readonly double VideoTime;
    public readonly TelemetrySnapshot Snapshot;
    public readonly MissionEvent[] PlannedBurns;

    public TelemetryLogEntry(double videoTime, TelemetrySnapshot snapshot, MissionEvent[] plannedBurns)
    {
        VideoTime = videoTime;
        Snapshot = snapshot;
        PlannedBurns = plannedBurns;
    }
}

/// <summary>
/// In-game flight recording in two passes:
/// Pass 1 (in flight) records the clean 3D scene and, for every captured frame, the telemetry
/// that was current when it was rendered.
/// Pass 2 (after the flight) renders the overlay frame by frame from that log, so the countdown
/// and intro can be placed retroactively around liftoff, and composites it into the final video.
/// </summary>
public sealed class FlightRecorder : IDisposable
{
    private const string LogPrefix = "[FlightRecorder] ";
    private const int CleanStopTimeoutMs = 30_000;
    private const int CompositorStopTimeoutMs = 600_000;

    public static FlightRecorder Instance { get; } = new();

    private readonly FfmpegEncoder _cleanEncoder = new();
    private readonly FfmpegEncoder _compositorEncoder = new();
    private readonly VulkanOverlayCompositor _compositor = new();
    private readonly GameAudioCapture _audioCapture = new();

    private volatile RecordingState _state = RecordingState.Idle;
    private Task? _backgroundTask;

    // Recording clock: video time 0 is the first captured frame
    private long _recordStartTicks;
    private long _originTicks;
    private bool _hasOrigin;
    private int _lockedWidth;
    private int _lockedHeight;

    // Clean video writes happen on the capture writer thread
    private readonly object _cleanWriteLock = new();
    private bool _cleanAccepting;
    private volatile bool _cleanWriteFailed;

    private readonly List<TelemetryLogEntry> _telemetryLog = new(4096);
    private MissionEvent[] _lastPlannedBurns = [];
    private readonly List<MissionEvent> _recordedEvents = new(32);
    private MissionEventLog? _liveEventsSource;
    private int _liveEventsGeneration;
    private int _liveEventsCopied;

    // Replay timeline, worked out once the flight is over
    private double _liftoffVideoTime = double.NaN;
    private bool _useGameMet;
    private bool _replayHasLiftoff;
    private double _overlayOffset;
    private double _introStartVideoTime;
    private double _audioOffsetSeconds;
    private double _cleanDurationSeconds;
    private bool _hasCleanAudio;

    // Overlay pass
    private int _finalizeTotalFrames;
    private int _finalizeRenderFrame;
    private int _finalizeReadFrame;
    private int _finalizeSubFrames = 1;
    private int _nextStagingSlot;
    private readonly Queue<(int Slot, ulong SubmittedAt)> _stagingInFlight = new();
    private FrameQueue? _finalizeQueue;
    private int _logSearchIndex;
    private int _eventsReplayedCount;
    private readonly TelemetrySnapshot _frameSnapshot = new();
    private readonly MissionEventLog _recorderEvents = new();

    private string? _cleanVideoPath;
    private string? _cleanAudioPath;
    private string? _finalVideoPath;
    private int _targetWidth = 1920;
    private int _targetHeight = 1080;
    private int _targetFps = 30;
    private string? _activeFfmpegPath;
    private string _activeCodec = "libx264";

    private bool _autoArmed;
    private double _landingGraceRemaining;
    private double _toastRemaining;
    private string _toastText = "";
    private bool _toastIsError;

    private string? _lastSavedPath;
    private double _lastSavedSizeMb;
    private string _statusMessage = "Ready";

    // Independent overlay instance for the recording, so the replay never disturbs the live HUD
    private OverlayConfig? _recorderConfig;
    private OverlayRenderer? _recorderRenderer;

    public RecordingState State => _state;
    public bool IsRecording => _state == RecordingState.Recording;
    public bool IsFinalizing => _state == RecordingState.Finalizing;
    public bool IsSaving => _state == RecordingState.Saving;
    public long FramesWritten => _cleanEncoder.FramesWritten;
    public double ElapsedSeconds => IsRecording ? Stopwatch.GetElapsedTime(_recordStartTicks).TotalSeconds : 0.0;
    public string ActiveCodec => _activeCodec;
    public string? LastSavedPath => _lastSavedPath;
    public double LastSavedSizeMb => _lastSavedSizeMb;
    public string StatusMessage => _statusMessage;
    public OverlayRenderer? RecorderRenderer => _recorderRenderer;
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
        string timeStamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");

        _finalVideoPath = Path.Combine(recordingsDir, $"{vehicleSafe}_{timeStamp}.mp4");
        _cleanVideoPath = Path.Combine(recordingsDir, $".clean_{vehicleSafe}_{timeStamp}.mp4");
        _cleanAudioPath = Path.Combine(recordingsDir, $".clean_{vehicleSafe}_{timeStamp}.wav");

        GameCaptureHook.GetCaptureDimensions(config, isRecording: true, out _targetWidth, out _targetHeight);
        _targetFps = Math.Clamp(config.RecordFps, 15, 60);

        _activeFfmpegPath = ffmpeg;
        _activeCodec = FfmpegEncoder.DetectBestCodec(ffmpeg);

        // Independent overlay state for the recorder, preserving all visual configuration.
        // Scale is in pixels, so it follows the recording's height to keep the HUD's proportions.
        _recorderConfig = config.CloneForRecording();
        float liveHeight = LiveDisplayHeight();
        if (liveHeight > 0)
        {
            _recorderConfig.Scale = config.Scale * (_targetHeight / liveHeight);
        }

        _recorderRenderer = null;

        _telemetryLog.Clear();
        _lastPlannedBurns = [];
        _recordedEvents.Clear();
        _liveEventsSource = snapshot.Events;
        _liveEventsGeneration = snapshot.Events?.Generation ?? 0;
        _liveEventsCopied = 0;
        CopyLiveEvents(snapshot.Events);
        _landingGraceRemaining = 0;
        _hasOrigin = false;
        _cleanWriteFailed = false;

        if (!_cleanEncoder.Start(ffmpeg, _cleanVideoPath, _targetWidth, _targetHeight, _targetFps, _activeCodec))
        {
            SetToast("Failed to launch FFmpeg encoder process", isError: true);
            _statusMessage = "Error: Failed to launch FFmpeg";
            return false;
        }

        _recordStartTicks = Stopwatch.GetTimestamp();
        _lockedWidth = _targetWidth & ~1;
        _lockedHeight = _targetHeight & ~1;
        lock (_cleanWriteLock)
        {
            _cleanAccepting = true;
        }

        // Audio starts after the encoder; its exact offset from the first frame is measured
        if (config.RecordAudio)
        {
            _audioCapture.Start(_cleanAudioPath);
        }

        _state = RecordingState.Recording;
        bool includeOverlay = _recorderConfig.RecordOverlay;
        SetToast(includeOverlay
            ? $"Recording flight ({FfmpegEncoder.CodecDisplayName(_activeCodec)})"
            : $"Recording clean flight ({FfmpegEncoder.CodecDisplayName(_activeCodec)})", isError: false);
        _statusMessage = includeOverlay ? "Recording flight (with overlay)..." : "Recording clean flight...";

        Console.WriteLine(LogPrefix + $"recording session started: {_targetWidth}x{_targetHeight} @ {_targetFps}fps ({_activeCodec})");
        return true;
    }

    private static float LiveDisplayHeight()
    {
        try
        {
            return ImGui.GetCurrentContext().IsNull() ? 0f : ImGui.GetIO().DisplaySize.Y;
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>
    /// The frame size the encoder was started with. The capture hook scales into it for the
    /// whole recording, whatever happens to the window.
    /// </summary>
    public bool TryGetLockedCaptureSize(out int width, out int height)
    {
        width = _lockedWidth;
        height = _lockedHeight;
        return _state == RecordingState.Recording && width > 0 && height > 0;
    }

    /// <summary>
    /// Called by the capture hook (render thread) as it records the copy of a frame. Returns that
    /// frame's video timestamp and logs the telemetry the frame was rendered with.
    /// </summary>
    public double OnCaptureSubmitted()
    {
        if (_state != RecordingState.Recording) return double.NaN;

        long now = Stopwatch.GetTimestamp();
        if (!_hasOrigin)
        {
            _originTicks = now;
            _hasOrigin = true;
        }

        double videoTime = Stopwatch.GetElapsedTime(_originTicks, now).TotalSeconds;
        if (GameCaptureHook.CurrentSnapshot is { } live)
        {
            TelemetrySnapshot copy = live.Clone();
            copy.Events = null; // the live log keeps changing; events are tracked separately
            _telemetryLog.Add(new TelemetryLogEntry(videoTime, copy, CapturePlannedBurns(live.Events)));
        }

        return videoTime;
    }

    /// <summary>
    /// Planned burns as they stood at this moment. Consecutive frames almost always share the
    /// same plan, so the previous array is reused rather than allocating one per frame.
    /// </summary>
    private MissionEvent[] CapturePlannedBurns(MissionEventLog? events)
    {
        if (events == null) return [];

        IReadOnlyList<MissionEvent> predicted = events.Predicted;
        int count = 0;
        bool same = true;
        for (int i = 0; i < predicted.Count; i++)
        {
            if (predicted[i].Kind != MissionEventKind.PlannedBurn) continue;
            same &= count < _lastPlannedBurns.Length && _lastPlannedBurns[count].MissionTime == predicted[i].MissionTime;
            count++;
        }

        if (same && count == _lastPlannedBurns.Length)
        {
            return _lastPlannedBurns;
        }

        var burns = new MissionEvent[count];
        int n = 0;
        for (int i = 0; i < predicted.Count; i++)
        {
            if (predicted[i].Kind == MissionEventKind.PlannedBurn)
            {
                burns[n++] = predicted[i];
            }
        }

        _lastPlannedBurns = burns;
        return burns;
    }

    /// <summary>
    /// Called on the capture writer thread with a finished frame. Frames are placed on the
    /// constant-framerate timeline by their capture timestamp: a frame that arrives after a hitch
    /// fills the gap, one that arrives early for its slot is dropped.
    /// </summary>
    public void WriteCapturedFrame(ReadOnlySpan<byte> rgba, int width, int height, double videoTime)
    {
        lock (_cleanWriteLock)
        {
            if (!_cleanAccepting || width != _lockedWidth || height != _lockedHeight)
            {
                return;
            }

            long targetFrames = (long)Math.Floor(videoTime * _targetFps + 0.5) + 1;
            long count = Math.Min(targetFrames - _cleanEncoder.FramesWritten, _targetFps * 10L);
            if (count <= 0)
            {
                return;
            }

            if (!_cleanEncoder.WriteFrame(rgba, (int)count))
            {
                _cleanAccepting = false;
                _cleanWriteFailed = true;
            }
        }
    }

    /// <summary>
    /// Stops recording. Closing the encoder and preparing the overlay pass run in the background.
    /// </summary>
    public void StopRecording()
    {
        if (_state != RecordingState.Recording) return;

        _state = RecordingState.Saving;
        _statusMessage = "Saving clean video...";
        bool includeOverlay = _recorderConfig?.RecordOverlay ?? true;
        _backgroundTask = Task.Run(() => FinishRecording(includeOverlay));
    }

    private void FinishRecording(bool includeOverlay)
    {
        try
        {
            // Let frames already captured reach the encoder, then shut the door before closing it
            GameCaptureHook.WaitForEncoderIdle(2000);
            lock (_cleanWriteLock)
            {
                _cleanAccepting = false;
            }

            var cleanResult = _cleanEncoder.Stop(CleanStopTimeoutMs);

            var audioResult = _audioCapture.IsRecording ? _audioCapture.Stop() : default;
            _hasCleanAudio = audioResult.Success && !string.IsNullOrEmpty(_cleanAudioPath) && File.Exists(_cleanAudioPath);
            _audioOffsetSeconds = 0;
            if (_hasCleanAudio && _hasOrigin && _audioCapture.FirstSampleTimestamp != 0)
            {
                _audioOffsetSeconds = (_audioCapture.FirstSampleTimestamp - _originTicks) / (double)Stopwatch.Frequency;
            }

            if (!_hasCleanAudio)
            {
                TryDelete(_cleanAudioPath);
            }

            _cleanDurationSeconds = cleanResult.Frames / (double)_targetFps;

            if (!cleanResult.Success || cleanResult.Frames <= 0)
            {
                _statusMessage = "Recording failed (see console log)";
                SetToast("Recording failed or produced empty file", isError: true);
                TryDelete(_cleanVideoPath);
                TryDelete(_cleanAudioPath);
                _state = RecordingState.Idle;
                return;
            }

            Console.WriteLine(LogPrefix + $"clean pass finished: {cleanResult.Frames} frames, audio {(_hasCleanAudio ? $"offset {_audioOffsetSeconds:F3}s" : "none")}");

            if (!includeOverlay)
            {
                SaveWithoutOverlay(_finalVideoPath!, "Clean flight recorded");
                _state = RecordingState.Idle;
                return;
            }

            PrepareReplay((int)cleanResult.Frames);

            bool compStarted = _compositorEncoder.StartCompositor(
                _activeFfmpegPath ?? "ffmpeg",
                _cleanVideoPath!,
                _finalVideoPath!,
                _targetWidth,
                _targetHeight,
                _targetFps,
                _activeCodec,
                _cleanDurationSeconds,
                _hasCleanAudio ? _cleanAudioPath : null,
                _audioOffsetSeconds);

            if (!compStarted)
            {
                Salvage("compositor failed to start");
                _state = RecordingState.Idle;
                return;
            }

            _statusMessage = $"Finalizing replay video... 0% (0/{_finalizeTotalFrames})";
            _state = RecordingState.Finalizing;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"error finishing recording: {ex}");
            Salvage(ex.Message);
            _state = RecordingState.Idle;
        }
    }

    /// <summary>Moves the clean video (with its audio, if any) to <paramref name="outputPath"/>.</summary>
    private bool SaveWithoutOverlay(string outputPath, string toastPrefix)
    {
        string cleanPath = _cleanVideoPath ?? string.Empty;
        if (!File.Exists(cleanPath))
        {
            return false;
        }

        string? audio = _hasCleanAudio ? _cleanAudioPath : null;
        bool remuxed = !string.IsNullOrEmpty(_activeFfmpegPath) &&
            FfmpegEncoder.Remux(_activeFfmpegPath, cleanPath, audio, _audioOffsetSeconds, _cleanDurationSeconds, outputPath);

        if (remuxed)
        {
            TryDelete(cleanPath);
        }
        else
        {
            try
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(cleanPath, outputPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine(LogPrefix + $"error saving clean video: {ex.Message}");
                outputPath = cleanPath;
            }
        }

        TryDelete(_cleanAudioPath);

        _lastSavedPath = outputPath;
        _lastSavedSizeMb = File.Exists(outputPath) ? new FileInfo(outputPath).Length / (1024.0 * 1024.0) : 0;
        string baseName = Path.GetFileName(outputPath);
        _statusMessage = $"Saved: {baseName} ({_lastSavedSizeMb:F1} MB)";
        SetToast($"{toastPrefix}: {baseName} ({_lastSavedSizeMb:F1} MB, {_cleanDurationSeconds:F0}s)", isError: false);
        Console.WriteLine(LogPrefix + $"saved without overlay: {outputPath} ({_lastSavedSizeMb:F1} MB)");
        return true;
    }

    /// <summary>
    /// Keeps the flight when the overlay pass can't finish: the clean video is saved next to
    /// where the replay would have gone, and any partial replay is removed.
    /// </summary>
    private void Salvage(string reason)
    {
        Console.WriteLine(LogPrefix + $"overlay pass failed ({reason}); keeping the clean video");
        TryDelete(_finalVideoPath);

        string final = _finalVideoPath ?? _cleanVideoPath ?? "recording.mp4";
        string salvagePath = Path.Combine(
            Path.GetDirectoryName(final) ?? "",
            Path.GetFileNameWithoutExtension(final) + "_no-overlay.mp4");

        if (SaveWithoutOverlay(salvagePath, "Overlay failed, saved without it"))
        {
            _toastIsError = true;
        }
        else
        {
            _statusMessage = $"Replay failed: {reason}";
            SetToast($"Replay failed: {reason}", isError: true);
        }
    }

    // ----- Replay timeline -----

    /// <summary>
    /// Works out liftoff, the intro and the event list from the telemetry log.
    /// Mission time comes from the game's own clock after liftoff, so pauses and time warp
    /// during the recording keep the replay clock and events where they happened.
    /// </summary>
    private void PrepareReplay(int totalFrames)
    {
        _overlayOffset = _recorderConfig?.ReplayOverlayOffset ?? 0.0;
        double introLead = _recorderConfig?.ReplayIntroLeadSeconds ?? 10.0;

        _liftoffVideoTime = double.NaN;
        _useGameMet = false;

        // The game's liftoff flag, when the mission clock saw it
        for (int i = 0; i < _telemetryLog.Count; i++)
        {
            TelemetryLogEntry entry = _telemetryLog[i];
            if (entry.Snapshot.HasLiftoff)
            {
                _useGameMet = true;
                // Negative when recording started mid-flight
                _liftoffVideoTime = entry.VideoTime - Math.Max(0.0, entry.Snapshot.MissionElapsedSeconds);
                break;
            }
        }

        // Otherwise, the vehicle visibly leaving the ground
        if (double.IsNaN(_liftoffVideoTime))
        {
            for (int i = 0; i < _telemetryLog.Count; i++)
            {
                TelemetrySnapshot s = _telemetryLog[i].Snapshot;
                if ((s.Thrust > 0 && s.BurningEngineCount > 0 && !s.HasSurfaceContact) ||
                    (s.VerticalSpeed > 1.0 && s.RadarAltitude > 10.0))
                {
                    _liftoffVideoTime = _telemetryLog[i].VideoTime;
                    Console.WriteLine(LogPrefix + $"liftoff inferred from telemetry at {_liftoffVideoTime:F2}s");
                    break;
                }
            }
        }

        _replayHasLiftoff = !double.IsNaN(_liftoffVideoTime);
        _introStartVideoTime = _replayHasLiftoff
            ? Math.Max(0.0, _liftoffVideoTime + _overlayOffset - introLead)
            : 0.0;

        if (_replayHasLiftoff && !_recordedEvents.Exists(e => e.Kind == MissionEventKind.Liftoff))
        {
            _recordedEvents.Add(new MissionEvent(MissionEventKind.Liftoff, 0.0));
        }

        // If the live event log caught little or nothing, rebuild the milestones from telemetry
        if (_recordedEvents.Count <= 1 && _telemetryLog.Count > 0)
        {
            MissionEventLog scanner = new();
            bool liftoffPassed = false;
            TelemetrySnapshot s = new();
            for (int i = 0; i < _telemetryLog.Count; i++)
            {
                TelemetryLogEntry entry = _telemetryLog[i];
                entry.Snapshot.CopyTo(s);
                ApplyMissionTime(s, entry.VideoTime, i);
                bool liftoffThisFrame = !liftoffPassed && s.HasLiftoff;
                liftoffPassed |= s.HasLiftoff;
                scanner.Update(s, liftoffThisFrame, 1, false);
            }

            foreach (var ev in scanner.Recorded)
            {
                if (!IsDuplicateEvent(ev))
                {
                    _recordedEvents.Add(ev);
                }
            }
        }

        _recordedEvents.Sort((a, b) => a.MissionTime.CompareTo(b.MissionTime));

        _recorderEvents.Reset();
        _eventsReplayedCount = 0;

        _finalizeTotalFrames = totalFrames;
        _finalizeRenderFrame = 0;
        _finalizeReadFrame = 0;
        _nextStagingSlot = 0;
        _stagingInFlight.Clear();
        _logSearchIndex = 0;
        _finalizeSubFrames = Math.Clamp(_recorderConfig?.ReplayFinalizeSpeed ?? 2, OverlayConfig.MinFinalizeSpeed, OverlayConfig.MaxFinalizeSpeed);

        Console.WriteLine(LogPrefix + $"{_telemetryLog.Count} telemetry samples, {_recordedEvents.Count} events, " +
            (_replayHasLiftoff ? $"liftoff at {_liftoffVideoTime:F2}s ({(_useGameMet ? "game clock" : "inferred")})" : "no liftoff") +
            $", intro at {_introStartVideoTime:F2}s, overlay offset {_overlayOffset:F2}s");
    }

    /// <summary>
    /// Sets mission time, liftoff and countdown on <paramref name="s"/> for the telemetry sampled
    /// at <paramref name="lookupTime"/>; <paramref name="index"/> is the log entry at or before it.
    /// </summary>
    private void ApplyMissionTime(TelemetrySnapshot s, double lookupTime, int index)
    {
        if (!_replayHasLiftoff)
        {
            return; // nothing to synchronise to: show the telemetry as it was
        }

        if (lookupTime < _liftoffVideoTime)
        {
            s.MissionElapsedSeconds = lookupTime - _liftoffVideoTime;
            s.HasLiftoff = false;
            s.CountingDown = true;
            return;
        }

        s.HasLiftoff = true;
        s.CountingDown = false;

        if (!_useGameMet || index < 0 || index >= _telemetryLog.Count || !_telemetryLog[index].Snapshot.HasLiftoff)
        {
            s.MissionElapsedSeconds = lookupTime - _liftoffVideoTime;
            return;
        }

        // Between two samples the game clock may run at any rate (paused, warped): interpolate
        TelemetryLogEntry a = _telemetryLog[index];
        double met = a.Snapshot.MissionElapsedSeconds;
        double dtVideo = lookupTime - a.VideoTime;
        if (index + 1 < _telemetryLog.Count && _telemetryLog[index + 1].Snapshot.HasLiftoff)
        {
            TelemetryLogEntry b = _telemetryLog[index + 1];
            double span = b.VideoTime - a.VideoTime;
            if (span > 1e-6)
            {
                met += (b.Snapshot.MissionElapsedSeconds - met) * Math.Clamp(dtVideo / span, 0.0, 1.0);
            }
        }
        else
        {
            met += Math.Max(0.0, dtVideo);
        }

        s.MissionElapsedSeconds = met;
    }

    private bool IsDuplicateEvent(MissionEvent ev)
    {
        foreach (MissionEvent existing in _recordedEvents)
        {
            if (existing.Kind == ev.Kind && Math.Abs(existing.MissionTime - ev.MissionTime) < 2.0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Appends events the live log recorded since the last call. A reset of the live log (vehicle
    /// switch, revert) starts its count again rather than silently skipping new events.
    /// </summary>
    private void CopyLiveEvents(MissionEventLog? log)
    {
        if (log == null) return;

        if (!ReferenceEquals(log, _liveEventsSource) || log.Generation != _liveEventsGeneration || log.Recorded.Count < _liveEventsCopied)
        {
            _liveEventsSource = log;
            _liveEventsGeneration = log.Generation;
            _liveEventsCopied = 0;
        }

        IReadOnlyList<MissionEvent> recorded = log.Recorded;
        while (_liveEventsCopied < recorded.Count)
        {
            MissionEvent ev = recorded[_liveEventsCopied++];
            if (!IsDuplicateEvent(ev))
            {
                _recordedEvents.Add(ev);
            }
        }
    }

    // ----- Overlay pass (render thread) -----

    /// <summary>
    /// Renders and reads back overlay frames, paced by KSA's render loop. A staging slot is read
    /// only after KSA has waited on the fence of the frame that filled it, and only when the
    /// writer thread has a free buffer for it, so nothing ever stalls or gets overwritten.
    /// </summary>
    public void StepFinalize(CommandBuffer commandBuffer, Renderer renderer)
    {
        if (_state != RecordingState.Finalizing) return;

        if (_finalizeQueue?.Error is { } writerError)
        {
            AbortFinalize(renderer, writerError);
            return;
        }

        int lag = Math.Max(1, renderer.MaxFramesInFlight);
        int stagingCount = _finalizeSubFrames * (lag + 1);
        if (!_compositor.EnsureFinalizerResources(renderer, _targetWidth, _targetHeight, _finalizeSubFrames, stagingCount))
        {
            AbortFinalize(renderer, "Failed to allocate GPU compositor resources");
            return;
        }

        _finalizeQueue ??= new FrameQueue(_targetWidth * _targetHeight * 4, _finalizeSubFrames + 3, WriteOverlayFrame, "KSAFlightRecorderFinalizer");
        _recorderRenderer ??= new OverlayRenderer(_recorderConfig ?? new OverlayConfig());

        // 1. Read back every slot whose frame the GPU has finished
        while (_stagingInFlight.Count > 0 && renderer.FrameCount - _stagingInFlight.Peek().SubmittedAt >= (ulong)lag)
        {
            if (!_finalizeQueue.TryRent(out byte[] cpuBuffer))
            {
                break; // FFmpeg is behind; try again next frame
            }

            int slot = _stagingInFlight.Dequeue().Slot;
            try
            {
                using (MappedMemory mapped = _compositor.GetStagingBuffer(slot).Map())
                {
                    mapped.AsSpan().CopyTo(cpuBuffer);
                }
            }
            catch (Exception ex)
            {
                _finalizeQueue.Return(cpuBuffer);
                AbortFinalize(renderer, $"Error reading staging buffer: {ex.Message}");
                return;
            }

            _finalizeQueue.Submit(cpuBuffer, 0);
            _finalizeReadFrame++;
        }

        _statusMessage = $"Finalizing replay video... {FinalizeProgressPercent}% ({_finalizeReadFrame}/{_finalizeTotalFrames})";

        // 2. Everything read: hand over to the background to finish the file
        if (_finalizeReadFrame >= _finalizeTotalFrames)
        {
            BeginCompleteFinalize(renderer);
            return;
        }

        // 3. Render as many new frames as there are free staging slots and writer buffers
        int canRender = Math.Min(_finalizeSubFrames, stagingCount - _stagingInFlight.Count);
        canRender = Math.Min(canRender, _finalizeTotalFrames - _finalizeRenderFrame);
        canRender = Math.Min(canRender, _finalizeQueue.FreeCount - _stagingInFlight.Count);

        for (int sub = 0; sub < canRender; sub++)
        {
            int frameIdx = _finalizeRenderFrame;
            double frameTime = frameIdx / (double)_targetFps;
            TelemetrySnapshot? frameSnap = BuildFrameSnapshot(frameTime);
            bool drawHud = frameTime >= _introStartVideoTime;

            if (!_compositor.RenderTransparentOverlay(commandBuffer, _targetWidth, _targetHeight, frameSnap, 1.0 / _targetFps, drawHud, sub))
            {
                AbortFinalize(renderer, $"Overlay render pass failed at frame {frameIdx}");
                return;
            }

            _compositor.CopyOverlayToStaging(commandBuffer, _nextStagingSlot, _targetWidth, _targetHeight);
            _stagingInFlight.Enqueue((_nextStagingSlot, renderer.FrameCount));
            _nextStagingSlot = (_nextStagingSlot + 1) % stagingCount;
            _finalizeRenderFrame++;
        }
    }

    private void WriteOverlayFrame(byte[] buffer, double _)
    {
        if (!_compositorEncoder.WriteFrame(buffer))
        {
            throw new IOException("FFmpeg compositor stdin closed unexpectedly");
        }
    }

    /// <summary>The overlay's view of the flight for the video frame at <paramref name="frameTime"/>.</summary>
    private TelemetrySnapshot? BuildFrameSnapshot(double frameTime)
    {
        if (_telemetryLog.Count == 0) return null;

        // A capture becomes frame k when its timestamp rounds to k, so it can sit up to half a
        // frame after k's nominal time: search that far ahead to find the frame's own sample.
        double lookupTime = frameTime - _overlayOffset;
        double searchTime = lookupTime + 0.5 / _targetFps;
        while (_logSearchIndex + 1 < _telemetryLog.Count && _telemetryLog[_logSearchIndex + 1].VideoTime <= searchTime)
        {
            _logSearchIndex++;
        }

        TelemetryLogEntry entry = _telemetryLog[_logSearchIndex];
        TelemetrySnapshot s = _frameSnapshot;
        entry.Snapshot.CopyTo(s);
        ApplyMissionTime(s, lookupTime, _logSearchIndex);
        double missionSeconds = s.MissionElapsedSeconds;

        // Events whose time has come move into Recorded (that triggers their banners)
        while (_eventsReplayedCount < _recordedEvents.Count && _recordedEvents[_eventsReplayedCount].MissionTime <= missionSeconds)
        {
            _recorderEvents.Record(_recordedEvents[_eventsReplayedCount]);
            _eventsReplayedCount++;
        }

        // The rest are shown ahead of time on the timeline, with the burns planned at that moment
        _recorderEvents.ClearPredictions();
        for (int i = _eventsReplayedCount; i < _recordedEvents.Count; i++)
        {
            _recorderEvents.AddPrediction(_recordedEvents[i]);
        }

        foreach (MissionEvent burn in entry.PlannedBurns)
        {
            if (burn.MissionTime > missionSeconds)
            {
                _recorderEvents.AddPrediction(burn);
            }
        }

        s.Events = _recorderEvents;
        return s;
    }

    private void BeginCompleteFinalize(Renderer renderer)
    {
        // Every staging slot has been read, so the GPU is done with these
        _compositor.DisposeFinalizerResources(renderer);
        _state = RecordingState.Saving;
        _statusMessage = "Finalizing replay video... writing file";
        _backgroundTask = Task.Run(CompleteFinalize);
    }

    private void CompleteFinalize()
    {
        try
        {
            _finalizeQueue?.Stop(60_000);
            _finalizeQueue = null;
            var result = _compositorEncoder.Stop(CompositorStopTimeoutMs);

            if (result.Success)
            {
                TryDelete(_cleanVideoPath);
                TryDelete(_cleanAudioPath);
                _lastSavedPath = result.OutputPath;
                _lastSavedSizeMb = result.FileSizeBytes / (1024.0 * 1024.0);
                string baseName = Path.GetFileName(result.OutputPath);
                _statusMessage = $"Saved: {baseName} ({_lastSavedSizeMb:F1} MB)";
                SetToast($"Flight recorded: {baseName} ({_lastSavedSizeMb:F1} MB, {_cleanDurationSeconds:F0}s)", isError: false);
                Console.WriteLine(LogPrefix + $"replay completed successfully: {result.OutputPath} ({result.Frames} frames, {_lastSavedSizeMb:F1} MB)");
            }
            else
            {
                Salvage("compositing failed (see console log)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"error completing replay: {ex}");
            Salvage(ex.Message);
        }
        finally
        {
            _recorderRenderer = null;
            _state = RecordingState.Idle;
        }
    }

    private void AbortFinalize(Renderer? renderer, string reason)
    {
        Console.WriteLine(LogPrefix + $"finalization aborted: {reason}");
        _compositor.DisposeFinalizerResources(renderer);
        _state = RecordingState.Saving;
        _statusMessage = "Finalization aborted, saving clean video...";
        _backgroundTask = Task.Run(() =>
        {
            try
            {
                StopCompositorNow();
                Salvage(reason);
            }
            finally
            {
                _recorderRenderer = null;
                _state = RecordingState.Idle;
            }
        });
    }

    /// <summary>Kills FFmpeg first so a writer blocked on its stdin is released, then stops the writer.</summary>
    private void StopCompositorNow()
    {
        _compositorEncoder.Kill();
        _finalizeQueue?.Stop(0);
        _finalizeQueue = null;
        _compositorEncoder.Stop(2000);
    }

    // ----- Per-frame update -----

    /// <summary>
    /// Per-frame update hook for hotkeys, auto-triggers, and event tracking.
    /// </summary>
    public void Update(double dt, TelemetrySnapshot snapshot, OverlayConfig config, string? modDirectory)
    {
        if (_toastRemaining > 0)
        {
            _toastRemaining = Math.Max(0, _toastRemaining - dt);
        }

        if (_cleanWriteFailed && IsRecording)
        {
            _cleanWriteFailed = false;
            Console.WriteLine(LogPrefix + "the encoder stopped accepting frames, stopping the recording");
            StopRecording();
        }

        // 1. Hotkey check
        try
        {
            if (!ImGui.GetCurrentContext().IsNull() && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(config.RecordKey, repeat: false))
            {
                if (IsRecording)
                {
                    _autoArmed = false;
                    StopRecording();
                }
                else if (_state == RecordingState.Idle)
                {
                    StartRecording(config, snapshot, modDirectory);
                }
                else
                {
                    SetToast("Still saving the previous recording...", isError: false);
                }
            }
        }
        catch
        {
            // Outside of active ImGui frame or context
        }

        // 2. Auto-record on engine ignition. It arms only while the vehicle sits on the pad with its
        //    engines off, so a manual stop or a failed start doesn't immediately start again.
        bool enginesLit = snapshot.Thrust > 0f || snapshot.BurningEngineCount > 0;
        if (!config.AutoRecordOnLiftoff || !snapshot.HasVehicle || snapshot.HasLiftoff)
        {
            _autoArmed = false;
        }
        else if (!enginesLit && _state == RecordingState.Idle)
        {
            _autoArmed = true;
        }
        else if (enginesLit && _autoArmed && _state == RecordingState.Idle)
        {
            _autoArmed = false;
            StartRecording(config, snapshot, modDirectory);
        }

        // 3. During recording, track events and the mission end
        if (_state == RecordingState.Recording)
        {
            CopyLiveEvents(snapshot.Events);
            _statusMessage = $"Recording: {ElapsedSeconds:F1}s ({_cleanEncoder.FramesWritten} frames)";

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
        else if (IsFinalizing || IsSaving)
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

            float pct = IsFinalizing ? FinalizeProgressPercent / 100f : 1f;
            float innerW = (barW - 8f) * pct;
            if (innerW > 0)
            {
                drawList.AddRectFilled(new float2(fx + 4f, fy + 4f), new float2(fx + 4f + innerW, fy + barH - 4f), 0x553399FF, 4f);
            }

            string finalText = IsFinalizing ? $"FINALIZING REPLAY {FinalizeProgressPercent}%" : "SAVING RECORDING...";
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
            string opener = OperatingSystem.IsWindows() ? "explorer.exe"
                : OperatingSystem.IsMacOS() ? "open"
                : "xdg-open";
            ProcessStartInfo psi = new(opener) { UseShellExecute = false };
            psi.ArgumentList.Add(dir);
            Process.Start(psi)?.Dispose();
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
            // Beyond the OS's own rules, keep names portable (Windows' set is stricter than Linux's)
            result[i] = Array.IndexOf(invalid, c) >= 0 || c is ' ' or '"' or '\'' or ':' or '*' or '?' or '<' or '>' or '|' or '\\'
                ? '_'
                : c;
        }
        return new string(result);
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }

    /// <summary>
    /// Game shutdown: never loses the flight. A recording in progress or an unfinished overlay
    /// pass is saved as the clean video instead.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (_state == RecordingState.Recording)
            {
                StopRecording();
            }

            // Finish (or abandon) whatever runs in the background before deciding what is left to save
            _backgroundTask?.Wait(TimeSpan.FromSeconds(15));

            if (_state == RecordingState.Finalizing)
            {
                StopCompositorNow();
                Salvage("game closed before the replay finished");
                _state = RecordingState.Idle;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"error during shutdown: {ex.Message}");
        }

        _cleanEncoder.Dispose();
        _audioCapture.Dispose();
        _compositorEncoder.Dispose();
        _compositor.Dispose();
    }
}
