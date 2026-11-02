using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string? outputPath = null;
            if (args.Length != 0)
            {
                if (args.Length != 2 || args[0] != "--output")
                {
                    Console.Error.WriteLine("Usage: PerformanceProbe [--output path.json]");
                    return 2;
                }
                outputPath = Path.GetFullPath(args[1]);
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new ProbeWindow();
            app.Run(window);
            var json = JsonSerializer.Serialize(window.Report(), new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            if (outputPath is not null)
            {
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(outputPath, json);
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}

internal sealed class ProbeWindow : Window
{
    private const double WarmupMilliseconds = 250;
    private const double SampleMilliseconds = 1200;
    private readonly Stopwatch _clock = new();
    private readonly List<double> _callbackIntervals = new();
    private readonly List<double> _distinctFrameIntervals = new();
    private readonly List<double> _renderingTimeIntervals = new();
    private readonly TranslateTransform _motion = new();
    private readonly DispatcherTimer _deadline = new() { Interval = TimeSpan.FromSeconds(3) };
    private double? _previousCallback;
    private double? _previousDistinctFrame;
    private double? _previousRenderingTime;
    private int _sampleCallbacks;
    private int _duplicateRenderingTimes;
    private int _regressingRenderingTimes;
    private double _sampleStarted;
    private double _sampleEnded;
    private bool _finished;
    private bool _timedOut;

    internal ProbeWindow()
    {
        Title = "OsuCursorWin rendering diagnostic";
        Width = 180;
        Height = 180;
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - Width - 16);
        Top = Math.Max(workArea.Top, workArea.Bottom - Height - 16);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        IsHitTestVisible = false;
        var canvas = new Canvas { IsHitTestVisible = false };
        var mark = new Rectangle
        {
            Width = 2,
            Height = 2,
            Fill = Brushes.Gray,
            Opacity = 0.05,
            RenderTransform = _motion,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(mark, 80);
        Canvas.SetTop(mark, 80);
        canvas.Children.Add(mark);
        Content = canvas;
        SourceInitialized += (_, _) => ConfigureWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
            _deadline.Start();
        };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            _deadline.Stop();
        };
        _deadline.Tick += (_, _) =>
        {
            _timedOut = true;
            Finish();
        };
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        if (_finished)
        {
            return;
        }
        var elapsed = _clock.Elapsed.TotalMilliseconds;
        _motion.X = Math.Sin(elapsed / 100.0) * 4;
        _motion.Y = Math.Cos(elapsed / 100.0) * 4;
        if (elapsed < WarmupMilliseconds)
        {
            return;
        }
        if (_sampleCallbacks == 0)
        {
            _sampleStarted = elapsed;
        }
        _sampleEnded = elapsed;
        _sampleCallbacks++;
        if (_previousCallback is double previousCallback)
        {
            _callbackIntervals.Add(elapsed - previousCallback);
        }
        _previousCallback = elapsed;
        var renderingTime = ((RenderingEventArgs)args).RenderingTime.TotalMilliseconds;
        if (_previousRenderingTime is double previousRenderingTime)
        {
            if (renderingTime > previousRenderingTime)
            {
                _renderingTimeIntervals.Add(renderingTime - previousRenderingTime);
                _distinctFrameIntervals.Add(elapsed - _previousDistinctFrame!.Value);
            }
            else if (renderingTime == previousRenderingTime)
            {
                _duplicateRenderingTimes++;
            }
            else
            {
                _regressingRenderingTimes++;
            }
        }
        if (_previousRenderingTime is null || renderingTime > _previousRenderingTime)
        {
            _previousRenderingTime = renderingTime;
            _previousDistinctFrame = elapsed;
        }
        if (elapsed - _sampleStarted >= SampleMilliseconds)
        {
            Finish();
        }
    }

    private void Finish()
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        CompositionTarget.Rendering -= OnRendering;
        _deadline.Stop();
        Close();
    }

    internal object Report() => new
    {
        Probe = "Standalone WPF transparent overlay with continuous lightweight transform updates",
        Measurement = "CompositionTarget.Rendering callback cadence, not physical display presentation or mouse latency",
        RenderTier = RenderCapability.Tier >> 16,
        ProcessRenderMode = RenderOptions.ProcessRenderMode.ToString(),
        WarmupMilliseconds,
        RequestedSampleMilliseconds = SampleMilliseconds,
        ActualSampleMilliseconds = Math.Round(_sampleEnded - _sampleStarted, 3),
        SampleCallbacks = _sampleCallbacks,
        DuplicateRenderingTimes = _duplicateRenderingTimes,
        RegressingRenderingTimes = _regressingRenderingTimes,
        TimedOut = _timedOut,
        StopwatchCallbackIntervals = Summarize(_callbackIntervals),
        StopwatchDistinctFrameIntervals = Summarize(_distinctFrameIntervals),
        RenderingTimeIntervals = Summarize(_renderingTimeIntervals)
    };

    private static object Summarize(List<double> samples)
    {
        if (samples.Count == 0)
        {
            return new { Count = 0 };
        }
        var sorted = samples.Order().ToArray();
        var mean = samples.Average();
        return new
        {
            Count = samples.Count,
            MeanMilliseconds = Math.Round(mean, 3),
            P50Milliseconds = Math.Round(Percentile(sorted, 0.50), 3),
            P95Milliseconds = Math.Round(Percentile(sorted, 0.95), 3),
            MinMilliseconds = Math.Round(sorted[0], 3),
            MaxMilliseconds = Math.Round(sorted[^1], 3),
            EstimatedHertz = Math.Round(1000.0 / mean, 2)
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static void ConfigureWindow(IntPtr window)
    {
        var style = IntPtr.Size == 8 ? GetWindowLongPtr(window, -20).ToInt64() : GetWindowLong(window, -20);
        style |= 0x00000020L | 0x00000080L | 0x08000000L;
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr(window, -20, new IntPtr(style));
        }
        else
        {
            SetWindowLong(window, -20, unchecked((int)style));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
