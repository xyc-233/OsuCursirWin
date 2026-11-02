using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using OsuCursorWin;

namespace ResizeSamplerRegression
{
    internal static class Program
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private static int Main()
        {
            var tests = new Action[]
            {
                SlowDetectionCoalescesRequests,
                RepeatedActiveSampleDoesNotQueryAgain,
                ResetInvalidatesOldResultsWithoutParallelDetection,
                NewGenerationInvalidatesAnIdenticalPosition,
                NoneAxisCancelsPendingDetection,
                IdenticalSamplesRefreshAfterCacheExpires,
                NoneResultsRefreshAfterCacheExpires,
                DisposeDoesNotWaitForDetection,
                DisposeDoesNotWaitForCompletion,
                FailuresDoNotStopTheWorker
            };
            try
            {
                foreach (var test in tests)
                {
                    test();
                    Console.WriteLine($"PASS {test.Method.Name}");
                }
                Console.WriteLine($"Passed {tests.Length} resize sampler regression checks without native calls.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void SlowDetectionCoalescesRequests()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var completed = new BlockingCollection<ResizeSample>();
            var detected = new ConcurrentQueue<ResizeSample>();
            using var sampler = new ResizeDirectionSampler(sample =>
            {
                detected.Enqueue(sample);
                if (sample.X == 1)
                {
                    entered.Set();
                    Await(release);
                }
                return ResizeDirection.Right;
            }, (sample, _) => completed.Add(sample));
            sampler.Request(Sample(1));
            Await(entered);
            try
            {
                var requests = Task.Run(() =>
                {
                    for (var x = 2; x <= 1000; x++)
                    {
                        sampler.Request(Sample(x));
                    }
                });
                Check(requests.Wait(Timeout), "Request waited for the blocked detector.");
                Check(detected.Count == 1, "Detection ran concurrently.");
            }
            finally
            {
                release.Set();
            }
            Check(Take(completed) == Sample(1000), "An obsolete request delivered a completion.");
            Check(detected.ToArray().SequenceEqual(new[] { Sample(1), Sample(1000) }), "Intermediate requests were not coalesced.");
            Check(completed.Count == 0, "More than the latest result was delivered.");
        }

        private static void RepeatedActiveSampleDoesNotQueryAgain()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var completed = new BlockingCollection<ResizeSample>();
            var calls = 0;
            using var sampler = new ResizeDirectionSampler(sample =>
            {
                Interlocked.Increment(ref calls);
                entered.Set();
                Await(release);
                return ResizeDirection.Left;
            }, (sample, _) => completed.Add(sample));
            sampler.Request(Sample(1));
            Await(entered);
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    sampler.Request(Sample(1));
                }
            }
            finally
            {
                release.Set();
            }
            Check(Take(completed) == Sample(1), "The active sample was discarded.");
            sampler.Dispose();
            AwaitIdle(sampler);
            Check(calls == 1, "Repeated in-flight samples started extra queries.");
        }

        private static void ResetInvalidatesOldResultsWithoutParallelDetection()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var completed = new BlockingCollection<ResizeSample>();
            var detected = new ConcurrentQueue<ResizeSample>();
            using var sampler = new ResizeDirectionSampler(sample =>
            {
                detected.Enqueue(sample);
                if (sample.X == 1)
                {
                    entered.Set();
                    Await(release);
                }
                return ResizeDirection.Top;
            }, (sample, _) => completed.Add(sample));
            sampler.Request(Sample(1));
            Await(entered);
            try
            {
                sampler.Request(Sample(2));
                sampler.Reset();
                sampler.Request(Sample(3));
                Check(detected.Count == 1, "Reset allowed a parallel detector.");
            }
            finally
            {
                release.Set();
            }
            Check(Take(completed) == Sample(3), "A pre-reset result was delivered.");
            Check(detected.ToArray().SequenceEqual(new[] { Sample(1), Sample(3) }), "Reset retained a pending obsolete query.");
        }

        private static void NoneAxisCancelsPendingDetection()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var completed = new BlockingCollection<ResizeSample>();
            var detected = new ConcurrentQueue<ResizeSample>();
            using var sampler = new ResizeDirectionSampler(sample =>
            {
                detected.Enqueue(sample);
                if (sample.X == 1)
                {
                    entered.Set();
                    Await(release);
                }
                return ResizeDirection.Bottom;
            }, (sample, _) => completed.Add(sample));
            sampler.Request(Sample(1));
            Await(entered);
            try
            {
                sampler.Request(Sample(2));
                sampler.Request(Sample(0) with { Axis = ResizeCursorAxis.None });
                sampler.Request(Sample(3));
            }
            finally
            {
                release.Set();
            }
            Check(Take(completed) == Sample(3), "Axis.None did not cancel obsolete results.");
            Check(detected.ToArray().SequenceEqual(new[] { Sample(1), Sample(3) }), "Axis.None reached the detector or preserved old pending work.");
        }

        private static void NewGenerationInvalidatesAnIdenticalPosition()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var completed = new BlockingCollection<ResizeSample>();
            var calls = 0;
            using var sampler = new ResizeDirectionSampler(sample =>
            {
                Interlocked.Increment(ref calls);
                if (sample.Generation == 1)
                {
                    entered.Set();
                    Await(release);
                }
                return ResizeDirection.Left;
            }, (sample, _) => completed.Add(sample));
            sampler.Request(Sample(1) with { Generation = 1 });
            Await(entered);
            try
            {
                sampler.Request(Sample(1) with { Generation = 2 });
                Check(calls == 1, "Generation changes started a parallel detector.");
            }
            finally
            {
                release.Set();
            }
            Check(Take(completed).Generation == 2 && calls == 2,
                "An identical coordinate delivered a result from an obsolete generation.");
        }

        private static void IdenticalSamplesRefreshAfterCacheExpires() => VerifyCache(ResizeDirection.Left);

        private static void NoneResultsRefreshAfterCacheExpires() => VerifyCache(ResizeDirection.None);

        private static void VerifyCache(ResizeDirection result)
        {
            using var completed = new BlockingCollection<ResizeDirection>();
            var calls = 0;
            using var sampler = new ResizeDirectionSampler(_ =>
            {
                Interlocked.Increment(ref calls);
                return result;
            }, (_, direction) => completed.Add(direction));
            sampler.Request(Sample(1));
            Check(Take(completed) == result, "The detector result changed.");
            sampler.Request(Sample(1));
            Check(calls == 1, "A completed identical sample ignored the cache interval.");
            var elapsed = Stopwatch.StartNew();
            Check(SpinWait.SpinUntil(() => elapsed.ElapsedMilliseconds >= 30, Timeout), "The cache clock did not advance.");
            sampler.Request(Sample(1));
            Check(Take(completed) == result && calls == 2, "An expired identical sample was not queried again.");
        }

        private static void DisposeDoesNotWaitForDetection()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var detectionFinished = new ManualResetEventSlim();
            var callbacks = 0;
            using var sampler = new ResizeDirectionSampler(_ =>
            {
                entered.Set();
                Await(release);
                detectionFinished.Set();
                return ResizeDirection.Left;
            }, (_, _) => Interlocked.Increment(ref callbacks));
            sampler.Request(Sample(1));
            Await(entered);
            try
            {
                var disposal = Task.Run(sampler.Dispose);
                Check(disposal.Wait(Timeout), "Dispose waited for an active detector.");
                sampler.Dispose();
                sampler.Reset();
                sampler.Request(Sample(2));
            }
            finally
            {
                release.Set();
            }
            Await(detectionFinished);
            AwaitIdle(sampler);
            Check(callbacks == 0, "Dispose delivered a pending callback.");
        }

        private static void DisposeDoesNotWaitForCompletion()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var callbackFinished = new ManualResetEventSlim();
            var calls = 0;
            using var sampler = new ResizeDirectionSampler(_ =>
            {
                Interlocked.Increment(ref calls);
                return ResizeDirection.Left;
            }, (_, _) =>
            {
                entered.Set();
                Await(release);
                callbackFinished.Set();
            });
            sampler.Request(Sample(1));
            Await(entered);
            try
            {
                sampler.Request(Sample(2));
                var disposal = Task.Run(sampler.Dispose);
                Check(disposal.Wait(Timeout), "Dispose waited for the completion callback.");
            }
            finally
            {
                release.Set();
            }
            Await(callbackFinished);
            AwaitIdle(sampler);
            Check(calls == 1, "Dispose allowed queued detection to continue.");
        }

        private static void FailuresDoNotStopTheWorker()
        {
            using var completed = new BlockingCollection<ResizeDirection>();
            using var callbackEntered = new ManualResetEventSlim();
            using var sampler = new ResizeDirectionSampler(sample => sample.X == 1
                ? throw new InvalidOperationException("Expected detector failure.") : ResizeDirection.Right,
                (sample, direction) =>
                {
                    if (sample.X == 2)
                    {
                        callbackEntered.Set();
                        throw new InvalidOperationException("Expected callback failure.");
                    }
                    completed.Add(direction);
                });
            sampler.Request(Sample(1));
            Check(Take(completed) == ResizeDirection.None, "Detection failure did not clear the result.");
            sampler.Request(Sample(2));
            Await(callbackEntered);
            sampler.Request(Sample(3));
            Check(Take(completed) == ResizeDirection.Right, "A callback exception terminated the worker.");
        }

        private static ResizeSample Sample(int x) => new(x, 100, new IntPtr(123), ResizeCursorAxis.Horizontal);

        private static T Take<T>(BlockingCollection<T> queue)
        {
            Check(queue.TryTake(out var value, Timeout), "Timed out waiting for a result.");
            return value!;
        }

        private static void Await(ManualResetEventSlim signal) => Check(signal.Wait(Timeout), "Timed out waiting for a test signal.");

        private static void AwaitIdle(ResizeDirectionSampler sampler)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var sync = typeof(ResizeDirectionSampler).GetField("_sync", flags)!.GetValue(sampler)!;
            var running = typeof(ResizeDirectionSampler).GetField("_workerRunning", flags)!;
            Check(SpinWait.SpinUntil(() =>
            {
                lock (sync)
                {
                    return !(bool)running.GetValue(sampler)!;
                }
            }, Timeout), "The background worker did not stop.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
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
