using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OsuCursorWin;

internal static class Program
{
    private static readonly string TemporaryRoot = Path.Combine(Path.GetTempPath(), $"OsuCursorWin.Regression.{Guid.NewGuid():N}");
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(TemporaryRoot);
        try
        {
            Run("Missing settings keep defaults", MissingSettings);
            Run("Invalid JSON recovers with defaults", InvalidSettings);
            Run("Older settings enable system cursor visibility tracking by default", LegacyVisibilitySettings);
            Run("Disabled system cursor visibility tracking survives saving", SaveVisibilitySettings);
            Run("Older settings retain the current rendering defaults", LegacyPerformanceSettings);
            Run("Performance choices survive saving and invalid limits are normalized", SavePerformanceSettings);
            Run("Animation limits respect the monitor refresh rate", AnimationFrameRateLimits);
            Run("Loaded dimensions and volumes are normalized", NormalizeSettings);
            Run("Save normalizes non-finite values", NormalizeNonFiniteSettings);
            Run("Saving replaces complete JSON and removes temporary files", SaveSettings);
            Run("Identical settings do not rewrite the file", DeduplicateSave);
            Run("Failed replacement preserves the previous settings", FailedSave);
            Run("Pending changes flush before the timer expires", FlushPendingSettings);
            Run("The debounce timer saves the latest values", DeferredSettings);
            Run("Startup commands quote the running executable", StartupExecutable);
            Run("Startup commands preserve a dotnet assembly argument", StartupAssembly);
            Run("Incomplete startup commands are rejected", InvalidStartup);
            Run("Enabling auto-start clears the Windows disabled state", EnableDisabledStartup);
            Run("Reading startup state respects Task Manager without changing it", ReadDisabledStartup);
            Run("Disabling auto-start removes only this application's registration", DisableStartup);
            Run("Legacy startup entries without approval remain enabled", LegacyStartup);
            Run("Invalid startup commands preserve existing registration", PreserveStartup);
            Run("Duplicate mouse events do not change state", DuplicateButtons);
            Run("Releasing one mouse button preserves the other", ConcurrentButtons);
            Run("Middle and extra mouse buttons remain independent", ExtraButtons);
            Run("Reset clears or replaces all pressed buttons", ResetButtons);
            Console.WriteLine($"Passed {_passed} regression checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(TemporaryRoot))
            {
                File.Delete(path);
            }

            Directory.Delete(TemporaryRoot);
        }
    }

