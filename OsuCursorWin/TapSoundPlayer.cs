using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace OsuCursorWin;

internal sealed class TapSoundPlayer : IDisposable
{
    private const int PlayerCount = 12;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMilliseconds(250);

    private readonly PcmSample _sample;
    private readonly BlockingCollection<PlayRequest> _queue;
    private readonly Thread _worker;
    private int _disposed;
    private volatile bool _enabled;

    internal bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    internal TapSoundPlayer(byte[] wavBytes, Func<IWavePlayer>? outputFactory = null)
    {
        _sample = new PcmSample(wavBytes);
        _queue = new BlockingCollection<PlayRequest>(new ConcurrentQueue<PlayRequest>(), PlayerCount * 2);
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker = new Thread(() => ProcessQueue(outputFactory ?? CreateOutput, initialized))
        {
            IsBackground = true,
            Name = "OsuCursorAudio"
        };

        try
        {
            _worker.Start();
        }
        catch
        {
            _queue.Dispose();
            throw;
        }

        initialized.Task.GetAwaiter().GetResult();
    }

    internal void Play(double frequency, double volume, double balance)
    {
        if (!Enabled || Volatile.Read(ref _disposed) != 0
            || !double.IsFinite(frequency) || frequency <= 0
            || !double.IsFinite(volume) || volume <= 0
            || !double.IsFinite(balance))
        {
            return;
        }

        try
        {
            _queue.TryAdd(new PlayRequest(frequency, Math.Clamp(volume, 0.0, 1.0), Math.Clamp(balance, -1.0, 1.0)));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Enabled = false;
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        if (Thread.CurrentThread != _worker && !_worker.Join(ShutdownTimeout))
        {
            Program.Log("Audio shutdown is continuing on its background worker.");
        }
    }

    private void ProcessQueue(Func<IWavePlayer> outputFactory, TaskCompletionSource initialized)
    {
        var voices = new AudioVoice?[PlayerCount];
        Exception? initializationError = null;
        try
        {
            for (var i = 0; i < voices.Length; i++)
            {
                voices[i] = new AudioVoice(outputFactory());
            }

            initialized.SetResult();
            var nextPlayer = 0;
            foreach (var request in _queue.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }

                if (!Enabled)
                {
                    continue;
                }

                var playerIndex = nextPlayer;
                nextPlayer = (nextPlayer + 1) % voices.Length;
                try
                {
                    var voice = voices[playerIndex]!;
                    if (!voice.Stop(StopTimeout))
                    {
                        continue;
                    }

                    if (voice.HasFailed)
                    {
                        voice.FinishDisposal();
                        voices[playerIndex] = null;
                        voice = new AudioVoice(outputFactory());
                        voices[playerIndex] = voice;
                    }

                    if (Volatile.Read(ref _disposed) == 0 && Enabled)
                    {
                        voice.Play(_sample.CreateStream(request.Frequency, request.Volume, request.Balance));
                    }
                }
                catch (Exception exception)
                {
                    Program.Log($"Failed to play queued sound: {exception}");
                    if (voices[playerIndex] is null)
                    {
                        throw;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!initialized.Task.IsCompleted)
            {
                initializationError = exception;
            }
            else
            {
                Program.Log($"Audio worker failed: {exception}");
            }
        }
        finally
        {
            Enabled = false;
            Interlocked.Exchange(ref _disposed, 1);
            _queue.CompleteAdding();
            foreach (var voice in voices)
            {
                voice?.RequestStop();
            }

            var pending = voices.Where(voice => voice is not null).Cast<AudioVoice>().ToList();
            while (pending.Count != 0)
            {
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].IsStopped)
                    {
                        pending[i].FinishDisposal();
                        pending.RemoveAt(i);
                    }
                }

                if (pending.Count != 0)
                {
                    Task.WaitAny(pending.Select(voice => voice.Completion).ToArray());
                }
            }

            _queue.Dispose();
            if (initializationError is not null)
            {
                initialized.SetException(initializationError);
            }
        }
    }

    private static IWavePlayer CreateOutput()
    {
        return new WaveOutEvent
        {
            DesiredLatency = 60,
            NumberOfBuffers = 3
        };
    }

    private readonly record struct PlayRequest(double Frequency, double Volume, double Balance);
}
