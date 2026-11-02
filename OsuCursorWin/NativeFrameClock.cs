using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace OsuCursorWin;

internal sealed class NativeFrameClock : IDisposable
{
    private readonly object _sync = new();
    private readonly Dispatcher _dispatcher;
    private readonly Action _tick;
    private readonly AutoResetEvent _wake = new(false);
    private readonly TimerHandle _timer;
    private readonly Thread _worker;
    private PendingTick? _pending;
    private double _refreshRate = 60.0;
    private long _generation;
    private bool _active;
    private bool _delivering;
    private bool _disposed;

    internal NativeFrameClock(Dispatcher dispatcher, Action tick)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(tick);
        _dispatcher = dispatcher;
        _tick = tick;
        try
        {
            _timer = new TimerHandle();
            _worker = new Thread(Run) { IsBackground = true, Name = "OsuCursorFrameClock" };
            _worker.Start();
        }
        catch
        {
            _timer?.Dispose();
            _wake.Dispose();
            throw;
        }
    }

    internal void Start(double refreshRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _refreshRate = NormalizeRate(refreshRate);
            if (!_active)
            {
                _active = true;
                _generation++;
            }
            _wake.Set();
        }
    }

    internal void Stop()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _active = false;
            _generation++;
            CancelPending();
            _wake.Set();
        }
    }

    internal void SetRefreshRate(double refreshRate)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _refreshRate = NormalizeRate(refreshRate);
            _wake.Set();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            _generation++;
            CancelPending();
            _wake.Set();
        }
        if (Thread.CurrentThread != _worker)
        {
            _worker.Join(TimeSpan.FromMilliseconds(250));
        }
    }

    private void Run()
    {
        var waits = new WaitHandle[] { _wake, _timer };
        var generation = -1L;
        var period = 0.0;
        var deadline = 0.0;
        try
        {
            while (true)
            {
                bool active;
                long currentGeneration;
                double refreshRate;
                lock (_sync)
                {
                    if (_disposed) return;
                    active = _active;
                    currentGeneration = _generation;
                    refreshRate = _refreshRate;
                }
                if (!active)
                {
                    _wake.WaitOne();
                    continue;
                }

                var now = (double)Stopwatch.GetTimestamp();
                var nextPeriod = Stopwatch.Frequency / refreshRate;
                if (generation != currentGeneration)
                {
                    generation = currentGeneration;
                    period = nextPeriod;
                    deadline = now;
                }
                else if (period != nextPeriod)
                {
                    var remaining = Math.Clamp((deadline - now) / period, 0.0, 1.0);
                    deadline = now + remaining * nextPeriod;
                    period = nextPeriod;
                }

                if (now >= deadline)
                {
                    QueueTick(generation);
                    deadline += (Math.Floor((now - deadline) / period) + 1.0) * period;
                }

                var remainingTicks = Math.Max(1.0,
                    Math.Ceiling((deadline - Stopwatch.GetTimestamp()) * 10000000.0 / Stopwatch.Frequency));
                var dueTime = -(long)Math.Min(remainingTicks, long.MaxValue);
                if (!SetWaitableTimer(_timer.SafeWaitHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                WaitHandle.WaitAny(waits);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Native frame clock failed: {exception}");
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                try
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                        throw new InvalidOperationException("The cursor frame clock stopped unexpectedly.", exception)));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _disposed = true;
                _active = false;
                CancelPending();
            }
            _timer.Dispose();
            _wake.Dispose();
        }
    }

    private void QueueTick(long generation)
    {
        lock (_sync)
        {
            if (_disposed || !_active || generation != _generation || _pending is not null || _delivering
                || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }
            var pending = new PendingTick(generation);
            _pending = pending;
            try
            {
                pending.Operation = _dispatcher.BeginInvoke(DispatcherPriority.Render,
                    new Action(() => Deliver(pending)));
            }
            catch (InvalidOperationException)
            {
                _pending = null;
            }
        }
    }

    private void Deliver(PendingTick pending)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pending, pending)) return;
            _pending = null;
            if (_disposed || !_active || pending.Generation != _generation) return;
            _delivering = true;
        }
        try
        {
            _tick();
        }
        finally
        {
            lock (_sync)
            {
                _delivering = false;
            }
        }
    }

    private void CancelPending()
    {
        var pending = _pending;
        _pending = null;
        pending?.Operation?.Abort();
    }

    private static double NormalizeRate(double rate) => double.IsFinite(rate) && rate > 1.0 && rate <= 1000.0 ? rate : 60.0;

    private sealed class PendingTick(long generation)
    {
        internal long Generation { get; } = generation;
        internal DispatcherOperation? Operation { get; set; }
    }

    private sealed class TimerHandle : WaitHandle
    {
        internal TimerHandle()
        {
            var handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x001F0003);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x001F0003);
            }
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            SafeWaitHandle = handle;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
