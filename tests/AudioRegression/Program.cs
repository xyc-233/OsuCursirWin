using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using NAudio.Wave;
using OsuCursorWin;

namespace AudioRegression
{
    internal static class Program
    {
        private static int Main()
        {
            var tests = new Action[]
            {
                EightBitSamplesPreserveUnsignedEncoding,
                SixteenBitStereoAppliesVolumeAndPan,
                PlaybackRotationKeepsActiveStreamsAlive,
                PlaybackStoppedControlsStreamLifetime,
                QueueIsBoundedAndShutdownDiscardsPendingSounds,
                InvalidPlaybackParametersAreIgnored,
                InvalidWaveChunksAreRejected,
                EmbeddedSoundsAreValid,
                PartialConstructionDisposesCreatedOutputs,
                ShutdownRetainsResourcesUntilStopArrives,
                StopFailureAndLateCallbackAreSafe,
                BlockingStopDoesNotBlockShutdown,
                DisposalFailureDoesNotInterruptConstructorRollback,
                BackgroundPlaybackFailureReplacesTheVoice,
                AudioResourcesStayOnTheirWorker,
                InitializationFailureReleasesOwnedStream,
                PlaybackFailureRetainsStreamUntilStop
            };

            foreach (var test in tests)
            {
                try
                {
                    test();
                    Assert(OsuCursorWin.Program.Errors.IsEmpty, string.Join(Environment.NewLine, OsuCursorWin.Program.Errors));
                    Console.WriteLine($"PASS {test.Method.Name}");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"FAIL {test.Method.Name}: {exception}");
                    return 1;
                }
            }

            Console.WriteLine($"Passed {tests.Length} audio regression checks without accessing an audio device.");
            return 0;
        }

        private static void EightBitSamplesPreserveUnsignedEncoding()
        {
            byte[] samples = [0, 64, 128, 192, 255];
            var sample = new PcmSample(CreateWave(samples, 1, 8));
            using var fullVolume = sample.CreateStream(1, 1, 0);
            Assert(ReadSamples(fullVolume).SequenceEqual(samples), "8-bit PCM samples changed at unity gain.");
            Assert(fullVolume.WaveFormat.BitsPerSample == 8, "8-bit PCM format was lost.");
            using var halfVolume = sample.CreateStream(1, 0.5, 0);
            Assert(ReadSamples(halfVolume).SequenceEqual(new byte[] { 64, 96, 128, 160, 192 }), "8-bit PCM volume scaling is incorrect.");
        }

        private static void SixteenBitStereoAppliesVolumeAndPan()
        {
            using var harness = new AudioHarness(CreateWave(ToPcm16(20000, -20000, 30000, -30000), 2, 16));
            var playback = harness.Play(frequency: 0.5, volume: 0.5);
            Assert(playback.Data.SequenceEqual(ToPcm16(10000, -10000, 15000, -15000)), "Stereo gain is incorrect.");
            Assert(playback.Stream.WaveFormat.SampleRate == 22050, "Playback frequency was not applied.");
            playback = harness.Play(balance: 1);
            Assert(playback.Data.SequenceEqual(ToPcm16(0, -28284, 0, short.MinValue)), "Stereo panning or clipping is incorrect.");
            playback = harness.Play(frequency: double.MaxValue);
            Assert(playback.Stream.WaveFormat.SampleRate == 96000, "Large finite frequency did not clamp safely.");
        }

        private static void PlaybackRotationKeepsActiveStreamsAlive()
        {
            using var harness = new AudioHarness(CreateWave(ToPcm16(1000, -1000), 1, 16));
            var first = harness.Play().Stream;
            for (var i = 1; i < 12; i++)
            {
                harness.Play();
                AssertReadable(first);
            }

            harness.Play();
            AssertDisposed(first);
            for (var i = 0; i < 24; i++)
            {
                harness.Play();
                foreach (var output in harness.Outputs)
                {
                    AssertReadable(output.Source!);
                }
            }

            var active = harness.Outputs.Select(output => output.Source!).ToArray();
            harness.Player.Dispose();
            harness.Player.Dispose();
            harness.Player.Play(1, 1, 0);
            Assert(harness.Outputs.All(output => output.Disposed), "An output was not disposed.");
            foreach (var stream in active)
            {
                AssertDisposed(stream);
            }
        }

