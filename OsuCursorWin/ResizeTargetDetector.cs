using System;

namespace OsuCursorWin;

internal sealed class ResizeTargetDetector
{
    private const uint HitTestTimeoutMilliseconds = 8;
    private const int FailureBackoffMilliseconds = 250;
    private const int CacheMilliseconds = 24;
    private readonly BackoffEntry[] _backoff = new BackoffEntry[4];
    private int _nextBackoff;
    private IntPtr _cachedWindow;
    private NativeMethods.POINT _cachedPoint;
    private ResizeCursorAxis _cachedAxis;
    private ResizeDirection _cachedDirection;
    private long _cacheExpires;

    internal ResizeDirection GetDirection(NativeMethods.POINT point, IntPtr overlayWindow, ResizeCursorAxis axis)
    {
        if (axis == ResizeCursorAxis.None)
        {
            Reset();
            return ResizeDirection.None;
        }

        var target = FindTarget(point, overlayWindow);
        if (target == IntPtr.Zero)
        {
            return ResizeDirection.None;
        }

        var now = Environment.TickCount64;
        if (target == _cachedWindow && axis == _cachedAxis
            && point.X == _cachedPoint.X && point.Y == _cachedPoint.Y && now < _cacheExpires)
        {
            return _cachedDirection;
        }

        var direction = QueryDirection(target, point, axis, now);
        _cachedWindow = target;
        _cachedPoint = point;
        _cachedAxis = axis;
        _cachedDirection = direction;
        _cacheExpires = now + CacheMilliseconds;
        return direction;
    }

    internal void Reset()
    {
        _cachedWindow = IntPtr.Zero;
        _cachedAxis = ResizeCursorAxis.None;
        _cachedDirection = ResizeDirection.None;
        _cacheExpires = 0;
        _nextBackoff = 0;
        Array.Clear(_backoff);
    }

    internal static ResizeDirection FromHitTest(int hitTest) => hitTest switch
    {
        NativeMethods.HtLeft => ResizeDirection.Left,
        NativeMethods.HtRight => ResizeDirection.Right,
        NativeMethods.HtTop => ResizeDirection.Top,
        NativeMethods.HtTopLeft => ResizeDirection.TopLeft,
        NativeMethods.HtTopRight => ResizeDirection.TopRight,
        NativeMethods.HtBottom => ResizeDirection.Bottom,
        NativeMethods.HtBottomLeft => ResizeDirection.BottomLeft,
        NativeMethods.HtBottomRight => ResizeDirection.BottomRight,
        _ => ResizeDirection.None
    };

    internal static ResizeDirection FromBounds(NativeMethods.POINT point, NativeMethods.RECT bounds, ResizeCursorAxis axis)
    {
        if (!Contains(bounds, point))
        {
            return ResizeDirection.None;
        }

        var left = (double)point.X - bounds.Left;
        var right = (double)bounds.Right - 1.0 - point.X;
        var top = (double)point.Y - bounds.Top;
        var bottom = (double)bounds.Bottom - 1.0 - point.Y;
        return axis switch
        {
            ResizeCursorAxis.Horizontal => left <= right ? ResizeDirection.Left : ResizeDirection.Right,
            ResizeCursorAxis.Vertical => top <= bottom ? ResizeDirection.Top : ResizeDirection.Bottom,
            ResizeCursorAxis.NorthWestSouthEast => left * left + top * top <= right * right + bottom * bottom
                ? ResizeDirection.TopLeft : ResizeDirection.BottomRight,
            ResizeCursorAxis.NorthEastSouthWest => right * right + top * top <= left * left + bottom * bottom
                ? ResizeDirection.TopRight : ResizeDirection.BottomLeft,
            _ => ResizeDirection.None
        };
    }

    internal static bool TryPackPoint(NativeMethods.POINT point, out IntPtr parameter)
    {
        if (point.X < short.MinValue || point.X > short.MaxValue
            || point.Y < short.MinValue || point.Y > short.MaxValue)
        {
            parameter = IntPtr.Zero;
            return false;
        }

        var packed = unchecked((ushort)(short)point.X | ((uint)(ushort)(short)point.Y << 16));
        parameter = new IntPtr(unchecked((int)packed));
        return true;
    }

