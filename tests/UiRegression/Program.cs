using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OsuCursorWin;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OsuCursorUiTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = AppSettings.Load(settingsPath);
        var app = new Application();
        NativeMethods.CURSORINFO? cursorInfo = CursorInfo(1);
        var resizeDirection = ResizeDirection.None;
        var resizeQueries = 0;
        Action? inspectResizeQuery = null;
        var window = new MainWindow(smoke: true, settings: settings, cursorInfoProvider: () => cursorInfo,
            resizeDirectionProvider: _ =>
            {
                resizeQueries++;
                inspectResizeQuery?.Invoke();
                return resizeDirection;
            }, nativeOverlay: false);
        Exception? failure = null;
        var checksStarted = false;
        window.ContentRendered += (_, _) =>
        {
            if (checksStarted)
            {
                return;
            }

            checksStarted = true;
            try
            {
                window.StopRendering();
                Check(!window.IsRendering, "Rendering can stop after becoming idle");
                window.RequestRender();
                window.RequestRender();
                Check(window.IsRendering, "Rendering can resume without duplicate subscriptions");
                window.StopRendering();

                var visual = (CursorVisual)window.Content;
                var rotation = (Grid)visual.Children[0];
                var scaling = (Grid)rotation.Children[0];
                var glow = (Image)scaling.Children[1];
                window.Model.IsHovering = true;
                Settle(window.Model);
                visual.Render(window.Model);
                Check(Math.Abs(((RotateTransform)rotation.RenderTransform).Angle) < 0.001,
                    "Clickable hover keeps the original cursor orientation");
                Check(glow.Opacity == 1.0, "The view renders hover glow");

                window.Model.SetButton(CursorButtons.Left, true);
                Settle(window.Model);
                visual.Render(window.Model);
                Check(((ScaleTransform)scaling.RenderTransform).ScaleX == 0.9,
                    "The view renders the model press scale");
                window.Model.Reset();
                visual.Render(window.Model);
                Check(glow.Opacity == 0.0 && ((ScaleTransform)scaling.RenderTransform).ScaleX == 1.0,
                    "Reset state renders without stale glow or scale");

                window.ApplyCursorDimensions(64.0);
                Check(Math.Abs(window.Width - 64.0 * 160.0 / 30.0) < 0.001
                    && Math.Abs(Canvas.GetLeft(rotation) - 64.0 * 64.0 / 30.0) < 0.001,
                    "Resizing updates window bounds and hotspot margin");
                Check(rotation.Width == 64.0 && scaling.Width == 64.0,
                    "Resizing updates both transform layers");
                Check(!File.Exists(settingsPath), "Size changes wait for the debounce timer");
                VerifyInputBatches(window, settings, () => resizeQueries, info => cursorInfo = info);
                var boundsBeforePolling = WindowBounds(new WindowInteropHelper(window).Handle);
                NativeMethods.RECT? boundsDuringResizeQuery = null;
                inspectResizeQuery = () => boundsDuringResizeQuery = WindowBounds(new WindowInteropHelper(window).Handle);
                cursorInfo = new NativeMethods.CURSORINFO
                {
                    flags = 1,
                    ptScreenPos = new NativeMethods.POINT { X = 520, Y = 360 }
                };
                window.UpdateMouseState();
                inspectResizeQuery = null;
                var sampledDpi = VisualTreeHelper.GetDpi(window);
                var sampledDimensions = new CursorDimensions(settings.CursorWidth);
                Check(boundsDuringResizeQuery is { } sampledBounds
                    && sampledBounds.Left == boundsBeforePolling.Left && sampledBounds.Top == boundsBeforePolling.Top
                    && window.Model.Position == new CursorPosition(520, 360),
                    "Polling updates input state without moving the native window during the resize query");
                WaitForRender(window);
                var polledBounds = WindowBounds(new WindowInteropHelper(window).Handle);
                Check(polledBounds.Left == 520 - (int)Math.Round(sampledDimensions.Margin * sampledDpi.DpiScaleX)
                    && polledBounds.Top == 360 - (int)Math.Round(sampledDimensions.Margin * sampledDpi.DpiScaleY),
                    "The next render submits the latest polled pointer position");
                cursorInfo = CursorInfo(1);
                resizeDirection = ResizeDirection.Right;
                settings.HoverSoundAsResizePrompt = false;
                window.UpdateMouseState();
                Check(window.Model.ResizeDirection == ResizeDirection.Right,
                    "Resize direction reaches the model independently of the sound option");
                Settle(window.Model);
                visual.Render(window.Model);
                Check(Math.Abs(((RotateTransform)rotation.RenderTransform).Angle - 294.3) < 0.001,
                    "The WPF cursor renders the reversed resize edge orientation");
                resizeDirection = ResizeDirection.None;
                window.UpdateMouseState();
                Settle(window.Model);
                visual.Render(window.Model);
                Check(((RotateTransform)rotation.RenderTransform).Angle == 0.0,
                    "Leaving a resize edge restores the WPF cursor orientation");
                resizeDirection = ResizeDirection.Right;
                window.UpdateMouseState();
                window.Model.MoveTo(700, 300, 30);
                window.HandleButtonTransition(CursorButtons.Left, true);
                resizeDirection = ResizeDirection.None;
                cursorInfo = new NativeMethods.CURSORINFO
                {
                    flags = 1,
                    ptScreenPos = new NativeMethods.POINT { X = 700, Y = 300 }
                };
                window.UpdateMouseState();
                window.Model.MoveTo(800, 300, 30);
                Settle(window.Model);
                Check(window.Model.IsDragging && Math.Abs(window.Model.Angle + 65.7) < 0.001,
                    "A press after moving away from an edge rotates from its current press point");
                window.HandleButtonTransition(CursorButtons.Left, false);
                window.Model.Reset();
                resizeDirection = ResizeDirection.Right;
                cursorInfo = CursorInfo(1);
                window.UpdateMouseState();
                window.HandleButtonTransition(CursorButtons.Left, true);
                cursorInfo = new NativeMethods.CURSORINFO
                {
                    flags = 1,
                    ptScreenPos = new NativeMethods.POINT { X = 500, Y = 300 }
                };
                window.UpdateMouseState();
                Settle(window.Model);
                visual.Render(window.Model);
                Check(window.Model.IsDragging && Math.Abs(((RotateTransform)rotation.RenderTransform).Angle + 65.7) < 0.001,
                    "Holding a resize rotates the WPF cursor from the initial press point");
                resizeDirection = ResizeDirection.Top;
                cursorInfo = new NativeMethods.CURSORINFO
                {
                    flags = 1,
                    ptScreenPos = new NativeMethods.POINT { X = 400, Y = 200 }
                };
                window.UpdateMouseState();
                Settle(window.Model);
                visual.Render(window.Model);
                Check(window.Model.IsDragging && Math.Abs(((RotateTransform)rotation.RenderTransform).Angle + 155.7) < 0.001,
                    "Changing the sampled edge preserves rotation around the original press point");
                window.HandleButtonTransition(CursorButtons.Left, false);
                Settle(window.Model);
                visual.Render(window.Model);
                Check(!window.Model.IsReturning && Math.Abs(((RotateTransform)rotation.RenderTransform).Angle - 204.3) < 0.001,
                    "Releasing a resize restores the latest sampled edge orientation");
                window.Model.Reset();
                resizeDirection = ResizeDirection.None;
                window.RequestRender();
                VerifySystemCursorVisibility(window, settings, flags => cursorInfo = flags is { } value ? CursorInfo(value) : null);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window.Close();
            }
        };

        try
        {
            app.Run(window);
            if (failure is not null)
            {
                throw failure;
            }
            Check(_passed == 49, "WPF content rendered and all checks executed");
            Check(AppSettings.Load(settingsPath).CursorWidth == 64.0, "Closing flushes the pending settings change");
            Check(!window.IsRendering, "Closing unsubscribes from rendering");
            window.RequestRender();
            Check(!window.IsRendering, "A closed window cannot resume rendering");
            window.ProcessMouseInput(new[] { Input(NativeMethods.WmLButtonDown, 420, 340), Input(NativeMethods.WmMouseMove, 500, 340) });
            Check(!window.IsVisible && !window.IsRendering && !window.Model.IsPressed,
                "Queued input after closing cannot recreate or reactivate the overlay");
            Console.WriteLine($"Passed {_passed} WPF regression checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void VerifySystemCursorVisibility(MainWindow window, AppSettings settings, Action<int?> setFlags)
    {
        Check(settings.HideWhenCursorHidden, "Following system cursor visibility is enabled by default");
        CheckVisibility(window, true, "A showing system cursor keeps the overlay visible", rendering: true);

        window.Model.MoveTo(100, 100, 30);
        window.Model.SetButton(CursorButtons.Left, true);
        window.Model.MoveTo(250, 100, 30);
        window.Model.Advance(0.1);
        Check(window.Model.IsDragging && window.Model.IsPressed, "The visibility test starts with an active drag");

        setFlags(0);
        window.RefreshSystemCursorVisibility();
        CheckVisibility(window, false, "Flags 0 hide the overlay and stop rendering");
        window.ProcessMouseInput(new[] { Input(NativeMethods.WmLButtonDown, 500, 300), Input(NativeMethods.WmMouseMove, 700, 300) });
        Check(!window.IsVisible && !NativeMethods.IsWindowVisible(new WindowInteropHelper(window).Handle)
            && !window.IsRendering && !window.Model.IsPressed,
            "A mouse input batch cannot reveal or press a system-hidden overlay");
        setFlags(null);
        window.RefreshSystemCursorVisibility();
        CheckVisibility(window, false, "A failed cursor query preserves the hidden state");
        window.RequestRender();
        window.ApplyCursorDimensions(32.0);
        window.RequestRender();
        CheckVisibility(window, false, "Render requests and resizing cannot reveal a hidden overlay");

        setFlags(1);
        window.RefreshSystemCursorVisibility();
        CheckVisibility(window, true, "Flags 1 restore the overlay and rendering", rendering: true);
        Check(!window.Model.IsPressed && !window.Model.IsDragging && !window.Model.IsReturning
            && !window.Model.IsHovering && window.Model.Angle == 0.0
            && window.Model.Scale == 1.0 && window.Model.Opacity == 0.0,
            "Restoring visibility resets stale drag and animation state");

        setFlags(0);
        window.RefreshSystemCursorVisibility();
        setFlags(1);
        window.ProcessMouseInput(new[] { Input(NativeMethods.WmLButtonDown, 20, 30), Input(NativeMethods.WmMouseMove, 200, 300) });
        Check(window.IsVisible && !window.Model.IsPressed && !window.Model.IsDragging
            && window.Model.Position == new CursorPosition(400, 300),
            "Visibility recovery discards stale queued input and uses the current system state");

        foreach (var flags in new[] { 2, 3 })
        {
            setFlags(flags);
            window.RefreshSystemCursorVisibility();
            CheckVisibility(window, false, $"Suppressed cursor flags {flags} hide the overlay");
            setFlags(1);
            window.RefreshSystemCursorVisibility();
            CheckVisibility(window, true, $"Showing after flags {flags} restores the overlay", rendering: true);
        }

        window.StopRendering();
        setFlags(null);
        window.RefreshSystemCursorVisibility();
        CheckVisibility(window, true, "A failed cursor query preserves visible state and stopped rendering", rendering: false);

        setFlags(0);
        window.RefreshSystemCursorVisibility();
        window.SetCursorEnabled(false);
        setFlags(1);
        window.RefreshSystemCursorVisibility();
        window.RequestRender();
        CheckVisibility(window, false, "A system cursor recovery cannot override manual disabling");
        window.ProcessMouseInput(new[] { Input(NativeMethods.WmRButtonDown, 500, 300), Input(NativeMethods.WmMouseMove, 700, 300) });
        Check(!window.IsVisible && !NativeMethods.IsWindowVisible(new WindowInteropHelper(window).Handle)
            && !window.IsRendering && !window.Model.IsPressed,
            "A mouse input batch cannot reveal or press a manually disabled overlay");
        window.ApplyHideWhenCursorHidden(false);
        CheckVisibility(window, false, "Turning off automatic hiding cannot override manual disabling");

        setFlags(0);
        window.SetCursorEnabled(true);
        CheckVisibility(window, true, "Manual enabling shows the overlay when automatic hiding is disabled", rendering: true);
        window.ApplyHideWhenCursorHidden(true);
        CheckVisibility(window, false, "Turning on automatic hiding immediately applies the hidden cursor state");
        window.ApplyHideWhenCursorHidden(false);
        CheckVisibility(window, true, "Turning off automatic hiding immediately restores the overlay", rendering: true);

        setFlags(1);
        window.ApplyHideWhenCursorHidden(true);
        CheckVisibility(window, true, "Re-enabling visibility tracking preserves a showing cursor", rendering: true);
        setFlags(0);
        window.RefreshSystemCursorVisibility();
        setFlags(1);
        WaitForVisibility(window);
        CheckVisibility(window, true, "The polling timer restores visibility without mouse input or an active render loop");
        window.ApplyCursorDimensions(64.0);
    }

    private static void VerifyInputBatches(MainWindow window, AppSettings settings, Func<int> resizeQueries,
        Action<NativeMethods.CURSORINFO> setCursorInfo)
    {
        window.Model.Reset();
        window.UpdateMouseState();
        WaitForRender(window);
        var handle = new WindowInteropHelper(window).Handle;
        var before = WindowBounds(handle);
        var styleBefore = NativeMethods.GetWindowExtendedStyle(handle);
        var queriesBefore = resizeQueries();
        var batch = new[]
        {
            Input(NativeMethods.WmMouseMove, 420, 320),
            Input(NativeMethods.WmMouseMove, 460, 360),
            Input(NativeMethods.WmMouseMove, 480, 380)
        };
        var positionMessages = new List<int>();
        IntPtr ObservePositionMessages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message is 0x0046 or 0x0047)
            {
                positionMessages.Add(message);
            }
            return IntPtr.Zero;
        }
        var source = HwndSource.FromHwnd(handle);
        source.AddHook(ObservePositionMessages);
        var submission = Stopwatch.StartNew();
        try
        {
            window.ProcessMouseInput(batch);
            window.ProcessMouseInput(new[] { Input(NativeMethods.WmMouseMove, 490, 390) });
            window.ProcessMouseInput(new[] { Input(NativeMethods.WmMouseMove, 500, 400) });
            var beforeRendering = WindowBounds(handle);
            Check(window.Model.Position == new CursorPosition(500, 400)
                && beforeRendering.Left == before.Left && beforeRendering.Top == before.Top
                && positionMessages.Count == 0 && resizeQueries() == queriesBefore,
                "Several input batches update the model immediately without native movement or resize queries");
            setCursorInfo(new NativeMethods.CURSORINFO
            {
                flags = NativeMethods.CursorShowing,
                ptScreenPos = new NativeMethods.POINT { X = 500, Y = 400 }
            });
            WaitForRender(window);
        }
        finally
        {
            source.RemoveHook(ObservePositionMessages);
        }
        submission.Stop();
        Check(positionMessages.SequenceEqual(new[] { 0x0046, 0x0047 }),
            "One render submits one synchronized native position change for several input batches");
        var after = WindowBounds(handle);
        var dpi = VisualTreeHelper.GetDpi(window);
        var dimensions = new CursorDimensions(settings.CursorWidth);
        Check(after.Left == 500 - (int)Math.Round(dimensions.Margin * dpi.DpiScaleX)
            && after.Top == 400 - (int)Math.Round(dimensions.Margin * dpi.DpiScaleY),
            "The rendered native window uses the most recent input point from every queued batch");
        var styleAfter = NativeMethods.GetWindowExtendedStyle(handle);
        var requiredStyles = NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
        Check(after.Right - after.Left == before.Right - before.Left
            && after.Bottom - after.Top == before.Bottom - before.Top
            && styleAfter == styleBefore && (styleAfter & requiredStyles) == requiredStyles,
            "Ordinary batched motion preserves size, topmost, no-activate and click-through styles");
        Console.WriteLine($"DIAGNOSTIC Batched input to rendered native window submission: {submission.Elapsed.TotalMilliseconds:0.000} ms");

        queriesBefore = resizeQueries();
        window.ProcessMouseInput(new[]
        {
            Input(NativeMethods.WmLButtonDown, 600, 400),
            Input(NativeMethods.WmMouseMove, 1600, 400),
            Input(NativeMethods.WmMouseMove, 600, 400),
            Input(NativeMethods.WmRButtonDown, 600, 400),
            Input(NativeMethods.WmLButtonUp, 600, 400)
        });
        Check(window.Model.IsDragging && window.Model.Buttons == CursorButtons.Right
            && window.Model.Position == new CursorPosition(600, 400),
            "A batch preserves a quick out-and-back drag and the ordering of multiple buttons");
        window.ProcessMouseInput(new[] { Input(NativeMethods.WmMouseMove, 600, 600) });
        Settle(window.Model);
        Check(Math.Abs(window.Model.Angle - CursorModel.SpriteAngle) < 0.001,
            "Batched button changes preserve the initial drag origin for later motion");
        window.ProcessMouseInput(new[] { Input(NativeMethods.WmRButtonUp, 600, 600) });
        Check(!window.Model.IsPressed && !window.Model.IsDragging,
            "The final batched release clears the press without waiting for rendering");
        Check(resizeQueries() == queriesBefore,
            "Mouse input batches never execute a synchronous resize-target query");
        window.Model.Reset();
        window.StopRendering();
    }

    private static MouseInputEvent Input(uint message, int x, int y) => new(message, x, y, 0);

    private static NativeMethods.RECT WindowBounds(IntPtr handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out var bounds))
        {
            throw new InvalidOperationException("Unable to read the test overlay bounds.");
        }
        return bounds;
    }

    private static void WaitForVisibility(MainWindow window)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        probe.Tick += (_, _) =>
        {
            if (window.IsVisible || clock.Elapsed >= TimeSpan.FromSeconds(1))
            {
                frame.Continue = false;
            }
        };
        probe.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            probe.Stop();
        }
    }

    private static void WaitForRender(MainWindow window)
    {
        var frame = new DispatcherFrame();
        var rendered = false;
        var deadline = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        void OnRendering(object? sender, EventArgs args)
        {
            rendered = true;
            frame.Continue = false;
        }
        deadline.Tick += (_, _) => frame.Continue = false;
        window.StopRendering();
        window.RequestRender();
        CompositionTarget.Rendering += OnRendering;
        deadline.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            deadline.Stop();
            CompositionTarget.Rendering -= OnRendering;
            window.StopRendering();
        }
        if (!rendered)
        {
            throw new InvalidOperationException("No composition render arrived within one second.");
        }
    }

    private static NativeMethods.CURSORINFO CursorInfo(int flags) => new()
    {
        flags = flags,
        ptScreenPos = new NativeMethods.POINT { X = 400, Y = 300 }
    };

    private static void CheckVisibility(MainWindow window, bool visible, string name, bool? rendering = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Check(window.IsVisible == visible && NativeMethods.IsWindowVisible(handle) == visible
            && (visible || !window.IsRendering)
            && (!rendering.HasValue || window.IsRendering == rendering.Value), name);
    }

    private static void Settle(CursorModel model)
    {
        for (var i = 0; i < 1200; i++)
        {
            if (model.Advance(1.0 / 120.0))
            {
                return;
            }
        }
        throw new InvalidOperationException("Animation did not settle within ten simulated seconds.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
}
