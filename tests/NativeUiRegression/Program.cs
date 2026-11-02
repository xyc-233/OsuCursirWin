using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using OsuCursorWin;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OsuCursorNativeUi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settings = AppSettings.Load(Path.Combine(directory, "settings.json"));
        var app = new Application();
        NativeMethods.CURSORINFO? cursorInfo = CursorInfo(1, 400, 300);
        var resizeDirection = ResizeDirection.None;
        var window = new MainWindow(smoke: true, settings: settings,
            cursorInfoProvider: () => cursorInfo, resizeDirectionProvider: _ => resizeDirection);
        Exception? failure = null;
        var checksStarted = false;
        var nativeHandle = IntPtr.Zero;
        var foreground = GetForegroundWindow();
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (checksStarted) return;
            checksStarted = true;
            try
            {
                nativeHandle = window.OverlayHandle;
                Check(nativeHandle != IntPtr.Zero && nativeHandle != new WindowInteropHelper(window).Handle
                    && IsWindow(nativeHandle) && window.Content is null,
                    "Native rendering uses a separate window without attached WPF cursor content");
                CheckVisibility(window, true, "The native overlay is visible after loading");
                Check(GetForegroundWindow() == foreground, "Loading the native overlay does not take foreground activation");
                window.StopRendering();

                cursorInfo = CursorInfo(1, 620, 450);
                window.ProcessMouseInput(new[] { Input(NativeMethods.WmMouseMove, 620, 450) });
                AssertHotspot(window, 620, 450, settings.CursorWidth);
                Check(window.Model.Position == new CursorPosition(620, 450),
                    "Native movement submits the latest input position immediately");

                resizeDirection = ResizeDirection.Right;
                window.Model.Reset();
                window.RequestRender();
                PumpUntil(() => Math.Abs(window.Model.Angle) > 1.0);
                Check(Math.Abs(window.Model.Angle) > 1.0,
                    "The native frame clock advances rotation with no WPF cursor visual");
                resizeDirection = ResizeDirection.None;

                window.ApplyAnimationFrameRateLimit(30);
                window.ApplyPauseRenderingWhenIdle(false);
                var frames = new List<long>();
                void RecordFrame() => frames.Add(Stopwatch.GetTimestamp());
                window.FrameRendered += RecordFrame;
                try
                {
                    PumpUntil(() => frames.Count >= 8);
                }
                finally
                {
                    window.FrameRendered -= RecordFrame;
                }
                var averageInterval = Stopwatch.GetElapsedTime(frames[2], frames[^1]).TotalMilliseconds / (frames.Count - 3);
                Check(settings.AnimationFrameRateLimit == 30 && averageInterval >= 20 && averageInterval <= 100,
                    "A live animation limit changes the native rendering cadence");
                cursorInfo = CursorInfo(1, 640, 470);
                window.ProcessMouseInput(new[] { Input(NativeMethods.WmMouseMove, 640, 470) });
                AssertHotspot(window, 640, 470, settings.CursorWidth);
                Check(window.IsRendering && !settings.PauseRenderingWhenIdle,
                    "Disabling idle pauses keeps continuous rendering active");
                window.ApplyHighQualityRendering(false);
                Check(!settings.HighQualityRendering, "A live quality change updates the saved preference");
                window.ApplyHighQualityRendering(true);
                window.ApplyAnimationFrameRateLimit(0);
                window.Model.Reset();
                window.ApplyPauseRenderingWhenIdle(true);
                PumpUntil(() => !window.IsRendering);
                Check(settings.PauseRenderingWhenIdle && !window.IsRendering,
                    "Re-enabling idle pauses stops rendering after animation settles");

                window.Model.SetButton(CursorButtons.Left, true);
                window.Model.MoveTo(850, 450, 30);
                cursorInfo = CursorInfo(0, 850, 450);
                window.RefreshSystemCursorVisibility();
                CheckVisibility(window, false, "A hidden system cursor hides the native overlay and stops its clock");
                Check(!window.Model.IsPressed && !window.Model.IsDragging,
                    "Hiding clears the previous drag state");
                window.ProcessMouseInput(new[] { Input(NativeMethods.WmLButtonDown, 850, 450) });
                window.ApplyCursorDimensions(48);
                window.RequestRender();
                CheckVisibility(window, false, "Input, size changes and render requests cannot reveal a hidden overlay");

                cursorInfo = null;
                window.RefreshSystemCursorVisibility();
                CheckVisibility(window, false, "A failed system cursor query preserves native hidden state");
                cursorInfo = CursorInfo(1, 500, 350);
                window.ProcessMouseInput(new[] { Input(NativeMethods.WmLButtonDown, 100, 100), Input(NativeMethods.WmMouseMove, 150, 150) });
                CheckVisibility(window, true, "A showing system cursor restores native visibility");
                Check(!window.Model.IsPressed && !window.Model.IsDragging
                    && window.Model.Position == new CursorPosition(500, 350),
                    "Visibility recovery discards queued drag input and uses the current cursor position");
                AssertHotspot(window, 500, 350, 48);

                foreach (var flags in new[] { 2, 3 })
                {
                    cursorInfo = CursorInfo(flags, 500, 350);
                    window.RefreshSystemCursorVisibility();
                    CheckVisibility(window, false, $"Suppressed cursor flags {flags} hide the native overlay");
                }
                window.SetCursorEnabled(false);
                cursorInfo = CursorInfo(1, 500, 350);
                window.RefreshSystemCursorVisibility();
                window.ApplyHideWhenCursorHidden(false);
                window.ProcessMouseInput(new[] { Input(NativeMethods.WmRButtonDown, 900, 900) });
                window.RequestRender();
                CheckVisibility(window, false, "Manual disabling survives cursor recovery, input and automatic-hide changes");
                Check(!window.Model.IsPressed, "A disabled native overlay ignores button input");

                cursorInfo = CursorInfo(0, 500, 350);
                foreground = GetForegroundWindow();
                window.SetCursorEnabled(true);
                CheckVisibility(window, true, "Manual enabling respects disabled automatic hiding");
                Check(GetForegroundWindow() == foreground, "Re-enabling the native overlay does not take foreground activation");
                window.ApplyHideWhenCursorHidden(true);
                CheckVisibility(window, false, "Enabling automatic hiding applies native visibility immediately");
                cursorInfo = CursorInfo(1, 500, 350);
                PumpUntil(() => NativeMethods.IsWindowVisible(nativeHandle));
                CheckVisibility(window, true, "Visibility polling restores the native overlay while rendering is stopped");
                window.ApplyCursorDimensions(64);
                PumpUntil(() => WindowBounds(nativeHandle).Right - WindowBounds(nativeHandle).Left
                    == (int)Math.Ceiling(new CursorDimensions(64).WindowSize * NativeMethods.GetDpiForWindow(nativeHandle) / 96.0));
                AssertHotspot(window, 500, 350, 64);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window.Close();
            }
        }));

        try
        {
            app.Run(window);
            if (failure is not null) throw failure;
            Check(checksStarted && !IsWindow(nativeHandle) && !window.IsRendering,
                "Closing destroys the native overlay and stops rendering");
            window.RequestRender();
            window.ProcessMouseInput(new[] { Input(NativeMethods.WmLButtonDown, 900, 900) });
            Check(!IsWindow(nativeHandle) && !window.IsRendering && !window.Model.IsPressed,
                "Queued work cannot recreate the native overlay after closing");
            Console.WriteLine($"Passed {_passed} native UI integration checks without modifying the system cursor.");
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

    private static void AssertHotspot(MainWindow window, int x, int y, double width)
    {
        var handle = window.OverlayHandle;
        var dpi = NativeMethods.GetDpiForWindow(handle) / 96.0;
        var dimensions = new CursorDimensions(width);
        var bounds = WindowBounds(handle);
        Check(bounds.Left + (int)Math.Round(dimensions.Margin * dpi) == x
            && bounds.Top + (int)Math.Round(dimensions.Margin * dpi) == y
            && bounds.Right - bounds.Left == (int)Math.Ceiling(dimensions.WindowSize * dpi)
            && bounds.Bottom - bounds.Top == (int)Math.Ceiling(dimensions.WindowSize * dpi),
            "Native position and size preserve the cursor hotspot at the current DPI");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            if (condition() || elapsed.Elapsed >= TimeSpan.FromSeconds(1)) frame.Continue = false;
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
        if (!condition()) throw new InvalidOperationException("A native UI transition did not complete within one second.");
    }

    private static void CheckVisibility(MainWindow window, bool visible, string name)
    {
        Check(NativeMethods.IsWindowVisible(window.OverlayHandle) == visible && (visible || !window.IsRendering), name);
    }

    private static NativeMethods.RECT WindowBounds(IntPtr handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out var bounds)) throw new InvalidOperationException("Cannot read native overlay bounds.");
        return bounds;
    }

    private static NativeMethods.CURSORINFO CursorInfo(int flags, int x, int y) => new()
    {
        flags = flags,
        ptScreenPos = new NativeMethods.POINT { X = x, Y = y }
    };

    private static MouseInputEvent Input(uint message, int x, int y) => new(message, x, y, 0);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
