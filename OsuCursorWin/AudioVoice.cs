using System;
using System.Threading.Tasks;
using NAudio.Wave;

namespace OsuCursorWin;

internal sealed class AudioVoice
{
    private readonly IWavePlayer _output;
    private volatile TaskCompletionSource<Exception?> _stopped = CreateCompletion(true);
    private WaveStream? _stream;
    private bool _stopRequested = true;
    private bool _failed;
    private bool _disposed;
    private bool _reportedPlaybackError;

    internal AudioVoice(IWavePlayer output)
    {
        _output = output;
        _output.PlaybackStopped += OnPlaybackStopped;
    }

    internal bool IsStopped => _stopped.Task.IsCompleted;
    internal Task Completion => _stopped.Task;

    internal bool HasFailed
    {
        get
        {
            ReportPlaybackError();
            return _failed;
        }
    }

    internal bool Stop(TimeSpan timeout)
    {
        RequestStop();
        return _stopped.Task.Wait(timeout);
    }

    internal void RequestStop()
    {
        if (_disposed || _stopRequested)
        {
            return;
        }

        _stopRequested = true;
        try
        {
            _output.Stop();
        }
        catch (Exception exception)
        {
            _failed = true;
            Program.Log($"Failed to stop audio output: {exception}");
        }
    }

    internal void Play(WaveStream stream)
    {
        if (_disposed || !IsStopped)
        {
            stream.Dispose();
            throw new InvalidOperationException("The audio voice is not ready for playback.");
        }

        _stream?.Dispose();
        _stream = stream;
        try
        {
            _output.Init(stream);
            _stopped = CreateCompletion(false);
            _reportedPlaybackError = false;
            _stopRequested = false;
            _output.Play();
        }
        catch
        {
            _failed = true;
            RequestStop();
            throw;
        }
    }

    internal void FinishDisposal()
    {
        if (_disposed)
        {
            return;
        }

        RequestStop();
        _stopped.Task.GetAwaiter().GetResult();
        ReportPlaybackError();
        _output.PlaybackStopped -= OnPlaybackStopped;
        try
        {
            _output.Dispose();
        }
        catch (Exception exception)
        {
            Program.Log($"Failed to dispose audio output: {exception}");
        }
        finally
        {
            _stream?.Dispose();
            _stream = null;
            _disposed = true;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        _stopped.TrySetResult(args.Exception);
    }

    private void ReportPlaybackError()
    {
        if (_reportedPlaybackError || !_stopped.Task.IsCompleted)
        {
            return;
        }

        _reportedPlaybackError = true;
        var exception = _stopped.Task.Result;
        if (exception is not null)
        {
            _failed = true;
            Program.Log($"Audio playback failed: {exception}");
        }
    }

    private static TaskCompletionSource<Exception?> CreateCompletion(bool stopped)
    {
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stopped)
        {
            completion.SetResult(null);
        }

        return completion;
    }
}
