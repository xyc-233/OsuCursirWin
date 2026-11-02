using OsuCursorWin;

var cases = new (string Name, Action Run)[]
{
    ("Restore without installation has no system side effects", RestoreWithoutInstall),
    ("Successful installation keeps valid shared handles and releases backups", InstallAndRestore),
    ("Failed backup never replaces cursors", BackupFailure),
    ("Partial creation failure rolls back", CreationFailure),
    ("First replacement failure releases resources", FirstReplacementFailure),
    ("Partial replacement failure restores original backup contents", PartialReplacementFailure),
    ("Shared handle lookup failure rolls back", SharedHandleFailure),
    ("Failed fallback restoration can retry", RestoreRetry),
    ("Reinstallation waits until outstanding restoration succeeds", InstallAfterFailedRestore)
};

foreach (var test in cases)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

Console.WriteLine($"{cases.Length} cursor regression checks passed.");

static void RestoreWithoutInstall()
{
    var api = new FakeCursorApi();
    var replacement = new CursorReplacement(api);
    replacement.Restore();
    Check(api.ReloadCalls == 0 && api.SetCalls == 0, "Restore mutated system without an installation.");
}

static void InstallAndRestore()
{
    var api = new FakeCursorApi();
    var replacement = new CursorReplacement(api);
    Check(replacement.Install(), "Installation failed.");
    var normal = replacement.GetBlankHandle(NativeMethods.OCR_NORMAL);
    Check(normal == api.Load(NativeMethods.OCR_NORMAL), "Replacement exposed an invalid consumed handle.");
    Check(api.SetCalls == 14 && api.Owned.Count == 14, "Cursor ownership was not transferred correctly.");
    Check(replacement.Install() && api.SetCalls == 14, "Repeated installation changed the system.");
    replacement.Restore();
    Check(api.AllOriginal && api.Owned.Count == 0, "Restore left cursors or owned handles behind.");
    Check(replacement.GetBlankHandle(NativeMethods.OCR_NORMAL) == IntPtr.Zero, "Restore retained a blank handle.");
    replacement.Restore();
    Check(api.ReloadCalls == 1, "Repeated restore changed the system.");
    Check(replacement.Install(), "Installation after restoration failed.");
    replacement.Restore();
    Check(api.AllOriginal && api.Owned.Count == 0, "Reinstallation leaked resources.");
}

static void BackupFailure()
{
    var api = new FakeCursorApi { FailCopyCall = 3 };
    var replacement = new CursorReplacement(api);
    Check(!replacement.Install(), "Backup failure was ignored.");
    Check(api.SetCalls == 0 && api.ReloadCalls == 0 && api.Owned.Count == 0, "Failed backup modified cursors or leaked resources.");
}

static void CreationFailure()
{
    var api = new FakeCursorApi { FailCreateCall = 3 };
    var replacement = new CursorReplacement(api);
    Check(!replacement.Install(), "Creation failure was ignored.");
    Check(api.AllOriginal && api.ReloadCalls == 1 && api.Owned.Count == 0, "Creation failure did not roll back fully.");
}

static void FirstReplacementFailure()
{
    var api = new FakeCursorApi();
    api.FailSetCalls.Add(1);
    var replacement = new CursorReplacement(api);
    Check(!replacement.Install(), "First replacement failure was ignored.");
    Check(api.AllOriginal && api.ReloadCalls == 0 && api.Owned.Count == 0, "First replacement failure leaked resources.");
}

static void PartialReplacementFailure()
{
    var api = new FakeCursorApi { ReloadSucceeds = false };
    api.FailSetCalls.Add(3);
    var replacement = new CursorReplacement(api);
    Check(!replacement.Install(), "Partial replacement failure was ignored.");
    Check(api.AllOriginal && api.ReloadCalls == 1 && api.Owned.Count == 0, "Fallback did not restore saved original contents.");
}

static void SharedHandleFailure()
{
    var api = new FakeCursorApi { FailLoadCall = 16 };
    var replacement = new CursorReplacement(api);
    Check(!replacement.Install(), "Post-replacement lookup failure was ignored.");
    Check(api.AllOriginal && api.ReloadCalls == 1 && api.Owned.Count == 0, "Lookup failure did not roll back.");
}

