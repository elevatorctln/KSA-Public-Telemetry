using System;
using System.Collections.Generic;
using System.Threading;

namespace KSATelemetryOverlay.Recording;

/// <summary>
/// Bounded hand-off of raw frames from the render thread to a dedicated writer thread.
/// Every buffer has exactly one owner at a time: the render thread rents a free buffer,
/// fills it and submits it; the worker hands it to the sink and only then returns it to
/// the free pool. When the pool is empty the render thread is told so (TryRent fails)
/// instead of overwriting a buffer the worker is still reading.
/// </summary>
public sealed class FrameQueue : IDisposable
{
    public delegate void FrameSink(byte[] buffer, double timestamp);

    private readonly object _lock = new();
    private readonly Stack<byte[]> _free = new();
    private readonly Queue<(byte[] Buffer, double Timestamp)> _pending = new();
    private readonly FrameSink _sink;
    private readonly Thread _thread;
    private bool _running = true;
    private bool _busy;
    private volatile string? _error;

    public int FrameBytes { get; }
    public string? Error => _error;

    public FrameQueue(int frameBytes, int bufferCount, FrameSink sink, string threadName)
    {
        FrameBytes = frameBytes;
        _sink = sink;
        for (int i = 0; i < bufferCount; i++)
        {
            _free.Push(new byte[frameBytes]);
        }

        _thread = new Thread(WorkerLoop)
        {
            Name = threadName,
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public int FreeCount
    {
        get
        {
            lock (_lock)
            {
                return _free.Count;
            }
        }
    }

    public bool TryRent(out byte[] buffer)
    {
        lock (_lock)
        {
            if (_running && _free.Count > 0)
            {
                buffer = _free.Pop();
                return true;
            }
        }

        buffer = null!;
        return false;
    }

    /// <summary>Gives back a rented buffer without writing it.</summary>
    public void Return(byte[] buffer)
    {
        lock (_lock)
        {
            _free.Push(buffer);
        }
    }

    public void Submit(byte[] buffer, double timestamp)
    {
        lock (_lock)
        {
            _pending.Enqueue((buffer, timestamp));
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Blocks until every submitted frame has reached the sink, or the timeout passes.</summary>
    public bool WaitIdle(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        lock (_lock)
        {
            while (_pending.Count > 0 || _busy)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || _error != null)
                {
                    return false;
                }

                Monitor.Wait(_lock, (int)remaining);
            }
        }

        return true;
    }

    /// <summary>Writes out what is queued (up to the timeout), then stops the worker.</summary>
    public void Stop(int drainTimeoutMs)
    {
        WaitIdle(drainTimeoutMs);
        lock (_lock)
        {
            _running = false;
            _pending.Clear();
            Monitor.PulseAll(_lock);
        }

        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(2000);
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            (byte[] Buffer, double Timestamp) item;
            lock (_lock)
            {
                while (_running && _pending.Count == 0)
                {
                    Monitor.Wait(_lock);
                }

                if (!_running)
                {
                    return;
                }

                item = _pending.Dequeue();
                _busy = true;
            }

            try
            {
                _sink(item.Buffer, item.Timestamp);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }

            lock (_lock)
            {
                _free.Push(item.Buffer);
                _busy = false;
                Monitor.PulseAll(_lock);
            }
        }
    }

    public void Dispose() => Stop(0);
}
