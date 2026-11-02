using System;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace OsuCursorWin;

internal sealed class AppSettings
{
    private const double MinCursorWidth = 16.0;
    private const double MaxCursorWidth = 64.0;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private string _settingsPath = SettingsPath;
    private string? _lastSavedJson;
    private DispatcherTimer? _saveTimer;
    private bool _savePending;

    public double CursorWidth { get; set; } = 30.0;
    public bool HideWhenCursorHidden { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool TapSoundEnabled { get; set; } = true;
    public double TapSoundVolume { get; set; } = 1.0;
    public bool HoverSoundEnabled { get; set; } = true;
    public double HoverSoundVolume { get; set; } = 1.0;
    public bool HoverSoundAsResizePrompt { get; set; }
    public int AnimationFrameRateLimit { get; set; }
    public bool HighQualityRendering { get; set; } = true;
    public bool PauseRenderingWhenIdle { get; set; } = true;

    internal static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OsuCursorWin",
            "settings.json");

    internal static bool Exists => File.Exists(SettingsPath);

    internal static AppSettings Load() => Load(SettingsPath);

    internal double GetAnimationFrameRate(double monitorRefreshRate)
    {
        var refreshRate = double.IsFinite(monitorRefreshRate) && monitorRefreshRate > 1.0
            ? Math.Min(monitorRefreshRate, 1000.0) : 60.0;
        var limit = NormalizeAnimationFrameRateLimit(AnimationFrameRateLimit);
        return limit == 0 ? refreshRate : Math.Min(refreshRate, limit);
    }

    internal static int NormalizeAnimationFrameRateLimit(int value) => value <= 0 ? 0 : Math.Clamp(value, 30, 1000);

    internal static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (settings is not null)
                {
                    settings._settingsPath = path;
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Program.Log($"Failed to load settings: {ex}");
        }

        return new AppSettings { _settingsPath = path };
    }

    internal void ScheduleSave()
    {
        _savePending = true;
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _saveTimer.Tick += (_, _) => Save();
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    internal void FlushPendingSave()
    {
        if (_savePending)
        {
            Save();
        }
    }

    internal void Save()
    {
        _saveTimer?.Stop();
        _savePending = true;
        string? temporaryPath = null;
        try
        {
            Normalize();
            var json = JsonSerializer.Serialize(this, SerializerOptions);
            if (json == _lastSavedJson && File.Exists(_settingsPath))
            {
                _savePending = false;
                return;
            }

            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            _lastSavedJson = json;
            _savePending = false;
        }
        catch (Exception ex)
        {
            Program.Log($"Failed to save settings: {ex}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    Program.Log($"Failed to remove temporary settings file: {ex}");
                }
            }
        }
    }

    private void Normalize()
    {
        CursorWidth = NormalizeValue(CursorWidth, MinCursorWidth, MaxCursorWidth, 30.0);
        TapSoundVolume = NormalizeValue(TapSoundVolume, 0.0, 1.0, 1.0);
        HoverSoundVolume = NormalizeValue(HoverSoundVolume, 0.0, 1.0, 1.0);
        AnimationFrameRateLimit = NormalizeAnimationFrameRateLimit(AnimationFrameRateLimit);
    }

    private static double NormalizeValue(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
