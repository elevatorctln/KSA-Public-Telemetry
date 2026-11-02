using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using Brutal.FmodApi;
using KSA;

namespace KSATelemetryOverlay.Recording;

/// <summary>
/// High-performance audio capture hook for KSA.
/// Attaches a custom DSP pass-through tap to KSA's FMOD Master ChannelGroup to capture
/// in-game audio directly to a 16-bit PCM WAV file during flight recording.
/// Uses a lock-free circular ring buffer and a dedicated background writer thread to
/// guarantee zero heap allocations, zero GC pressure, and zero audio thread latency.
/// </summary>
public sealed class GameAudioCapture : IDisposable
{
    private const string LogPrefix = "[AudioCapture] ";

    // Circular ring buffer: 4 seconds of stereo 16-bit PCM at 48kHz (384,000 samples = 768 KB)
    private const int RingBufferCapacity = 48000 * 2 * 4;
    private readonly short[] _ringBuffer = new short[RingBufferCapacity];
    private volatile int _writePos;
    private volatile int _readPos;

    private readonly AutoResetEvent _writeSignal = new(false);
    private Thread? _writerThread;
    private volatile bool _writerRunning;
    private readonly object _writerLock = new();

    private FileStream? _fileStream;
    private string? _activeWavPath;
    private long _totalPcmBytesWritten;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private volatile bool _isRecording;
    private long _firstSampleTimestamp;

    // FMOD DSP tap
    private Dsp _tapDsp;
    private DspReadCallback? _readCallback; // Prevent GC collection
    private bool _dspCreated;
    private bool _dspAttached;
    private bool _isDisposed;

    public bool IsRecording => _isRecording;

    /// <summary>
    /// <see cref="System.Diagnostics.Stopwatch"/> timestamp of the mixer block that produced the
    /// first recorded sample, or 0 if none arrived yet. Used to line the audio up with the video.
    /// </summary>
    public long FirstSampleTimestamp => Interlocked.Read(ref _firstSampleTimestamp);
    public string? ActiveWavPath => _activeWavPath;
    public long TotalBytesWritten => _totalPcmBytesWritten;
    public double DurationSeconds => _channels > 0 && _sampleRate > 0
        ? (double)_totalPcmBytesWritten / (_sampleRate * _channels * 2)
        : 0.0;

    public GameAudioCapture()
    {
    }

