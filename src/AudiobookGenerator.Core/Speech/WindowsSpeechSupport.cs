#if AUDIOBOOKGENERATOR_WINDOWS_CORE
using Microsoft.Extensions.Logging;

using System.Globalization;
using System.Media;
using System.Speech.Synthesis;

namespace YewCone.AudiobookGenerator.Core;

internal sealed class WindowsSpeechProvider(ILogger<WindowsSpeechProvider> logger) : ISpeechProvider
{
    public SpeechProviderInfo Info { get; } = new(
        TtsSettings.WindowsProviderId,
        "Windows voices",
        SpeechProviderKind.Windows);

    public int MaximumInputCharacters => int.MaxValue;

    public Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var synthesizer = new SpeechSynthesizer();
        IReadOnlyList<SpeechVoice> voices = [.. synthesizer
            .GetInstalledVoices()
            .Where(static voice => voice.Enabled)
            .Select(static voice => new SpeechVoice(
                TtsSettings.WindowsProviderId,
                voice.VoiceInfo.Name,
                voice.VoiceInfo.Name,
                voice.VoiceInfo.Culture.Name,
                voice.VoiceInfo.Gender.ToString(),
                voice.VoiceInfo.Age.ToString()))];
        return Task.FromResult(voices);
    }

    public async Task<Stream> SynthesizeWavAsync(string content, SpeechVoice voice, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var synthesizer = new SpeechSynthesizer();
        var stream = new MemoryStream();
        try
        {
            var culture = string.IsNullOrWhiteSpace(voice.Culture)
                ? CultureInfo.CurrentCulture
                : CultureInfo.GetCultureInfo(voice.Culture);
            var prompt = new PromptBuilder(culture);
            prompt.AppendText(content);

            synthesizer.SelectVoice(voice.Id);
            synthesizer.SetOutputToWaveStream(stream);

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            synthesizer.SpeakCompleted += HandleCompleted;
            using var cancellationRegistration = cancellationToken.Register(synthesizer.SpeakAsyncCancelAll);
            try
            {
                _ = synthesizer.SpeakAsync(prompt);
                await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                synthesizer.SpeakCompleted -= HandleCompleted;
            }

            stream.Position = 0;
            return stream;

            void HandleCompleted(object? sender, SpeakCompletedEventArgs e)
            {
                if (e.Cancelled)
                {
                    completion.TrySetCanceled(cancellationToken.IsCancellationRequested
                        ? cancellationToken
                        : new CancellationToken(canceled: true));
                }
                else if (e.Error != null)
                {
                    completion.TrySetException(e.Error);
                }
                else
                {
                    completion.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
            stream.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            stream.Dispose();
            logger.LogError(ex, "Windows speech synthesis failed for voice {VoiceId}.", voice.Id);
            throw;
        }
    }
}

internal sealed class WaveAudioPreviewService(
    IAudioSynthesizer synthesizer,
    ITextChunker textChunker) : IAudioPreviewService, IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? currentRequest;
    private SoundPlayer? player;
    private Stream? audio;

    public async Task PlayAsync(string content, SpeechVoice voice, CancellationToken cancellationToken)
    {
        CancellationTokenSource request;
        lock (gate)
        {
            StopCore();
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            currentRequest = request;
        }

        Stream? currentAudio = null;
        SoundPlayer? currentPlayer = null;
        try
        {
            var session = await synthesizer.CreateSessionAsync(voice, request.Token);
            var previewContent = textChunker.Split(content, session.MaximumInputCharacters)[0];
            currentAudio = await session.SynthesizeWavAsync(previewContent, request.Token);
            request.Token.ThrowIfCancellationRequested();
            currentPlayer = new SoundPlayer(currentAudio);
            currentPlayer.Load();

            lock (gate)
            {
                if (!ReferenceEquals(currentRequest, request))
                {
                    throw new OperationCanceledException(request.Token);
                }

                audio = currentAudio;
                player = currentPlayer;
                currentAudio = null;
                currentPlayer = null;
                player.Play();
            }
        }
        finally
        {
            currentPlayer?.Dispose();
            currentAudio?.Dispose();
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

    private void StopCore()
    {
        currentRequest?.Cancel();
        currentRequest?.Dispose();
        currentRequest = null;
        player?.Stop();
        player?.Dispose();
        player = null;
        audio?.Dispose();
        audio = null;
    }
}
#endif
