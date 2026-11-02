using System.Reflection;
using System.Runtime.InteropServices;
using OsuCursorWin;

var tests = new Action[]
{
    FirstFrameAndReset,
    ExcessCallbacksFollowDisplayCadence,
    DisplayCallbacksTolerateSchedulingJitter,
    SlowCallbackPhaseDriftDoesNotDropFrames,
    SlowerCallbacksAreNeverSuppressed,
    MovingAndStationaryCadencesShareOneBudget,
    FractionalRefreshRatesDoNotDrift,
    SparseCallbacksDoNotCatchUp,
    ChangingMonitorsUsesThePreviousRateForElapsedTime,
    RepeatedMonitorChangesDoNotGrantExtraFrames,
    InvalidRefreshRatesKeepTheLastGoodRate,
    RefreshProviderFailuresKeepTheLastGoodRate,
    InvalidTimesDoNotChangeState,
    BackwardTimeRestartsTheBudget,
    NativeStructuresMatchWindowsLayout
};

try
{
    foreach (var test in tests)
    {
        test();
        Console.WriteLine($"PASS {test.Method.Name}");
    }
    Console.WriteLine($"Passed {tests.Length} frame pacer regression checks without querying displays.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static void FirstFrameAndReset()
{
    var pacer = new DisplayFramePacer((_, _) => 60);
    Check(pacer.ShouldAdvance(0, 0, 0), "The first callback was suppressed.");
    Check(!pacer.ShouldAdvance(0, 0, 0), "Repeated callbacks at one instant advanced again.");
    Check(!pacer.ShouldAdvance(0.005, 0, 0), "Callbacks less than half a frame apart both advanced.");
    pacer.Reset();
    Check(pacer.ShouldAdvance(0.005, 0, 0), "Reset did not force the next callback.");
    Check(!pacer.ShouldAdvance(0.005, 0, 0), "Reset permitted more than one immediate callback.");
    Check(!pacer.ShouldAdvance(0.006, 0, 0), "Reset granted an extra burst after its first callback.");
}

static void ExcessCallbacksFollowDisplayCadence()
{
    foreach (var rate in new[] { 60.0, 120.0, 144.0, 165.0, 240.0, 360.0 })
    {
        var pacer = new DisplayFramePacer((_, _) => rate);
        var accepted = new List<double>();
        for (var i = 0; i < 750 * 20; i++)
        {
            var time = i / 750.0;
            if (pacer.ShouldAdvance(time, 0, 0)) accepted.Add(time);
            Check(!pacer.ShouldAdvance(time, 0, 0), "A duplicate high-frequency callback advanced.");
        }
        Check(Math.Abs(accepted.Count - rate * 20) <= 1,
            $"750 Hz callbacks drifted from {rate} Hz: {accepted.Count} advances.");
        CheckCadenceBounds(accepted, rate, 1.0 / rate + 1.0 / 750);
    }
}

static void DisplayCallbacksTolerateSchedulingJitter()
{
    const double rate = 165.0;
    const int totalFrames = 165 * 30;
    foreach (var amplitude in new[] { 0.0003, 0.0005, 0.001, 0.0015 })
    foreach (var phase in new[] { -1, 1 })
    {
        var pacer = new DisplayFramePacer((_, _) => rate);
        for (var frame = 0; frame < totalFrames; frame++)
        {
            var jitter = (frame % 2 == 0 ? amplitude : -amplitude) * phase;
            var timestamp = 1 + frame / rate + jitter;
            Check(pacer.ShouldAdvance(timestamp, 0, 0),
                $"Alternating {amplitude * 1000} ms jitter dropped frame {frame} with phase {phase}.");
            Check(!pacer.ShouldAdvance(timestamp, 0, 0), "Scheduling jitter allowed a duplicate frame.");
        }
    }
}

static void SlowCallbackPhaseDriftDoesNotDropFrames()
{
    const double rate = 165.0;
    foreach (var amplitude in new[] { 0.0005, 0.001, 0.0015 })
    foreach (var phase in new[] { 0.0, Math.PI / 2 })
    {
        var pacer = new DisplayFramePacer((_, _) => rate);
        for (var frame = 0; frame < 165 * 30; frame++)
        {
            var timestamp = 1 + frame / rate + Math.Sin(frame / rate * 2 * Math.PI + phase) * amplitude;
            Check(pacer.ShouldAdvance(timestamp, 0, 0),
                $"Slow {amplitude * 1000} ms phase drift dropped frame {frame}.");
        }
    }
}

static void SlowerCallbacksAreNeverSuppressed()
{
    foreach (var callbackRate in new[] { 30.0, 60.0, 164.0, 164.9, 165.0 })
    {
        var pacer = new DisplayFramePacer((_, _) => 165);
        for (var i = 0; i < callbackRate * 20; i++)
        {
            Check(pacer.ShouldAdvance(i / callbackRate, 0, 0),
                $"A {callbackRate} Hz callback was suppressed below the 165 Hz limit.");
        }
    }
}

static void MovingAndStationaryCadencesShareOneBudget()
{
    const double rate = 165.0;
    var pacer = new DisplayFramePacer((_, _) => rate);
    var callbacks = Enumerable.Range(0, 165 * 30).Select(i => i / rate)
        .Concat(Enumerable.Range(750 * 10, 750 * 10).Select(i => i / 750.0))
        .OrderBy(time => time);
    var accepted = new List<double>();
    foreach (var time in callbacks)
    {
        if (pacer.ShouldAdvance(time, 0, 0)) accepted.Add(time);
    }
    Check(Math.Abs(accepted.Count - 165 * 30) <= 1,
        $"Moving and stationary callbacks produced {accepted.Count} frames instead of 4950.");
    Check(accepted.Count(time => time < 10) == 1650, "Stationary animation initially dropped frames.");
    Check(accepted.Count(time => time >= 20) >= 1649, "Animation stayed throttled after movement stopped.");
    CheckCadenceBounds(accepted, rate, 1.5 / rate);
}

static void FractionalRefreshRatesDoNotDrift()
{
    foreach (var rate in new[] { 60000.0 / 1001.0, 165000.0 / 1001.0 })
    {
        var pacer = new DisplayFramePacer((_, _) => rate);
        var count = 0;
        for (var i = 0; i < 1000 * 120; i++)
        {
            if (pacer.ShouldAdvance(i / 1000.0, 0, 0)) count++;
        }
        Check(Math.Abs(count - Math.Ceiling(120 * rate)) <= 1,
            $"Fractional refresh rate {rate} accumulated a scheduling drift: {count} frames.");
    }
}

static void SparseCallbacksDoNotCatchUp()
{
    var pacer = new DisplayFramePacer((_, _) => 240);
    Check(pacer.ShouldAdvance(0, 0, 0), "The first sparse callback failed.");
    Check(pacer.ShouldAdvance(10, 0, 0), "A late callback did not advance.");
    for (var i = 0; i < 20; i++)
    {
        Check(!pacer.ShouldAdvance(10, 0, 0), "Late callbacks replayed accumulated frames.");
    }
    Check(!pacer.ShouldAdvance(10.001, 0, 0), "A late frame left catch-up work pending.");
    for (var frame = 1; frame <= 240; frame++)
    {
        Check(pacer.ShouldAdvance(10 + frame / 240.0, 0, 0), "Normal cadence did not resume after a pause.");
    }
}

static void ChangingMonitorsUsesThePreviousRateForElapsedTime()
{
    var pacer = new DisplayFramePacer((x, y) => x < 0 && y < 0 ? 60 : 240);
    Check(pacer.ShouldAdvance(0, -100, -100), "The initial monitor frame failed.");
    Check(!pacer.ShouldAdvance(0.005, 100, 100), "The faster monitor retroactively increased elapsed time credit.");
    Check(pacer.ShouldAdvance(0.006, 100, 100), "The faster monitor retained the slower rate.");
    Check(!pacer.ShouldAdvance(0.007, -100, -100), "A monitor switch granted an extra immediate frame.");
    Check(!pacer.ShouldAdvance(0.018, -100, -100), "The slower monitor retained the faster rate.");
    Check(pacer.ShouldAdvance(0.019, -100, -100), "The slower monitor discarded elapsed time at the previous rate.");
    Check(!pacer.ShouldAdvance(0.019, -100, -100), "A monitor switch permitted a duplicate frame.");
}

static void RepeatedMonitorChangesDoNotGrantExtraFrames()
{
    var pacer = new DisplayFramePacer((x, _) => x < 0 ? 60 : 240);
    Check(pacer.ShouldAdvance(0, -100, 0), "The initial monitor frame failed.");
    for (var i = 0; i < 100; i++)
    {
        Check(!pacer.ShouldAdvance(0, i % 2 == 0 ? 100 : -100, 0),
            "Changing monitors at the same instant granted a frame.");
    }
    Check(!pacer.ShouldAdvance(0.005, -100, 0), "Repeated monitor changes inflated the pending frame budget.");
    Check(pacer.ShouldAdvance(1.0 / 60, -100, 0), "Repeated monitor changes lost the normal cadence.");
}

static void InvalidRefreshRatesKeepTheLastGoodRate()
{
    double rate = double.NaN;
    var pacer = new DisplayFramePacer((_, _) => rate);
    Check(pacer.ShouldAdvance(0, 0, 0), "An invalid initial rate suppressed the first frame.");
    Check(!pacer.ShouldAdvance(0.005, 0, 0), "An invalid initial rate did not fall back to 60 Hz.");
    Check(pacer.ShouldAdvance(1.0 / 60, 0, 0), "The fallback refresh cadence was lost.");
    rate = 120;
    pacer.Reset();
    Check(pacer.ShouldAdvance(1, 0, 0), "A valid replacement refresh rate was ignored.");
    foreach (var invalid in new[] { 0.0, 1.0, -10.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        rate = invalid;
        Check(!pacer.ShouldAdvance(1.001, 0, 0), "An invalid rate corrupted the last valid cadence.");
    }
    Check(pacer.ShouldAdvance(1 + 1.0 / 120, 0, 0), "An invalid rate replaced the last valid refresh rate.");
}

static void RefreshProviderFailuresKeepTheLastGoodRate()
{
    var fail = false;
    var pacer = new DisplayFramePacer((_, _) => fail ? throw new InvalidOperationException() : 165);
    Check(pacer.ShouldAdvance(0, 0, 0), "The initial valid refresh query failed.");
    fail = true;
    Check(!pacer.ShouldAdvance(0.001, 0, 0), "A failed refresh query granted an extra frame.");
    Check(pacer.ShouldAdvance(1.0 / 165, 0, 0), "A failed refresh query lost the previous refresh rate.");
    var initiallyFailing = new DisplayFramePacer((_, _) => throw new InvalidOperationException());
    Check(initiallyFailing.ShouldAdvance(0, 0, 0), "An initial refresh query failure suppressed the first frame.");
    Check(!initiallyFailing.ShouldAdvance(0.005, 0, 0), "An initial query failure did not use the fallback rate.");
}

static void InvalidTimesDoNotChangeState()
{
    var pacer = new DisplayFramePacer((_, _) => 60);
    Check(pacer.ShouldAdvance(1, 0, 0), "The initial valid frame failed.");
    foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
    {
        Check(!pacer.ShouldAdvance(invalid, 0, 0), "An invalid timestamp advanced the animation.");
    }
    Check(!pacer.ShouldAdvance(1.005, 0, 0), "An invalid timestamp reset the frame budget.");
    Check(pacer.ShouldAdvance(1 + 1.0 / 60, 0, 0), "An invalid timestamp corrupted the normal cadence.");
}

static void BackwardTimeRestartsTheBudget()
{
    var pacer = new DisplayFramePacer((_, _) => 60);
    Check(pacer.ShouldAdvance(10, 0, 0), "The initial timestamp failed.");
    Check(pacer.ShouldAdvance(1, 0, 0), "A clock reset did not allow an immediate frame.");
    Check(!pacer.ShouldAdvance(1, 0, 0), "A clock reset allowed a duplicate frame.");
    Check(!pacer.ShouldAdvance(1.001, 0, 0), "A clock reset granted excess frame credit.");
    Check(pacer.ShouldAdvance(1 + 1.0 / 60, 0, 0), "A clock reset lost the normal cadence.");
}

static void NativeStructuresMatchWindowsLayout()
{
    var mode = typeof(DisplayFramePacer).GetNestedType("DisplayMode", BindingFlags.NonPublic)!;
    var monitor = typeof(DisplayFramePacer).GetNestedType("MonitorInfo", BindingFlags.NonPublic)!;
    Check(Marshal.SizeOf(mode) == 220, "DEVMODEW does not match the Windows ABI.");
    Check(Marshal.OffsetOf(mode, "DisplayFrequency").ToInt32() == 184, "The refresh rate field uses the wrong native offset.");
    Check(Marshal.SizeOf(monitor) == 104, "MONITORINFOEXW does not match the Windows ABI.");
}

static void CheckCadenceBounds(IReadOnlyList<double> accepted, double rate, double maximumGap)
{
    for (var i = 1; i < accepted.Count; i++)
    {
        var gap = accepted[i] - accepted[i - 1];
        Check(gap >= 0.5 / rate - 1e-9, $"Frames arrived too close together at {rate} Hz: {gap} seconds.");
        Check(gap <= maximumGap + 1e-9, $"The {rate} Hz cadence stalled for {gap} seconds.");
    }
    foreach (var window in new[] { 0.05, 0.25, 1.0, 5.0 })
    {
        var start = 0;
        for (var end = 0; end < accepted.Count; end++)
        {
            while (accepted[end] - accepted[start] > window) start++;
            var count = end - start + 1;
            var elapsed = accepted[end] - accepted[start];
            Check(count <= elapsed * rate + 1.5 + 1e-7,
                $"A {window}-second interval exceeded the {rate} Hz frame allowance.");
        }
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
