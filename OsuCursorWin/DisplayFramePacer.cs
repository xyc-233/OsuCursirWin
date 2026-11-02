using System;
using System.Runtime.InteropServices;

namespace OsuCursorWin;

internal sealed class DisplayFramePacer
{
    private const double DefaultRefreshRate = 60.0;
    private const double FrameCapacity = 1.5;
    private readonly Func<int, int, double> _refreshRateProvider;
    private double _refreshRate = DefaultRefreshRate;
    private double _availableFrames;
    private double _lastTime;
    private bool _initialized;
    private IntPtr _monitor;
    private long _nextRefreshCheck;
    private double _monitorRefreshRate = DefaultRefreshRate;

    internal DisplayFramePacer(Func<int, int, double>? refreshRateProvider = null)
    {
        _refreshRateProvider = refreshRateProvider ?? ReadMonitorRefreshRate;
    }

    internal bool ShouldAdvance(double nowSeconds, int x, int y)
    {
        if (!double.IsFinite(nowSeconds) || nowSeconds < 0.0)
        {
            return false;
        }

        double refreshRate;
        try
        {
            refreshRate = _refreshRateProvider(x, y);
        }
        catch
        {
            refreshRate = _refreshRate;
        }

        if (!double.IsFinite(refreshRate) || refreshRate <= 1.0)
        {
            refreshRate = _refreshRate;
        }

        if (!_initialized || nowSeconds < _lastTime)
        {
            _initialized = true;
            _refreshRate = refreshRate;
            _lastTime = nowSeconds;
            _availableFrames = FrameCapacity - 1.0;
            return true;
        }

        _availableFrames = Math.Min(FrameCapacity, _availableFrames + (nowSeconds - _lastTime) * _refreshRate);
        _lastTime = nowSeconds;
        _refreshRate = refreshRate;
        if (_availableFrames + 1e-9 < 1.0)
        {
            return false;
        }

        _availableFrames = Math.Max(0.0, _availableFrames - 1.0);
        return true;
    }

    internal void Reset()
    {
        _initialized = false;
    }

    internal double GetRefreshRate(int x, int y) => _refreshRateProvider(x, y);

    private double ReadMonitorRefreshRate(int x, int y)
    {
        var monitor = MonitorFromPoint(new Point { X = x, Y = y }, 2);
        var now = Environment.TickCount64;
        if (monitor != _monitor)
        {
            _monitor = monitor;
            _nextRefreshCheck = 0;
            _monitorRefreshRate = DefaultRefreshRate;
        }

        if (now < _nextRefreshCheck)
        {
            return _monitorRefreshRate;
        }

        _nextRefreshCheck = now + 1000;
        var info = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
            DeviceName = string.Empty
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return _monitorRefreshRate;
        }

        var mode = new DisplayMode
        {
            Size = (ushort)Marshal.SizeOf<DisplayMode>(),
            DeviceName = string.Empty,
            FormName = string.Empty
        };
        if (EnumDisplaySettingsEx(info.DeviceName, -1, ref mode, 0) && mode.DisplayFrequency > 1)
        {
            _monitorRefreshRate = mode.DisplayFrequency;
        }

        return _monitorRefreshRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect MonitorBounds;
        public Rect WorkBounds;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public Point Position;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNumber, ref DisplayMode mode, uint flags);
}
