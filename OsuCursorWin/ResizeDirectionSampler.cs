using System;
using System.Threading;

namespace OsuCursorWin;

internal readonly record struct ResizeSample(int X, int Y, IntPtr OverlayWindow, ResizeCursorAxis Axis, int Generation = 0);

internal sealed class ResizeDirectionSampler : IDisposable
{
    private const long CacheMilliseconds = 24;
    private readonly object _sync = new();
    private readonly Func<ResizeSample, ResizeDirection> _detect;
    private readonly Action<ResizeSample, ResizeDirection> _completed;
    private ResizeSample _latest;
    private long _generation;
    private long _activeGeneration;
    private long _completedAt;
    private bool _hasLatest;
    private bool _hasCompleted;
    private bool _pending;
    private bool _queryActive;
    private bool _workerRunning;
    private bool _disposed;

    internal ResizeDirectionSampler(Func<ResizeSample, ResizeDirection> detect,
        Action<ResizeSample, ResizeDirection> completed)
    {
        ArgumentNullException.ThrowIfNull(detect);
        ArgumentNullException.ThrowIfNull(completed);
        _detect = detect;
        _completed = completed;
    }

    internal void Request(ResizeSample sample)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (sample.Axis == ResizeCursorAxis.None)
            {
                ResetCore();
                return;
            }

            if (_hasLatest && _latest == sample)
            {
                if (_pending || (_queryActive && _activeGeneration == _generation)
                    || (_hasCompleted && Environment.TickCount64 - _completedAt < CacheMilliseconds))
                {
                    return;
                }
            }
            else
            {
                _generation++;
                _latest = sample;
                _hasLatest = true;
                _hasCompleted = false;
            }

            _pending = true;
            if (!_workerRunning)
            {
                _workerRunning = true;
                try
                {
                    ThreadPool.QueueUserWorkItem(static sampler => sampler.ProcessRequests(), this, false);
                }
                catch
                {
                    _workerRunning = false;
                    ResetCore();
                    throw;
                }
            }
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            ResetCore();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            ResetCore();
        }
    }

    private void ResetCore()
    {
        _generation++;
        _hasLatest = false;
        _hasCompleted = false;
        _pending = false;
    }

    private void ProcessRequests()
    {
        while (true)
        {
            ResizeSample sample;
            long generation;
            lock (_sync)
            {
                if (_disposed || !_pending)
                {
                    _workerRunning = false;
                    return;
                }

                sample = _latest;
                generation = _generation;
                _pending = false;
                _queryActive = true;
                _activeGeneration = generation;
            }

            var direction = ResizeDirection.None;
            var succeeded = false;
            try
            {
                direction = _detect(sample);
                succeeded = true;
            }
            catch (Exception exception)
            {
                Program.Log($"Resize direction detection failed: {exception}");
            }

            lock (_sync)
            {
                _queryActive = false;
                if (_disposed || !_hasLatest || generation != _generation)
                {
                    continue;
                }

                _hasCompleted = succeeded;
                _completedAt = Environment.TickCount64;
            }

            try
            {
                _completed(sample, direction);
            }
            catch (Exception exception)
            {
                Program.Log($"Resize direction completion failed: {exception}");
            }
        }
    }
}
