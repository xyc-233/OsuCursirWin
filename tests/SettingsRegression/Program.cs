using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OsuCursorWin;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SettingsWindow? window = null;
        try
        {
            var settings = new AppSettings();
            window = CreateWindow(settings);
            var rates = new List<int>();
            var qualities = new List<bool>();
            var idle = new List<bool>();
            window.AnimationFrameRateLimitChanged += rates.Add;
            window.HighQualityRenderingChanged += qualities.Add;
            window.PauseRenderingWhenIdleChanged += idle.Add;
            Show(window);

            var frameRate = Field<ComboBox>(window, "_animationFrameRateComboBox");
            var quality = Field<ComboBox>(window, "_renderQualityComboBox");
            var pause = Field<CheckBox>(window, "_pauseRenderingWhenIdleCheckBox");
            Check(SelectedRate(frameRate) == 0 && quality.SelectedIndex == 0 && pause.IsChecked == true,
                "The performance controls preserve the default rendering behavior");
            Check(rates.Count == 0 && qualities.Count == 0 && idle.Count == 0,
                "Opening settings does not emit changes for the stored performance values");

            frameRate.SelectedItem = frameRate.Items.Cast<ComboBoxItem>().Single(item => (int)item.Tag == 144);
            frameRate.SelectedItem = frameRate.SelectedItem;
            frameRate.SelectedIndex = 0;
            Check(rates.SequenceEqual(new[] { 144, 0 }), "Selecting frame caps and automatic mode emits their exact values once");
            quality.SelectedIndex = 1;
            quality.SelectedIndex = 0;
            Check(qualities.SequenceEqual(new[] { false, true }), "Quality selection emits the requested rendering mode");
            pause.IsChecked = false;
            pause.IsChecked = true;
            Check(idle.SequenceEqual(new[] { false, true }), "The idle checkbox emits both real-time transitions");

            var groups = Descendants<GroupBox>((DependencyObject)window.Content).ToArray();
            Check(groups.Select(group => group.Header?.ToString()).SequenceEqual(new[] { "光标", "音效", "性能", "启动和状态" }),
                "All settings sections remain available in reading order");
            var autoStart = Field<CheckBox>(window, "_autoStartCheckBox");
            Check(frameRate.TabIndex < quality.TabIndex && quality.TabIndex < pause.TabIndex && pause.TabIndex < autoStart.TabIndex,
                "Keyboard navigation follows the performance section and then startup settings");
            foreach (var group in groups)
            {
                Check(group.ActualWidth > 0 && group.ActualWidth <= ((FrameworkElement)window.Content).ActualWidth,
                    $"The {group.Header} section fits the standard-width settings window");
            }

            SavePreview(window);
            window.SizeToContent = SizeToContent.Manual;
            window.Width = 360;
            window.Height = 440;
            window.UpdateLayout();
            DrainDispatcher();
            var scroll = Descendants<ScrollViewer>((DependencyObject)window.Content).Single();
            Check(scroll.ScrollableHeight > 0 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                "A small window exposes the settings through vertical scrolling");
            pause.BringIntoView();
            DrainDispatcher();
            window.UpdateLayout();
            var pauseBounds = pause.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), pause.RenderSize));
            Check(pauseBounds.Top >= -1 && pauseBounds.Bottom <= scroll.ActualHeight + 1,
                "The idle performance setting can be brought fully into view in a small window");
            Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1,
                "The settings content wraps without requiring horizontal scrolling");
            window.ForceClose();
            window = null;

            window = CreateWindow(new AppSettings
            {
                AnimationFrameRateLimit = 200,
                HighQualityRendering = false,
                PauseRenderingWhenIdle = false
            });
            Check(SelectedRate(Field<ComboBox>(window, "_animationFrameRateComboBox")) == 200,
                "A saved frame limit outside the presets remains visible and selected");
            Check(Field<ComboBox>(window, "_renderQualityComboBox").SelectedIndex == 1
                && Field<CheckBox>(window, "_pauseRenderingWhenIdleCheckBox").IsChecked == false,
                "Saved non-default quality and idle settings initialize correctly");
            window.ForceClose();
            window = null;
            Console.WriteLine($"Passed {_passed} settings regression checks without changing user settings.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            window?.ForceClose();
            app.Shutdown();
        }
    }

    private static SettingsWindow CreateWindow(AppSettings settings) => new(settings)
    {
        ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000,
        Top = -10000
    };

    private static void Show(SettingsWindow window)
    {
        window.Show();
        DrainDispatcher();
        window.UpdateLayout();
    }

    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static int SelectedRate(ComboBox comboBox) => (int)((ComboBoxItem)comboBox.SelectedItem).Tag;

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void SavePreview(SettingsWindow window)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            drawing.DrawRectangle(SystemColors.ControlBrush, null, bounds);
            drawing.DrawRectangle(new VisualBrush(content), null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(AppContext.BaseDirectory, "settings-performance.png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"Settings preview: {path}");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
}
