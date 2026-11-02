using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OsuCursorWin;

internal static class NativeMethods
{
    internal const int CursorShowing = 0x00000001;
    internal const int CursorSuppressed = 0x00000002;

    internal const uint OCR_NORMAL = 32512;
    internal const uint OCR_IBEAM = 32513;
    internal const uint OCR_WAIT = 32514;
    internal const uint OCR_CROSS = 32515;
    internal const uint OCR_UP = 32516;
    internal const uint OCR_SIZENWSE = 32642;
    internal const uint OCR_SIZENESW = 32643;
    internal const uint OCR_SIZEWE = 32644;
    internal const uint OCR_SIZENS = 32645;
    internal const uint OCR_SIZEALL = 32646;
    internal const uint OCR_NO = 32648;
    internal const uint OCR_HAND = 32649;
    internal const uint OCR_APPSTARTING = 32650;
    internal const uint OCR_HELP = 32651;

    internal const int GWL_EXSTYLE = -20;
    internal const int GwlStyle = -16;
    internal const long WS_EX_TRANSPARENT = 0x00000020L;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_LAYERED = 0x00080000L;
    internal const long WS_EX_TOPMOST = 0x00000008L;
    internal const long WS_EX_NOACTIVATE = 0x08000000L;
    internal const long WsThickFrame = 0x00040000L;
    internal const long WsMaximize = 0x01000000L;
    internal const int SmCxSizeFrame = 32;
    internal const int SmCySizeFrame = 33;
    internal const int SmCxPaddedBorder = 92;
    internal const int DwmwaCloaked = 14;

    internal const uint WmNcHitTest = 0x0084;
    internal const uint SmtoBlock = 0x0001;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const uint SmtoErrorOnExit = 0x0020;
    internal const int HtLeft = 10;
    internal const int HtRight = 11;
    internal const int HtTop = 12;
    internal const int HtTopLeft = 13;
    internal const int HtTopRight = 14;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;

    internal static readonly IntPtr HwndTopmost = new(-1);
    internal static readonly IntPtr HwndTop = IntPtr.Zero;

    internal const int GwHwndNext = 2;
    internal const int GaRoot = 2;

    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint SwpNoOwnerZOrder = 0x0200;

    internal const int WhMouseLl = 14;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;
    internal const uint WmXButtonDown = 0x020B;
    internal const uint WmXButtonUp = 0x020C;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        internal int cbSize;
        internal int flags;
        internal IntPtr hCursor;
        internal POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        internal POINT pt;
        internal uint mouseData;
        internal uint flags;
        internal uint time;
        internal IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    internal static bool GetCursorInfo(out CURSORINFO info)
    {
        info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfoNative(ref info);
    }

    internal static void SetClickThrough(IntPtr hwnd)
    {
        Marshal.SetLastPInvokeError(0);
        long exstyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if (exstyle == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        exstyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TOPMOST;
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exstyle)) == IntPtr.Zero
            && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static long GetWindowStyle(IntPtr hwnd)
    {
        return GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
    }

    internal static long GetWindowExtendedStyle(IntPtr hwnd)
    {
        return GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
    }

    internal static bool MoveTopmost(IntPtr hwnd, int x, int y, int width, int height)
    {
        return SetWindowPos(hwnd, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
    }

    internal static bool MoveOverlay(IntPtr hwnd, int x, int y)
    {
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    internal static void BringAbove(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetCursorInfo", SetLastError = true)]
    private static extern bool GetCursorInfoNative(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCursor(
        IntPtr hInst,
        int xHotSpot,
        int yHotSpot,
        int nWidth,
        int nHeight,
        byte[] pvANDPlane,
        byte[] pvXORPlane);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);
}
