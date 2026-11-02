using OsuCursorWin;
using System.Windows.Interop;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            Run("Native hit-test results identify all eight resize directions", HitTestDirections);
            Run("Client, caption and other non-resize hit tests are ignored", NonResizeHitTests);
            Run("Each resize cursor axis selects the nearest permitted edge or corner", AxisDirections);
            Run("Negative monitor coordinates preserve resize direction", NegativeCoordinates);
            Run("Updated window bounds immediately change the inferred direction", UpdatedBounds);
            Run("Invalid and out-of-window bounds do not infer resize targets", BoundsLimits);
            Run("Extreme coordinates do not overflow geometric distance calculations", ExtremeCoordinates);
            Run("Hit-test coordinates preserve signed values and reject truncated points", PackedCoordinates);
            Run("Absent resize axes return no target before native window lookup", NoResizeAxis);
            Run("Native window hit testing detects all eight resize directions", NativeWindowDirections);
            Run("An overlay window is skipped to find the resize target beneath it", NativeOverlayIsSkipped);
            Run("A native client-area hit does not infer an outer resize edge", NativeClientHitIsIgnored);
            Console.WriteLine($"Passed {_passed} resize regression checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void HitTestDirections()
    {
        var expected = new[]
        {
            ResizeDirection.Left,
            ResizeDirection.Right,
            ResizeDirection.Top,
            ResizeDirection.TopLeft,
            ResizeDirection.TopRight,
            ResizeDirection.Bottom,
            ResizeDirection.BottomLeft,
            ResizeDirection.BottomRight
        };
        for (var i = 0; i < expected.Length; i++)
        {
            Equal(expected[i], ResizeTargetDetector.FromHitTest(10 + i));
        }
    }

    private static void NonResizeHitTests()
    {
        foreach (var hitTest in new[] { -2, -1, 0, 1, 2, 3, 9, 18, 20, int.MinValue, int.MaxValue })
        {
            Equal(ResizeDirection.None, ResizeTargetDetector.FromHitTest(hitTest));
        }
    }

    private static void AxisDirections()
    {
        var bounds = Bounds(100, 200, 500, 600);
        Direction(ResizeDirection.Left, Point(101, 400), bounds, ResizeCursorAxis.Horizontal);
        Direction(ResizeDirection.Right, Point(498, 400), bounds, ResizeCursorAxis.Horizontal);
        Direction(ResizeDirection.Top, Point(300, 201), bounds, ResizeCursorAxis.Vertical);
        Direction(ResizeDirection.Bottom, Point(300, 598), bounds, ResizeCursorAxis.Vertical);
        Direction(ResizeDirection.TopLeft, Point(101, 201), bounds, ResizeCursorAxis.NorthWestSouthEast);
        Direction(ResizeDirection.BottomRight, Point(498, 598), bounds, ResizeCursorAxis.NorthWestSouthEast);
        Direction(ResizeDirection.TopRight, Point(498, 201), bounds, ResizeCursorAxis.NorthEastSouthWest);
        Direction(ResizeDirection.BottomLeft, Point(101, 598), bounds, ResizeCursorAxis.NorthEastSouthWest);
        Direction(ResizeDirection.None, Point(101, 201), bounds, ResizeCursorAxis.None);
    }

    private static void NegativeCoordinates()
    {
        var bounds = Bounds(-1400, -900, -600, -200);
        Direction(ResizeDirection.Left, Point(-1399, -500), bounds, ResizeCursorAxis.Horizontal);
        Direction(ResizeDirection.Right, Point(-601, -500), bounds, ResizeCursorAxis.Horizontal);
        Direction(ResizeDirection.Top, Point(-1000, -899), bounds, ResizeCursorAxis.Vertical);
        Direction(ResizeDirection.Bottom, Point(-1000, -201), bounds, ResizeCursorAxis.Vertical);
        Direction(ResizeDirection.TopLeft, Point(-1399, -899), bounds, ResizeCursorAxis.NorthWestSouthEast);
        Direction(ResizeDirection.BottomRight, Point(-601, -201), bounds, ResizeCursorAxis.NorthWestSouthEast);
        Direction(ResizeDirection.TopRight, Point(-601, -899), bounds, ResizeCursorAxis.NorthEastSouthWest);
        Direction(ResizeDirection.BottomLeft, Point(-1399, -201), bounds, ResizeCursorAxis.NorthEastSouthWest);
    }

    private static void UpdatedBounds()
    {
        var point = Point(190, 190);
        var original = Bounds(0, 0, 200, 200);
        var expanded = Bounds(0, 0, 600, 600);
        Direction(ResizeDirection.Right, point, original, ResizeCursorAxis.Horizontal);
        Direction(ResizeDirection.Left, point, expanded, ResizeCursorAxis.Horizontal);
        Direction(ResizeDirection.Bottom, point, original, ResizeCursorAxis.Vertical);
        Direction(ResizeDirection.Top, point, expanded, ResizeCursorAxis.Vertical);
        Direction(ResizeDirection.BottomRight, point, original, ResizeCursorAxis.NorthWestSouthEast);
        Direction(ResizeDirection.TopLeft, point, expanded, ResizeCursorAxis.NorthWestSouthEast);
    }

    private static void BoundsLimits()
    {
        var bounds = Bounds(100, 200, 500, 600);
        Direction(ResizeDirection.Left, Point(100, 400), bounds, ResizeCursorAxis.Horizontal);
        Direction(ResizeDirection.Right, Point(499, 400), bounds, ResizeCursorAxis.Horizontal);
        Direction(ResizeDirection.Top, Point(300, 200), bounds, ResizeCursorAxis.Vertical);
        Direction(ResizeDirection.Bottom, Point(300, 599), bounds, ResizeCursorAxis.Vertical);
        foreach (var point in new[] { Point(99, 400), Point(500, 400), Point(300, 199), Point(300, 600) })
        {
            Direction(ResizeDirection.None, point, bounds, ResizeCursorAxis.NorthWestSouthEast);
        }
        foreach (var invalid in new[] { Bounds(1, 1, 1, 3), Bounds(1, 3, 4, 3), Bounds(4, 1, 2, 3), Bounds(1, 5, 4, 3) })
        {
            Direction(ResizeDirection.None, Point(2, 2), invalid, ResizeCursorAxis.Horizontal);
        }
    }

    private static void ExtremeCoordinates()
    {
        var bounds = Bounds(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);
        Direction(ResizeDirection.TopLeft, Point(int.MinValue, int.MinValue), bounds, ResizeCursorAxis.NorthWestSouthEast);
        Direction(ResizeDirection.BottomRight, Point(int.MaxValue - 1, int.MaxValue - 1), bounds, ResizeCursorAxis.NorthWestSouthEast);
        Direction(ResizeDirection.TopRight, Point(int.MaxValue - 1, int.MinValue), bounds, ResizeCursorAxis.NorthEastSouthWest);
        Direction(ResizeDirection.BottomLeft, Point(int.MinValue, int.MaxValue - 1), bounds, ResizeCursorAxis.NorthEastSouthWest);
    }

    private static void NoResizeAxis()
    {
        var detector = new ResizeTargetDetector();
        Equal(ResizeDirection.None, detector.GetDirection(Point(int.MinValue, int.MaxValue), new IntPtr(-1), ResizeCursorAxis.None));
        detector.Reset();
        Equal(ResizeDirection.None, detector.GetDirection(Point(0, 0), IntPtr.Zero, ResizeCursorAxis.None));
    }

    private static void PackedCoordinates()
    {
        foreach (var point in new[] { Point(-1234, -5678), Point(short.MinValue, short.MaxValue), Point(short.MaxValue, short.MinValue) })
        {
            Equal(true, ResizeTargetDetector.TryPackPoint(point, out var packed));
            var value = packed.ToInt64();
            Equal(point.X, (int)unchecked((short)(value & 0xFFFF)));
            Equal(point.Y, (int)unchecked((short)((value >> 16) & 0xFFFF)));
        }
        foreach (var point in new[] { Point(short.MinValue - 1, 0), Point(short.MaxValue + 1, 0), Point(0, short.MinValue - 1), Point(0, short.MaxValue + 1) })
        {
            Equal(false, ResizeTargetDetector.TryPackPoint(point, out _));
        }
    }

    private static void NativeWindowDirections()
    {
        using var window = new TestWindow(GetTestWindowPosition());
        var point = window.Center;
        Equal(window.Handle, NativeMethods.WindowFromPoint(point));
        var detector = new ResizeTargetDetector();
        var cases = new[]
        {
            (NativeMethods.HtLeft, ResizeCursorAxis.Horizontal, ResizeDirection.Left),
            (NativeMethods.HtRight, ResizeCursorAxis.Horizontal, ResizeDirection.Right),
            (NativeMethods.HtTop, ResizeCursorAxis.Vertical, ResizeDirection.Top),
            (NativeMethods.HtBottom, ResizeCursorAxis.Vertical, ResizeDirection.Bottom),
            (NativeMethods.HtTopLeft, ResizeCursorAxis.NorthWestSouthEast, ResizeDirection.TopLeft),
            (NativeMethods.HtBottomRight, ResizeCursorAxis.NorthWestSouthEast, ResizeDirection.BottomRight),
            (NativeMethods.HtTopRight, ResizeCursorAxis.NorthEastSouthWest, ResizeDirection.TopRight),
            (NativeMethods.HtBottomLeft, ResizeCursorAxis.NorthEastSouthWest, ResizeDirection.BottomLeft)
        };
        foreach (var (hitTest, axis, expected) in cases)
        {
            window.HitTest = hitTest;
            detector.Reset();
            Equal(expected, detector.GetDirection(point, IntPtr.Zero, axis));
        }
    }

    private static void NativeOverlayIsSkipped()
    {
        var position = GetTestWindowPosition();
        using var target = new TestWindow(position) { HitTest = NativeMethods.HtBottomRight };
        using var overlay = new TestWindow(position);
        NativeMethods.BringAbove(overlay.Handle);
        var point = target.Center;
        Equal(overlay.Handle, NativeMethods.WindowFromPoint(point));
        var detector = new ResizeTargetDetector();
        Equal(ResizeDirection.BottomRight,
            detector.GetDirection(point, overlay.Handle, ResizeCursorAxis.NorthWestSouthEast));
        target.HitTest = NativeMethods.HtLeft;
        detector.Reset();
        Equal(ResizeDirection.Left, detector.GetDirection(point, overlay.Handle, ResizeCursorAxis.Horizontal));
    }

    private static void NativeClientHitIsIgnored()
    {
        using var window = new TestWindow(GetTestWindowPosition());
        Equal(true, NativeMethods.GetWindowRect(window.Handle, out var bounds));
        var point = Point(bounds.Left + 1, bounds.Top + 1);
        Equal(window.Handle, NativeMethods.WindowFromPoint(point));
        var detector = new ResizeTargetDetector();
        foreach (var axis in new[] { ResizeCursorAxis.Horizontal, ResizeCursorAxis.Vertical,
            ResizeCursorAxis.NorthWestSouthEast, ResizeCursorAxis.NorthEastSouthWest })
        {
            Equal(ResizeDirection.None, detector.GetDirection(point, IntPtr.Zero, axis));
        }
    }

    private static NativeMethods.POINT GetTestWindowPosition()
    {
        var width = NativeMethods.GetSystemMetrics(0);
        var height = NativeMethods.GetSystemMetrics(1);
        var x = 16;
        var y = 16;
        if (NativeMethods.GetCursorInfo(out var cursor))
        {
            if (cursor.ptScreenPos.X < width / 2)
            {
                x = Math.Max(16, width - 40);
            }
            if (cursor.ptScreenPos.Y < height / 2)
            {
                y = Math.Max(16, height - 40);
            }
        }
        return Point(x, y);
    }

    private sealed class TestWindow : IDisposable
    {
        private readonly HwndSource _source;

        internal int HitTest { get; set; } = 1;
        internal IntPtr Handle => _source.Handle;

        internal NativeMethods.POINT Center
        {
            get
            {
                Equal(true, NativeMethods.GetWindowRect(Handle, out var bounds));
                return Point(bounds.Left + (bounds.Right - bounds.Left) / 2,
                    bounds.Top + (bounds.Bottom - bounds.Top) / 2);
            }
        }

        internal TestWindow(NativeMethods.POINT position)
        {
            _source = new HwndSource(new HwndSourceParameters("OsuCursor Resize Regression")
            {
                PositionX = position.X,
                PositionY = position.Y,
                Width = 24,
                Height = 24,
                WindowStyle = unchecked((int)0x90000000),
                ExtendedWindowStyle = unchecked((int)(NativeMethods.WS_EX_NOACTIVATE
                    | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST)),
                HwndSourceHook = WindowProcedure
            });
        }

        private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == NativeMethods.WmNcHitTest)
            {
                handled = true;
                return new IntPtr(HitTest);
            }
            if (message == 0x0021)
            {
                handled = true;
                return new IntPtr(3);
            }
            return IntPtr.Zero;
        }

        public void Dispose() => _source.Dispose();
    }

    private static NativeMethods.POINT Point(int x, int y) => new() { X = x, Y = y };

    private static NativeMethods.RECT Bounds(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static void Direction(ResizeDirection expected, NativeMethods.POINT point, NativeMethods.RECT bounds, ResizeCursorAxis axis) =>
        Equal(expected, ResizeTargetDetector.FromBounds(point, bounds, axis));

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
}
