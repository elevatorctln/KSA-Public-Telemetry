using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace KSATelemetryOverlay.Recording;

/// <summary>
/// High-performance asynchronous video encoder that pipes raw composited RGBA frames
/// directly into FFmpeg stdin. Automatically probes and prioritizes hardware encoders
/// (NVENC, VAAPI, AMF, QSV) to achieve near-zero CPU and GPU overhead during flight recording.
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
    public long FramesWritten => _framesWritten;
    public double ElapsedSeconds => _timer.Elapsed.TotalSeconds;
    public string ActiveCodec => _codec;
    public string? OutputPath => _outputPath;

    // Cache probed hardware codec so we don't re-test on every flight
    private static string? _cachedBestCodec;
    private static string? _cachedFfmpegPath;

    /// <summary>
    /// Searches for ffmpeg executable across custom path, beside mod directory, and system PATH.
    /// </summary>
    public static string? FindFfmpeg(string? customPath, string? modDirectory)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }

        if (!string.IsNullOrWhiteSpace(modDirectory))
        {
            string besideName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            string beside = Path.Combine(modDirectory, besideName);
            if (File.Exists(beside)) return beside;

            string besideBin = Path.Combine(modDirectory, "bin", besideName);
            if (File.Exists(besideBin)) return besideBin;
        }

        // Check system PATH
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            char sep = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (string dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
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

        // Standard Linux locations
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

    public static void InvalidateCodecCache()
    {
        _cachedBestCodec = null;
        _cachedFfmpegPath = null;
    }

    /// <summary>
    /// Probes available hardware codecs on the active system to pick the fastest silicon path.
    /// Order: NVENC (NVIDIA) -> VAAPI (Linux Intel/AMD) -> AMF (AMD) -> QSV (Intel) -> libx264.
    /// </summary>
    public static string DetectBestCodec(string ffmpegPath)
    {
        if (_cachedBestCodec != null && string.Equals(_cachedFfmpegPath, ffmpegPath, StringComparison.Ordinal))
        {
            return _cachedBestCodec;
        }

        string[] candidates = OperatingSystem.IsLinux()
            ? ["h264_nvenc", "h264_vaapi", "libx264"]
            : ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

        foreach (string candidate in candidates)
        {
            if (candidate == "libx264")
            {
                _cachedBestCodec = "libx264";
                _cachedFfmpegPath = ffmpegPath;
                return "libx264";
            }

            try
            {
                // Test with 256x256 test pattern (NVENC/VAAPI require minimum dimensions >= 144)
                string testArgs = candidate switch
                {
                    "h264_nvenc" => "-nostats -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.04 -c:v h264_nvenc -f null -",
                    "h264_vaapi" => "-nostats -loglevel error -vaapi_device /dev/dri/renderD128 -f lavfi -i color=c=black:s=256x256:d=0.04 -vf format=nv12,hwupload -c:v h264_vaapi -f null -",
                    "h264_amf" => "-nostats -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.04 -c:v h264_amf -f null -",
                    "h264_qsv" => "-nostats -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.04 -c:v h264_qsv -f null -",
                    _ => "-nostats -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.04 -c:v libx264 -f null -"
                };

                ProcessStartInfo psi = new()
                {
                    FileName = ffmpegPath,
                    Arguments = testArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using Process? p = Process.Start(psi);
                if (p != null)
                {
                    // Drain stdout and stderr before WaitForExit to prevent Linux pipe buffer deadlock!
                    string stderr = p.StandardError.ReadToEnd();
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    if (p.HasExited && p.ExitCode == 0)
                    {
                        Console.WriteLine(LogPrefix + $"hardware encoder detected & verified: {candidate}");
                        _cachedBestCodec = candidate;
                        _cachedFfmpegPath = ffmpegPath;
                        return candidate;
                    }
                    else
                    {
                        Console.WriteLine(LogPrefix + $"candidate {candidate} probe failed (code {(p.HasExited ? p.ExitCode : -1)}): {stderr.Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(LogPrefix + $"exception testing {candidate}: {ex.Message}");
            }
        }

        _cachedBestCodec = "libx264";
        _cachedFfmpegPath = ffmpegPath;
        return "libx264";
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

    public static string GetCodecArgs(string codec) => codec switch
    {
        "h264_nvenc" => "-c:v h264_nvenc -preset p1 -tune ll -rc vbr -cq 19 -b:v 0",
        "h264_vaapi" => "-vaapi_device /dev/dri/renderD128 -vf \"format=nv12,hwupload\" -c:v h264_vaapi -qp 20",
        "h264_amf" => "-c:v h264_amf -quality speed -rc cqp -qp-i 20 -qp-p 20",
        "h264_qsv" => "-c:v h264_qsv -preset veryfast -global_quality 20",
        _ => "-c:v libx264 -preset ultrafast -crf 18 -tune zerolatency"
    };

    /// <summary>
    /// Begins a new recording process targeting the specified output MP4 file path.
    /// </summary>
    public bool Start(string ffmpegPath, string outputPath, int width, int height, int fps, string? preferredCodec = null)
    {
        if (IsRunning)
        {
            Console.WriteLine(LogPrefix + "encoder already running, ignoring Start");
            return false;
        }

        try
        {
            string dir = Path.GetDirectoryName(outputPath) ?? "";
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _width = width & ~1;   // Ensure even dimension for H.264
            _height = height & ~1;
            _fps = Math.Clamp(fps, 15, 60);
            _outputPath = outputPath;
            _codec = preferredCodec ?? DetectBestCodec(ffmpegPath);
            _framesWritten = 0;
            _errorLog.Clear();

            string codecArgs = GetCodecArgs(_codec);

            // Use faststart so MP4 header/moov atom is placed at the front of the file
            string arguments = $"-y -f rawvideo -pix_fmt rgba -s {_width}x{_height} -r {_fps} -i - {codecArgs} -pix_fmt yuv420p -movflags +faststart \"{_outputPath}\"";

            ProcessStartInfo psi = new()
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
            };

            _process = new Process { StartInfo = psi };
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
                Console.WriteLine(LogPrefix + "failed to start FFmpeg process");
                return false;
            }

            _process.BeginErrorReadLine();
            _stdinStream = _process.StandardInput.BaseStream;
            _timer.Restart();

            Console.WriteLine(LogPrefix + $"started recording: {_width}x{_height} @ {_fps}fps using {_codec} -> '{_outputPath}'");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"error starting encoder: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts a two-stream hardware compositor process: reads clean video from disk, overlays transparent RGBA
    /// frames streamed through stdin, and encodes the final polished MP4 directly to destination.
    /// </summary>
    public bool StartCompositor(string ffmpegPath, string cleanVideoPath, string outputPath, int width, int height, int fps, string? preferredCodec = null)
    {
        if (IsRunning)
        {
            Console.WriteLine(LogPrefix + "encoder/compositor already running, ignoring StartCompositor");
            return false;
        }

        try
        {
            string dir = Path.GetDirectoryName(outputPath) ?? "";
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _width = width & ~1;
            _height = height & ~1;
            _fps = Math.Clamp(fps, 15, 60);
            _outputPath = outputPath;
            _codec = preferredCodec ?? DetectBestCodec(ffmpegPath);
            _framesWritten = 0;
            _errorLog.Clear();

            string codecArgs = GetCodecArgs(_codec);
            string arguments = $"-y -i \"{cleanVideoPath}\" -f rawvideo -pix_fmt rgba -s {_width}x{_height} -r {_fps} -i - -filter_complex \"[0:v][1:v]overlay=0:0:format=yuv420:alpha=premultiplied\" {codecArgs} -pix_fmt yuv420p -movflags +faststart \"{_outputPath}\"";

            ProcessStartInfo psi = new()
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
            };

            _process = new Process { StartInfo = psi };
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
                Console.WriteLine(LogPrefix + "failed to start FFmpeg compositor process");
                return false;
            }

            _process.BeginErrorReadLine();
            _stdinStream = _process.StandardInput.BaseStream;
            _timer.Restart();

            Console.WriteLine(LogPrefix + $"started hardware compositor: {_width}x{_height} @ {_fps}fps using {_codec} -> '{_outputPath}'");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"error starting compositor: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes uncompressed frames with constant-framerate (CFR) pacing based on elapsed recording time.
    /// If the game dropped frames, repeats the frame to prevent video speed acceleration.
    /// </summary>
    public bool WriteFramePaced(ReadOnlySpan<byte> rgbaData)
    {
        if (_stdinStream == null || _process == null || _process.HasExited)
        {
            return false;
        }

        try
        {
            double elapsed = _timer.Elapsed.TotalSeconds;
            long expectedFrames = (long)(elapsed * _fps);
            if (expectedFrames < _framesWritten)
            {
                // Already caught up for this timeslice
                return true;
            }

            long framesToSend = Math.Clamp(expectedFrames - _framesWritten + 1, 1, 5);
            for (int i = 0; i < framesToSend; i++)
            {
                _stdinStream.Write(rgbaData);
                _framesWritten++;
            }
            return true;
        }
        catch (Exception ex)
        {
            #if DEBUG
            Console.WriteLine(LogPrefix + $"write error: {ex.Message}");
            #endif
            return false;
        }
    }

    /// <summary>
    /// Writes a single uncompressed RGBA frame directly into FFmpeg's standard input pipe.
    /// </summary>
    public bool WriteFrame(ReadOnlySpan<byte> rgbaData)
    {
        if (_stdinStream == null || _process == null || _process.HasExited)
        {
            return false;
        }

        try
        {
            _stdinStream.Write(rgbaData);
            _framesWritten++;
            return true;
        }
        catch (Exception ex)
        {
            #if DEBUG
            Console.WriteLine(LogPrefix + $"write error: {ex.Message}");
            #endif
            return false;
        }
    }

    /// <summary>
    /// Writes a byte array directly into FFmpeg's standard input pipe.
    /// </summary>
    public bool WriteFrame(byte[] buffer)
    {
        if (_stdinStream == null || _process == null || _process.HasExited)
        {
            return false;
        }

        try
        {
            _stdinStream.Write(buffer, 0, buffer.Length);
            _framesWritten++;
            return true;
        }
        catch (Exception ex)
        {
            #if DEBUG
            Console.WriteLine(LogPrefix + $"write error: {ex.Message}");
            #endif
            return false;
        }
    }

    /// <summary>
    /// Flushes and closes standard input, waits for FFmpeg to finish multiplexing the MP4 container,
    /// and returns the final recording statistics.
    /// </summary>
    public (bool Success, string OutputPath, long Frames, double DurationSeconds, long FileSizeBytes) Stop()
    {
        if (_process == null)
        {
            return (false, _outputPath ?? "", 0, 0, 0);
        }

        _timer.Stop();
        string outPath = _outputPath ?? "";
        long frames = _framesWritten;
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

            if (!_process.HasExited)
            {
                if (!_process.WaitForExit(3000))
                {
                    Console.WriteLine(LogPrefix + "FFmpeg process did not exit within 3s, terminating");
                    try { _process.Kill(entireProcessTree: true); } catch { }
                }
            }

            long fileSize = 0;
            if (File.Exists(outPath))
            {
                fileSize = new FileInfo(outPath).Length;
            }

            bool success = _process.ExitCode == 0 && fileSize > 0;
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
                Console.WriteLine(LogPrefix + $"process exited with code {_process.ExitCode}, size: {fileSize}. Log:\n{err}");
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

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (IsRunning)
        {
            Stop();
        }
    }
}