    internal static void Log(string message)
    {
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static string NewPath() => Path.Combine(TemporaryRoot, $"{Guid.NewGuid():N}.json");

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
        }
    }

    private static void MissingSettings()
    {
        var path = NewPath();
        var settings = AppSettings.Load(path);
        Equal(30.0, settings.CursorWidth);
        Equal(1.0, settings.TapSoundVolume);
        Equal(1.0, settings.HoverSoundVolume);
        Equal(false, settings.AutoStart);
        Equal(true, settings.HideWhenCursorHidden);
        Equal(false, File.Exists(path));
    }

    private static void InvalidSettings()
    {
        foreach (var json in new[] { "{broken", "null", "[]" })
        {
            var path = NewPath();
            File.WriteAllText(path, json);
            var settings = AppSettings.Load(path);
            Equal(30.0, settings.CursorWidth);
            Equal(true, settings.TapSoundEnabled);
            Equal(json, File.ReadAllText(path));
        }
    }

    private static void NormalizeSettings()
    {
        var path = NewPath();
        File.WriteAllText(path, """{"CursorWidth":1000,"TapSoundVolume":-3,"HoverSoundVolume":20,"AutoStart":true}""");
        var settings = AppSettings.Load(path);
        Equal(64.0, settings.CursorWidth);
        Equal(0.0, settings.TapSoundVolume);
        Equal(1.0, settings.HoverSoundVolume);
        Equal(true, settings.AutoStart);
        File.WriteAllText(path, """{"CursorWidth":-5,"TapSoundVolume":0.24,"HoverSoundVolume":0.63}""");
        settings = AppSettings.Load(path);
        Equal(16.0, settings.CursorWidth);
        Equal(0.24, settings.TapSoundVolume);
        Equal(0.63, settings.HoverSoundVolume);
    }

    private static void LegacyVisibilitySettings()
    {
        var path = NewPath();
        File.WriteAllText(path, """{"CursorWidth":42,"TapSoundEnabled":false}""");
        var settings = AppSettings.Load(path);
        Equal(true, settings.HideWhenCursorHidden);
        Equal(42.0, settings.CursorWidth);
        Equal(false, settings.TapSoundEnabled);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Equal(false, json.RootElement.TryGetProperty("HideWhenCursorHidden", out _));
    }

    private static void SaveVisibilitySettings()
    {
        var path = NewPath();
        var settings = AppSettings.Load(path);
        settings.HideWhenCursorHidden = false;
        settings.Save();
        Equal(false, AppSettings.Load(path).HideWhenCursorHidden);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Equal(false, json.RootElement.GetProperty("HideWhenCursorHidden").GetBoolean());
    }

    private static void NormalizeNonFiniteSettings()
    {
        var path = NewPath();
        var settings = AppSettings.Load(path);
        settings.CursorWidth = double.NaN;
        settings.TapSoundVolume = double.PositiveInfinity;
        settings.HoverSoundVolume = double.NegativeInfinity;
        settings.Save();
        var saved = AppSettings.Load(path);
        Equal(30.0, saved.CursorWidth);
        Equal(1.0, saved.TapSoundVolume);
        Equal(1.0, saved.HoverSoundVolume);
    }

    private static void LegacyPerformanceSettings()
    {
        var path = NewPath();
        File.WriteAllText(path, """{"CursorWidth":42}""");
        var settings = AppSettings.Load(path);
        Equal(0, settings.AnimationFrameRateLimit);
        Equal(true, settings.HighQualityRendering);
        Equal(true, settings.PauseRenderingWhenIdle);
    }

    private static void SavePerformanceSettings()
    {
        var path = NewPath();
        var settings = AppSettings.Load(path);
        settings.AnimationFrameRateLimit = 90;
        settings.HighQualityRendering = false;
        settings.PauseRenderingWhenIdle = false;
        settings.Save();
        var saved = AppSettings.Load(path);
        Equal(90, saved.AnimationFrameRateLimit);
        Equal(false, saved.HighQualityRendering);
        Equal(false, saved.PauseRenderingWhenIdle);
        foreach (var (input, expected) in new[] { (-1, 0), (1, 30), (2000, 1000) })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { AnimationFrameRateLimit = input }));
            Equal(expected, AppSettings.Load(path).AnimationFrameRateLimit);
        }
    }

    private static void AnimationFrameRateLimits()
    {
        var settings = new AppSettings();
        Equal(165.0, settings.GetAnimationFrameRate(165));
        settings.AnimationFrameRateLimit = 60;
        Equal(60.0, settings.GetAnimationFrameRate(165));
        Equal(30.0, settings.GetAnimationFrameRate(30));
        Equal(60.0, settings.GetAnimationFrameRate(double.NaN));
        settings.AnimationFrameRateLimit = 480;
        Equal(165.0, settings.GetAnimationFrameRate(165));
        settings.AnimationFrameRateLimit = -1;
        Equal(165.0, settings.GetAnimationFrameRate(165));
    }

    private static void SaveSettings()
    {
        var path = NewPath();
        var settings = AppSettings.Load(path);
        settings.Save();
        settings.CursorWidth = 41;
        settings.TapSoundEnabled = false;
        settings.HoverSoundAsResizePrompt = true;
        settings.Save();
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Equal(41.0, json.RootElement.GetProperty("CursorWidth").GetDouble());
        Equal(false, json.RootElement.GetProperty("TapSoundEnabled").GetBoolean());
        Equal(true, json.RootElement.GetProperty("HoverSoundAsResizePrompt").GetBoolean());
        Equal(0, Directory.GetFiles(TemporaryRoot, "*.tmp").Length);
    }

    private static void DeduplicateSave()
    {
        var path = NewPath();
        var settings = AppSettings.Load(path);
        settings.Save();
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = File.GetLastWriteTimeUtc(path);
        settings.Save();
        Equal(before, File.GetLastWriteTimeUtc(path));
        File.Delete(path);
        settings.Save();
        Equal(true, File.Exists(path));
    }

    private static void FailedSave()
    {
        var path = NewPath();
        var settings = AppSettings.Load(path);
        settings.Save();
        var before = File.ReadAllText(path);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            settings.CursorWidth = 57;
            settings.Save();
            Equal(before, File.ReadAllText(path));
            Equal(0, Directory.GetFiles(TemporaryRoot, "*.tmp").Length);
        }

        settings.FlushPendingSave();
        Equal(57.0, AppSettings.Load(path).CursorWidth);
    }

    private static void FlushPendingSettings()
    {
        var path = NewPath();
        var settings = AppSettings.Load(path);
        settings.CursorWidth = 21;
        settings.ScheduleSave();
        settings.CursorWidth = 51;
        settings.ScheduleSave();
        Equal(false, File.Exists(path));
        settings.FlushPendingSave();
        Equal(51.0, AppSettings.Load(path).CursorWidth);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = File.GetLastWriteTimeUtc(path);
        settings.FlushPendingSave();
        Equal(before, File.GetLastWriteTimeUtc(path));
    }

    private static void DeferredSettings()
    {
        var path = NewPath();
        var settings = AppSettings.Load(path);
        settings.CursorWidth = 25;
        settings.ScheduleSave();
        settings.CursorWidth = 45;
        settings.ScheduleSave();
        Equal(false, File.Exists(path));
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            frame.Continue = false;
        };
        timeout.Start();
        Dispatcher.PushFrame(frame);
        Equal(true, File.Exists(path));
        Equal(45.0, AppSettings.Load(path).CursorWidth);
    }

    private static void StartupExecutable()
    {
        Equal("\"D:\\My Apps\\OsuCursorWin.exe\"", AutoStartManager.BuildStartupCommand(@"D:\My Apps\OsuCursorWin.exe", null));
    }

    private static void StartupAssembly()
    {
        Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"D:\\My Apps\\OsuCursorWin.dll\"", AutoStartManager.BuildStartupCommand(@"C:\Program Files\dotnet\dotnet.exe", @"D:\My Apps\OsuCursorWin.dll"));
        Equal("\"C:\\DOTNET.EXE\" \"D:\\cursor.dll\"", AutoStartManager.BuildStartupCommand(@"C:\DOTNET.EXE", @"D:\cursor.dll"));
        Equal($"\"C:\\dotnet.exe\" \"{Path.GetFullPath("OsuCursorWin.dll")}\"", AutoStartManager.BuildStartupCommand(@"C:\dotnet.exe", "OsuCursorWin.dll"));
    }

    private static void InvalidStartup()
    {
        Equal<string?>(null, AutoStartManager.BuildStartupCommand(null, null));
        Equal<string?>(null, AutoStartManager.BuildStartupCommand(" ", @"D:\cursor.dll"));
        Equal<string?>(null, AutoStartManager.BuildStartupCommand(@"C:\dotnet.exe", null));
        Equal<string?>(null, AutoStartManager.BuildStartupCommand(@"C:\dotnet.exe", " "));
    }

    private static void WithStartupRegistry(Action<RegistryKey> action)
    {
        var path = @"Software\OsuCursorWin.Regression\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            action(root);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    private static void EnableDisabledStartup() => WithStartupRegistry(root =>
    {
        using var run = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        using var approval = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        run.SetValue("OsuCursorWin", "old.exe");
        approval.SetValue("OsuCursorWin", new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 });
        Equal<bool?>(false, AutoStartManager.GetEnabled(root));
        Equal(true, AutoStartManager.Apply(true, root, @"C:\Program Files\OsuCursorWin\OsuCursorWin.exe", null));
        Equal<bool?>(true, AutoStartManager.GetEnabled(root));
        Equal("\"C:\\Program Files\\OsuCursorWin\\OsuCursorWin.exe\"", run.GetValue("OsuCursorWin"));
        var state = (byte[])approval.GetValue("OsuCursorWin")!;
        Equal(12, state.Length);
        Equal((byte)2, state[0]);
        Equal(true, state.Skip(1).All(value => value == 0));
    });

    private static void ReadDisabledStartup() => WithStartupRegistry(root =>
    {
        using var run = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        using var approval = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        run.SetValue("OsuCursorWin", "existing.exe");
        foreach (var status in new byte[] { 3, 7 })
        {
            var state = new byte[] { status, 0, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1 };
            approval.SetValue("OsuCursorWin", state);
            Equal<bool?>(false, AutoStartManager.GetEnabled(root));
            Equal(true, state.SequenceEqual((byte[])approval.GetValue("OsuCursorWin")!));
            Equal("existing.exe", run.GetValue("OsuCursorWin"));
        }
    });

    private static void DisableStartup() => WithStartupRegistry(root =>
    {
        Equal(true, AutoStartManager.Apply(true, root, @"C:\cursor.exe", null));
        using var run = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
        using var approval = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true)!;
        run.SetValue("OtherApplication", "other.exe");
        approval.SetValue("OtherApplication", new byte[] { 3, 0, 0, 0 });
        Equal(true, AutoStartManager.Apply(false, root, null, null));
        Equal<bool?>(false, AutoStartManager.GetEnabled(root));
        Equal<object?>(null, run.GetValue("OsuCursorWin"));
        Equal<object?>(null, approval.GetValue("OsuCursorWin"));
        Equal("other.exe", run.GetValue("OtherApplication"));
        Equal((byte)3, ((byte[])approval.GetValue("OtherApplication")!)[0]);
        Equal(true, AutoStartManager.Apply(false, root, null, null));
    });

    private static void LegacyStartup() => WithStartupRegistry(root =>
    {
        Equal<bool?>(false, AutoStartManager.GetEnabled(root));
        using var run = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        run.SetValue("OsuCursorWin", "legacy.exe");
        Equal<bool?>(true, AutoStartManager.GetEnabled(root));
        using var approval = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        approval.SetValue("OsuCursorWin", new byte[] { 6, 0, 0, 0 });
        Equal<bool?>(true, AutoStartManager.GetEnabled(root));
    });

    private static void PreserveStartup() => WithStartupRegistry(root =>
    {
        Equal(true, AutoStartManager.Apply(true, root, @"C:\cursor.exe", null));
        Equal(false, AutoStartManager.Apply(true, root, @"C:\dotnet.exe", null));
        Equal<bool?>(true, AutoStartManager.GetEnabled(root));
        using var run = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")!;
        Equal("\"C:\\cursor.exe\"", run.GetValue("OsuCursorWin"));
    });

    private static void DuplicateButtons()
    {
        var state = new MouseButtonState();
        Equal(false, state.SetButton(CursorButtons.Left, false));
        Equal(true, state.SetButton(CursorButtons.Left, true));
        Equal(false, state.SetButton(CursorButtons.Left, true));
        Equal(true, state.SetButton(CursorButtons.Left, false));
        Equal(false, state.SetButton(CursorButtons.Left, false));
        Equal(false, state.IsPressed);
    }

    private static void ConcurrentButtons()
    {
        var state = new MouseButtonState();
        state.SetButton(CursorButtons.Left, true);
        state.SetButton(CursorButtons.Right, true);
        Equal(CursorButtons.Left | CursorButtons.Right, state.Buttons);
        state.SetButton(CursorButtons.Left, false);
        Equal(CursorButtons.Right, state.Buttons);
        Equal(true, state.IsPressed);
        state.SetButton(CursorButtons.Right, false);
        Equal(false, state.IsPressed);
    }

    private static void ExtraButtons()
    {
        var state = new MouseButtonState();
        state.SetButton(CursorButtons.Middle, true);
        state.SetButton(CursorButtons.X1, true);
        state.SetButton(CursorButtons.X2, true);
        Equal(CursorButtons.Middle | CursorButtons.X1 | CursorButtons.X2, state.Buttons);
        state.SetButton(CursorButtons.X1, false);
        Equal(CursorButtons.Middle | CursorButtons.X2, state.Buttons);
        state.SetButton(CursorButtons.Middle, false);
        Equal(CursorButtons.X2, state.Buttons);
        state.SetButton(CursorButtons.X2, false);
        Equal(false, state.IsPressed);
    }

    private static void ResetButtons()
    {
        var state = new MouseButtonState();
        state.SetButton(CursorButtons.Left, true);
        state.SetButton(CursorButtons.X2, true);
        state.Reset();
        Equal(CursorButtons.None, state.Buttons);
        Equal(false, state.IsPressed);
        state.Reset(CursorButtons.Right | CursorButtons.Middle);
        Equal(CursorButtons.Right | CursorButtons.Middle, state.Buttons);
        Equal(true, state.IsPressed);
    }
}
