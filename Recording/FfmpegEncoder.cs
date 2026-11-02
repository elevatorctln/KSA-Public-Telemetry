using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace KSATelemetryOverlay.Recording;

/// <summary>
/// Pipes raw RGBA frames into an FFmpeg process. Hardware encoders (NVENC, VAAPI, AMF, QSV)
/// are probed once and preferred over libx264. Arguments are passed through
/// <see cref="ProcessStartInfo.ArgumentList"/>, so paths and vehicle names never need quoting.
/// </summary>
public sealed class FfmpegEncoder : IDisposable
{
    private const string LogPrefix = "[FlightRecorder:FFmpeg] ";

    private Process? _process;
    private Stream? _stdinStream;
    private string? _outputPath;
    private int _width;
    private int _height;
    private int _fps;
    private string _codec = "libx264";
    private long _framesWritten;
    private readonly Stopwatch _timer = new();
    private readonly StringBuilder _errorLog = new();
    private bool _isDisposed;

    public bool IsRunning => _process is { HasExited: false };
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public double ElapsedSeconds => _timer.Elapsed.TotalSeconds;
    public string ActiveCodec => _codec;
    public string? OutputPath => _outputPath;

    // Probed hardware codec, cached so the probe runs once per ffmpeg binary
    private static readonly object _codecLock = new();
    private static string? _cachedBestCodec;
    private static string? _cachedFfmpegPath;
    private static volatile bool _detecting;

    // FindFfmpeg result cache for per-frame UI queries
    private static string? _findKey;
    private static string? _findResult;
    private static long _findCheckedAt = long.MinValue;

    public static bool IsDetectingCodec => _detecting;

