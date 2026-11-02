using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OsuCursorWin;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OsuCursorLatency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string? output = null;
            var rotate = false;
            int? inputHertz = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--rotate")
                {
                    rotate = true;
                }
                else if (args[i] == "--output" && i + 1 < args.Length)
                {
                    output = Path.GetFullPath(args[++i]);
                }
                else if (args[i] == "--input-hz" && i + 1 < args.Length
                    && int.TryParse(args[++i], out var hertz) && hertz is 1000 or 4000 or 8000)
                {
                    inputHertz = hertz;
                }
                else
                {
                    Console.Error.WriteLine("Usage: LatencyProbe [--rotate] [--input-hz 1000|4000|8000] [--output path.json]");
                    return 2;
                }
            }
            var probe = new Probe(AppSettings.Load(Path.Combine(directory, "settings.json")), rotate, inputHertz);
            var json = JsonSerializer.Serialize(probe.Run(), new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            if (output is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, json);
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}

internal sealed class Probe
{
    private const double WarmupMilliseconds = 250;
    private const double SampleMilliseconds = 1200;
    private static readonly Dictionary<string, FieldInfo> WindowFields = typeof(MainWindow)
        .GetFields(BindingFlags.NonPublic | BindingFlags.Instance).ToDictionary(field => field.Name);
    private readonly ConcurrentDictionary<uint, QueuedSample> _queuedAt = new();
    private readonly List<double> _queueWait = new();
    private readonly List<double> _oldestQueueWait = new();
    private readonly List<double> _queueToSubmission = new();
    private readonly List<double> _processing = new();
    private readonly List<double> _callbackIntervals = new();
    private readonly List<double> _stateUpdateIntervals = new();
    private readonly List<double> _visualRotationIntervals = new();
    private readonly List<double> _visualAngleJumps = new();
    private readonly List<double> _acceptedRenderSpans = new();
    private readonly List<double> _acceptedRenderCpuCycles = new();
    private readonly List<double> _nativeMessageSpans = new();
    private readonly List<double> _queueToNativePosition = new();
    private readonly Dictionary<CursorPosition, QueuedSample> _deliveredPositions = new();
    private readonly List<object> _positionMismatchExamples = new();
    private readonly Queue<object> _positionMessages = new();
    private readonly Stopwatch _clock = new();
    private NativeMethods.CURSORINFO _cursorInfo = new() { flags = NativeMethods.CursorShowing };
    private MainWindow _window = null!;
    private MouseInputSource _source = null!;
    private Action<MouseInputEvent> _queue = null!;
    private object _sourceSync = null!;
    private MouseInputBuffer _sourceBuffer = null!;
    private Thread? _generator;
    private IntPtr _handle;
    private readonly WindowSubclass _windowSubclass;
    private bool _subclassAttached;
    private double _dpiX;
    private double _dpiY;
    private readonly AppSettings _settings;
    private readonly bool _rotate;
    private readonly int? _inputHertz;
    private int _observedMaxBufferedEvents;
    private int _maximumBatchEvents;
    private int _skippedGenerationDeadlines;
    private int _positionDeferredBatches;
    private int _nativeMoveRequests;
    private int _nativePositionChanges;
    private int _nativePositionMatches;
    private long _nativeMoveBegan;
    private bool _nativeMoveRequested;
    private CursorPosition? _lastNativePosition;
    private int _marginX;
    private int _marginY;
    private double _monitorRefreshRate;
    private long _renderBegan;
    private ulong _renderCpuCycles;
    private bool _hasCpuCycles;
    private double? _previousVisualRotation;
    private double? _previousVisualRotationTime;
    private int _visualRotationUpdates;
    private int _stop;
    private int _generated;
    private int _sampleGenerated;
    private int _sampleDelivered;
    private int _positionChangingBatches;
    private int _boundsMismatches;
    private int _maxPositionError;
    private int _sampleRenderCallbacks;
    private int _sampleStateUpdates;
    private long _firstSampleTimestamp;
    private long _lastSampleTimestamp;
    private double? _previousCallback;
    private double? _previousStateUpdate;
    private Exception? _failure;
    private MouseInputEvent? _lastDelivered;