        private static void QueueIsBoundedAndShutdownDiscardsPendingSounds()
        {
            using var gate = new ManualResetEventSlim();
            using var entered = new ManualResetEventSlim();
            using var harness = new AudioHarness(CreateWave([128, 255], 1, 8), () =>
            {
                entered.Set();
                Assert(gate.Wait(TimeSpan.FromSeconds(5)), "Playback gate timed out.");
            });
            harness.Player.Play(1, 1, 0);
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "Audio worker did not start.");
            try
            {
                for (var i = 0; i < 1000; i++)
                {
                    harness.Player.Play(1, 1, 0);
                }

                var queue = typeof(TapSoundPlayer).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Player)!;
                var count = (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
                Assert(count == 24, "Playback queue did not enforce its bound.");
                harness.Player.Enabled = false;
            }
            finally
            {
                gate.Set();
            }

            harness.Player.Dispose();
            Assert(harness.Playbacks.Count == 1, "Shutdown played pending sounds.");
        }

        private static void PlaybackStoppedControlsStreamLifetime()
        {
            using var stopRequested = new ManualResetEventSlim();
            using var releaseStop = new ManualResetEventSlim();
            using var harness = new AudioHarness(CreateWave(ToPcm16(1000, -1000), 1, 16), onStop: output =>
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    stopRequested.Set();
                    releaseStop.Wait(TimeSpan.FromSeconds(5));
                    output.CompleteStop();
                });
            });
            var first = harness.Play().Stream;
            for (var i = 1; i < 12; i++)
            {
                harness.Play();
            }

            harness.Player.Play(1, 1, 0);
            try
            {
                Assert(stopRequested.Wait(TimeSpan.FromSeconds(5)), "The reused output was not stopped.");
                AssertReadable(first);
                Assert(harness.Playbacks.Count == 0, "Output was reused before playback stopped.");
            }
            finally
            {
                releaseStop.Set();
            }

            Assert(harness.Playbacks.TryTake(out _, TimeSpan.FromSeconds(5)), "Playback did not resume after stop completed.");
            AssertDisposed(first);
        }

        private static void InvalidPlaybackParametersAreIgnored()
        {
            using var harness = new AudioHarness(CreateWave([128, 255], 1, 8));
            harness.Player.Play(double.NaN, 1, 0);
            harness.Player.Play(double.PositiveInfinity, 1, 0);
            harness.Player.Play(-1, 1, 0);
            harness.Player.Play(1, double.NaN, 0);
            harness.Player.Play(1, 0, 0);
            harness.Player.Play(1, 1, double.NaN);
            harness.Player.Dispose();
            Assert(harness.Playbacks.Count == 0, "Invalid playback parameters reached the output.");
        }

        private static void InvalidWaveChunksAreRejected()
        {
            var valid = CreateWave([0, 0], 1, 16);
            AssertRejected(valid[..10]);
            AssertRejected(valid[..^1]);
            var malformed = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(16, 4), uint.MaxValue);
            AssertRejected(malformed);
            malformed = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(16, 4), 4);
            AssertRejected(malformed);
            malformed = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(40, 4), uint.MaxValue);
            AssertRejected(malformed);
            AssertRejected(CreateWave([0, 0], 3, 16));
            AssertRejected(CreateWave([0, 0, 0], 1, 24));
            AssertRejected(CreateWave([0], 1, 16));
            AssertRejected(CreateWave([], 1, 16));
        }

        private static void EmbeddedSoundsAreValid()
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            foreach (var name in new[] { "cursor-tap.wav", "default-hover.wav" })
            {
                using var harness = new AudioHarness(File.ReadAllBytes(Path.Combine(root, "OsuCursorWin", name)));
                Assert(harness.Play().Data.Length > 0, $"Embedded sound {name} contains no samples.");
            }
        }

        private static void PartialConstructionDisposesCreatedOutputs()
        {
            var output = new TestOutput(new BlockingCollection<PlaybackSnapshot>());
            var count = 0;
            try
            {
                using var player = new TapSoundPlayer(CreateWave([128, 255], 1, 8), () =>
                {
                    if (count++ == 1)
                    {
                        throw new InvalidOperationException("Expected construction failure.");
                    }

                    return output;
                });
                throw new Exception("Construction unexpectedly succeeded.");
            }
            catch (InvalidOperationException)
            {
                Assert(output.Disposed, "Partially constructed audio outputs leaked.");
            }
        }

        private static void ShutdownRetainsResourcesUntilStopArrives()
        {
            using var harness = new AudioHarness(CreateWave([128, 255], 1, 8), onStop: _ => { });
            var playback = harness.Play();
            var elapsed = Stopwatch.StartNew();
            harness.Player.Dispose();
            elapsed.Stop();
            Assert(elapsed.Elapsed < TimeSpan.FromSeconds(1), "Shutdown waited indefinitely for a missing stop callback.");
            AssertReadable(playback.Stream);
            Assert(!harness.Outputs[0].Disposed, "Active output was disposed before its stop callback.");
            harness.Outputs[0].CompleteStop();
            harness.WaitForCleanup();
            AssertDisposed(playback.Stream);
            ConsumeExpectedLogs("Audio shutdown is continuing");
        }

        private static void StopFailureAndLateCallbackAreSafe()
        {
            using var harness = new AudioHarness(CreateWave([128, 255], 1, 8), onStop: _ => throw new IOException("Expected stop failure."));
            var playback = harness.Play();
            var output = harness.Outputs[0];
            var callback = output.CaptureStopCallback();
            harness.Player.Dispose();
            AssertReadable(playback.Stream);
            Assert(!output.Disposed, "Failed stop released an active output.");
            output.CompleteStop();
            harness.WaitForCleanup();
            AssertDisposed(playback.Stream);
            callback?.Invoke(output, new StoppedEventArgs());
            Assert(output.CaptureStopCallback() is null, "Disposed voice kept its playback callback attached.");
            ConsumeExpectedLogs("Failed to stop audio output", "Audio shutdown is continuing");
        }

        private static void BlockingStopDoesNotBlockShutdown()
        {
            using var releaseStop = new ManualResetEventSlim();
            using var harness = new AudioHarness(CreateWave([128, 255], 1, 8), onStop: output =>
            {
                releaseStop.Wait(TimeSpan.FromSeconds(5));
                output.CompleteStop();
            });
            var playback = harness.Play();
            try
            {
                var elapsed = Stopwatch.StartNew();
                harness.Player.Dispose();
                elapsed.Stop();
                Assert(elapsed.Elapsed < TimeSpan.FromSeconds(1), "A blocking output Stop blocked shutdown.");
                AssertReadable(playback.Stream);
            }
            finally
            {
                releaseStop.Set();
            }

            harness.WaitForCleanup();
            AssertDisposed(playback.Stream);
            ConsumeExpectedLogs("Audio shutdown is continuing");
        }

        private static void DisposalFailureDoesNotInterruptConstructorRollback()
        {
            using var playbacks = new BlockingCollection<PlaybackSnapshot>();
            var outputs = new List<TestOutput>();
            try
            {
                using var player = new TapSoundPlayer(CreateWave([128, 255], 1, 8), () =>
                {
                    if (outputs.Count == 2)
                    {
                        throw new InvalidOperationException("Expected factory failure.");
                    }

                    var output = new TestOutput(playbacks, onDispose: () => throw new IOException("Expected disposal failure."));
                    outputs.Add(output);
                    return output;
                });
                throw new Exception("Construction unexpectedly succeeded.");
            }
            catch (InvalidOperationException exception)
            {
                Assert(exception.Message == "Expected factory failure.", "Cleanup hid the construction exception.");
            }

            Assert(outputs.Count == 2 && outputs.All(output => output.Disposed), "Disposal failure interrupted remaining cleanup.");
            ConsumeExpectedLogs("Failed to dispose audio output", "Failed to dispose audio output");
        }

        private static void BackgroundPlaybackFailureReplacesTheVoice()
        {
            using var harness = new AudioHarness(CreateWave([128, 255], 1, 8));
            var playback = harness.Play();
            var failedOutput = harness.Outputs[0];
            failedOutput.CompleteStop(new IOException("Expected background playback failure."));
            for (var i = 0; i < 12; i++)
            {
                harness.Play();
            }

            Assert(failedOutput.Disposed, "Failed playback output was reused.");
            AssertDisposed(playback.Stream);
            Assert(harness.Outputs.Count == 13, "Failed voice was not replaced.");
            ConsumeExpectedLogs("Audio playback failed");
        }

        private static void AudioResourcesStayOnTheirWorker()
        {
            var callerThread = Environment.CurrentManagedThreadId;
            using var harness = new AudioHarness(CreateWave([128, 255], 1, 8));
            harness.Play();
            harness.Player.Dispose();
            foreach (var output in harness.Outputs)
            {
                Assert(output.OwnerThread != callerThread, "Audio output was created on the caller thread.");
                Assert(output.OperationThreads.All(thread => thread == output.OwnerThread), "Audio resource changed owning threads.");
            }
        }

        private static void InitializationFailureReleasesOwnedStream()
        {
            using var playbacks = new BlockingCollection<PlaybackSnapshot>();
            var output = new TestOutput(playbacks, onInit: () => throw new IOException("Expected initialization failure."));
            var voice = new AudioVoice(output);
            var sample = new PcmSample(CreateWave([128, 255], 1, 8));
            var stream = sample.CreateStream(1, 1, 0);
            try
            {
                voice.Play(stream);
                throw new Exception("Initialization unexpectedly succeeded.");
            }
            catch (IOException)
            {
            }

            voice.FinishDisposal();
            AssertDisposed(stream);
            Assert(output.Disposed, "Failed initialization leaked its output.");
        }

        private static void PlaybackFailureRetainsStreamUntilStop()
        {
            using var entered = new ManualResetEventSlim();
            using var harness = new AudioHarness(CreateWave([128, 255], 1, 8), onStop: _ => { }, onPlay: () =>
            {
                entered.Set();
                throw new IOException("Expected playback startup failure.");
            });
            harness.Player.Play(1, 1, 0);
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "Playback did not start.");
            harness.Player.Dispose();
            var stream = harness.Outputs[0].Source!;
            AssertReadable(stream);
            Assert(!harness.Outputs[0].Disposed, "Playback failure released a potentially active output.");
            harness.Outputs[0].CompleteStop();
            harness.WaitForCleanup();
            AssertDisposed(stream);
            ConsumeExpectedLogs("Failed to play queued sound", "Audio shutdown is continuing");
        }

        private static void ConsumeExpectedLogs(params string[] expected)
        {
            var messages = new List<string>();
            while (OsuCursorWin.Program.Errors.TryDequeue(out var message))
            {
                messages.Add(message);
            }

            Assert(messages.Count == expected.Length, $"Unexpected log count: {string.Join(Environment.NewLine, messages)}");
            foreach (var prefix in expected)
            {
                var index = messages.FindIndex(message => message.StartsWith(prefix, StringComparison.Ordinal));
                Assert(index >= 0, $"Missing log: {prefix}");
                messages.RemoveAt(index);
            }
        }

        private static void AssertRejected(byte[] wav)
        {
            try
            {
                _ = new PcmSample(wav);
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
                return;
            }

            throw new Exception("Malformed WAV was accepted.");
        }

        private static byte[] CreateWave(byte[] pcm, short channels, short bits)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length + (pcm.Length & 1));
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(44100);
            writer.Write(44100 * channels * (bits / 8));
            writer.Write((short)(channels * (bits / 8)));
            writer.Write(bits);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm);
            if ((pcm.Length & 1) != 0)
            {
                writer.Write((byte)0);
            }

            return stream.ToArray();
        }

        private static byte[] ToPcm16(params short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
            }

            return bytes;
        }

        private static void AssertReadable(WaveStream stream)
        {
            stream.Read([], 0, 0);
        }

        private static byte[] ReadSamples(WaveStream stream)
        {
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static void AssertDisposed(WaveStream stream)
        {
            try
            {
                AssertReadable(stream);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            throw new Exception("Expected disposed audio stream.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }

        private sealed class AudioHarness : IDisposable
        {
            internal List<TestOutput> Outputs { get; } = [];
            internal BlockingCollection<PlaybackSnapshot> Playbacks { get; } = new();
            internal TapSoundPlayer Player { get; }

            internal AudioHarness(byte[] wav, Action? beforePlay = null, Action<TestOutput>? onStop = null, Action? onPlay = null)
            {
                Player = new TapSoundPlayer(wav, () =>
                {
                    var output = new TestOutput(Playbacks, beforePlay, onStop, onPlay: onPlay);
                    Outputs.Add(output);
                    return output;
                }) { Enabled = true };
            }

            internal PlaybackSnapshot Play(double frequency = 1, double volume = 1, double balance = 0)
            {
                Player.Play(frequency, volume, balance);
                Assert(Playbacks.TryTake(out var playback, TimeSpan.FromSeconds(5)), "No playback was received.");
                return playback!;
            }

            internal void WaitForCleanup()
            {
                var worker = (Thread)typeof(TapSoundPlayer).GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Player)!;
                Assert(worker.Join(TimeSpan.FromSeconds(5)), "Background audio cleanup did not finish.");
                Assert(Outputs.All(output => output.Disposed), "Background cleanup missed an output.");
            }

            public void Dispose()
            {
                Player.Dispose();
                Playbacks.Dispose();
            }
        }

        private sealed record PlaybackSnapshot(byte[] Data, WaveStream Stream);

        private sealed class TestOutput(BlockingCollection<PlaybackSnapshot> playbacks, Action? beforePlay = null, Action<TestOutput>? onStop = null, Action? onDispose = null, Action? onInit = null, Action? onPlay = null) : IWavePlayer
        {
            internal WaveStream? Source { get; private set; }
            internal volatile bool Disposed;
            internal int OwnerThread { get; } = Environment.CurrentManagedThreadId;
            internal ConcurrentQueue<int> OperationThreads { get; } = new();
            public float Volume { get; set; } = 1;
            public PlaybackState PlaybackState { get; private set; }
            public WaveFormat OutputWaveFormat => Source?.WaveFormat ?? new WaveFormat();
            public event EventHandler<StoppedEventArgs>? PlaybackStopped;

            public void Init(IWaveProvider waveProvider)
            {
                OperationThreads.Enqueue(Environment.CurrentManagedThreadId);
                Assert(!Disposed, "A disposed output was reused.");
                Source = (WaveStream)waveProvider;
                onInit?.Invoke();
            }

            public void Play()
            {
                OperationThreads.Enqueue(Environment.CurrentManagedThreadId);
                beforePlay?.Invoke();
                PlaybackState = PlaybackState.Playing;
                onPlay?.Invoke();
                var data = new byte[Source!.Length];
                Source.ReadExactly(data);
                Source.Position = 0;
                playbacks.Add(new PlaybackSnapshot(data, Source));
            }

            public void Pause()
            {
                PlaybackState = PlaybackState.Paused;
            }

            public void Stop()
            {
                OperationThreads.Enqueue(Environment.CurrentManagedThreadId);
                if (Source is not null)
                {
                    AssertReadable(Source);
                }

                var wasPlaying = PlaybackState != PlaybackState.Stopped;
                PlaybackState = PlaybackState.Stopped;
                if (wasPlaying && onStop is not null)
                {
                    onStop(this);
                    return;
                }

                CompleteStop();
            }

            internal EventHandler<StoppedEventArgs>? CaptureStopCallback() => PlaybackStopped;

            internal void CompleteStop(Exception? exception = null)
            {
                PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));
            }

            public void Dispose()
            {
                OperationThreads.Enqueue(Environment.CurrentManagedThreadId);
                Stop();
                Disposed = true;
                onDispose?.Invoke();
            }
        }
    }
}

namespace OsuCursorWin
{
    internal static class Program
    {
        internal static ConcurrentQueue<string> Errors { get; } = new();

        internal static void Log(string message)
        {
            Errors.Enqueue(message);
        }
    }
}
