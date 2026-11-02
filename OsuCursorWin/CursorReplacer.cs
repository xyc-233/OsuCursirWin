using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OsuCursorWin;

internal static class CursorReplacer
{
    private static readonly CursorReplacement Replacement = new(new WindowsCursorApi());

    internal static bool Install() => Replacement.Install();

    internal static IntPtr GetBlankHandle(uint cursorId) => Replacement.GetBlankHandle(cursorId);

    internal static void Restore() => Replacement.Restore();
}

internal sealed class CursorReplacement
{
    private static readonly uint[] CursorIds =
    {
        NativeMethods.OCR_NORMAL,
        NativeMethods.OCR_IBEAM,
        NativeMethods.OCR_WAIT,
        NativeMethods.OCR_CROSS,
        NativeMethods.OCR_UP,
        NativeMethods.OCR_SIZENWSE,
        NativeMethods.OCR_SIZENESW,
        NativeMethods.OCR_SIZEWE,
        NativeMethods.OCR_SIZENS,
        NativeMethods.OCR_SIZEALL,
        NativeMethods.OCR_NO,
        NativeMethods.OCR_HAND,
        NativeMethods.OCR_APPSTARTING,
        NativeMethods.OCR_HELP
    };

    private readonly object _sync = new();
    private readonly ICursorApi _api;
    private readonly Dictionary<uint, IntPtr> _originalHandles = new();
    private readonly Dictionary<uint, IntPtr> _blankHandles = new();
    private readonly HashSet<uint> _replaced = new();
    private bool _installed;

    internal CursorReplacement(ICursorApi api)
    {
        _api = api;
    }

    internal bool Install()
    {
        lock (_sync)
        {
            if (_installed)
            {
                return true;
            }

            if (_replaced.Count != 0)
            {
                RestoreCore();
                if (_replaced.Count != 0)
                {
                    return false;
                }
            }

            foreach (var id in CursorIds)
            {
                var original = _api.Load(id);
                var backup = original == IntPtr.Zero ? IntPtr.Zero : _api.Copy(original);
                if (backup == IntPtr.Zero)
                {
                    Program.Log($"Backup cursor failed for id={id} error={_api.LastError}");
                    ReleaseBackups();
                    return false;
                }

                _originalHandles.Add(id, backup);
            }

            foreach (var id in CursorIds)
            {
                var blank = _api.CreateBlank();
                if (blank == IntPtr.Zero)
                {
                    Program.Log($"CreateCursor failed for id={id} error={_api.LastError}");
                    RestoreCore();
                    return false;
                }

                if (!_api.SetSystem(blank, id))
                {
                    Program.Log($"SetSystemCursor failed for id={id} error={_api.LastError}");
                    _api.Destroy(blank);
                    RestoreCore();
                    return false;
                }

                _replaced.Add(id);
                var shared = _api.Load(id);
                if (shared == IntPtr.Zero)
                {
                    Program.Log($"LoadCursor failed for id={id} error={_api.LastError}");
                    RestoreCore();
                    return false;
                }

                _blankHandles.Add(id, shared);
            }

            _installed = true;
            Program.Log($"CursorReplacer.Install count={_replaced.Count}");
            return true;
        }
    }

    internal IntPtr GetBlankHandle(uint cursorId)
    {
        lock (_sync)
        {
            return _blankHandles.TryGetValue(cursorId, out var handle) ? handle : IntPtr.Zero;
        }
    }

    internal void Restore()
    {
        lock (_sync)
        {
            RestoreCore();
        }
    }

    private void RestoreCore()
    {
        _installed = false;
        if (_replaced.Count == 0)
        {
            ReleaseBackups();
            return;
        }

        if (_api.ReloadSystemCursors())
        {
            _replaced.Clear();
            _blankHandles.Clear();
            ReleaseBackups();
            Program.Log("Restored system cursor scheme.");
            return;
        }

        Program.Log($"Reload system cursors failed error={_api.LastError}; restoring saved cursors.");
        foreach (var id in CursorIds)
        {
            if (!_replaced.Contains(id))
            {
                continue;
            }

            var copy = _api.Copy(_originalHandles[id]);
            if (copy == IntPtr.Zero)
            {
                Program.Log($"Copy saved cursor failed for id={id} error={_api.LastError}");
                continue;
            }

            if (!_api.SetSystem(copy, id))
            {
                Program.Log($"Restore saved cursor failed for id={id} error={_api.LastError}");
                _api.Destroy(copy);
                continue;
            }

            _replaced.Remove(id);
            _blankHandles.Remove(id);
        }

        ReleaseBackups();
        Program.Log($"Saved cursor restore pending={_replaced.Count}");
    }

    private void ReleaseBackups()
    {
        foreach (var id in CursorIds)
        {
            if (!_replaced.Contains(id) && _originalHandles.Remove(id, out var original))
            {
                _api.Destroy(original);
            }
        }
    }
}

internal interface ICursorApi
{
    int LastError { get; }
    IntPtr Load(uint cursorId);
    IntPtr Copy(IntPtr cursor);
    IntPtr CreateBlank();
    bool SetSystem(IntPtr cursor, uint cursorId);
    bool Destroy(IntPtr cursor);
    bool ReloadSystemCursors();
}

internal sealed class WindowsCursorApi : ICursorApi
{
    private static readonly byte[] AndMask = CreateAndMask();
    private static readonly byte[] XorMask = new byte[128];

    public int LastError => Marshal.GetLastWin32Error();

    public IntPtr Load(uint cursorId) => NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(cursorId));

    public IntPtr Copy(IntPtr cursor) => NativeMethods.CopyIcon(cursor);

    public IntPtr CreateBlank() => NativeMethods.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, AndMask, XorMask);

    public bool SetSystem(IntPtr cursor, uint cursorId) => NativeMethods.SetSystemCursor(cursor, cursorId);

    public bool Destroy(IntPtr cursor) => NativeMethods.DestroyCursor(cursor);

    public bool ReloadSystemCursors() => NativeMethods.SystemParametersInfo(0x0057, 0, IntPtr.Zero, 0x0002);

    private static byte[] CreateAndMask()
    {
        var mask = new byte[128];
        Array.Fill(mask, (byte)0xFF);
        return mask;
    }
}