    internal Probe(AppSettings settings, bool rotate, int? inputHertz)
    {
        _settings = settings;
        _rotate = rotate;
        _inputHertz = inputHertz;
        _windowSubclass = TrackPositionMessages;
    }

    internal object Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        _window = new MainWindow(smoke: true, settings: _settings,
            cursorInfoProvider: () => _cursorInfo, resizeDirectionProvider: _ => RecordStateUpdate());
        _source = new MouseInputSource(_window.Dispatcher, Deliver);
        _queue = typeof(MouseInputSource).GetMethod("Queue", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action<MouseInputEvent>>(_source);
        _sourceSync = typeof(MouseInputSource).GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_source)!;
        _sourceBuffer = (MouseInputBuffer)typeof(MouseInputSource).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_source)!;
        var started = false;
        _window.Loaded += (_, _) => _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (started) return;
            started = true;
            _handle = _window.OverlayHandle;
            _subclassAttached = SetWindowSubclass(_handle, _windowSubclass, 1, 0);
            if (!_subclassAttached) throw new InvalidOperationException("Unable to observe the native overlay window.");
            var dpi = NativeMethods.GetDpiForWindow(_handle) / 96.0;
            _dpiX = dpi;
            _dpiY = dpi;
            var cursorMargin = new CursorDimensions(_settings.CursorWidth).Margin;
            _marginX = (int)Math.Round(cursorMargin * _dpiX);
            _marginY = (int)Math.Round(cursorMargin * _dpiY);
            var workArea = SystemParameters.WorkArea;
            var margin = _rotate ? 200 : 100;
            var originX = (int)((workArea.Right - margin) * _dpiX);
            var originY = (int)((workArea.Bottom - margin) * _dpiY);
            _monitorRefreshRate = new DisplayFramePacer().GetRefreshRate(originX, originY);
            _cursorInfo.ptScreenPos = new NativeMethods.POINT { X = originX, Y = originY };
            _window.UpdateMouseState();
            _window.StopRendering();
            _window.FrameRendering += BeforeRendering;
            _window.RequestRender();
            _clock.Restart();
            _window.FrameRendered += OnRendering;
            _generator = new Thread(() => Generate(originX, originY))
            {
                IsBackground = true,
                Name = "SyntheticCursorProbe",
                Priority = ThreadPriority.BelowNormal
            };
            _generator.Start();
        }));
        _window.Closed += (_, _) =>
        {
            Volatile.Write(ref _stop, 1);
            _window.FrameRendered -= OnRendering;
            _window.FrameRendering -= BeforeRendering;
            _source.Dispose();
            RemoveWindowObserver();
        };
        try
        {
            app.Run(_window);
        }
        finally
        {
            Volatile.Write(ref _stop, 1);
            _generator?.Join(TimeSpan.FromSeconds(1));
            _source.Dispose();
            _window.FrameRendered -= OnRendering;
            _window.FrameRendering -= BeforeRendering;
        }
        if (_failure is not null) throw _failure;
        var generatedSpan = Stopwatch.GetElapsedTime(_firstSampleTimestamp, _lastSampleTimestamp).TotalSeconds;
        return new
        {
            Measurement = "Synthetic production input queue, batch processing and native position notifications; excludes hardware input and display presentation",
            RenderingMeasurement = "Completed native overlay rendering cadence, observed after UpdateLayeredWindow returns; not display presentation",
            Source = "MouseInputSource.Queue -> Dispatcher -> MainWindow.ProcessMouseInput; no native input hook started",
            Mode = _rotate ? "Held-button circular drag" : "Movement",
            MonitorRefreshRate = _monitorRefreshRate,
            WarmupMilliseconds,
            SampleMilliseconds,
            RequestedInputHertz = _inputHertz ?? 1000,
            Generator = _inputHertz.HasValue ? "Stopwatch deadlines with bounded spin and frequent yield; no catch-up bursts" : "Thread.Sleep(1)",
            SkippedGenerationDeadlines = _skippedGenerationDeadlines,
            ActualInputHertz = generatedSpan > 0 ? Math.Round((_sampleGenerated - 1) / generatedSpan, 2) : 0,
            GeneratedEvents = _generated,
            SampleGeneratedEvents = _sampleGenerated,
            SampleDeliveredEvents = _sampleDelivered,
            SampleDeliveredBatches = _queueWait.Count,
            PositionChangingBatches = _positionChangingBatches,
            PositionDeferredBatches = _positionDeferredBatches,
            WindowBoundsMismatches = _boundsMismatches,
            MaxWindowPositionErrorPixels = _maxPositionError,
            WindowPositionMismatchExamples = _positionMismatchExamples,
            RenderTier = RenderCapability.Tier >> 16,
            SampleRenderCallbacks = _sampleRenderCallbacks,
            QueueWaitMilliseconds = Statistics(_queueWait),
            OldestEventQueueWaitMilliseconds = Statistics(_oldestQueueWait),
            QueueToBatchCompletionMilliseconds = Statistics(_queueToSubmission),
            MainWindowProcessingMilliseconds = Statistics(_processing),
            InputQueue = new
            {
                ObservedMaximumBufferedEvents = _observedMaxBufferedEvents,
                MaximumBatchEvents = _maximumBatchEvents,
                Measurement = "Source buffer sampled after each enqueue under its existing lock; peak may occur between observations"
            },
            NativePositioning = new
            {
                Measurement = "Own HWND WM_WINDOWPOSCHANGING/CHANGED observations; message span excludes time before/after these callbacks, so is not full SetWindowPos duration",
                MoveRequests = _nativeMoveRequests,
                PositionChanges = _nativePositionChanges,
                PositionChangesPerSecond = Math.Round(_nativePositionChanges * 1000 / SampleMilliseconds, 2),
                ChangesMatchedToDeliveredInput = _nativePositionMatches,
                MessageSpanMilliseconds = Statistics(_nativeMessageSpans),
                QueueToPositionNotificationMilliseconds = Statistics(_queueToNativePosition)
            },
            StopwatchFrameIntervalsMilliseconds = Statistics(_callbackIntervals),
            EstimatedRenderingCallbackHertz = _callbackIntervals.Count > 0 ? Math.Round(1000 / _callbackIntervals.Average(), 2) : 0,
            AnimationStateUpdates = new
            {
                Measurement = "Accepted animation and cursor-state sampling cadence, not display presentation",
                Count = _sampleStateUpdates,
                IntervalsMilliseconds = Statistics(_stateUpdateIntervals),
                EstimatedHertz = _stateUpdateIntervals.Count > 0 ? Math.Round(1000 / _stateUpdateIntervals.Average(), 2) : 0
            },
            VisualRotation = !_rotate ? null : new
            {
                Measurement = "Model angle used for the completed native render; not presented frames",
                Count = _visualRotationUpdates,
                IntervalsMilliseconds = Statistics(_visualRotationIntervals),
                AngleJumpsDegrees = Statistics(_visualAngleJumps, milliseconds: false),
                IntervalsOverOneAndHalfFrames = _visualRotationIntervals.Count(value => value > 1500 / _monitorRefreshRate),
                FractionOverOneAndHalfFrames = _visualRotationIntervals.Count == 0 ? 0
                    : Math.Round((double)_visualRotationIntervals.Count(value => value > 1500 / _monitorRefreshRate) / _visualRotationIntervals.Count, 4),
                EstimatedUpdatesPerSecond = _visualRotationIntervals.Count > 0 ? Math.Round(1000 / _visualRotationIntervals.Average(), 2) : 0
            },
            RenderingWork = !_rotate ? null : new
            {
                Measurement = "Observers bracket production cursor-state sampling, animation advance and native rendering; elapsed time includes scheduling, CPU cycles exclude descheduling",
                AcceptedCallbackSpanMilliseconds = Statistics(_acceptedRenderSpans),
                AcceptedCallbackCpuCycles = Statistics(_acceptedRenderCpuCycles, milliseconds: false)
            }
        };
    }

    private ResizeDirection RecordStateUpdate()
    {
        var elapsed = _clock.Elapsed.TotalMilliseconds;
        if (_clock.IsRunning && elapsed >= WarmupMilliseconds && elapsed <= WarmupMilliseconds + SampleMilliseconds)
        {
            _sampleStateUpdates++;
            if (_previousStateUpdate is double previous) _stateUpdateIntervals.Add(elapsed - previous);
            _previousStateUpdate = elapsed;
        }
        return ResizeDirection.None;
    }

    private void Generate(int originX, int originY)
    {
        try
        {
            if (_rotate)
            {
                _queuedAt[0] = new QueuedSample(Stopwatch.GetTimestamp(), false);
                _queue(new MouseInputEvent(NativeMethods.WmLButtonDown, originX, originY, 0));
            }
            var radius = _settings.CursorWidth * Math.Max(_dpiX, _dpiY) * 2;
            var generationPeriod = Stopwatch.Frequency / (double)(_inputHertz ?? 1000);
            var generationDeadline = (double)Stopwatch.GetTimestamp();
            while (Volatile.Read(ref _stop) == 0 && _clock.Elapsed.TotalMilliseconds < WarmupMilliseconds + SampleMilliseconds)
            {
                if (_inputHertz.HasValue) WaitUntil(generationDeadline);
                if (Volatile.Read(ref _stop) != 0 || _clock.Elapsed.TotalMilliseconds >= WarmupMilliseconds + SampleMilliseconds) break;
                var elapsed = _clock.Elapsed.TotalMilliseconds;
                var id = (uint)++_generated;
                var timestamp = Stopwatch.GetTimestamp();
                _queuedAt[id] = new QueuedSample(timestamp, elapsed >= WarmupMilliseconds);
                if (elapsed >= WarmupMilliseconds)
                {
                    if (_sampleGenerated++ == 0) _firstSampleTimestamp = timestamp;
                    _lastSampleTimestamp = timestamp;
                }
                var phase = elapsed * Math.PI * 4 / 1000;
                var x = _rotate ? originX + (int)(Math.Cos(phase) * radius)
                    : originX + (int)(Math.Sin(elapsed / 70) * 24);
                var y = _rotate ? originY + (int)(Math.Sin(phase) * radius)
                    : originY + (int)(Math.Cos(elapsed / 90) * 12) * 2 + (int)(id & 1);
                _queue(new MouseInputEvent(NativeMethods.WmMouseMove, x, y, id));
                lock (_sourceSync)
                {
                    _observedMaxBufferedEvents = Math.Max(_observedMaxBufferedEvents, _sourceBuffer.Count);
                }
                if (_inputHertz.HasValue)
                {
                    generationDeadline += generationPeriod;
                    var now = Stopwatch.GetTimestamp();
                    if (now - generationDeadline > generationPeriod)
                    {
                        _skippedGenerationDeadlines += (int)((now - generationDeadline) / generationPeriod);
                        generationDeadline = now + generationPeriod;
                    }
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _window.Close()));
        }
        catch (Exception exception)
        {
            _failure = exception;
        }
    }

    private void Deliver(IReadOnlyList<MouseInputEvent> events)
    {
        var began = Stopwatch.GetTimestamp();
        var last = events[^1];
        _maximumBatchEvents = Math.Max(_maximumBatchEvents, events.Count);
        foreach (var input in events)
        {
            if (_queuedAt.TryGetValue(input.MouseData, out var sample))
            {
                _deliveredPositions[new CursorPosition(input.X, input.Y)] = sample;
            }
        }
        _cursorInfo.ptScreenPos = new NativeMethods.POINT { X = last.X, Y = last.Y };
        _window.ProcessMouseInput(events);
        var submitted = Stopwatch.GetTimestamp();
        var positionChanged = _lastDelivered is not { } previous || previous.X != last.X || previous.Y != last.Y;
        _lastDelivered = last;
        if (!_queuedAt.TryGetValue(last.MouseData, out var queued) || !queued.IsSample) return;
        foreach (var input in events)
        {
            if (_queuedAt[input.MouseData].IsSample) _sampleDelivered++;
        }
        if (positionChanged) _positionChangingBatches++;
        _queueWait.Add(Stopwatch.GetElapsedTime(queued.Timestamp, began).TotalMilliseconds);
        if (_queuedAt.TryGetValue(events[0].MouseData, out var oldest))
        {
            _oldestQueueWait.Add(Stopwatch.GetElapsedTime(oldest.Timestamp, began).TotalMilliseconds);
        }
        _queueToSubmission.Add(Stopwatch.GetElapsedTime(queued.Timestamp, submitted).TotalMilliseconds);
        _processing.Add(Stopwatch.GetElapsedTime(began, submitted).TotalMilliseconds);
        var margin = new CursorDimensions(_settings.CursorWidth).Margin;
        var expectedX = last.X - (int)Math.Round(margin * _dpiX);
        var expectedY = last.Y - (int)Math.Round(margin * _dpiY);
        if (!NativeMethods.GetWindowRect(_handle, out var bounds)
            || bounds.Left != expectedX || bounds.Top != expectedY)
        {
            if (ReadWindowField<int>("_lastWindowX") != expectedX || ReadWindowField<int>("_lastWindowY") != expectedY)
            {
                _positionDeferredBatches++;
                return;
            }
            _boundsMismatches++;
            _maxPositionError = Math.Max(_maxPositionError, Math.Max(Math.Abs(bounds.Left - expectedX), Math.Abs(bounds.Top - expectedY)));
            if (_positionMismatchExamples.Count < 6)
            {
                var cursorPoint = ReadWindowField<NativeMethods.POINT>("_cursorPoint");
                _positionMismatchExamples.Add(new
                {
                    SampleMilliseconds = _clock.Elapsed.TotalMilliseconds,
                    ExpectedX = expectedX,
                    ExpectedY = expectedY,
                    ActualX = bounds.Left,
                    ActualY = bounds.Top,
                    ActualWidth = bounds.Right - bounds.Left,
                    ActualHeight = bounds.Bottom - bounds.Top,
                    LastInputX = last.X,
                    LastInputY = last.Y,
                    ModelX = _window.Model.Position.X,
                    ModelY = _window.Model.Position.Y,
                    CursorPointX = cursorPoint.X,
                    CursorPointY = cursorPoint.Y,
                    CachedWindowX = ReadWindowField<int>("_lastWindowX"),
                    CachedWindowY = ReadWindowField<int>("_lastWindowY"),
                    CachedWidth = ReadWindowField<int>("_lastWindowWidth"),
                    CachedHeight = ReadWindowField<int>("_lastWindowHeight"),
                    ForceTopmost = ReadWindowField<bool>("_forceTopmost"),
                    WpfLeft = _window.Left,
                    WpfTop = _window.Top,
                    WpfWidth = _window.ActualWidth,
                    WpfHeight = _window.ActualHeight,
                    RecentPositionMessages = _positionMessages.ToArray()
                });
            }
        }
    }

    private void OnRendering()
    {
        var finished = Stopwatch.GetTimestamp();
        ulong cpuCycles = 0;
        var hasCpuCycles = _rotate && QueryThreadCycleTime(new IntPtr(-2), out cpuCycles);
        var elapsed = _clock.Elapsed.TotalMilliseconds;
        if (elapsed < WarmupMilliseconds || elapsed > WarmupMilliseconds + SampleMilliseconds) return;
        _sampleRenderCallbacks++;
        if (_previousCallback is double previous) _callbackIntervals.Add(elapsed - previous);
        _previousCallback = elapsed;
        if (_rotate)
        {
            if (_renderBegan != 0)
            {
                _acceptedRenderSpans.Add(Stopwatch.GetElapsedTime(_renderBegan, finished).TotalMilliseconds);
                if (_hasCpuCycles && hasCpuCycles && cpuCycles >= _renderCpuCycles)
                {
                    _acceptedRenderCpuCycles.Add(cpuCycles - _renderCpuCycles);
                }
            }
            var angle = _window.Model.Angle;
            if (_previousVisualRotation is double previousAngle)
            {
                var difference = Math.Abs(Math.IEEERemainder(angle - previousAngle, 360));
                if (difference <= 0.000001) return;
                _visualAngleJumps.Add(difference);
                _visualRotationIntervals.Add(elapsed - _previousVisualRotationTime!.Value);
            }
            _visualRotationUpdates++;
            _previousVisualRotation = angle;
            _previousVisualRotationTime = elapsed;
        }
    }

    private void BeforeRendering()
    {
        _hasCpuCycles = _rotate && QueryThreadCycleTime(new IntPtr(-2), out _renderCpuCycles);
        _renderBegan = Stopwatch.GetTimestamp();
    }

    private void WaitUntil(double deadline)
    {
        var spins = 0;
        while (Volatile.Read(ref _stop) == 0)
        {
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            if (remaining > Stopwatch.Frequency / 1000.0 * 1.5)
            {
                Thread.Sleep(1);
            }
            else if (remaining > Stopwatch.Frequency / 1000.0 * 0.05 || ++spins % 4 == 0)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
    }

    private T ReadWindowField<T>(string name) =>
        (T)WindowFields[name].GetValue(_window)!;

    private IntPtr TrackPositionMessages(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, nuint referenceData)
    {
        if (message is 0x0046 or 0x0047)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            var timestamp = Stopwatch.GetTimestamp();
            var elapsed = _clock.Elapsed.TotalMilliseconds;
            var inSample = _clock.IsRunning && elapsed >= WarmupMilliseconds && elapsed <= WarmupMilliseconds + SampleMilliseconds;
            if (message == 0x0046)
            {
                _nativeMoveBegan = timestamp;
                _nativeMoveRequested = (position.Flags & NativeMethods.SwpNoMove) == 0;
                if (inSample && _nativeMoveRequested) _nativeMoveRequests++;
            }
            else
            {
                if (inSample && _nativeMoveRequested && _nativeMoveBegan != 0)
                {
                    _nativeMessageSpans.Add(Stopwatch.GetElapsedTime(_nativeMoveBegan, timestamp).TotalMilliseconds);
                }
                _nativeMoveBegan = 0;
                var point = new CursorPosition(position.X, position.Y);
                if ((position.Flags & NativeMethods.SwpNoMove) == 0 && point != _lastNativePosition)
                {
                    if (inSample)
                    {
                        _nativePositionChanges++;
                        if (_deliveredPositions.TryGetValue(new CursorPosition(point.X + _marginX, point.Y + _marginY), out var sample) && sample.IsSample)
                        {
                            _nativePositionMatches++;
                            _queueToNativePosition.Add(Stopwatch.GetElapsedTime(sample.Timestamp, timestamp).TotalMilliseconds);
                        }
                    }
                    _lastNativePosition = point;
                }
            }
            _positionMessages.Enqueue(new
            {
                Time = _clock.Elapsed.TotalMilliseconds,
                Message = message == 0x0046 ? "WM_WINDOWPOSCHANGING" : "WM_WINDOWPOSCHANGED",
                position.X,
                position.Y,
                position.Width,
                position.Height,
                Flags = $"0x{position.Flags:X}"
            });
            if (_positionMessages.Count > 12) _positionMessages.Dequeue();
        }
        if (message == 0x0082) RemoveWindowObserver();
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void RemoveWindowObserver()
    {
        if (!_subclassAttached) return;
        RemoveWindowSubclass(_handle, _windowSubclass, 1);
        _subclassAttached = false;
    }

    private static object Statistics(List<double> samples, bool milliseconds = true)
    {
        if (samples.Count == 0) return new { Count = 0 };
        var sorted = samples.Order().ToArray();
        double Percentile(double p)
        {
            var position = (sorted.Length - 1) * p;
            var index = (int)position;
            return sorted[index] + (sorted[(int)Math.Ceiling(position)] - sorted[index]) * (position - index);
        }
        return new
        {
            Count = samples.Count,
            Mean = Math.Round(samples.Average(), 4),
            P50 = Math.Round(Percentile(0.5), 4),
            P95 = Math.Round(Percentile(0.95), 4),
            Max = Math.Round(sorted[^1], 4),
            CountOver5Milliseconds = milliseconds ? samples.Count(value => value > 5) : (int?)null,
            CountOver20Milliseconds = milliseconds ? samples.Count(value => value > 20) : (int?)null
        };
    }

    private readonly record struct QueuedSample(long Timestamp, bool IsSample);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    private delegate IntPtr WindowSubclass(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, WindowSubclass callback, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, WindowSubclass callback, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
