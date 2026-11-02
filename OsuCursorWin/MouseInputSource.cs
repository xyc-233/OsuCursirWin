using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace OsuCursorWin;

internal sealed class MouseInputSource : IDisposable
{
    private const uint WmQuit = 0x0012;
    private readonly object _sync = new();
    private readonly Dispatcher _dispatcher;
    private readonly Action<IReadOnlyList<MouseInputEvent>> _onInput;
    private readonly MouseInputBuffer _buffer = new();
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly NativeMethods.LowLevelMouseProc _hookCallback;
    private DispatcherOperation? _pending;
    private bool _delivering;
    private IntPtr _hook;
    private uint _threadId;
    private int _startRequested;
    private int _disposed;
    private Exception? _callbackError;

    internal MouseInputSource(Dispatcher dispatcher, Action<IReadOnlyList<MouseInputEvent>> onInput)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(onInput);
        _dispatcher = dispatcher;
        _onInput = onInput;
        _hookCallback = OnMouse;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "OsuCursorMouseInput"
        };
    }

    internal bool IsRunning => _started.Task.IsCompletedSuccessfully
        && _started.Task.Result && Volatile.Read(ref _disposed) == 0;

    internal MouseInputSnapshot ResetTracking()
    {
        lock (_sync)
        {
            return _buffer.ResetTracking();
        }
    }

    internal bool Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _startRequested, 1, 0) == 0)
        {
            try
            {
                _thread.Start();
            }
            catch (Exception exception)
            {
                Program.Log($"Failed to start mouse input thread: {exception}");
                _started.TrySetResult(false);
            }
        }

        if (!_started.Task.Wait(TimeSpan.FromSeconds(2)))
        {
            Dispose();
            return false;
        }

        return _started.Task.Result && Volatile.Read(ref _disposed) == 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelDelivery();
        var threadId = Volatile.Read(ref _threadId);
        if (threadId != 0)
        {
            PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        }

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromMilliseconds(250));
        }
    }

    private void Run()
    {
        try
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _hookCallback,
                NativeMethods.GetModuleHandle(IntPtr.Zero), 0);
            if (_hook == IntPtr.Zero)
            {
                Program.Log($"Mouse hook install failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            InitializeButtons();
            _started.TrySetResult(true);
            while (Volatile.Read(ref _disposed) == 0)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0)
                {
                    if (result < 0)
                    {
                        Program.Log($"Mouse input message loop failed: {Marshal.GetLastWin32Error()}");
                    }
                    break;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            Program.Log($"Mouse input thread failed: {exception}");
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }

            Interlocked.Exchange(ref _disposed, 1);
            CancelDelivery();
            _started.TrySetResult(false);
            if (_callbackError is not null)
            {
                Program.Log($"Mouse input callback failed: {_callbackError}");
            }
        }
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                var input = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(data);
                Queue(new MouseInputEvent(unchecked((uint)message.ToInt64()), input.pt.X, input.pt.Y, input.mouseData));
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _callbackError, exception, null);
                PostThreadMessage(Volatile.Read(ref _threadId), WmQuit, UIntPtr.Zero, IntPtr.Zero);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, message, data);
    }

    private void Queue(MouseInputEvent input)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted)
            {
                return;
            }

            _buffer.Add(input);
            if (_pending is null && !_delivering)
            {
                _pending = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Deliver));
            }
        }
    }

    private void Deliver()
    {
        MouseInputEvent[] inputs;
        lock (_sync)
        {
            _pending = null;
            if (Volatile.Read(ref _disposed) != 0)
            {
                _buffer.Clear();
                return;
            }

            inputs = _buffer.Drain();
            _delivering = true;
        }

        try
        {
            if (inputs.Length != 0 && Volatile.Read(ref _disposed) == 0)
            {
                _onInput(inputs);
            }
        }
        finally
        {
            lock (_sync)
            {
                _delivering = false;
                if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted)
                {
                    _buffer.Clear();
                }
                else if (_buffer.Count != 0)
                {
                    _pending = _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Deliver));
                }
            }
        }
    }

    private void CancelDelivery()
    {
        lock (_sync)
        {
            _pending?.Abort();
            _pending = null;
            _buffer.Clear();
        }
    }

    private void InitializeButtons()
    {
        uint buttons = 0;
        if ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0) buttons |= 1;
        if ((NativeMethods.GetAsyncKeyState(0x02) & 0x8000) != 0) buttons |= 2;
        if ((NativeMethods.GetAsyncKeyState(0x04) & 0x8000) != 0) buttons |= 4;
        if ((NativeMethods.GetAsyncKeyState(0x05) & 0x8000) != 0) buttons |= 8;
        if ((NativeMethods.GetAsyncKeyState(0x06) & 0x8000) != 0) buttons |= 16;
        if (NativeMethods.GetCursorInfo(out var info))
        {
            lock (_sync)
            {
                _buffer.InitializeButtons(buttons, info.ptScreenPos.X, info.ptScreenPos.Y);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal IntPtr Window;
        internal uint Id;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal NativeMethods.POINT Point;
        internal uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
