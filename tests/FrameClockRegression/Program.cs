using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using OsuCursorWin;

internal static class Program
{
    private static readonly FieldInfo PendingField = typeof(NativeFrameClock).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo WorkerField = typeof(NativeFrameClock).GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [STAThread]
    private static int Main()
    {
        var tests = new Action[]
        {
            IdleClockDoesNotSchedule,
            StartDeliversOnItsDispatcher,
            PendingWorkRemainsBounded,
            StopCancelsQueuedWork,
            RestartDeliversFreshWork,
            NestedDispatcherDoesNotReenter,
            RefreshRateCanChangeWhileRunning,
            InvalidRatesRemainUsable,
            StopAndRestartInsideTick,
            DisposeCancelsQueuedWork,
            DisposeInsideTickDoesNotDeadlock,
            RepeatedLifecyclesReleaseWorkers
        };
        try
        {
            foreach (var test in tests)
            {
                test();
                Console.WriteLine($"PASS {test.Method.Name}");
            }
            Console.WriteLine($"Passed {tests.Length} native frame clock regression checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void IdleClockDoesNotSchedule()
    {
        var ticks = 0;
        using var clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () => ticks++);
        Pump(40);
        Check(ticks == 0 && PendingField.GetValue(clock) is null, "An inactive clock scheduled a frame.");
    }

    private static void StartDeliversOnItsDispatcher()
    {
        var ticks = 0;
        var expectedThread = Environment.CurrentManagedThreadId;
        using var clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () =>
        {
            Check(Environment.CurrentManagedThreadId == expectedThread, "A frame ran outside the owning dispatcher.");
            ticks++;
        });
        clock.Start(165);
        PumpUntil(() => ticks >= 6);
        Check(ticks >= 6, "The clock did not produce frames.");
    }

    private static void PendingWorkRemainsBounded()
    {
        var ticks = 0;
        using var clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () => ticks++);
        clock.Start(165);
        WaitUntilPending(clock);
        var pending = PendingField.GetValue(clock);
        Thread.Sleep(80);
        for (var i = 0; i < 20; i++) clock.Start(165);
        Thread.Sleep(40);
        Check(ReferenceEquals(pending, PendingField.GetValue(clock)), "The blocked dispatcher accumulated replacement frames.");
        Check(ticks == 0, "A frame bypassed its dispatcher.");
        clock.Stop();
        Pump(20);
        Check(ticks == 0, "A stopped blocked frame was delivered.");
    }

    private static void StopCancelsQueuedWork()
    {
        var ticks = 0;
        using var clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () => ticks++);
        clock.Start(165);
        WaitUntilPending(clock);
        clock.Stop();
        Check(PendingField.GetValue(clock) is null, "Stop retained pending work.");
        Pump(60);
        Check(ticks == 0, "Stop did not cancel queued work.");
        clock.Stop();
    }

    private static void RestartDeliversFreshWork()
    {
        var ticks = 0;
        using var clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () => ticks++);
        clock.Start(165);
        WaitUntilPending(clock);
        var previous = PendingField.GetValue(clock);
        clock.Stop();
        clock.Start(165);
        WaitUntilPending(clock);
        Check(!ReferenceEquals(previous, PendingField.GetValue(clock)), "Restart reused the canceled generation.");
        PumpUntil(() => ticks >= 3);
    }

    private static void NestedDispatcherDoesNotReenter()
    {
        var depth = 0;
        var ticks = 0;
        NativeFrameClock? clock = null;
        using (clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () =>
        {
            depth++;
            Check(depth == 1, "A nested dispatcher reentered the frame callback.");
            ticks++;
            if (ticks == 1)
            {
                Pump(45);
                Check(ticks == 1, "Frames accumulated while a callback was still running.");
            }
            depth--;
            clock!.Stop();
        }))
        {
            clock.Start(165);
            PumpUntil(() => ticks > 0);
            Check(ticks == 1, "Nested dispatch caused extra frame callbacks.");
        }
    }

    private static void RefreshRateCanChangeWhileRunning()
    {
        var ticks = 0;
        using var clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () => ticks++);
        clock.Start(30);
        PumpUntil(() => ticks >= 3);
        clock.SetRefreshRate(165);
        var before = ticks;
        PumpUntil(() => ticks >= before + 12);
        clock.Stop();
        clock.SetRefreshRate(60);
        var stopped = ticks;
        Pump(40);
        Check(ticks == stopped, "Changing rate started an inactive clock.");
    }

    private static void InvalidRatesRemainUsable()
    {
        var ticks = 0;
        using var clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () => ticks++);
        foreach (var rate in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -5.0, double.MaxValue })
        {
            clock.Start(rate);
            var before = ticks;
            PumpUntil(() => ticks > before);
            clock.SetRefreshRate(rate);
            clock.Stop();
        }
    }

    private static void StopAndRestartInsideTick()
    {
        var ticks = 0;
        NativeFrameClock? clock = null;
        using (clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () =>
        {
            ticks++;
            clock!.Stop();
            if (ticks == 1) clock.Start(165);
        }))
        {
            clock.Start(165);
            PumpUntil(() => ticks == 2);
            Pump(30);
            Check(ticks == 2, "Stop from the callback did not cancel the clock.");
        }
    }

    private static void DisposeCancelsQueuedWork()
    {
        var ticks = 0;
        var clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () => ticks++);
        clock.Start(165);
        WaitUntilPending(clock);
        clock.Dispose();
        Pump(40);
        Check(ticks == 0, "A disposed clock delivered queued work.");
        Check(!((Thread)WorkerField.GetValue(clock)!).IsAlive, "The disposed worker remained alive.");
        clock.Dispose();
        clock.Stop();
        ExpectDisposed(() => clock.Start(165));
        ExpectDisposed(() => clock.SetRefreshRate(60));
    }

    private static void DisposeInsideTickDoesNotDeadlock()
    {
        var disposed = false;
        NativeFrameClock? clock = null;
        using (clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () =>
        {
            var watch = Stopwatch.StartNew();
            clock!.Dispose();
            Check(watch.ElapsedMilliseconds < 500, "Disposal inside a callback blocked its dispatcher.");
            disposed = true;
        }))
        {
            clock.Start(165);
            PumpUntil(() => disposed);
            Check(!((Thread)WorkerField.GetValue(clock)!).IsAlive, "The callback disposal leaked its worker.");
        }
    }

    private static void RepeatedLifecyclesReleaseWorkers()
    {
        for (var i = 0; i < 20; i++)
        {
            var clock = new NativeFrameClock(Dispatcher.CurrentDispatcher, () => { });
            clock.Start(165);
            WaitUntilPending(clock);
            clock.Dispose();
            Check(!((Thread)WorkerField.GetValue(clock)!).IsAlive, "Repeated creation leaked a frame worker.");
        }
    }

    private static void WaitUntilPending(NativeFrameClock clock)
    {
        var watch = Stopwatch.StartNew();
        while (PendingField.GetValue(clock) is null && watch.ElapsedMilliseconds < 1000) Thread.Sleep(1);
        Check(PendingField.GetValue(clock) is not null, "The clock did not schedule a pending frame.");
    }

    private static void PumpUntil(Func<bool> finished)
    {
        var watch = Stopwatch.StartNew();
        while (!finished() && watch.ElapsedMilliseconds < 2000) Pump(10);
        Check(finished(), "Timed out waiting for a frame callback.");
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void ExpectDisposed(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("A disposed clock accepted new work.");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
