namespace YewCone.AudiobookGenerator.Core;

internal sealed class ExternalAudioPreviewService(
    IAudioSynthesizer synthesizer,
    ITextChunker textChunker,
    IProcessRunner processRunner) : IAudioPreviewService, IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? currentRequest;
    private PlaybackState? currentPlayback;

    public async Task PlayAsync(
        string content,
        SpeechVoice voice,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource request;
        lock (gate)
        {
            StopCore();
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            currentRequest = request;
        }

        string? temporaryFile = null;
        try
        {
            var session = await synthesizer.CreateSessionAsync(voice, request.Token);
            var previewContent = textChunker.Split(content, session.MaximumInputCharacters)[0];
            await using var audio = await session.SynthesizeWavAsync(previewContent, request.Token);
            temporaryFile = Path.Combine(
                Path.GetTempPath(),
                $"audiobook-preview-{Guid.NewGuid():N}.wav");
            await using (var output = new FileStream(
                temporaryFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous))
            {
                await audio.CopyToAsync(output, request.Token);
            }

            var (player, arguments) = ResolvePlayer(temporaryFile);
            var process = processRunner.Start(player, arguments);
            var playback = new PlaybackState(process, temporaryFile);
            temporaryFile = null;
            lock (gate)
            {
                if (!ReferenceEquals(currentRequest, request))
                {
                    _ = MonitorPlaybackAsync(playback);
                    playback.RequestStop();
                    throw new OperationCanceledException(request.Token);
                }

                currentPlayback = playback;
            }

            _ = MonitorPlaybackAsync(playback);
        }
        catch
        {
            lock (gate)
            {
                if (ReferenceEquals(currentRequest, request))
                {
                    currentRequest = null;
                    request.Dispose();
                }
            }
            throw;
        }
        finally
        {
            if (temporaryFile != null)
            {
                File.Delete(temporaryFile);
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            StopCore();
        }
    }

    public void Dispose() => Stop();

    private async Task MonitorPlaybackAsync(PlaybackState playback)
    {
        try
        {
            _ = await playback.Process.WaitForExitAsync(CancellationToken.None);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(currentPlayback, playback))
                {
                    currentPlayback = null;
                    currentRequest?.Dispose();
                    currentRequest = null;
                }
            }

            playback.Complete();
        }
    }

    private void StopCore()
    {
        currentRequest?.Cancel();
        currentRequest?.Dispose();
        currentRequest = null;
        currentPlayback?.RequestStop();
        currentPlayback = null;
    }

    private static (string Player, string[] Arguments) ResolvePlayer(string audioFile)
    {
        if (OperatingSystem.IsMacOS())
        {
            var afplay = ExecutableLocator.Find("afplay");
            if (afplay != null)
            {
                return (afplay, [audioFile]);
            }
        }

        var ffplay = ExecutableLocator.Find("ffplay");
        if (ffplay != null)
        {
            return (ffplay, ["-nodisp", "-autoexit", "-loglevel", "error", audioFile]);
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var player in new[] { "pw-play", "paplay", "aplay" })
            {
                var executable = ExecutableLocator.Find(player);
                if (executable != null)
                {
                    return (executable, [audioFile]);
                }
            }
        }

        throw new InvalidOperationException(
            "No supported WAV player was found. Install ffplay, afplay, pw-play, paplay, or aplay.");
    }

    private sealed class PlaybackState(
        IExternalProcess process,
        string temporaryFile)
    {
        private int completed;

        public IExternalProcess Process { get; } = process;

        public void RequestStop() => Process.Terminate();

        public void Complete()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
            {
                return;
            }

            Process.Dispose();
            File.Delete(temporaryFile);
        }
    }
}
