using System.Reflection;
using System.Windows.Threading;
using OsuCursorWin;

namespace OsuCursorWin;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            Run("Ordinary event batches retain every point and five-button transition", OrdinaryBatch);
            Run("A compressed move burst retains an out-and-back drag", OutAndBackDrag);
            Run("Diagonal distance determines the retained excursion", DiagonalExcursion);
            Run("Compressed movement stays ordered between button events", OrderedSegments);
            Run("Additional buttons preserve the first press origin", MultipleButtons);
            Run("A new gesture uses its own press origin", NewGestureOrigin);
            Run("Draining a batch retains the active gesture origin", DrainPreservesOrigin);
            Run("Extreme pointer coordinates do not overflow distances", ExtremeCoordinates);
            Run("Continuous movement remains bounded", BoundedMovement);
            Run("The dispatcher coalesces pending delivery without installing hooks", DispatcherBatching);
            Run("Input queued during delivery reaches the next batch", InputDuringDelivery);
            Run("Continuous input delivery yields to pending rendering", ContinuousInputYieldsToRendering);
            Run("Disposal cancels pending and future input delivery", DisposeCancelsDelivery);
            Run("Resetting input tracking discards old events and preserves the new drag origin", ResetTracking);
            Run("Resetting tracking keeps releases already received by the hook", ResetKeepsLatestButtonState);
            Run("Independent mouse hooks can start together and shut down cleanly", NativeHookLifecycle);
            Console.WriteLine($"Passed {_passed} input regression checks without injecting mouse input.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    internal static void Log(string message) => Console.Error.WriteLine(message);

    private static void OrdinaryBatch()
    {
        MouseInputEvent[] events =
        [
            Move(1, 2), new(0x0201, 3, 4, 0), Move(5, 6), new(0x0204, 7, 8, 0),
            new(0x0207, 9, 10, 0), new(0x020B, 11, 12, 1u << 16),
            new(0x020B, 13, 14, 2u << 16), new(0x0202, 15, 16, 0),
            new(0x0205, 17, 18, 0), new(0x0208, 19, 20, 0),
            new(0x020C, 21, 22, 1u << 16), new(0x020C, 23, 24, 2u << 16), Move(25, 26)
        ];
        var buffer = new MouseInputBuffer();
        foreach (var input in events) buffer.Add(input);
        Assert(buffer.Drain().SequenceEqual(events), "Events changed order, coordinates or button data.");
        Assert(buffer.Count == 0 && buffer.Drain().Length == 0, "Draining did not empty the batch.");
    }

    private static void OutAndBackDrag()
    {
        var buffer = new MouseInputBuffer();
        buffer.Add(new(0x0201, 0, 0, 0));
        buffer.Add(Move(100, 0));
        FillMoves(buffer, 0, 0);
        var events = buffer.Drain();
        var model = new CursorModel();
        foreach (var input in events)
        {
            model.MoveTo(input.X, input.Y, 30);
            if (input.Message == 0x0201) model.SetButton(CursorButtons.Left, true);
        }
        Assert(model.IsDragging && model.Position == new CursorPosition(0, 0), "Compaction erased the brief threshold crossing.");
        Assert(events.Length <= MouseInputBuffer.MaximumMovesPerSegment + 1, "A movement burst exceeded its bound.");
    }

    private static void DiagonalExcursion()
    {
        var buffer = new MouseInputBuffer();
        buffer.Add(new(0x0201, 0, 0, 0));
        buffer.Add(Move(100, 0));
        buffer.Add(Move(0, 100));
        buffer.Add(Move(90, 90));
        FillMoves(buffer, 0, 0);
        var events = buffer.Drain();
        Assert(events.Contains(Move(90, 90)), "The furthest diagonal point was lost to axis extrema.");
        Assert(events[^1] == Move(0, 0), "The latest pointer location was lost.");
    }

    private static void OrderedSegments()
    {
        var buffer = new MouseInputBuffer();
        var down = new MouseInputEvent(0x0201, 10, 20, 0);
        var up = new MouseInputEvent(0x0202, 30, 40, 0);
        buffer.Add(down);
        buffer.Add(Move(12, 20));
        buffer.Add(Move(70, 80));
        FillMoves(buffer, 11, 20);
        buffer.Add(up);
        FillMoves(buffer, 31, 40);
        var events = buffer.Drain();
        var farthestIndex = Array.IndexOf(events, Move(70, 80));
        var upIndex = Array.IndexOf(events, up);
        Assert(events[0] == down && farthestIndex > 0 && farthestIndex < upIndex, "Compaction reordered gesture points or button transitions.");
        Assert(events[1] == Move(12, 20) && events[upIndex - 1] == Move(11, 20), "A movement segment lost its first or last point.");
        Assert(events[^1] == Move(31, 40), "The final movement segment was lost.");
    }

    private static void MultipleButtons()
    {
        var buffer = new MouseInputBuffer();
        buffer.Add(new(0x0201, 0, 0, 0));
        buffer.Add(new(0x0204, 100, 100, 0));
        buffer.Add(new(0x0202, 100, 100, 0));
        buffer.Add(Move(0, 0));
        buffer.Add(Move(80, 80));
        FillMoves(buffer, 1, 1);
        Assert(buffer.Drain().Contains(Move(80, 80)), "An additional button replaced the original press origin.");
    }

    private static void NewGestureOrigin()
    {
        var buffer = new MouseInputBuffer();
        buffer.Add(new(0x0201, 0, 0, 0));
        buffer.Add(new(0x0202, 0, 0, 0));
        buffer.Add(new(0x0201, 100, 100, 0));
        buffer.Add(Move(90, 90));
        buffer.Add(Move(0, 0));
        FillMoves(buffer, 100, 100);
        Assert(buffer.Drain().Contains(Move(0, 0)), "The next gesture reused the previous press origin.");
    }

    private static void DrainPreservesOrigin()
    {
        var buffer = new MouseInputBuffer();
        buffer.Add(new(0x020B, 0, 0, 2u << 16));
        buffer.Drain();
        buffer.Add(Move(100, 100));
        buffer.Add(Move(-90, -90));
        buffer.Add(Move(130, 130));
        FillMoves(buffer, 0, 0);
        Assert(buffer.Drain().Contains(Move(130, 130)), "A new batch forgot the active press origin.");
    }

    private static void ExtremeCoordinates()
    {
        var buffer = new MouseInputBuffer();
        buffer.Add(new(0x0201, int.MinValue, int.MinValue, 0));
        buffer.Add(Move(int.MinValue, int.MinValue));
        buffer.Add(Move(int.MaxValue, int.MaxValue));
        FillMoves(buffer, 0, 0);
        Assert(buffer.Drain().Contains(Move(int.MaxValue, int.MaxValue)), "Distance arithmetic overflow discarded the furthest point.");
    }

    private static void BoundedMovement()
    {
        var buffer = new MouseInputBuffer();
        for (var i = 0; i < 100000; i++)
        {
            buffer.Add(Move(i, -i));
            Assert(buffer.Count <= MouseInputBuffer.MaximumMovesPerSegment, "Continuous movement exceeded the segment bound.");
        }
        Assert(buffer.Drain()[^1] == Move(99999, -99999), "Bounded movement lost its final point.");
    }

    private static void DispatcherBatching()
    {
        var batches = new List<MouseInputEvent[]>();
        using var source = new MouseInputSource(Dispatcher.CurrentDispatcher, inputs => batches.Add(inputs.ToArray()));
        for (var i = 0; i < 20; i++) Queue(source, Move(i, i));
        Assert(batches.Count == 0, "Input was delivered synchronously on the producer.");
        PumpDispatcher();
        Assert(batches.Count == 1 && batches[0].Length == 20, "A pending dispatch was duplicated or lost events.");
        Queue(source, Move(20, 20));
        PumpDispatcher();
        Assert(batches.Count == 2 && batches[1].Single() == Move(20, 20), "Input after draining did not schedule delivery.");
    }

    private static void InputDuringDelivery()
    {
        var batches = new List<MouseInputEvent[]>();
        MouseInputSource? source = null;
        using (source = new MouseInputSource(Dispatcher.CurrentDispatcher, inputs =>
        {
            batches.Add(inputs.ToArray());
            if (batches.Count == 1) Queue(source!, Move(2, 2));
        }))
        {
            Queue(source, Move(1, 1));
            PumpDispatcher();
            Assert(batches.Count == 2 && batches[0].Single() == Move(1, 1)
                && batches[1].Single() == Move(2, 2), "Input arriving during delivery was lost or merged into an active batch.");
        }
    }

    private static void DisposeCancelsDelivery()
    {
        var calls = 0;
        var source = new MouseInputSource(Dispatcher.CurrentDispatcher, _ => calls++);
        Queue(source, Move(1, 1));
        source.Dispose();
        source.Dispose();
        Queue(source, Move(2, 2));
        PumpDispatcher();
        Assert(calls == 0, "Disposed input was delivered to the UI.");
        Assert(!source.Start(), "A disposed source started a mouse hook.");
    }

    private static void ContinuousInputYieldsToRendering()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var order = new List<string>();
        var batches = 0;
        MouseInputSource? source = null;
        using (source = new MouseInputSource(dispatcher, inputs =>
        {
            var batch = ++batches;
            order.Add($"input{batch}");
            dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => order.Add($"render{batch}")));
            if (batch < 8)
            {
                for (var i = 0; i < 20; i++) Queue(source!, Move(batch, i));
            }
        }))
        {
            Queue(source, Move(0, 0));
            PumpDispatcher();
            Assert(batches == 8, "Continuous input generated duplicate or missing dispatcher batches.");
            var expected = Enumerable.Range(1, 8).SelectMany(batch => new[] { $"input{batch}", $"render{batch}" });
            Assert(order.SequenceEqual(expected), "A continuous input chain overtook pending rendering.");
        }
    }

    private static void ResetTracking()
    {
        MouseInputEvent[]? received = null;
        using var source = new MouseInputSource(Dispatcher.CurrentDispatcher, inputs => received = inputs.ToArray());
        Queue(source, new(0x0201, 0, 0, 0));
        Queue(source, Move(200, 200));
        var snapshot = source.ResetTracking();
        Assert(snapshot == new MouseInputSnapshot(1, 200, 200), "Reset did not capture the latest hook state and pointer location.");
        Queue(source, Move(200, 200));
        Queue(source, Move(50, 50));
        for (var i = 0; i < 1024; i++) Queue(source, Move(199, 199));
        PumpDispatcher();
        var events = received ?? throw new InvalidOperationException("Reset prevented input delivery.");
        Assert(events.All(input => input.Message == 0x0200), "Reset retained events from the old gesture.");
        Assert(events.Contains(Move(50, 50)), "Compression did not use the restored gesture origin.");
        Assert(events[^1] == Move(199, 199), "Reset tracking lost the latest pointer position.");
    }

    private static void ResetKeepsLatestButtonState()
    {
        var batches = new List<MouseInputEvent[]>();
        using var source = new MouseInputSource(Dispatcher.CurrentDispatcher, inputs => batches.Add(inputs.ToArray()));
        Queue(source, new(0x0201, 0, 0, 0));
        Queue(source, new(0x0204, 10, 10, 0));
        Queue(source, new(0x0202, 20, 20, 0));
        var held = source.ResetTracking();
        Assert(held == new MouseInputSnapshot(2, 20, 20), "Reset lost a release or incorrectly cleared another held button.");
        Queue(source, new(0x0205, 30, 30, 0));
        var released = source.ResetTracking();
        Assert(released == new MouseInputSnapshot(0, 30, 30), "Reset restored an obsolete pressed snapshot over a queued release.");
        PumpDispatcher();
        Assert(batches.Count == 0, "Reset replayed discarded button transitions.");
        Queue(source, Move(31, 32));
        PumpDispatcher();
        Assert(batches.Count == 1 && batches[0].Single() == Move(31, 32), "Input after reset was lost or combined with old transitions.");
        Assert(source.ResetTracking() == new MouseInputSnapshot(0, 31, 32), "Movement without buttons did not update the reset position.");
    }

    private static void NativeHookLifecycle()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var first = new MouseInputSource(dispatcher, _ => { });
        using var second = new MouseInputSource(dispatcher, _ => { });
        Assert(!first.IsRunning && !second.IsRunning, "An unstarted input source reported a running hook.");
        Assert(first.Start() && second.Start(), "Independent low-level mouse hooks could not start together.");
        Assert(first.IsRunning && second.IsRunning, "Started input sources did not report running hooks.");
        Assert(first.Start(), "Starting an active source again failed.");

        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);

        first.Dispose();
        Assert(!first.IsRunning && second.IsRunning, "Disposing one hook changed the other hook's lifecycle.");
        second.Dispose();
        Assert(!second.IsRunning, "A disposed source still reported a running hook.");
        foreach (var source in new[] { first, second })
        {
            var thread = (Thread)typeof(MouseInputSource).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
            Assert(thread.Join(TimeSpan.FromSeconds(1)), "Mouse hook thread did not stop after disposal.");
            var hook = (IntPtr)typeof(MouseInputSource).GetField("_hook", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
            Assert(hook == IntPtr.Zero, "Mouse hook was not uninstalled by its owning thread.");
        }
    }

    private static void Queue(MouseInputSource source, MouseInputEvent input) =>
        typeof(MouseInputSource).GetMethod("Queue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, [input]);

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void FillMoves(MouseInputBuffer buffer, int x, int y)
    {
        for (var i = 0; i < 1024; i++) buffer.Add(Move(x, y));
    }

    private static MouseInputEvent Move(int x, int y) => new(0x0200, x, y, 0);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
}