    private unsafe bool EnsureDspCreated()
    {
        if (_dspCreated && !_tapDsp.IsNull()) return true;

        try
        {
            if (GameAudio.System.IsNull())
            {
                return false;
            }

            // Query software format from FMOD system
            if (GameAudio.System.TryGetSoftwareFormat(out int rate, out var _, out int rawSpeakers) == Result.Ok)
            {
                if (rate > 0) _sampleRate = rate;
                if (rawSpeakers > 0) _channels = rawSpeakers;
            }

            // Determine PluginSdkVersion expected by FMOD
            DspDescription* faderDesc = GameAudio.System.GetDspInfoByType(DspType.Fader);
            uint pluginSdkVersion = faderDesc != null ? faderDesc->PluginSdkVersion : 110;

            DspDescription desc = default;
            desc.PluginSdkVersion = pluginSdkVersion;
            desc.Version = 0x00010000;
            desc.NumInputBuffers = 1;
            desc.NumOutputBuffers = 1;

            _readCallback = DspRead;
            desc.Read = _readCallback;

            // Set plugin name: "KSATap\0"
            byte* namePtr = desc.Name;
            namePtr[0] = (byte)'K';
            namePtr[1] = (byte)'S';
            namePtr[2] = (byte)'A';
            namePtr[3] = (byte)'T';
            namePtr[4] = (byte)'a';
            namePtr[5] = (byte)'p';
            namePtr[6] = 0;

            Result res = GameAudio.System.TryCreateDsp(in desc, out _tapDsp);
            if (res != Result.Ok || _tapDsp.IsNull())
            {
                Console.WriteLine(LogPrefix + $"failed to create FMOD DSP tap: {res}");
                return false;
            }

            _dspCreated = true;
            Console.WriteLine(LogPrefix + $"created FMOD DSP audio tap ({_sampleRate}Hz, {_channels}ch, SDK v{pluginSdkVersion})");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"exception creating DSP tap: {ex.Message}");
            return false;
        }
    }

    private unsafe Result DspRead(
        DspState* dspState,
        float* inBuffer,
        float* outBuffer,
        uint length,
        int inChannels,
        int* outChannels)
    {
        *outChannels = inChannels;

        // 1. Transparent audio pass-through
        if (inBuffer != null && outBuffer != null && inBuffer != outBuffer)
        {
            int totalFloats = (int)(length * inChannels);
            Buffer.MemoryCopy(inBuffer, outBuffer, totalFloats * sizeof(float), totalFloats * sizeof(float));
        }

        // 2. If actively recording, enqueue PCM samples into ring buffer
        float* srcBuffer = inBuffer != null ? inBuffer : outBuffer;
        if (_isRecording && srcBuffer != null && length > 0)
        {
            _channels = inChannels;
            int totalFloats = (int)(length * inChannels);

            int write = _writePos;
            int read = _readPos;
            int capacity = RingBufferCapacity;
            int available = capacity - (write - read);

            // Drop whole sample frames only, or every later frame would land on the wrong channel
            int count = Math.Min(totalFloats, Math.Max(0, available));
            count -= count % Math.Max(1, inChannels);
            if (count > 0 && Interlocked.Read(ref _firstSampleTimestamp) == 0)
            {
                Interlocked.Exchange(ref _firstSampleTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            }

            for (int i = 0; i < count; i++)
            {
                float val = srcBuffer[i];
                int s = (int)(val * 32767f);
                _ringBuffer[(write + i) % capacity] = (short)Math.Clamp(s, -32768, 32767);
            }

            _writePos = write + count;
            _writeSignal.Set();
        }

        return Result.Ok;
    }

    /// <summary>
    /// Begins recording audio to the specified WAV file path.
    /// </summary>
    public bool Start(string wavPath)
    {
        if (_isRecording)
        {
            Console.WriteLine(LogPrefix + "already recording audio, ignoring Start");
            return false;
        }

        try
        {
            if (!EnsureDspCreated())
            {
                Console.WriteLine(LogPrefix + "FMOD DSP tap unavailable, skipping audio recording");
                return false;
            }

            string dir = Path.GetDirectoryName(wavPath) ?? "";
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _activeWavPath = wavPath;
            _totalPcmBytesWritten = 0;
            _writePos = 0;
            _readPos = 0;
            Interlocked.Exchange(ref _firstSampleTimestamp, 0);

            // Open WAV file and write initial placeholder 44-byte header
            _fileStream = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
            Span<byte> header = stackalloc byte[44];
            WriteWavHeader(header, 0, _sampleRate, _channels);
            _fileStream.Write(header);

            // Start background writer thread
            lock (_writerLock)
            {
                _writerRunning = true;
                _writerThread = new Thread(WriterThreadLoop)
                {
                    Name = "KSAAudioRecorder",
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.BelowNormal
                };
                _writerThread.Start();
            }

            // Attach DSP to Master ChannelGroup tail
            ChannelGroup master = GameAudio.GetChannelGroup(ChannelGroupType.Master);
            if (!master.IsNull() && !_tapDsp.IsNull())
            {
                Result res = master.TryAddDsp((int)ChannelControlDspIndex.ChannelControlDspTail, _tapDsp);
                if (res == Result.Ok)
                {
                    _dspAttached = true;
                }
                else
                {
                    Console.WriteLine(LogPrefix + $"warning: failed to attach DSP to Master ChannelGroup: {res}");
                }
            }

            _isRecording = true;
            Console.WriteLine(LogPrefix + $"started audio capture -> '{wavPath}' ({_sampleRate}Hz, {_channels}ch)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"error starting audio capture: {ex.Message}");
            CleanupStream();
            return false;
        }
    }

    /// <summary>
    /// Stops audio recording, detaches the DSP tap, drains remaining buffers, and finalizes the WAV header.
    /// </summary>
    public (bool Success, string WavPath, long FileSizeBytes, double DurationSeconds) Stop()
    {
        if (!_isRecording)
        {
            return (false, _activeWavPath ?? "", 0, 0);
        }

        _isRecording = false;

        // Detach DSP from master channel group
        if (_dspAttached && !_tapDsp.IsNull())
        {
            try
            {
                ChannelGroup master = GameAudio.GetChannelGroup(ChannelGroupType.Master);
                if (!master.IsNull())
                {
                    master.TryRemoveDsp(_tapDsp);
                }
            }
            catch { }
            _dspAttached = false;
        }

        // Stop background worker and drain pending buffer
        lock (_writerLock)
        {
            _writerRunning = false;
            _writeSignal.Set();
        }

        if (_writerThread != null && _writerThread.IsAlive)
        {
            _writerThread.Join(5000);
            _writerThread = null;
        }

        string wavPath = _activeWavPath ?? "";
        long totalBytes = _totalPcmBytesWritten;
        double duration = DurationSeconds;
        long finalFileSize = 0;

        // Finalize WAV file header
        try
        {
            if (_fileStream != null)
            {
                // Write finalized header with exact data size
                _fileStream.Seek(0, SeekOrigin.Begin);
                Span<byte> finalHeader = stackalloc byte[44];
                WriteWavHeader(finalHeader, (uint)totalBytes, _sampleRate, _channels);
                _fileStream.Write(finalHeader);
                _fileStream.Flush();
                finalFileSize = _fileStream.Length;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"error finalizing WAV header: {ex.Message}");
        }
        finally
        {
            CleanupStream();
        }

        bool success = File.Exists(wavPath) && totalBytes > 0;
        if (success)
        {
            Console.WriteLine(LogPrefix + $"audio capture finished: {duration:F1}s ({finalFileSize / (1024.0 * 1024.0):F2} MB) -> '{wavPath}'");
        }
        else
        {
            Console.WriteLine(LogPrefix + $"audio capture produced empty file or failed -> '{wavPath}'");
        }

        return (success, wavPath, finalFileSize, duration);
    }

    private void WriterThreadLoop()
    {
        // Reusable buffer for file writes: 8192 samples (16 KB)
        byte[] writeBytes = new byte[8192 * sizeof(short)];

        while (_writerRunning)
        {
            _writeSignal.WaitOne(100);
            DrainRingBuffer(writeBytes);
        }

        // Final drain
        DrainRingBuffer(writeBytes);
    }

    private void DrainRingBuffer(byte[] writeBytes)
    {
        if (_fileStream == null) return;

        try
        {
            int write = _writePos;
            int read = _readPos;
            int capacity = RingBufferCapacity;
            int available = write - read;
            if (available <= 0) return;

            int chunkSamples = writeBytes.Length / sizeof(short);

            while (available > 0)
            {
                int toRead = Math.Min(available, chunkSamples);
                for (int i = 0; i < toRead; i++)
                {
                    short sample = _ringBuffer[(read + i) % capacity];
                    BinaryPrimitives.WriteInt16LittleEndian(writeBytes.AsSpan(i * sizeof(short), sizeof(short)), sample);
                }

                int byteCount = toRead * sizeof(short);
                _fileStream.Write(writeBytes, 0, byteCount);
                _totalPcmBytesWritten += byteCount;

                read += toRead;
                _readPos = read;
                available = write - read;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"writer exception: {ex.Message}");
        }
    }

    public static void WriteWavHeader(Span<byte> buffer, uint dataSize, int sampleRate, int channels)
    {
        channels = Math.Clamp(channels, 1, 8);
        sampleRate = Math.Clamp(sampleRate, 8000, 192000);

        // 0..3: "RIFF"
        buffer[0] = (byte)'R'; buffer[1] = (byte)'I'; buffer[2] = (byte)'F'; buffer[3] = (byte)'F';
        // 4..7: ChunkSize = 36 + dataSize
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), 36 + dataSize);
        // 8..11: "WAVE"
        buffer[8] = (byte)'W'; buffer[9] = (byte)'A'; buffer[10] = (byte)'V'; buffer[11] = (byte)'E';
        // 12..15: "fmt "
        buffer[12] = (byte)'f'; buffer[13] = (byte)'m'; buffer[14] = (byte)'t'; buffer[15] = (byte)' ';
        // 16..19: Subchunk1Size = 16 (for PCM)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(16, 4), 16);
        // 20..21: AudioFormat = 1 (PCM)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(20, 2), 1);
        // 22..23: NumChannels
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(22, 2), (ushort)channels);
        // 24..27: SampleRate
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(24, 4), (uint)sampleRate);
        // 28..31: ByteRate = SampleRate * NumChannels * BitsPerSample / 8
        uint byteRate = (uint)(sampleRate * channels * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(28, 4), byteRate);
        // 32..33: BlockAlign = NumChannels * BitsPerSample / 8
        ushort blockAlign = (ushort)(channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(32, 2), blockAlign);
        // 34..35: BitsPerSample = 16
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(34, 2), 16);
        // 36..39: "data"
        buffer[36] = (byte)'d'; buffer[37] = (byte)'a'; buffer[38] = (byte)'t'; buffer[39] = (byte)'a';
        // 40..43: Subchunk2Size = dataSize
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(40, 4), dataSize);
    }

    private void CleanupStream()
    {
        try
        {
            _fileStream?.Dispose();
        }
        catch { }
        _fileStream = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_isRecording)
        {
            Stop();
        }

        if (!_tapDsp.IsNull())
        {
            try
            {
                _tapDsp.Release();
            }
            catch { }
            _tapDsp = default;
        }

        _dspCreated = false;
        _dspAttached = false;
        _readCallback = null;
    }
}