    /// <summary>
    /// Searches for ffmpeg executable across custom path, beside mod directory, and system PATH.
    /// </summary>
    public static string? FindFfmpeg(string? customPath, string? modDirectory)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }

        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        if (!string.IsNullOrWhiteSpace(modDirectory))
        {
            string beside = Path.Combine(modDirectory, name);
            if (File.Exists(beside)) return beside;

            string besideBin = Path.Combine(modDirectory, "bin", name);
            if (File.Exists(besideBin)) return besideBin;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch { }
            }
        }

        if (OperatingSystem.IsLinux())
        {
            string[] standardLinux = ["/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/bin/ffmpeg"];
            foreach (string p in standardLinux)
            {
                if (File.Exists(p)) return p;
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="FindFfmpeg"/> for callers that ask every frame (the settings window):
    /// the search is repeated at most every few seconds.
    /// </summary>
    public static string? FindFfmpegCached(string? customPath, string? modDirectory)
    {
        string key = (customPath ?? "") + "|" + (modDirectory ?? "");
        long now = Environment.TickCount64;
        if (key == _findKey && now - _findCheckedAt < 5000)
        {
            return _findResult;
        }

        _findKey = key;
        _findResult = FindFfmpeg(customPath, modDirectory);
        _findCheckedAt = now;
        return _findResult;
    }

    public static void InvalidateCodecCache()
    {
        lock (_codecLock)
        {
            _cachedBestCodec = null;
            _cachedFfmpegPath = null;
        }

        _findCheckedAt = long.MinValue;
    }

    public static bool TryGetCachedCodec(string ffmpegPath, out string codec)
    {
        lock (_codecLock)
        {
            if (_cachedBestCodec != null && string.Equals(_cachedFfmpegPath, ffmpegPath, StringComparison.Ordinal))
            {
                codec = _cachedBestCodec;
                return true;
            }
        }

        codec = "libx264";
        return false;
    }

    /// <summary>Runs the hardware probe on a background thread so the game never waits on it.</summary>
    public static void WarmUpCodecDetection(string ffmpegPath)
    {
        if (_detecting || TryGetCachedCodec(ffmpegPath, out _))
        {
            return;
        }

        _detecting = true;
        Task.Run(() =>
        {
            try
            {
                DetectBestCodec(ffmpegPath);
            }
            finally
            {
                _detecting = false;
            }
        });
    }

    /// <summary>
    /// Probes available hardware codecs to pick the fastest silicon path, using the same
    /// arguments a real encode would, so a codec that passes the probe also works for real.
    /// Order: NVENC (NVIDIA) -> VAAPI (Linux Intel/AMD) -> AMF (AMD) -> QSV (Intel) -> libx264.
    /// </summary>
    public static string DetectBestCodec(string ffmpegPath)
    {
        lock (_codecLock)
        {
            if (_cachedBestCodec != null && string.Equals(_cachedFfmpegPath, ffmpegPath, StringComparison.Ordinal))
            {
                return _cachedBestCodec;
            }

            string[] candidates = OperatingSystem.IsLinux()
                ? ["h264_nvenc", "h264_vaapi"]
                : ["h264_nvenc", "h264_amf", "h264_qsv"];

            string best = "libx264";
            foreach (string candidate in candidates)
            {
                if (ProbeCodec(ffmpegPath, candidate))
                {
                    best = candidate;
                    break;
                }
            }

            Console.WriteLine(LogPrefix + $"using encoder: {best}");
            _cachedBestCodec = best;
            _cachedFfmpegPath = ffmpegPath;
            return best;
        }
    }

    private static bool ProbeCodec(string ffmpegPath, string codec)
    {
        try
        {
            // 256x256: NVENC/VAAPI reject frames smaller than 144 px
            List<string> args = ["-nostats", "-loglevel", "error"];
            args.AddRange(GlobalArgs(codec));
            args.AddRange(["-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.04", "-vf", OutputFilter(codec)]);
            args.AddRange(EncoderArgs(codec, final: true));
            args.AddRange(["-f", "null", "-"]);

            ProcessStartInfo psi = CreateStartInfo(ffmpegPath, args, redirectStdin: false);
            psi.RedirectStandardOutput = true;

            using Process? p = Process.Start(psi);
            if (p == null) return false;

            Task<string> stderr = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Console.WriteLine(LogPrefix + $"{codec} probe timed out");
                return false;
            }

            if (p.ExitCode == 0)
            {
                Console.WriteLine(LogPrefix + $"hardware encoder verified: {codec}");
                return true;
            }

            Console.WriteLine(LogPrefix + $"{codec} probe failed (code {p.ExitCode}): {stderr.Result.Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"exception probing {codec}: {ex.Message}");
            return false;
        }
    }

    public static string CodecDisplayName(string codec) => codec switch
    {
        "h264_nvenc" => "NVIDIA NVENC (Hardware)",
        "h264_vaapi" => "VAAPI Intel/AMD (Hardware)",
        "h264_amf" => "AMD AMF (Hardware)",
        "h264_qsv" => "Intel QuickSync (Hardware)",
        "libx264" => "x264 (Software CPU)",
        _ => codec
    };

    private static string? _vaapiDevice;

    private static string VaapiDevice
    {
        get
        {
            if (_vaapiDevice != null) return _vaapiDevice;

            _vaapiDevice = "/dev/dri/renderD128";
            for (int i = 128; i < 136; i++)
            {
                string candidate = $"/dev/dri/renderD{i}";
                if (File.Exists(candidate))
                {
                    _vaapiDevice = candidate;
                    break;
                }
            }

            return _vaapiDevice;
        }
    }

    /// <summary>Options that must appear before the inputs.</summary>
    private static string[] GlobalArgs(string codec) => codec == "h264_vaapi"
        ? ["-vaapi_device", VaapiDevice]
        : [];

    /// <summary>
    /// Last filter in the video chain: converts to the pixel format the encoder takes.
    /// VAAPI needs the frames uploaded to the GPU, which is why it can't use a plain -pix_fmt.
    /// </summary>
    private static string OutputFilter(string codec) => codec == "h264_vaapi"
        ? "format=nv12,hwupload"
        : "format=yuv420p";

    /// <summary>
    /// Encoder settings. The intermediate (clean) pass runs live, so it favours speed and keeps
    /// quality high because it is encoded a second time; the final pass favours quality.
    /// </summary>
    private static string[] EncoderArgs(string codec, bool final) => (codec, final) switch
    {
        ("h264_nvenc", false) => ["-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ll", "-rc", "vbr", "-cq", "15", "-b:v", "0"],
        ("h264_nvenc", true) => ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", "0"],
        ("h264_vaapi", false) => ["-c:v", "h264_vaapi", "-qp", "16"],
        ("h264_vaapi", true) => ["-c:v", "h264_vaapi", "-qp", "20"],
        ("h264_amf", false) => ["-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "16", "-qp_p", "16"],
        ("h264_amf", true) => ["-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20"],
        ("h264_qsv", false) => ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "16"],
        ("h264_qsv", true) => ["-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "20"],
        (_, false) => ["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "14"],
        (_, true) => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "18"],
    };

    private static ProcessStartInfo CreateStartInfo(string ffmpegPath, List<string> args, bool redirectStdin)
    {
        ProcessStartInfo psi = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStdin,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void EnsureDirectory(string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// Begins the live clean-scene recording: raw RGBA on stdin, a high quality intermediate on disk.
    /// </summary>
    public bool Start(string ffmpegPath, string outputPath, int width, int height, int fps, string codec)
    {
        List<string> args = ["-y"];
        args.AddRange(GlobalArgs(codec));
        args.AddRange(["-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{width & ~1}x{height & ~1}", "-r", fps.ToString(CultureInfo.InvariantCulture), "-i", "-"]);
        args.AddRange(["-vf", OutputFilter(codec)]);
        args.AddRange(EncoderArgs(codec, final: false));
        args.Add(outputPath);

        return Launch(ffmpegPath, outputPath, width, height, fps, codec, args, "recording");
    }

    /// <summary>
    /// Starts the compositor: reads the clean video from disk, overlays the transparent RGBA frames
    /// streamed through stdin, optionally adds the recorded game audio (delayed by
    /// <paramref name="audioOffsetSeconds"/> so it lines up with the video), and writes the final MP4.
    /// </summary>
    public bool StartCompositor(
        string ffmpegPath, string cleanVideoPath, string outputPath, int width, int height, int fps,
        string codec, double durationSeconds, string? audioPath = null, double audioOffsetSeconds = 0)
    {
        bool hasAudio = !string.IsNullOrEmpty(audioPath) && File.Exists(audioPath);

        List<string> args = ["-y"];
        args.AddRange(GlobalArgs(codec));
        args.AddRange(["-i", cleanVideoPath]);
        args.AddRange(["-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{width & ~1}x{height & ~1}", "-r", fps.ToString(CultureInfo.InvariantCulture), "-i", "-"]);
        if (hasAudio)
        {
            args.AddRange(["-i", audioPath!]);
        }

        // yuv444 keeps the HUD text free of chroma fringes until the final conversion
        string graph = $"[0:v][1:v]overlay=0:0:format=yuv444:alpha=premultiplied:eof_action=repeat,{OutputFilter(codec)}[v]";
        if (hasAudio)
        {
            graph += ";" + AudioDelayFilter("2:a", audioOffsetSeconds);
        }

        args.AddRange(["-filter_complex", graph, "-map", "[v]"]);
        if (hasAudio)
        {
            args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "192k"]);
        }

        args.AddRange(EncoderArgs(codec, final: true));
        args.AddRange(["-t", Seconds(durationSeconds), "-movflags", "+faststart", outputPath]);

        return Launch(ffmpegPath, outputPath, width, height, fps, codec, args, hasAudio ? "compositor (with audio)" : "compositor");
    }

    /// <summary>
    /// Shifts the audio so its first sample lands at <paramref name="offsetSeconds"/> in the video:
    /// pads with silence when audio started after the first video frame, trims when it started before.
    /// </summary>
    private static string AudioDelayFilter(string input, double offsetSeconds)
    {
        if (offsetSeconds >= 0)
        {
            long delayMs = (long)Math.Round(offsetSeconds * 1000.0);
            return $"[{input}]adelay=delays={delayMs}:all=1[a]";
        }

        return $"[{input}]atrim=start={Seconds(-offsetSeconds)},asetpts=PTS-STARTPTS[a]";
    }

    /// <summary>
    /// Copies an already encoded video into its final MP4 (moov atom up front), adding the
    /// recorded audio as AAC when there is one. The video stream is not re-encoded.
    /// </summary>
    public static bool Remux(
        string ffmpegPath, string videoPath, string? audioPath, double audioOffsetSeconds,
        double durationSeconds, string outputPath, int timeoutMs = 120_000)
    {
        try
        {
            EnsureDirectory(outputPath);
            bool hasAudio = !string.IsNullOrEmpty(audioPath) && File.Exists(audioPath);

            List<string> args = ["-y", "-nostats", "-loglevel", "error", "-i", videoPath];
            if (hasAudio)
            {
                args.AddRange(["-i", audioPath!, "-filter_complex", AudioDelayFilter("1:a", audioOffsetSeconds),
                    "-map", "0:v", "-map", "[a]", "-c:a", "aac", "-b:a", "192k"]);
            }

            args.AddRange(["-c:v", "copy"]);
            if (durationSeconds > 0)
            {
                args.AddRange(["-t", Seconds(durationSeconds)]);
            }

            args.AddRange(["-movflags", "+faststart", outputPath]);

            using Process? p = Process.Start(CreateStartInfo(ffmpegPath, args, redirectStdin: false));
            if (p == null) return false;

            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Console.WriteLine(LogPrefix + "remux timed out");
                return false;
            }

            bool success = p.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
            if (!success)
            {
                Console.WriteLine(LogPrefix + $"remux failed (exit code {p.ExitCode}): {stderr.Result.Trim()}");
            }

            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"exception during remux: {ex.Message}");
            return false;
        }
    }

    private bool Launch(string ffmpegPath, string outputPath, int width, int height, int fps, string codec, List<string> args, string what)
    {
        if (IsRunning)
        {
            Console.WriteLine(LogPrefix + $"encoder already running, ignoring {what} start");
            return false;
        }

        try
        {
            EnsureDirectory(outputPath);

            _width = width & ~1;   // H.264 needs even dimensions
            _height = height & ~1;
            _fps = fps;
            _outputPath = outputPath;
            _codec = codec;
            Interlocked.Exchange(ref _framesWritten, 0);
            lock (_errorLog)
            {
                _errorLog.Clear();
            }

            _process = new Process { StartInfo = CreateStartInfo(ffmpegPath, args, redirectStdin: true) };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    lock (_errorLog)
                    {
                        if (_errorLog.Length < 16384)
                        {
                            _errorLog.AppendLine(e.Data);
                        }
                    }
                }
            };

            if (!_process.Start())
            {
                Console.WriteLine(LogPrefix + $"failed to start FFmpeg {what} process");
                _process = null;
                return false;
            }

            _process.BeginErrorReadLine();
            _stdinStream = _process.StandardInput.BaseStream;
            _timer.Restart();

            Console.WriteLine(LogPrefix + $"started {what}: {_width}x{_height} @ {_fps}fps using {_codec} -> '{_outputPath}'");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"error starting {what}: {ex.Message}");
            _process = null;
            return false;
        }
    }

    /// <summary>
    /// Writes one raw RGBA frame <paramref name="count"/> times (count > 1 fills a gap left by a game hitch).
    /// </summary>
    public bool WriteFrame(ReadOnlySpan<byte> rgbaData, int count = 1)
    {
        Stream? stdin = _stdinStream;
        if (stdin == null || count <= 0)
        {
            return stdin != null;
        }

        try
        {
            for (int i = 0; i < count; i++)
            {
                stdin.Write(rgbaData);
                Interlocked.Increment(ref _framesWritten);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"write error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Closes stdin, waits for FFmpeg to finish writing the file, and returns the result.
    /// Only call this once no other thread is writing frames. It can take a long time for a
    /// compositor pass, so call it from a background thread.
    /// </summary>
    public (bool Success, string OutputPath, long Frames, double DurationSeconds, long FileSizeBytes) Stop(int timeoutMs = 3000)
    {
        if (_process == null)
        {
            return (false, _outputPath ?? "", 0, 0, 0);
        }

        _timer.Stop();
        string outPath = _outputPath ?? "";
        long frames = FramesWritten;
        double duration = _timer.Elapsed.TotalSeconds;

        try
        {
            if (_stdinStream != null)
            {
                try
                {
                    _stdinStream.Flush();
                    _stdinStream.Close();
                }
                catch { }
                _stdinStream = null;
            }

            if (!_process.WaitForExit(timeoutMs))
            {
                Console.WriteLine(LogPrefix + $"FFmpeg did not exit within {timeoutMs / 1000}s, terminating");
                try { _process.Kill(entireProcessTree: true); } catch { }
                _process.WaitForExit(2000);
            }

            long fileSize = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            bool success = _process.HasExited && _process.ExitCode == 0 && fileSize > 0;
            if (success)
            {
                Console.WriteLine(LogPrefix + $"completed: {frames} frames ({duration:F1}s), {fileSize / (1024.0 * 1024.0):F2} MB -> '{outPath}'");
            }
            else
            {
                string err;
                lock (_errorLog)
                {
                    err = _errorLog.ToString();
                }
                Console.WriteLine(LogPrefix + $"process exited with code {(_process.HasExited ? _process.ExitCode : -1)}, size: {fileSize}. Log:\n{err}");
            }

            return (success, outPath, frames, duration, fileSize);
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"error stopping encoder: {ex.Message}");
            return (false, outPath, frames, duration, 0);
        }
        finally
        {
            try { _process.Dispose(); } catch { }
            _process = null;
        }
    }

    /// <summary>Terminates FFmpeg at once, which also unblocks a thread stuck writing to its stdin.</summary>
    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_process != null)
        {
            Stop();
        }
    }
}
