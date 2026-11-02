using OsuCursorWin;

internal static class Program
{
    private static int _passed;

    private static int Main()
    {
        try
        {
            foreach (var frameRate in new[] { 30, 60, 144, 165, 240, 360 })
            {
                Run($"Hover, press, drag, release and rest settle at {frameRate} Hz", () => AnimationLifecycle(frameRate));
            }

            Run("Hover changes glow without rotating at any animation frame", HoverPreservesOrientation);
            Run("Animation stays close across display refresh rates", RefreshRateConsistency);
            Run("Invalid frame durations preserve the complete visible state", InvalidFrameDurations);
            Run("A long pause cannot destabilize the animation", LongPause);
            Run("Pressing a second button and releasing the first preserves the drag", MultipleButtons);
            Run("An atomic button snapshot preserves an ongoing drag", SnapshotTransition);
            Run("Unchanged button events report no transitions", RepeatedButtons);
            Run("Extra buttons report independent press and release changes", ExtraButtons);
            Run("A new press interrupts an elastic return", InterruptReturn);
            Run("Drag detection follows the origin and exact threshold", DragThreshold);
            Run("Drag movement is recorded without waiting for a frame", DragWithoutFrame);
            Run("All drag directions converge without angle wrapping errors", DragDirections);
            Run("Extreme screen coordinates retain their direction without overflow", ExtremeCoordinates);
            Run("Reset clears active inputs and animation while retaining the pointer position", Reset);
            Run("All eight resize directions face inward and stationary presses face forward", ResizeDirections);
            Run("A resize drag follows its original press vector after leaving the edge", ResizeDragFollowsOrigin);
            Run("Additional buttons and atomic snapshots preserve the resize drag origin", ResizeMultipleButtons);
            Run("Releasing a resize smoothly turns toward the latest detected edge", ResizeReleaseAtEdge);
            Run("Resize direction overrides hover and interrupts ordinary drag return", ResizeInterruptsReturn);
            Run("Changing resize detections never overrides an ongoing drag vector", ResizeDuringDrag);
            Run("Resize transitions across zero use the shortest rotation", ResizeShortestPath);
            Run("Reset clears resize detection, drag and return state", ResetResize);
            Run("Cursor dimensions preserve the default proportions", DefaultDimensions);
            Run("Cursor dimensions normalize invalid and out-of-range widths", NormalizeDimensions);
            Console.WriteLine($"Passed {_passed} animation regression checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void AnimationLifecycle(int frameRate)
    {
        var model = new CursorModel();
        Check(model.Advance(1.0 / frameRate), "An untouched cursor should already be at rest.");
        model.IsHovering = true;
        Settle(model, frameRate);
        Visual(model, 0.0, 1.0, 1.0);

        model.MoveTo(100, 200, 30);
        var down = model.SetButton(CursorButtons.Left, true);
        Equal(CursorButtons.Left, down.Pressed);
        Equal(CursorButtons.None, down.Released);
        Check(model.IsPressed && !model.IsDragging, "A stationary press should not start a drag.");
        Settle(model, frameRate);
        Visual(model, 0.0, 0.9, 1.0);

        model.MoveTo(100, 231, 30);
        Check(model.IsDragging, "A press moving past the threshold should start a drag.");
        Settle(model, frameRate);
        Visual(model, 24.3, 0.9, 1.0);

        var up = model.SetButton(CursorButtons.Left, false);
        Equal(CursorButtons.None, up.Pressed);
        Equal(CursorButtons.Left, up.Released);
        Check(!model.IsPressed && !model.IsDragging && model.IsReturning, "Releasing a rotated drag should start the return.");
        Settle(model, frameRate);
        Check(!model.IsReturning, "A completed return should stop requesting animation.");
        Visual(model, 0.0, 1.0, 1.0);

        model.IsHovering = false;
        Settle(model, frameRate);
        Visual(model, 0.0, 1.0, 0.0);
        Check(model.Advance(1.0 / frameRate), "Rest should remain settled on another frame.");
    }

    private static void RefreshRateConsistency()
    {
        var snapshots = new List<(double Angle, double Scale, double Opacity)>();
        foreach (var frameRate in new[] { 30, 60, 144, 165, 240, 360 })
        {
            var model = new CursorModel { IsHovering = true };
            for (var i = 0; i < frameRate / 2; i++)
            {
                model.Advance(1.0 / frameRate);
                ValidVisual(model);
            }

            snapshots.Add((model.Angle, model.Scale, model.Opacity));
        }

        foreach (var state in snapshots)
        {
            Near(snapshots[0].Angle, state.Angle, 0.5);
            Near(snapshots[0].Scale, state.Scale, 0.005);
            Near(snapshots[0].Opacity, state.Opacity, 0.02);
        }
    }

    private static void HoverPreservesOrientation()
    {
        foreach (var frameRate in new[] { 30, 60, 165, 360 })
        {
            var model = new CursorModel();
            foreach (var hovering in new[] { true, false, true })
            {
                model.IsHovering = hovering;
                for (var frame = 0; frame < frameRate; frame++)
                {
                    model.Advance(1.0 / frameRate);
                    Near(0.0, model.Angle);
                    Near(1.0, model.Scale);
                    ValidVisual(model);
                }
                Settle(model, frameRate);
                Visual(model, 0.0, 1.0, hovering ? 1.0 : 0.0);
            }
        }
    }

    private static void InvalidFrameDurations()
    {
        var model = RotatedDrag();
        model.SetButtons(CursorButtons.None);
        var expected = Snapshot(model);
        foreach (var elapsed in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Check(!model.Advance(elapsed), "An invalid duration should not advance animation.");
            Equal(expected, Snapshot(model));
        }

        Settle(model);
        Visual(model, 0.0, 1.0, 0.0);
    }

    private static void LongPause()
    {
        var model = RotatedDrag();
        model.SetButtons(CursorButtons.None);
        model.Advance(double.MaxValue);
        ValidVisual(model);
        Check(Math.Abs(model.Angle) < 360.0, "A long pause should not produce unbounded rotation.");
        model.Advance(double.Epsilon);
        ValidVisual(model);
        Settle(model);
        Visual(model, 0.0, 1.0, 0.0);
    }

    private static void MultipleButtons()
    {
        var model = RotatedDrag();
        var extra = model.SetButton(CursorButtons.Right, true);
        Equal(CursorButtons.Right, extra.Pressed);
        Check(model.IsDragging && !model.IsReturning, "Additional buttons should preserve the original drag.");
        var release = model.SetButton(CursorButtons.Left, false);
        Equal(CursorButtons.Left, release.Released);
        Equal(CursorButtons.Right, model.Buttons);
        Check(model.IsDragging && model.IsPressed && !model.IsReturning, "Releasing one held button should preserve the drag.");
        model.MoveTo(0, 50, 30);
        Settle(model);
        Visual(model, 24.3, 0.9, 1.0);
        model.SetButton(CursorButtons.Right, false);
        Check(model.IsReturning, "The last release should return the rotated cursor.");
        Settle(model);
        Visual(model, 0.0, 1.0, 0.0);
    }

    private static void SnapshotTransition()
    {
        var model = RotatedDrag();
        var changes = model.SetButtons(CursorButtons.Right | CursorButtons.X1);
        Equal(CursorButtons.Right | CursorButtons.X1, changes.Pressed);
        Equal(CursorButtons.Left, changes.Released);
        Check(changes.Any && model.IsDragging && !model.IsReturning, "A snapshot must not introduce an artificial full release.");
        model.MoveTo(0, 50, 30);
        Settle(model);
        Visual(model, 24.3, 0.9, 1.0);
        changes = model.SetButtons(CursorButtons.None);
        Equal(CursorButtons.None, changes.Pressed);
        Equal(CursorButtons.Right | CursorButtons.X1, changes.Released);
        Check(!model.IsPressed && model.IsReturning, "An empty snapshot should release all held buttons.");
    }

    private static void RepeatedButtons()
    {
        var model = RotatedDrag();
        var expected = Snapshot(model);
        Check(!model.SetButton(CursorButtons.Left, true).Any, "A repeated press must not emit another sound transition.");
        Check(!model.SetButton(CursorButtons.Right, false).Any, "An unmatched release must not emit a transition.");
        Check(!model.SetButtons(CursorButtons.Left).Any, "An unchanged snapshot must not emit a transition.");
        Equal(expected, Snapshot(model));
        model.MoveTo(0, 50, 30);
        Settle(model);
        Near(24.3, model.Angle);
    }

    private static void ExtraButtons()
    {
        var model = new CursorModel();
        var changes = model.SetButtons(CursorButtons.Middle | CursorButtons.X1 | CursorButtons.X2);
        Equal(CursorButtons.Middle | CursorButtons.X1 | CursorButtons.X2, changes.Pressed);
        Equal(CursorButtons.None, changes.Released);
        changes = model.SetButton(CursorButtons.X1, false);
        Equal(CursorButtons.X1, changes.Released);
        Equal(CursorButtons.Middle | CursorButtons.X2, model.Buttons);
        changes = model.SetButtons(CursorButtons.None);
        Equal(CursorButtons.Middle | CursorButtons.X2, changes.Released);
        Equal(false, model.IsPressed);
    }

    private static void InterruptReturn()
    {
        var model = RotatedDrag();
        model.SetButtons(CursorButtons.None);
        model.Advance(0.05);
        Check(model.IsReturning, "The return should still be active after a short interval.");
        model.MoveTo(200, 100, 30);
        model.SetButton(CursorButtons.Right, true);
        Check(model.IsPressed && !model.IsDragging && !model.IsReturning, "A new press should cancel the old return.");
        Settle(model);
        Visual(model, 0.0, 0.9, 1.0);
        model.MoveTo(200, 140, 30);
        Settle(model);
        Near(24.3, model.Angle);
    }

    private static void DragThreshold()
    {
        var model = new CursorModel();
        model.MoveTo(500, -200, 30);
        model.SetButton(CursorButtons.Left, true);
        model.MoveTo(518, -176, 30);
        Check(!model.IsDragging, "Movement exactly at the threshold should remain a press.");
        model.MoveTo(519, -176, 30);
        Check(model.IsDragging, "Movement beyond the threshold should begin dragging.");
        model.MoveTo(500, -200, 30);
        Check(model.IsDragging, "A drag remains active after returning to its origin.");
        model.SetButtons(CursorButtons.None);
        model.MoveTo(900, 900, 30);
        Check(!model.IsDragging, "Moving without a held button should not create a drag.");
    }

    private static void DragWithoutFrame()
    {
        var model = new CursorModel();
        model.MoveTo(0, 0, 30);
        model.SetButton(CursorButtons.Left, true);
        model.MoveTo(80, 0, 30);
        Check(model.IsDragging, "Input should detect a drag before the first animation frame.");
        model.MoveTo(1, 0, 30);
        Check(model.IsDragging, "A quick out-and-back movement must retain the drag transition.");
        model.SetButton(CursorButtons.Left, false);
        Check(!model.IsPressed && !model.IsDragging, "A quick release should clear the press even without a frame.");
        Settle(model);
        Visual(model, 0.0, 1.0, 0.0);
    }

    private static void DragDirections()
    {
        var model = new CursorModel();
        model.SetButton(CursorButtons.Left, true);
        foreach (var direction in new[] { (100, 0, -65.7), (0, -100, -155.7), (-100, 0, 114.3), (0, 100, 24.3) })
        {
            model.MoveTo(direction.Item1, direction.Item2, 30);
            Settle(model);
            Near(direction.Item3, Normalize(model.Angle));
        }
    }

    private static void ExtremeCoordinates()
    {
        var model = new CursorModel();
        model.MoveTo(int.MinValue, int.MinValue, 30);
        model.SetButton(CursorButtons.Left, true);
        model.MoveTo(int.MaxValue, int.MaxValue, 30);
        Check(model.IsDragging, "The largest coordinate delta should exceed the drag threshold.");
        Settle(model);
        Near(-20.7, model.Angle);
        model.Reset();
        model.SetButton(CursorButtons.Right, true);
        model.MoveTo(int.MinValue, int.MinValue, 30);
        Settle(model);
        Near(159.3, model.Angle);
    }

    private static void Reset()
    {
        var model = RotatedDrag();
        model.IsHovering = true;
        model.SetButtons(CursorButtons.None);
        model.Advance(0.03);
        var position = model.Position;
        model.Reset();
        Equal(position, model.Position);
        Equal(CursorButtons.None, model.Buttons);
        Check(!model.IsPressed && !model.IsDragging && !model.IsReturning && !model.IsHovering, "Reset must clear every active interaction.");
        Visual(model, 0.0, 1.0, 0.0);
        Check(model.Advance(1.0 / 60), "Reset must clear hidden spring velocity as well.");
        Visual(model, 0.0, 1.0, 0.0);
        model.SetButton(CursorButtons.Middle, true);
        model.MoveTo(position.X, position.Y + 50, 30);
        Settle(model);
        Near(24.3, model.Angle);
    }

    private static void DefaultDimensions()
    {
        var dimensions = new CursorDimensions(30);
        Near(30, dimensions.Width);
        Near(42.5, dimensions.Height);
        Near(160, dimensions.WindowSize);
        Near(64, dimensions.Margin);
        dimensions = new CursorDimensions(60);
        Near(85, dimensions.Height);
        Near(320, dimensions.WindowSize);
        Near(128, dimensions.Margin);
    }

    private static void ResizeDirections()
    {
        var directions = new[]
        {
            (ResizeDirection.Top, 204.3),
            (ResizeDirection.TopRight, 249.3),
            (ResizeDirection.Right, 294.3),
            (ResizeDirection.BottomRight, 339.3),
            (ResizeDirection.Bottom, 24.3),
            (ResizeDirection.BottomLeft, 69.3),
            (ResizeDirection.Left, 114.3),
            (ResizeDirection.TopLeft, 159.3)
        };
        foreach (var frameRate in new[] { 30, 60, 144, 165, 240, 360 })
        {
            foreach (var (direction, angle) in directions)
            {
                var model = new CursorModel { ResizeDirection = direction };
                Settle(model, frameRate);
                Near(0.0, Normalize(angle - model.Angle));
                Near(1.0, model.Scale);
                Check(model.Advance(1.0 / frameRate), "A stationary resize should remain settled.");
                model.SetButton(CursorButtons.Left, true);
                Settle(model, frameRate);
                Near(0.0, Normalize(model.Angle));
                Near(0.9, model.Scale);
                Near(1.0, model.Opacity);
                Check(!model.IsDragging, "A stationary resize press must not start dragging.");
                model.SetButtons(CursorButtons.None);
                Check(!model.IsReturning, "A stationary release at an edge must not start elastic return.");
                Settle(model, frameRate);
                Near(0.0, Normalize(angle - model.Angle));
                Near(1.0, model.Scale);
            }
        }
    }

    private static void ResizeDragFollowsOrigin()
    {
        var model = new CursorModel { ResizeDirection = ResizeDirection.Right };
        model.MoveTo(100, 100, 30);
        model.SetButton(CursorButtons.Left, true);
        model.MoveTo(130, 100, 30);
        Check(!model.IsDragging, "An edge press exactly at the threshold must remain a press.");
        Settle(model);
        Visual(model, 0.0, 0.9, 1.0);
        model.MoveTo(131, 100, 30);
        Check(model.IsDragging, "An edge press beyond the threshold must start dragging.");
        Settle(model);
        Visual(model, -65.7, 0.9, 1.0);
        model.ResizeDirection = ResizeDirection.None;
        model.MoveTo(0, 200, 30);
        Settle(model);
        Visual(model, 69.3, 0.9, 1.0);
        Check(model.IsPressed && model.IsDragging && !model.IsReturning, "Leaving an edge must preserve the ongoing drag.");
        model.SetButton(CursorButtons.Left, false);
        Check(model.IsReturning, "Releasing away from an edge must retain ordinary elastic return.");
        Settle(model);
        Visual(model, 0.0, 1.0, 0.0);
    }

    private static void ResizeMultipleButtons()
    {
        var model = new CursorModel { ResizeDirection = ResizeDirection.BottomLeft };
        model.MoveTo(100, 100, 30);
        model.SetButton(CursorButtons.Left, true);
        model.MoveTo(150, 100, 30);
        Settle(model);
        Visual(model, -65.7, 0.9, 1.0);
        model.ResizeDirection = ResizeDirection.Top;
        model.SetButton(CursorButtons.Right, true);
        model.SetButton(CursorButtons.Left, false);
        model.MoveTo(100, 150, 30);
        Settle(model);
        Visual(model, 24.3, 0.9, 1.0);
        Check(model.IsPressed && model.IsDragging, "Releasing one button must preserve the resize drag.");
        var changes = model.SetButtons(CursorButtons.Middle | CursorButtons.X1);
        Equal(CursorButtons.Right, changes.Released);
        Equal(CursorButtons.Middle | CursorButtons.X1, changes.Pressed);
        model.ResizeDirection = ResizeDirection.None;
        model.MoveTo(0, 100, 30);
        Settle(model);
        Visual(model, 114.3, 0.9, 1.0);
        model.SetButtons(CursorButtons.None);
        Check(model.IsReturning, "The last release away from an edge must start elastic return.");
        Settle(model);
        Visual(model, 0.0, 1.0, 0.0);
        model.SetButton(CursorButtons.Left, true);
        model.MoveTo(0, 150, 30);
        Settle(model);
        Visual(model, 24.3, 0.9, 1.0);
        model.SetButtons(CursorButtons.None);
        Check(model.IsReturning, "An ordinary drag after resize must retain elastic return.");
    }

    private static void ResizeReleaseAtEdge()
    {
        var model = new CursorModel { ResizeDirection = ResizeDirection.TopRight };
        model.SetButton(CursorButtons.Left, true);
        model.MoveTo(200, 200, 30);
        Settle(model);
        Visual(model, -20.7, 0.9, 1.0);
        var releaseAngle = model.Angle;
        model.SetButtons(CursorButtons.None);
        Check(!model.IsReturning, "A resize release at the edge must turn toward it without elastic return.");
        Near(releaseAngle, model.Angle);
        model.Advance(1.0 / 120.0);
        Check(model.Angle < releaseAngle && releaseAngle - model.Angle < 5.0,
            "An edge release must begin a smooth rotation along the shortest path.");
        Settle(model);
        Near(0.0, Normalize(249.3 - model.Angle));
        Near(1.0, model.Scale);
        Near(0.0, model.Opacity);
        model.SetButton(CursorButtons.Left, true);
        model.ResizeDirection = ResizeDirection.BottomRight;
        Settle(model);
        Visual(model, 0.0, 0.9, 1.0);
        model.MoveTo(200, 250, 30);
        Settle(model);
        Visual(model, 24.3, 0.9, 1.0);
        model.SetButtons(CursorButtons.None);
        Check(!model.IsReturning, "Releasing at the latest edge must suppress elastic return.");
        Settle(model);
        Near(0.0, Normalize(339.3 - model.Angle));
        Near(1.0, model.Scale);
        Near(0.0, model.Opacity);
        model.ResizeDirection = ResizeDirection.None;
        Settle(model);
        Visual(model, 0.0, 1.0, 0.0);
    }

    private static void ResizeInterruptsReturn()
    {
        var model = RotatedDrag();
        model.SetButtons(CursorButtons.None);
        model.Advance(0.03);
        Check(model.IsReturning, "An ordinary drag should have an elastic return to interrupt.");
        model.IsHovering = true;
        model.ResizeDirection = ResizeDirection.Left;
        Check(!model.IsReturning, "Entering resize must interrupt elastic return immediately.");
        Settle(model);
        Near(0.0, Normalize(114.3 - model.Angle));
        Near(1.0, model.Scale);
        Near(1.0, model.Opacity);
        model.ResizeDirection = ResizeDirection.None;
        Settle(model);
        Visual(model, 0.0, 1.0, 1.0);
    }

    private static void ResizeDuringDrag()
    {
        var model = RotatedDrag();
        var motions = new[] { (0, 100, 24.3), (-100, 0, 114.3), (0, -100, -155.7), (100, 0, -65.7) };
        var index = 0;
        foreach (var direction in Enum.GetValues<ResizeDirection>())
        {
            model.ResizeDirection = direction;
            Check(model.IsDragging, "A resize detection must not clear the active drag.");
            var motion = motions[index++ % motions.Length];
            model.MoveTo(motion.Item1, motion.Item2, 30);
            Settle(model);
            Visual(model, motion.Item3, 0.9, 1.0);
            model.ResizeDirection = direction;
            Check(model.IsDragging, "An unchanged resize sample must not clear the active drag.");
        }
        model.ResizeDirection = ResizeDirection.None;
        model.SetButtons(CursorButtons.None);
        Check(model.IsReturning, "Releasing after the edge disappears must retain elastic return.");
        Settle(model);
        Visual(model, 0.0, 1.0, 0.0);
    }

    private static void ResizeShortestPath()
    {
        var model = new CursorModel { ResizeDirection = ResizeDirection.BottomRight };
        Settle(model);
        foreach (var (direction, expectedTravel) in new[] { (ResizeDirection.Bottom, 45.0), (ResizeDirection.BottomRight, -45.0) })
        {
            model.ResizeDirection = direction;
            var travel = 0.0;
            var previous = model.Angle;
            for (var frame = 0; frame < 1200; frame++)
            {
                var settled = model.Advance(1.0 / 120.0);
                var step = Normalize(model.Angle - previous);
                Check(Math.Abs(step) < 5.0, "A resize transition must not jump through a long rotation.");
                if (frame == 0)
                {
                    Check(Math.Sign(step) == Math.Sign(expectedTravel), "Crossing zero must start in the shortest direction.");
                }
                travel += step;
                previous = model.Angle;
                if (settled)
                {
                    break;
                }
            }
            Near(expectedTravel, travel);
            Check(model.Advance(1.0 / 120.0), "A completed shortest-path resize must stop animating.");
        }
    }

    private static void ResetResize()
    {
        var model = new CursorModel { ResizeDirection = ResizeDirection.BottomRight, IsHovering = true };
        model.MoveTo(20, 30, 30);
        model.SetButtons(CursorButtons.Left | CursorButtons.Right);
        model.ResizeDirection = ResizeDirection.None;
        model.MoveTo(400, 500, 30);
        model.Advance(0.05);
        model.Reset();
        Equal(ResizeDirection.None, model.ResizeDirection);
        Equal(CursorButtons.None, model.Buttons);
        Check(!model.IsPressed && !model.IsDragging && !model.IsReturning && !model.IsHovering, "Reset must clear every resize interaction.");
        Visual(model, 0.0, 1.0, 0.0);
        Check(model.Advance(1.0 / 120.0), "Reset must clear resize angular velocity and drag history.");
        model.SetButton(CursorButtons.Left, true);
        model.MoveTo(400, 550, 30);
        Settle(model);
        Visual(model, 24.3, 0.9, 1.0);
        model.SetButtons(CursorButtons.None);
        Check(model.IsReturning, "Reset resize must not affect the next ordinary drag return.");
    }

    private static void NormalizeDimensions()
    {
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var dimensions = new CursorDimensions(value);
            Near(30, dimensions.Width);
            Near(30, CursorDimensions.NormalizeWidth(value));
            Check(double.IsFinite(dimensions.Height + dimensions.WindowSize + dimensions.Margin), "All normalized dimensions must be finite.");
        }

        foreach (var value in new[] { double.MinValue, -1.0, 0.0, 15.0 })
        {
            Near(16, new CursorDimensions(value).Width);
        }
        foreach (var value in new[] { 65.0, double.MaxValue })
        {
            Near(64, new CursorDimensions(value).Width);
        }
        Near(41.25, new CursorDimensions(41.25).Width);
    }

    private static CursorModel RotatedDrag()
    {
        var model = new CursorModel();
        model.MoveTo(0, 0, 30);
        model.SetButton(CursorButtons.Left, true);
        model.MoveTo(50, 0, 30);
        Settle(model);
        Visual(model, -65.7, 0.9, 1.0);
        return model;
    }

    private static void Settle(CursorModel model, int frameRate = 120)
    {
        for (var i = 0; i < frameRate * 10; i++)
        {
            var settled = model.Advance(1.0 / frameRate);
            ValidVisual(model);
            if (settled)
            {
                return;
            }
        }
        throw new InvalidOperationException($"Animation failed to settle within ten seconds at {frameRate} Hz.");
    }

    private static void ValidVisual(CursorModel model)
    {
        Check(double.IsFinite(model.Angle), "Angle must remain finite.");
        Check(double.IsFinite(model.Scale) && model.Scale >= 0.8 && model.Scale <= 1.1, "Scale must remain within its visual bounds.");
        Check(double.IsFinite(model.Opacity) && model.Opacity >= 0.0 && model.Opacity <= 1.0, "Opacity must remain within its visual bounds.");
    }

    private static void Visual(CursorModel model, double angle, double scale, double opacity)
    {
        Near(angle, model.Angle);
        Near(scale, model.Scale);
        Near(opacity, model.Opacity);
    }

    private static object Snapshot(CursorModel model) =>
        (model.Position, model.Buttons, model.IsDragging, model.IsReturning, model.IsHovering, model.ResizeDirection, model.Angle, model.Scale, model.Opacity);

    private static double Normalize(double angle) => angle > 180.0 ? angle - 360.0 : angle < -180.0 ? angle + 360.0 : angle;

    private static void Near(double expected, double actual, double tolerance = 0.000001) =>
        Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"Expected {expected}; received {actual}; tolerance {tolerance}.");

    private static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; received {actual}.");

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
}
