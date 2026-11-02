using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using GroupBox = System.Windows.Controls.GroupBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Label = System.Windows.Controls.Label;
using SystemColors = System.Windows.SystemColors;
using SystemFonts = System.Windows.SystemFonts;

namespace OsuCursorWin;

internal sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Slider _sizeSlider;
    private readonly TextBlock _sizeValueText;
    private readonly CheckBox _autoStartCheckBox;
    private readonly CheckBox _tapSoundCheckBox;
    private readonly CheckBox _hoverSoundCheckBox;
    private readonly CheckBox _resizeSoundCheckBox;
    private readonly Slider _tapSoundVolumeSlider;
    private readonly TextBlock _tapSoundVolumeText;
    private readonly Slider _hoverSoundVolumeSlider;
    private readonly TextBlock _hoverSoundVolumeText;
    private readonly Grid _tapSoundVolumeRow;
    private readonly Grid _hoverSoundVolumeRow;
    private readonly CheckBox _hideWhenCursorHiddenCheckBox;
    private readonly ComboBox _animationFrameRateComboBox;
    private readonly ComboBox _renderQualityComboBox;
    private readonly CheckBox _pauseRenderingWhenIdleCheckBox;
    private bool _allowClose;
    private bool _autoStartUpdating;
    private bool _tapSoundUpdating;
    private bool _hoverSoundUpdating;
    private bool _resizeSoundUpdating;

    internal event Action<double>? CursorSizeChanged;
    internal event Action<bool>? AutoStartChanged;
    internal event Action<bool>? TapSoundChanged;
    internal event Action<double>? TapSoundVolumeChanged;
    internal event Action<bool>? HoverSoundChanged;
    internal event Action<double>? HoverSoundVolumeChanged;
    internal event Action<bool>? ResizeSoundModeChanged;
    internal event Action<bool>? HideWhenCursorHiddenChanged;
    internal event Action<int>? AnimationFrameRateLimitChanged;
    internal event Action<bool>? HighQualityRenderingChanged;
    internal event Action<bool>? PauseRenderingWhenIdleChanged;

    internal SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "osu! Cursor 设置";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = true;
        Icon = ProgramIcon.CreateWindowIcon();
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, SystemColors.ControlBrushKey);
        SetResourceReference(ForegroundProperty, SystemColors.ControlTextBrushKey);
        SetResourceReference(FontFamilyProperty, SystemFonts.MessageFontFamilyKey);
        SetResourceReference(FontSizeProperty, SystemFonts.MessageFontSizeKey);
        SetResourceReference(FontWeightProperty, SystemFonts.MessageFontWeightKey);
        UpdateWindowBounds();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sections = new StackPanel { Margin = new Thickness(16, 12, 16, 4) };
        var scrollViewer = new ScrollViewer
        {
            Content = sections,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        root.Children.Add(scrollViewer);

        _sizeSlider = CreateSlider(settings.CursorWidth, 16, 64, "光标大小，16 至 64 像素", 0);
        _sizeValueText = CreateValueText($"{_sizeSlider.Value:0} px");
        var cursorSection = new StackPanel();
        cursorSection.Children.Add(CreateSliderRow("大小(_S):", _sizeSlider, _sizeValueText));
        var resetButton = new Button
        {
            Content = "恢复默认(_R)",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
            MinHeight = 28,
            TabIndex = 1,
            ToolTip = "仅将光标大小恢复为 30 px"
        };
        AutomationProperties.SetName(resetButton, "恢复默认光标大小，30 像素");
        resetButton.Click += (_, _) => _sizeSlider.Value = 30;
        cursorSection.Children.Add(resetButton);
        _hideWhenCursorHiddenCheckBox = CreateCheckBox(
            "跟随系统光标自动隐藏(_G)", "跟随系统光标自动隐藏", settings.HideWhenCursorHidden, 2);
        _hideWhenCursorHiddenCheckBox.Margin = new Thickness(0, 12, 0, 0);
        cursorSection.Children.Add(_hideWhenCursorHiddenCheckBox);
        cursorSection.Children.Add(new TextBlock
        {
            Text = "游戏隐藏系统光标时自动隐藏，恢复后重新显示。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 4, 0, 0)
        });
        sections.Children.Add(CreateSection("光标", cursorSection));

        _tapSoundCheckBox = CreateCheckBox("播放敲击音效(_T)", "播放敲击音效", settings.TapSoundEnabled, 3);
        _tapSoundVolumeSlider = CreateSlider(settings.TapSoundVolume * 100, 0, 100, "敲击音量，百分比", 4);
        _tapSoundVolumeText = CreateValueText($"{_tapSoundVolumeSlider.Value:0}%");
        _tapSoundVolumeRow = CreateSliderRow("敲击音量(_V):", _tapSoundVolumeSlider, _tapSoundVolumeText);
        _tapSoundVolumeRow.Margin = new Thickness(18, 4, 0, 12);

        _hoverSoundCheckBox = CreateCheckBox("播放悬停音效(_H)", "播放悬停音效", settings.HoverSoundEnabled, 5);
        _hoverSoundVolumeSlider = CreateSlider(settings.HoverSoundVolume * 100, 0, 100, "悬停音量，百分比", 6);
        _hoverSoundVolumeText = CreateValueText($"{_hoverSoundVolumeSlider.Value:0}%");
        _hoverSoundVolumeRow = CreateSliderRow("悬停音量(_U):", _hoverSoundVolumeSlider, _hoverSoundVolumeText);
        _hoverSoundVolumeRow.Margin = new Thickness(18, 4, 0, 0);
        _resizeSoundCheckBox = CreateCheckBox("仅在调整窗口大小时播放(_W)", "仅在调整窗口大小时播放悬停音效", settings.HoverSoundAsResizePrompt, 7);
        _resizeSoundCheckBox.Margin = new Thickness(18, 8, 0, 0);
        var soundSection = new StackPanel();
        soundSection.Children.Add(_tapSoundCheckBox);
        soundSection.Children.Add(_tapSoundVolumeRow);
        soundSection.Children.Add(_hoverSoundCheckBox);
        soundSection.Children.Add(_hoverSoundVolumeRow);
        soundSection.Children.Add(_resizeSoundCheckBox);
        sections.Children.Add(CreateSection("音效", soundSection));

        var performanceSection = new StackPanel();
        _animationFrameRateComboBox = CreateComboBox("动画帧率上限", 8);
        ComboBoxItem? selectedFrameRate = null;
        foreach (var limit in new[] { 0, 30, 60, 90, 120, 144, 165, 240, 360, 480 })
        {
            var item = CreateFrameRateItem(limit);
            _animationFrameRateComboBox.Items.Add(item);
            if (limit == settings.AnimationFrameRateLimit)
            {
                selectedFrameRate = item;
            }
        }
        if (selectedFrameRate is null)
        {
            selectedFrameRate = CreateFrameRateItem(settings.AnimationFrameRateLimit);
            _animationFrameRateComboBox.Items.Add(selectedFrameRate);
        }
        _animationFrameRateComboBox.SelectedItem = selectedFrameRate;
        performanceSection.Children.Add(CreateComboBoxRow("动画帧率(_F):", _animationFrameRateComboBox));
        performanceSection.Children.Add(new TextBlock
        {
            Text = "默认跟随显示器。降低帧率可减少资源占用，鼠标移动仍即时跟随。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12)
        });
        _renderQualityComboBox = CreateComboBox("渲染质量", 9);
        _renderQualityComboBox.Items.Add("高质量（默认）");
        _renderQualityComboBox.Items.Add("性能优先");
        _renderQualityComboBox.SelectedIndex = settings.HighQualityRendering ? 0 : 1;
        performanceSection.Children.Add(CreateComboBoxRow("渲染质量(_Q):", _renderQualityComboBox));
        performanceSection.Children.Add(new TextBlock
        {
            Text = "高质量使边缘更平滑，性能优先可减少旋转时的绘制开销。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12)
        });
        _pauseRenderingWhenIdleCheckBox = CreateCheckBox("静止时暂停连续绘制(_P)",
            "静止时暂停连续绘制", settings.PauseRenderingWhenIdle, 10);
        performanceSection.Children.Add(_pauseRenderingWhenIdleCheckBox);
        performanceSection.Children.Add(new TextBlock
        {
            Text = "动画结束后降低空闲占用，移动或点击时自动恢复。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 4, 0, 0)
        });
        sections.Children.Add(CreateSection("性能", performanceSection));

        _autoStartCheckBox = CreateCheckBox("登录 Windows 时自动启动(_A)", "登录 Windows 时自动启动", settings.AutoStart, 11);
        var systemSection = new StackPanel();
        systemSection.Children.Add(_autoStartCheckBox);
        var status = new TextBlock
        {
            Text = UiAccessStatus.IsEnabled
                ? "开始菜单覆盖：已启用"
                : "开始菜单覆盖：未启用，请使用安装版。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AutomationProperties.SetName(status, status.Text);
        systemSection.Children.Add(status);
        sections.Children.Add(CreateSection("启动和状态", systemSection));

        var footer = new DockPanel();
        var closeButton = new Button
        {
            Content = "关闭(_C)",
            IsCancel = true,
            MinWidth = 88,
            MinHeight = 28,
            Padding = new Thickness(16, 4, 16, 4),
            Margin = new Thickness(12, 0, 0, 0),
            TabIndex = 12
        };
        AutomationProperties.SetName(closeButton, "关闭设置");
        closeButton.Click += (_, _) => Close();
        DockPanel.SetDock(closeButton, Dock.Right);
        footer.Children.Add(closeButton);
        footer.Children.Add(new TextBlock
        {
            Text = "设置自动保存",
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        var footerBorder = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10, 16, 12),
            Child = footer
        };
        footerBorder.SetResourceReference(Border.BorderBrushProperty, SystemColors.ControlDarkBrushKey);
        Grid.SetRow(footerBorder, 1);
        root.Children.Add(footerBorder);
        Content = root;

        UpdateSoundControls();
        _sizeSlider.ValueChanged += OnSizeSliderChanged;
        _tapSoundVolumeSlider.ValueChanged += OnTapSoundVolumeChanged;
        _hoverSoundVolumeSlider.ValueChanged += OnHoverSoundVolumeChanged;
        _autoStartCheckBox.Checked += OnAutoStartChecked;
        _autoStartCheckBox.Unchecked += OnAutoStartChecked;
        _tapSoundCheckBox.Checked += OnTapSoundChecked;
        _tapSoundCheckBox.Unchecked += OnTapSoundChecked;
        _hoverSoundCheckBox.Checked += OnHoverSoundChecked;
        _hoverSoundCheckBox.Unchecked += OnHoverSoundChecked;
        _resizeSoundCheckBox.Checked += OnResizeSoundModeChecked;
        _resizeSoundCheckBox.Unchecked += OnResizeSoundModeChecked;
        _hideWhenCursorHiddenCheckBox.Checked += OnHideWhenCursorHiddenChecked;
        _hideWhenCursorHiddenCheckBox.Unchecked += OnHideWhenCursorHiddenChecked;
        _animationFrameRateComboBox.SelectionChanged += OnAnimationFrameRateChanged;
        _renderQualityComboBox.SelectionChanged += OnRenderQualityChanged;
        _pauseRenderingWhenIdleCheckBox.Checked += OnPauseRenderingWhenIdleChecked;
        _pauseRenderingWhenIdleCheckBox.Unchecked += OnPauseRenderingWhenIdleChecked;
        SourceInitialized += (_, _) => UpdateWindowBounds();
        DpiChanged += (_, _) => UpdateWindowBounds();
        LocationChanged += (_, _) => UpdateWindowBounds();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    internal void SetAutoStartChecked(bool enabled) =>
        SetChecked(_autoStartCheckBox, enabled, ref _autoStartUpdating);

    internal void SetTapSoundChecked(bool enabled)
    {
        SetChecked(_tapSoundCheckBox, enabled, ref _tapSoundUpdating);
        UpdateSoundControls();
    }

    internal void SetHoverSoundChecked(bool enabled)
    {
        SetChecked(_hoverSoundCheckBox, enabled, ref _hoverSoundUpdating);
        UpdateSoundControls();
    }

    internal void SetResizeSoundModeChecked(bool enabled) =>
        SetChecked(_resizeSoundCheckBox, enabled, ref _resizeSoundUpdating);

    private void OnSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var width = Math.Round(e.NewValue);
        _sizeValueText.Text = $"{width:0} px";
        CursorSizeChanged?.Invoke(width);
    }

    private void OnAutoStartChecked(object? sender, RoutedEventArgs e)
    {
        if (!_autoStartUpdating)
        {
            AutoStartChanged?.Invoke(_autoStartCheckBox.IsChecked == true);
        }
    }

    private void OnTapSoundChecked(object? sender, RoutedEventArgs e)
    {
        UpdateSoundControls();
        if (!_tapSoundUpdating)
        {
            TapSoundChanged?.Invoke(_tapSoundCheckBox.IsChecked == true);
        }
    }

    private void OnTapSoundVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var percent = Math.Round(e.NewValue);
        _tapSoundVolumeText.Text = $"{percent:0}%";
        TapSoundVolumeChanged?.Invoke(percent / 100.0);
    }

    private void OnHoverSoundChecked(object? sender, RoutedEventArgs e)
    {
        UpdateSoundControls();
        if (!_hoverSoundUpdating)
        {
            HoverSoundChanged?.Invoke(_hoverSoundCheckBox.IsChecked == true);
        }
    }

    private void OnHoverSoundVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var percent = Math.Round(e.NewValue);
        _hoverSoundVolumeText.Text = $"{percent:0}%";
        HoverSoundVolumeChanged?.Invoke(percent / 100.0);
    }

    private void OnResizeSoundModeChecked(object? sender, RoutedEventArgs e)
    {
        if (!_resizeSoundUpdating)
        {
            ResizeSoundModeChanged?.Invoke(_resizeSoundCheckBox.IsChecked == true);
        }
    }

    private void OnHideWhenCursorHiddenChecked(object? sender, RoutedEventArgs e) =>
        HideWhenCursorHiddenChanged?.Invoke(_hideWhenCursorHiddenCheckBox.IsChecked == true);

    private void OnAnimationFrameRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_animationFrameRateComboBox.SelectedItem is ComboBoxItem { Tag: int limit })
        {
            AnimationFrameRateLimitChanged?.Invoke(limit);
        }
    }

    private void OnRenderQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderQualityComboBox.SelectedIndex >= 0)
        {
            HighQualityRenderingChanged?.Invoke(_renderQualityComboBox.SelectedIndex == 0);
        }
    }

    private void OnPauseRenderingWhenIdleChecked(object? sender, RoutedEventArgs e) =>
        PauseRenderingWhenIdleChanged?.Invoke(_pauseRenderingWhenIdleCheckBox.IsChecked == true);

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _settings.FlushPendingSave();
            WindowState = WindowState.Normal;
            Hide();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _settings.FlushPendingSave();
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void UpdateSoundControls()
    {
        _tapSoundVolumeRow.IsEnabled = _tapSoundCheckBox.IsChecked == true;
        _hoverSoundVolumeRow.IsEnabled = _hoverSoundCheckBox.IsChecked == true;
        _resizeSoundCheckBox.IsEnabled = _hoverSoundCheckBox.IsChecked == true;
    }

    private void UpdateWindowBounds()
    {
        var width = SystemParameters.WorkArea.Width;
        var height = SystemParameters.WorkArea.Height;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var screen = System.Windows.Forms.Screen.FromHandle(handle);
            var dpi = VisualTreeHelper.GetDpi(this);
            width = screen.WorkingArea.Width / dpi.DpiScaleX;
            height = screen.WorkingArea.Height / dpi.DpiScaleY;
        }

        MaxWidth = Math.Max(1, width);
        MaxHeight = Math.Max(1, height);
    }

    private static void SetChecked(CheckBox checkBox, bool enabled, ref bool updating)
    {
        updating = true;
        try
        {
            checkBox.IsChecked = enabled;
        }
        finally
        {
            updating = false;
        }
    }

    private static GroupBox CreateSection(string title, UIElement content) => new()
    {
        Header = title,
        Content = content,
        Padding = new Thickness(12, 8, 12, 12),
        Margin = new Thickness(0, 0, 0, 12)
    };

    private static CheckBox CreateCheckBox(string content, string name, bool value, int tabIndex)
    {
        var checkBox = new CheckBox
        {
            Content = content,
            IsChecked = value,
            TabIndex = tabIndex,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(checkBox, name);
        return checkBox;
    }

    private static Slider CreateSlider(double value, double minimum, double maximum, string name, int tabIndex)
    {
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum,
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = maximum == 100 ? 10 : 5,
            IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true,
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center,
            TabIndex = tabIndex
        };
        AutomationProperties.SetName(slider, name);
        return slider;
    }

    private static ComboBox CreateComboBox(string name, int tabIndex)
    {
        var comboBox = new ComboBox
        {
            MinHeight = 28,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            TabIndex = tabIndex
        };
        AutomationProperties.SetName(comboBox, name);
        return comboBox;
    }

    private static ComboBoxItem CreateFrameRateItem(int limit) => new()
    {
        Content = limit == 0 ? "自动（跟随显示器）" : $"{limit} Hz",
        Tag = limit
    };

    private static Grid CreateComboBoxRow(string text, ComboBox comboBox)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new Label
        {
            Content = text,
            Target = comboBox,
            MinWidth = 96,
            Padding = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetLabeledBy(comboBox, label);
        row.Children.Add(label);
        Grid.SetColumn(comboBox, 1);
        row.Children.Add(comboBox);
        return row;
    }

    private static TextBlock CreateValueText(string text)
    {
        var style = new Style(typeof(TextBlock));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(SystemColors.GrayTextBrushKey)));
        style.Triggers.Add(disabled);
        return new TextBlock
        {
            Text = text,
            MinWidth = 48,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Style = style
        };
    }

    private static Grid CreateSliderRow(string text, Slider slider, TextBlock valueText)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new Label
        {
            Content = text,
            Target = slider,
            MinWidth = 96,
            Padding = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetLabeledBy(slider, label);
        row.Children.Add(label);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(valueText, 2);
        row.Children.Add(valueText);
        return row;
    }
}