    private ResizeDirection QueryDirection(IntPtr target, NativeMethods.POINT point, ResizeCursorAxis axis, long now)
    {
        if (!IsBackingOff(target, now) && TryPackPoint(point, out var parameter))
        {
            var sent = NativeMethods.SendMessageTimeout(target, NativeMethods.WmNcHitTest, IntPtr.Zero, parameter,
                NativeMethods.SmtoBlock | NativeMethods.SmtoAbortIfHung | NativeMethods.SmtoErrorOnExit,
                HitTestTimeoutMilliseconds, out var result);
            if (sent != IntPtr.Zero)
            {
                return FromHitTest(unchecked((int)result.ToInt64()));
            }

            _backoff[_nextBackoff] = new BackoffEntry(target, now + FailureBackoffMilliseconds);
            _nextBackoff = (_nextBackoff + 1) % _backoff.Length;
        }

        if (!NativeMethods.GetWindowRect(target, out var bounds))
        {
            return ResizeDirection.None;
        }

        var direction = FromBounds(point, bounds, axis);
        if (direction == ResizeDirection.None)
        {
            return direction;
        }

        var dpi = NativeMethods.GetDpiForWindow(target);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var padding = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCxPaddedBorder, dpi);
        var borderX = Math.Max(1, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCxSizeFrame, dpi) + padding);
        var borderY = Math.Max(1, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCySizeFrame, dpi) + padding);
        return IsNearEdge(point, bounds, direction, borderX, borderY) ? direction : ResizeDirection.None;
    }

    private bool IsBackingOff(IntPtr target, long now)
    {
        foreach (var entry in _backoff)
        {
            if (entry.Window == target && now < entry.Expires)
            {
                return true;
            }
        }

        return false;
    }

    private static IntPtr FindTarget(NativeMethods.POINT point, IntPtr overlayWindow)
    {
        var hit = NativeMethods.WindowFromPoint(point);
        if (hit == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var root = NativeMethods.GetAncestor(hit, (uint)NativeMethods.GaRoot);
        if (root == IntPtr.Zero)
        {
            root = hit;
        }

        if (hit != overlayWindow && root != overlayWindow)
        {
            return root;
        }

        var candidate = NativeMethods.GetWindow(root, (uint)NativeMethods.GwHwndNext);
        for (var count = 0; candidate != IntPtr.Zero && count < 512; count++)
        {
            if (candidate != overlayWindow && NativeMethods.IsWindowVisible(candidate)
                && !NativeMethods.IsIconic(candidate)
                && NativeMethods.GetWindowRect(candidate, out var bounds) && Contains(bounds, point))
            {
                var styles = NativeMethods.GetWindowExtendedStyle(candidate);
                var transparentLayer = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT;
                var isCloaked = NativeMethods.DwmGetWindowAttribute(candidate, NativeMethods.DwmwaCloaked,
                    out var cloaked, sizeof(int)) == 0 && cloaked != 0;
                if ((styles & transparentLayer) != transparentLayer && !isCloaked)
                {
                    return candidate;
                }
            }

            var next = NativeMethods.GetWindow(candidate, (uint)NativeMethods.GwHwndNext);
            if (next == candidate)
            {
                break;
            }

            candidate = next;
        }

        return IntPtr.Zero;
    }

    private static bool Contains(NativeMethods.RECT bounds, NativeMethods.POINT point) =>
        bounds.Right > bounds.Left && bounds.Bottom > bounds.Top
        && point.X >= bounds.Left && point.X < bounds.Right
        && point.Y >= bounds.Top && point.Y < bounds.Bottom;

    private static bool IsNearEdge(NativeMethods.POINT point, NativeMethods.RECT bounds,
        ResizeDirection direction, int borderX, int borderY)
    {
        var left = (long)point.X - bounds.Left < borderX;
        var right = (long)bounds.Right - 1 - point.X < borderX;
        var top = (long)point.Y - bounds.Top < borderY;
        var bottom = (long)bounds.Bottom - 1 - point.Y < borderY;
        return direction switch
        {
            ResizeDirection.Left => left,
            ResizeDirection.Right => right,
            ResizeDirection.Top => top,
            ResizeDirection.Bottom => bottom,
            ResizeDirection.TopLeft => top && left,
            ResizeDirection.TopRight => top && right,
            ResizeDirection.BottomLeft => bottom && left,
            ResizeDirection.BottomRight => bottom && right,
            _ => false
        };
    }

    private readonly record struct BackoffEntry(IntPtr Window, long Expires);
}
