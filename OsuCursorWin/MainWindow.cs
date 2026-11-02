using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace OsuCursorWin;

internal sealed class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CursorVisual _cursorVisual;
    private readonly NotifyIcon _trayIcon;
    private readonly TapSoundPlayer _tapSoundPlayer;
    private readonly TapSoundPlayer _hoverSoundPlayer;
    private SettingsWindow? _settingsWindow;
    private ToolStripMenuItem? _cursorToggleItem;
    private bool _cursorEnabled = true;
    private bool _systemCursorVisible = true;
    private readonly Func<NativeMethods.CURSORINFO?> _cursorInfoProvider;
    private readonly Func<NativeMethods.CURSORINFO, ResizeDirection>? _resizeDirectionProvider;
    private readonly ResizeDirectionSampler _resizeSampler;
    private ResizeSample? _resizeSample;
    private int _resizeGeneration;
    private NativeMethods.POINT _resizeSamplePoint;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _topmostTimer;
    private DispatcherTimer? _fallbackTimer;
    private bool _renderingSubscribed;
    private bool _updatingFrame;
    private int _coordinateRevision;
    private readonly bool _showInitialSettings;
    private readonly bool _useNativeOverlay;
    private NativeCursorOverlay? _nativeOverlay;
    private readonly NativeFrameClock? _frameClock;
    private readonly DisplayFramePacer _displayRefresh = new();

    private NativeMethods.POINT _cursorPoint;
    private IntPtr _hwnd;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private double _lastFrameTime;
    private double _lastMotionTime = double.NegativeInfinity;
    private TimeSpan? _lastRenderingTime;
    private bool _wasHoverCandidate;
    private bool _wasResizePrompt;
    private double _lastHoverSoundTime = double.NegativeInfinity;
    private bool _closing;
    private bool _forceTopmost = true;
    private IntPtr _lastCursorHandle;
    private int _lastWindowX = int.MinValue;
    private int _lastWindowY = int.MinValue;
    private int _lastWindowWidth;
    private int _lastWindowHeight;
    private MouseInputSource? _mouseInput;
    private CursorDimensions _dimensions;
    private readonly bool _smoke;
    private bool _loaded;

    internal CursorModel Model { get; } = new();
    internal bool IsRendering => _renderingSubscribed;
    internal IntPtr OverlayHandle => _hwnd;
    internal event Action? FrameRendering;
    internal event Action? FrameRendered;
    private bool CanShowOverlay => !_closing && _cursorEnabled
        && (!_settings.HideWhenCursorHidden || _systemCursorVisible);

    internal MainWindow(bool smoke = false, AppSettings? settings = null, bool showSettings = false,
        Func<NativeMethods.CURSORINFO?>? cursorInfoProvider = null,
        Func<NativeMethods.CURSORINFO, ResizeDirection>? resizeDirectionProvider = null, bool nativeOverlay = true)
    {
        _smoke = smoke;
        _useNativeOverlay = nativeOverlay;
        if (nativeOverlay)
        {
            _frameClock = new NativeFrameClock(Dispatcher, () => OnRendering(this, EventArgs.Empty));
        }
        _cursorInfoProvider = cursorInfoProvider ?? ReadSystemCursorInfo;
        _resizeDirectionProvider = resizeDirectionProvider;
        var resizeDetector = new ResizeTargetDetector();
        _resizeSampler = new ResizeDirectionSampler(sample => resizeDetector.GetDirection(
            new NativeMethods.POINT { X = sample.X, Y = sample.Y }, sample.OverlayWindow, sample.Axis),
            OnResizeSampleCompleted);
        _showInitialSettings = showSettings || (settings is null && !AppSettings.Exists);
        _settings = settings ?? AppSettings.Load();
        _dimensions = new CursorDimensions(_settings.CursorWidth);

        Width = nativeOverlay ? 1 : _dimensions.WindowSize;
        Height = nativeOverlay ? 1 : _dimensions.WindowSize;
        Left = 0;
        Top = 0;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        _cursorVisual = new CursorVisual(_dimensions);
        Content = nativeOverlay ? null : _cursorVisual;

        _tapSoundPlayer = new TapSoundPlayer(LoadResourceBytes("OsuCursorWin.Audio.cursorTap.wav"))
        {
            Enabled = !smoke && _settings.TapSoundEnabled
        };
        _hoverSoundPlayer = new TapSoundPlayer(LoadResourceBytes("OsuCursorWin.Audio.defaultHover.wav"))
        {
            Enabled = !smoke && _settings.HoverSoundEnabled
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        DpiChanged += (_, _) => UpdateCoordinateSystem();

        _trayIcon = CreateTrayIcon();

        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _topmostTimer.Tick += (_, _) =>
        {
            _frameClock?.SetRefreshRate(GetAnimationFrameRate());
            if (_mouseInput is { IsRunning: false })
            {
                UninstallMouseHook();
                InstallMouseHook();
            }
            RefreshSystemCursorVisibility();
            _forceTopmost = true;
            if (_hwnd != IntPtr.Zero && CanShowOverlay)
            {
                RequestRender();
                TryBringAboveTaskbarPreview();
            }
        };
        _topmostTimer.Start();

        if (smoke)
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2500)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Close();
            };
            timer.Start();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetClickThrough(_hwnd);
        if (_useNativeOverlay)
        {
            _nativeOverlay = new NativeCursorOverlay();
            _nativeOverlay.SetHighQualityRendering(_settings.HighQualityRendering);
            _hwnd = _nativeOverlay.Handle;
        }
        UpdateCoordinateSystem();
        _forceTopmost = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        UpdateCoordinateSystem();

        if (!_smoke)
        {
            if (!CursorReplacer.Install())
            {
                CursorReplacer.Restore();
                Program.Log("Unable to install system cursor replacement.");
                MessageBox.Show(
                    "无法替换系统光标，程序将退出。",
                    "osu! Cursor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                System.Windows.Application.Current.Shutdown(1);
                return;
            }
        }

        _forceTopmost = true;
        ResetInputState();
        if (!_smoke)
        {
            InstallMouseHook();
        }
        RefreshSystemCursorVisibility();
        ApplyOverlayVisibility();
        RequestRender();

        if (!_smoke)
        {
            RefreshAutoStart();
        }

        if (!_smoke && _showInitialSettings)
        {
            ShowSettingsWindow();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        CursorReplacer.Restore();
        _settingsWindow?.ForceClose();
        _settings.FlushPendingSave();
        StopRendering();
        UninstallMouseHook();
        _resizeSampler.Dispose();
        _nativeOverlay?.Dispose();
        _frameClock?.Dispose();
        _topmostTimer.Stop();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Icon?.Dispose();
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
        }

        _tapSoundPlayer.Dispose();
        _hoverSoundPlayer.Dispose();
    }

    private void UpdateCoordinateSystem()
    {
        var dpi = _nativeOverlay is null ? 0 : NativeMethods.GetDpiForWindow(_hwnd);
        var visualDpi = VisualTreeHelper.GetDpi(this);
        _dpiScaleX = dpi > 0 ? dpi / 96.0 : visualDpi.DpiScaleX;
        _dpiScaleY = dpi > 0 ? dpi / 96.0 : visualDpi.DpiScaleY;
        _coordinateRevision++;
        _forceTopmost = true;
        RequestRender();
    }

    private void InstallMouseHook()
    {
        if (_mouseInput is not null)
        {
            return;
        }

        var source = new MouseInputSource(Dispatcher, ProcessMouseInput);
        if (source.Start())
        {
            _mouseInput = source;
            ResetInputState(synchronizeButtons: true);
            _fallbackTimer?.Stop();
        }
        else
        {
            source.Dispose();
            Program.Log("Mouse hook unavailable; using cursor polling.");
            _fallbackTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(8)
            };
            _fallbackTimer.Tick -= OnFallbackTick;
            _fallbackTimer.Tick += OnFallbackTick;
            _fallbackTimer.Start();
        }
    }

    private void UninstallMouseHook()
    {
        _mouseInput?.Dispose();
        _mouseInput = null;
        _fallbackTimer?.Stop();
    }

    private void OnFallbackTick(object? sender, EventArgs e)
    {
        RequestRender();
    }

    internal void ProcessMouseInput(IReadOnlyList<MouseInputEvent> events)
    {
        if (!_cursorEnabled || _closing || events.Count == 0)
        {
            return;
        }

        var wasVisible = CanShowOverlay;
        RefreshSystemCursorVisibility();
        if (!CanShowOverlay)
        {
            return;
        }
        if (!wasVisible)
        {
            UpdateWindowPosition();
            RequestRender();
            return;
        }

        foreach (var data in events)
        {
            UpdateCursorPosition(new NativeMethods.POINT { X = data.X, Y = data.Y });
            switch (data.Message)
            {
                case NativeMethods.WmLButtonDown:
                    HandleButtonTransition(CursorButtons.Left, true);
                    break;
                case NativeMethods.WmRButtonDown:
                    HandleButtonTransition(CursorButtons.Right, true);
                    break;
                case NativeMethods.WmMButtonDown:
                    HandleButtonTransition(CursorButtons.Middle, true);
                    break;
                case NativeMethods.WmXButtonDown:
                    HandleButtonTransition((data.MouseData >> 16) == 1 ? CursorButtons.X1 : CursorButtons.X2, true);
                    break;
                case NativeMethods.WmLButtonUp:
                    HandleButtonTransition(CursorButtons.Left, false);
                    break;
                case NativeMethods.WmRButtonUp:
                    HandleButtonTransition(CursorButtons.Right, false);
                    break;
                case NativeMethods.WmMButtonUp:
                    HandleButtonTransition(CursorButtons.Middle, false);
                    break;
                case NativeMethods.WmXButtonUp:
                    HandleButtonTransition((data.MouseData >> 16) == 1 ? CursorButtons.X1 : CursorButtons.X2, false);
                    break;
            }
        }

        if (_nativeOverlay is not null)
        {
            UpdateWindowPosition();
        }
        RequestRender();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!CanShowOverlay || _hwnd == IntPtr.Zero || _updatingFrame)
        {
            return;
        }

        if (e is RenderingEventArgs rendering)
        {
            if (_lastRenderingTime == rendering.RenderingTime)
            {
                return;
            }
        }

        _updatingFrame = true;
        try
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (e is RenderingEventArgs acceptedFrame)
            {
                _lastRenderingTime = acceptedFrame.RenderingTime;
            }
            var elapsed = now - _lastFrameTime;
            _lastFrameTime = now;
            FrameRendering?.Invoke();
            UpdateMouseState();
            if (!CanShowOverlay)
            {
                return;
            }
            var settled = Model.Advance(elapsed);
            RenderCursor();
            FrameRendered?.Invoke();
            if (_settings.PauseRenderingWhenIdle && settled && !_forceTopmost && now - _lastMotionTime >= 0.12)
            {
                StopRendering();
            }
        }
        finally
        {
            _updatingFrame = false;
        }
    }

    internal void UpdateMouseState()
    {
        if (IsKeyPressed(0x5B) || IsKeyPressed(0x5C))
        {
            _forceTopmost = true;
        }

        var hasCursorInfo = TryReadCursorInfo(out var info);
        if (hasCursorInfo)
        {
            UpdateSystemCursorVisibility(info);
            if (_mouseInput is null)
            {
                UpdateCursorPosition(info.ptScreenPos);
            }
        }

        if (!CanShowOverlay)
        {
            return;
        }

        if (hasCursorInfo)
        {
            UpdateResizeDirection(info);
            _resizeSamplePoint = info.ptScreenPos;
        }

        if (!_smoke && _mouseInput is null)
        {
            HandleButtonChanges(Model.SetButtons(ReadMouseButtons()));
        }

        if (hasCursorInfo)
        {
            UpdatePointerState(info);
        }
    }

    private void UpdateCursorPosition(NativeMethods.POINT point)
    {
        if (_cursorPoint.X != point.X || _cursorPoint.Y != point.Y)
        {
            _lastMotionTime = _clock.Elapsed.TotalSeconds;
        }
        _cursorPoint = point;
        Model.MoveTo(point.X, point.Y, _dimensions.Width * _dpiScaleX);
    }

    private void UpdatePointerState(NativeMethods.CURSORINFO info)
    {
        var handHandle = CursorReplacer.GetBlankHandle(NativeMethods.OCR_HAND);

        if (info.hCursor != _lastCursorHandle)
        {
            _lastCursorHandle = info.hCursor;
            _forceTopmost = true;
        }

        Model.IsHovering = info.hCursor != IntPtr.Zero
            && info.hCursor == handHandle;

        if (_settings.HoverSoundAsResizePrompt)
        {
            var resize = IsResizeCursor(info.hCursor);
            if (resize && !_wasResizePrompt && !Model.IsPressed)
            {
                PlayHoverSample();
            }

            _wasResizePrompt = resize;
        }
        else
        {
            var isHoverCandidate = Model.IsHovering;

            if (isHoverCandidate && !_wasHoverCandidate && !Model.IsPressed)
            {
                PlayHoverSample();
            }

            _wasHoverCandidate = isHoverCandidate;
        }
    }

    internal void HandleButtonTransition(CursorButtons button, bool pressed)
    {
        if (pressed && !Model.IsPressed
            && (Model.Position.X != _resizeSamplePoint.X || Model.Position.Y != _resizeSamplePoint.Y))
        {
            Model.ResizeDirection = ResizeDirection.None;
            ResetResizeSampling();
        }
        HandleButtonChanges(Model.SetButton(button, pressed));
    }

    private void HandleButtonChanges(ButtonChanges changes)
    {
        if (!changes.Any)
        {
            return;
        }

        var pressed = (uint)changes.Pressed;
        while (pressed != 0)
        {
            PlayTapSample(1.0);
            pressed &= pressed - 1;
        }
        var released = (uint)changes.Released;
        while (released != 0)
        {
            PlayTapSample(0.8);
            released &= released - 1;
        }
        _forceTopmost = true;
    }

    private void UpdateWindowPosition(bool refreshDpi = true)
    {
        if (!CanShowOverlay)
        {
            return;
        }

        var windowWidth = Math.Max(1, (int)Math.Ceiling(_dimensions.WindowSize * _dpiScaleX));
        var windowHeight = Math.Max(1, (int)Math.Ceiling(_dimensions.WindowSize * _dpiScaleY));
        var x = _cursorPoint.X - (int)Math.Round(_dimensions.Margin * _dpiScaleX);
        var y = _cursorPoint.Y - (int)Math.Round(_dimensions.Margin * _dpiScaleY);
        var revision = _coordinateRevision;

        if (_hwnd != IntPtr.Zero
            && (_forceTopmost
                || x != _lastWindowX
                || y != _lastWindowY
                || windowWidth != _lastWindowWidth
                || windowHeight != _lastWindowHeight))
        {
            var fullUpdate = _forceTopmost || windowWidth != _lastWindowWidth || windowHeight != _lastWindowHeight;
            var moved = fullUpdate
                ? NativeMethods.MoveTopmost(_hwnd, x, y, windowWidth, windowHeight)
                : NativeMethods.MoveOverlay(_hwnd, x, y);
            if (!moved)
            {
                return;
            }
            _lastWindowX = x;
            _lastWindowY = y;
            _lastWindowWidth = windowWidth;
            _lastWindowHeight = windowHeight;
            _forceTopmost = revision != _coordinateRevision;
            if (_nativeOverlay is not null)
            {
                var dpi = NativeMethods.GetDpiForWindow(_hwnd);
                if (dpi > 0 && Math.Abs(_dpiScaleX - dpi / 96.0) > 0.001)
                {
                    UpdateCoordinateSystem();
                    if (refreshDpi)
                    {
                        UpdateWindowPosition(refreshDpi: false);
                    }
                }
            }
        }
    }

    private void RenderCursor()
    {
        if (_nativeOverlay is null)
        {
            _cursorVisual.Render(Model);
            UpdateWindowPosition();
            return;
        }

        UpdateWindowPosition();
        _nativeOverlay.Render(Model, _dimensions, _dpiScaleX, _dpiScaleY, _lastWindowX, _lastWindowY);
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => ShowSettingsWindow();
        menu.Items.Add(settingsItem);

        _cursorToggleItem = new ToolStripMenuItem("关闭光标");
        _cursorToggleItem.Click += (_, _) => SetCursorEnabled(!_cursorEnabled);
        menu.Items.Add(_cursorToggleItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _settingsWindow?.ForceClose();
            Close();
        };
        menu.Items.Add(exitItem);

        return new NotifyIcon
        {
            Icon = ProgramIcon.CreateIcon(),
            Text = "osu! Cursor",
            Visible = !_smoke,
            ContextMenuStrip = menu
        };
    }

    private void ShowSettingsWindow()
    {
        if (_smoke)
        {
            return;
        }

        RefreshAutoStart();
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.CursorSizeChanged += ApplyCursorDimensions;
            _settingsWindow.AutoStartChanged += ApplyAutoStart;
            _settingsWindow.TapSoundChanged += ApplyTapSound;
            _settingsWindow.TapSoundVolumeChanged += ApplyTapSoundVolume;
            _settingsWindow.HoverSoundChanged += ApplyHoverSound;
            _settingsWindow.HoverSoundVolumeChanged += ApplyHoverSoundVolume;
            _settingsWindow.ResizeSoundModeChanged += ApplyHoverSoundMode;
            _settingsWindow.HideWhenCursorHiddenChanged += ApplyHideWhenCursorHidden;
            _settingsWindow.AnimationFrameRateLimitChanged += ApplyAnimationFrameRateLimit;
            _settingsWindow.HighQualityRenderingChanged += ApplyHighQualityRendering;
            _settingsWindow.PauseRenderingWhenIdleChanged += ApplyPauseRenderingWhenIdle;
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    internal void ApplyCursorDimensions(double width)
    {
        _coordinateRevision++;
        _dimensions = new CursorDimensions(width);
        _cursorVisual.Resize(_dimensions);
        if (!_useNativeOverlay)
        {
            Width = Height = _dimensions.WindowSize;
        }
        _settings.CursorWidth = _dimensions.Width;
        _settings.ScheduleSave();
        _forceTopmost = true;
        RequestRender();
    }

    internal void ApplyAnimationFrameRateLimit(int limit)
    {
        if (_closing) return;
        _settings.AnimationFrameRateLimit = AppSettings.NormalizeAnimationFrameRateLimit(limit);
        _frameClock?.SetRefreshRate(GetAnimationFrameRate());
        RequestRender();
        _settings.ScheduleSave();
    }

    internal void ApplyHighQualityRendering(bool enabled)
    {
        if (_closing) return;
        _settings.HighQualityRendering = enabled;
        _nativeOverlay?.SetHighQualityRendering(enabled);
        RequestRender();
        _settings.ScheduleSave();
    }

    internal void ApplyPauseRenderingWhenIdle(bool enabled)
    {
        if (_closing) return;
        _settings.PauseRenderingWhenIdle = enabled;
        RequestRender();
        _settings.ScheduleSave();
    }

    internal void SetCursorEnabled(bool enabled)
    {
        if (_closing || _cursorEnabled == enabled)
        {
            return;
        }

        _cursorEnabled = enabled;
        if (_cursorToggleItem is not null)
        {
            _cursorToggleItem.Text = enabled ? "关闭光标" : "启用光标";
        }

        if (enabled)
        {
            if (!_smoke && !CursorReplacer.Install())
            {
                _cursorEnabled = false;
                if (_cursorToggleItem is not null)
                {
                    _cursorToggleItem.Text = "启用光标";
                }

                Program.Log("Failed to re-enable cursor replacement.");
                return;
            }

            _forceTopmost = true;
            ResetInputState(synchronizeButtons: true);
            if (!_smoke)
            {
                InstallMouseHook();
            }
            RefreshSystemCursorVisibility();
            ApplyOverlayVisibility();
            _topmostTimer.Start();
        }
        else
        {
            StopRendering();
            UninstallMouseHook();
            ResetResizeSampling();
            _topmostTimer.Stop();
            CursorReplacer.Restore();
            if (_nativeOverlay is not null)
            {
                _nativeOverlay.SetVisible(false);
            }
            else
            {
                Hide();
            }
            UpdateSoundAvailability();
        }
    }

    internal void ApplyHideWhenCursorHidden(bool enabled)
    {
        _settings.HideWhenCursorHidden = enabled;
        RefreshSystemCursorVisibility();
        ApplyOverlayVisibility();
        _settings.Save();
    }

    internal void RefreshSystemCursorVisibility()
    {
        if (!_closing && TryReadCursorInfo(out var info))
        {
            UpdateSystemCursorVisibility(info);
        }
    }

    private void UpdateSystemCursorVisibility(NativeMethods.CURSORINFO info)
    {
        var visible = (info.flags & NativeMethods.CursorShowing) != 0
            && (info.flags & NativeMethods.CursorSuppressed) == 0;
        if (_systemCursorVisible == visible)
        {
            return;
        }

        _systemCursorVisible = visible;
        ApplyOverlayVisibility();
    }

    private void ApplyOverlayVisibility()
    {
        if (_closing || !_loaded || _hwnd == IntPtr.Zero)
        {
            return;
        }

        UpdateSoundAvailability();
        if (_nativeOverlay is not null)
        {
            if (!CanShowOverlay)
            {
                StopRendering();
                if (_nativeOverlay.IsVisible)
                {
                    ResetInputState();
                    _nativeOverlay.SetVisible(false);
                }
                return;
            }
            if (!_nativeOverlay.IsVisible)
            {
                ResetInputState(synchronizeButtons: true);
                _forceTopmost = true;
                RenderCursor();
                _nativeOverlay.SetVisible(true);
            }
            RequestRender();
            return;
        }
        if (!CanShowOverlay)
        {
            StopRendering();
            if (IsVisible)
            {
                ResetInputState();
                Hide();
            }
            return;
        }

        if (!IsVisible)
        {
            ResetInputState(synchronizeButtons: true);
            _cursorVisual.Render(Model);
            _forceTopmost = true;
            UpdateWindowPosition();
            Show();
        }
        RequestRender();
    }

    private void UpdateSoundAvailability()
    {
        _tapSoundPlayer.Enabled = !_smoke && CanShowOverlay && _settings.TapSoundEnabled;
        _hoverSoundPlayer.Enabled = !_smoke && CanShowOverlay && _settings.HoverSoundEnabled;
    }

    private bool TryReadCursorInfo(out NativeMethods.CURSORINFO info)
    {
        var result = _cursorInfoProvider();
        info = result.GetValueOrDefault();
        return result.HasValue;
    }

    private static NativeMethods.CURSORINFO? ReadSystemCursorInfo() =>
        NativeMethods.GetCursorInfo(out var info) ? info : null;

    private void ApplyAutoStart(bool enabled)
    {
        var previous = _settings.AutoStart;
        var applied = AutoStartManager.Apply(enabled);
        if (!applied)
        {
            Program.Log($"Failed to apply auto-start={enabled}");
            _settingsWindow?.SetAutoStartChecked(previous);
            RefreshAutoStart();
            MessageBox.Show("无法修改登录启动项，请检查当前用户的启动项权限。", "osu! Cursor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.AutoStart = enabled;
        _settings.Save();
    }

    private void RefreshAutoStart()
    {
        if (AutoStartManager.GetEnabled() is not bool enabled)
        {
            return;
        }

        if (_settings.AutoStart != enabled)
        {
            _settings.AutoStart = enabled;
            _settings.Save();
        }
        _settingsWindow?.SetAutoStartChecked(enabled);
    }

    private void ApplyTapSound(bool enabled)
    {
        _settings.TapSoundEnabled = enabled;
        UpdateSoundAvailability();
        _settings.Save();
    }

    private void ApplyTapSoundVolume(double volume)
    {
        _settings.TapSoundVolume = Math.Clamp(volume, 0.0, 1.0);
        _settings.ScheduleSave();
    }

    private void ApplyHoverSound(bool enabled)
    {
        _settings.HoverSoundEnabled = enabled;
        UpdateSoundAvailability();
        _settings.Save();
    }

    private void ApplyHoverSoundVolume(double volume)
    {
        _settings.HoverSoundVolume = Math.Clamp(volume, 0.0, 1.0);
        _settings.ScheduleSave();
    }

    private void ApplyHoverSoundMode(bool resizePrompt)
    {
        _settings.HoverSoundAsResizePrompt = resizePrompt;
        _wasHoverCandidate = false;
        _wasResizePrompt = false;
        _settings.Save();
    }

    private void PlayTapSample(double baseFrequency)
    {
        if (!CanShowOverlay || !_tapSoundPlayer.Enabled)
        {
            return;
        }

        if (_settings.TapSoundVolume <= 0.0)
        {
            return;
        }

        var frequency = baseFrequency - 0.01 + Random.Shared.NextDouble() * 0.02;
        var volume = baseFrequency * _settings.TapSoundVolume;
        _tapSoundPlayer.Play(frequency, volume, GetCurrentBalance());
    }

    private void PlayHoverSample()
    {
        if (!CanShowOverlay || !_hoverSoundPlayer.Enabled || _settings.HoverSoundVolume <= 0.0)
        {
            return;
        }

        var now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastHoverSoundTime < 20.0)
        {
            return;
        }

        _lastHoverSoundTime = now;
        var frequency = 1.0 - 0.01 + Random.Shared.NextDouble() * 0.02;
        _hoverSoundPlayer.Play(frequency, _settings.HoverSoundVolume, GetCurrentBalance());
    }

    private double GetCurrentBalance()
    {
        var bounds = SystemInformation.VirtualScreen;
        return bounds.Width > 0
            ? Math.Clamp(((double)(_cursorPoint.X - bounds.Left) / bounds.Width) * 2.0 - 1.0, -0.6, 0.6)
            : 0.0;
    }

    internal void RequestRender()
    {
        if (!CanShowOverlay || _hwnd == IntPtr.Zero || _renderingSubscribed)
        {
            return;
        }

        _lastFrameTime = _clock.Elapsed.TotalSeconds;
        _lastRenderingTime = null;
        if (_frameClock is not null)
        {
            _frameClock.Start(GetAnimationFrameRate());
        }
        else
        {
            CompositionTarget.Rendering += OnRendering;
        }
        _renderingSubscribed = true;
        _cursorVisual.InvalidateVisual();
    }

    internal void StopRendering()
    {
        CompositionTarget.Rendering -= OnRendering;
        _frameClock?.Stop();
        _renderingSubscribed = false;
    }

    private double GetAnimationFrameRate() =>
        _settings.GetAnimationFrameRate(_displayRefresh.GetRefreshRate(_cursorPoint.X, _cursorPoint.Y));

    private void ResetInputState(bool synchronizeButtons = false)
    {
        Model.Reset();
        ResetResizeSampling();
        _wasHoverCandidate = false;
        _wasResizePrompt = false;
        _lastCursorHandle = IntPtr.Zero;
        _lastHoverSoundTime = double.NegativeInfinity;
        var inputSnapshot = _mouseInput?.ResetTracking();
        if (inputSnapshot is { } sample)
        {
            UpdateCursorPosition(new NativeMethods.POINT { X = sample.X, Y = sample.Y });
        }
        if (TryReadCursorInfo(out var info))
        {
            if (!inputSnapshot.HasValue)
            {
                UpdateCursorPosition(info.ptScreenPos);
            }
            if (synchronizeButtons)
            {
                Model.IsHovering = info.hCursor != IntPtr.Zero
                    && info.hCursor == CursorReplacer.GetBlankHandle(NativeMethods.OCR_HAND);
                _wasHoverCandidate = Model.IsHovering;
                _wasResizePrompt = IsResizeCursor(info.hCursor);
            }
        }
        if (synchronizeButtons && !_smoke)
        {
            Model.SetButtons(inputSnapshot is { } snapshot ? (CursorButtons)snapshot.Buttons : ReadMouseButtons());
        }
    }

    private static CursorButtons ReadMouseButtons()
    {
        var buttons = CursorButtons.None;
        if (IsKeyPressed(0x01)) buttons |= CursorButtons.Left;
        if (IsKeyPressed(0x02)) buttons |= CursorButtons.Right;
        if (IsKeyPressed(0x04)) buttons |= CursorButtons.Middle;
        if (IsKeyPressed(0x05)) buttons |= CursorButtons.X1;
        if (IsKeyPressed(0x06)) buttons |= CursorButtons.X2;
        return buttons;
    }

    private static bool IsKeyPressed(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool IsResizeCursor(IntPtr cursor)
        => GetResizeAxis(cursor) != ResizeCursorAxis.None;

    private void UpdateResizeDirection(NativeMethods.CURSORINFO info)
    {
        if (_resizeDirectionProvider is not null)
        {
            Model.ResizeDirection = _resizeDirectionProvider(info);
            return;
        }

        var axis = GetResizeAxis(info.hCursor);
        if (axis == ResizeCursorAxis.None)
        {
            if (_resizeSample.HasValue)
            {
                ResetResizeSampling();
            }
            Model.ResizeDirection = ResizeDirection.None;
            return;
        }

        var sample = new ResizeSample(info.ptScreenPos.X, info.ptScreenPos.Y, _hwnd, axis, _resizeGeneration);
        if (_resizeSample?.Axis != axis)
        {
            Model.ResizeDirection = ResizeDirection.None;
        }
        _resizeSample = sample;
        _resizeSampler.Request(sample);
    }

    private void OnResizeSampleCompleted(ResizeSample sample, ResizeDirection direction)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!CanShowOverlay || _resizeSample != sample
                || sample.X != _cursorPoint.X || sample.Y != _cursorPoint.Y)
            {
                return;
            }
            if (Model.ResizeDirection != direction)
            {
                Model.ResizeDirection = direction;
                RequestRender();
            }
        }));
    }

    private void ResetResizeSampling()
    {
        _resizeGeneration++;
        _resizeSample = null;
        _resizeSampler.Reset();
    }

    private static ResizeCursorAxis GetResizeAxis(IntPtr cursor)
    {
        if (cursor == IntPtr.Zero)
        {
            return ResizeCursorAxis.None;
        }
        if (cursor == CursorReplacer.GetBlankHandle(NativeMethods.OCR_SIZEWE))
        {
            return ResizeCursorAxis.Horizontal;
        }
        if (cursor == CursorReplacer.GetBlankHandle(NativeMethods.OCR_SIZENS))
        {
            return ResizeCursorAxis.Vertical;
        }
        if (cursor == CursorReplacer.GetBlankHandle(NativeMethods.OCR_SIZENWSE))
        {
            return ResizeCursorAxis.NorthWestSouthEast;
        }
        if (cursor == CursorReplacer.GetBlankHandle(NativeMethods.OCR_SIZENESW))
        {
            return ResizeCursorAxis.NorthEastSouthWest;
        }
        return ResizeCursorAxis.None;
    }

    private void TryBringAboveTaskbarPreview()
    {
        if (_hwnd == IntPtr.Zero || !CanShowOverlay)
        {
            return;
        }

        var preview = NativeMethods.WindowFromPoint(_cursorPoint);
        var root = preview != IntPtr.Zero
            ? NativeMethods.GetAncestor(preview, (uint)NativeMethods.GaRoot)
            : IntPtr.Zero;

        if (NativeMethods.IsWindowVisible(root) && IsTaskListThumbnail(root))
        {
            NativeMethods.BringAbove(_hwnd);
            return;
        }

        var found = NativeMethods.FindWindow("TaskListThumbnailWnd", null);
        while (found != IntPtr.Zero)
        {
            if (NativeMethods.IsWindowVisible(found)
                && IsTaskListThumbnail(found)
                && NativeMethods.GetWindowRect(found, out var rect)
                && PointInRect(_cursorPoint, rect))
            {
                NativeMethods.BringAbove(_hwnd);
                return;
            }

            found = NativeMethods.GetWindow(found, (uint)NativeMethods.GwHwndNext);
        }
    }

    private static bool IsTaskListThumbnail(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString() == "TaskListThumbnailWnd";
    }

    private static bool PointInRect(NativeMethods.POINT point, NativeMethods.RECT rect)
    {
        return point.X >= rect.Left
            && point.X < rect.Right
            && point.Y >= rect.Top
            && point.Y < rect.Bottom;
    }

    private static byte[] LoadResourceBytes(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