static void RestoreRetry()
{
    var api = new FakeCursorApi { ReloadSucceeds = false };
    var replacement = new CursorReplacement(api);
    Check(replacement.Install(), "Installation failed.");
    api.FailSetCalls.Add(15);
    replacement.Restore();
    Check(!api.AllOriginal && api.Owned.Count == 1, "Failed restoration discarded its backup.");
    replacement.Restore();
    Check(api.AllOriginal && api.Owned.Count == 0, "Retry failed to restore the remaining cursor.");
}

static void InstallAfterFailedRestore()
{
    var api = new FakeCursorApi { ReloadSucceeds = false };
    var replacement = new CursorReplacement(api);
    Check(replacement.Install(), "Installation failed.");
    api.FailSetCalls.Add(15);
    replacement.Restore();
    api.FailSetCalls.Add(29);
    Check(!replacement.Install() && api.CreateCalls == 14, "Reinstallation replaced an unrestored cursor.");
    Check(replacement.Install(), "Reinstallation failed after restoring the pending cursor.");
    replacement.Restore();
    Check(api.AllOriginal && api.Owned.Count == 0, "Reinstallation lost the original cursor contents.");
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeCursorApi : ICursorApi
{
    private readonly Dictionary<uint, string> _system = new();
    private int _nextHandle = 100_000;
    private int _copyCalls;
    private int _loadCalls;

    public Dictionary<IntPtr, string> Owned { get; } = new();
    public HashSet<int> FailSetCalls { get; } = new();
    public int LastError => 5;
    public int SetCalls { get; private set; }
    public int ReloadCalls { get; private set; }
    public int CreateCalls { get; private set; }
    public int FailCopyCall { get; init; }
    public int FailCreateCall { get; init; }
    public int FailLoadCall { get; init; }
    public bool ReloadSucceeds { get; init; } = true;
    public bool AllOriginal => _system.All(pair => pair.Value == $"original:{pair.Key}");

    public IntPtr Load(uint cursorId)
    {
        _loadCalls++;
        if (_loadCalls == FailLoadCall)
        {
            return IntPtr.Zero;
        }

        _system.TryAdd(cursorId, $"original:{cursorId}");
        return new IntPtr(cursorId);
    }

    public IntPtr Copy(IntPtr cursor)
    {
        _copyCalls++;
        if (_copyCalls == FailCopyCall)
        {
            return IntPtr.Zero;
        }

        var content = Owned.TryGetValue(cursor, out var owned) ? owned : _system[(uint)cursor.ToInt64()];
        return Allocate(content);
    }

    public IntPtr CreateBlank()
    {
        CreateCalls++;
        return CreateCalls == FailCreateCall ? IntPtr.Zero : Allocate("blank");
    }

    public bool SetSystem(IntPtr cursor, uint cursorId)
    {
        SetCalls++;
        if (!Owned.ContainsKey(cursor))
        {
            throw new InvalidOperationException("SetSystem received an unowned or consumed cursor.");
        }

        if (FailSetCalls.Contains(SetCalls))
        {
            return false;
        }

        _system[cursorId] = Owned[cursor];
        Owned.Remove(cursor);
        return true;
    }

    public bool Destroy(IntPtr cursor)
    {
        if (!Owned.Remove(cursor))
        {
            throw new InvalidOperationException("Destroyed an unowned or consumed cursor.");
        }

        return true;
    }

    public bool ReloadSystemCursors()
    {
        ReloadCalls++;
        if (ReloadSucceeds)
        {
            foreach (var id in _system.Keys.ToArray())
            {
                _system[id] = $"original:{id}";
            }
        }

        return ReloadSucceeds;
    }

    private IntPtr Allocate(string content)
    {
        var handle = new IntPtr(_nextHandle++);
        Owned.Add(handle, content);
        return handle;
    }
}

namespace OsuCursorWin
{
    internal static class Program
    {
        internal static void Log(string message)
        {
        }
    }
}
