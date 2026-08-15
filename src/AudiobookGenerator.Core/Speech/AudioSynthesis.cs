using Microsoft.Extensions.Logging;

using System.Globalization;
using System.Media;
using System.Speech.Synthesis;
using System.Text.Json.Serialization;

namespace YewCone.AudiobookGenerator.Core;

internal sealed class AudioSynthesizer(
    WindowsSpeechProvider windowsProvider,
    ITtsSettingsStore settingsStore,
    OpenAiCompatibleHttpClient openAiClient) : IAudioSynthesizer
{
    public async Task<IReadOnlyList<SpeechProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        return
        [
            windowsProvider.Info,
            .. settings.OpenAiCompatibleProfiles.Select(static profile =>
                new SpeechProviderInfo(profile.Id, profile.DisplayName, SpeechProviderKind.OpenAiCompatible))
        ];
    }

    public async Task<string> GetDefaultProviderIdAsync(CancellationToken cancellationToken) =>
        (await settingsStore.LoadAsync(cancellationToken)).DefaultProviderId;

    public async Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = await ResolveProviderAsync(providerId, cancellationToken);
        return await provider.GetVoicesAsync(cancellationToken);
    }

    public async Task<int> GetMaximumInputCharactersAsync(SpeechVoice voice, CancellationToken cancellationToken)
    {
        var session = await CreateSessionAsync(voice, cancellationToken);
        return session.MaximumInputCharacters;
    }

    public async Task<Stream> SynthesizeWavAsync(string content, SpeechVoice voice, CancellationToken cancellationToken)
    {
        var session = await CreateSessionAsync(voice, cancellationToken);
        return await session.SynthesizeWavAsync(content, cancellationToken);
    }

    public async Task<IAudioSynthesisSession> CreateSessionAsync(SpeechVoice voice, CancellationToken cancellationToken)
    {
        var provider = await ResolveProviderAsync(voice.ProviderId, cancellationToken);
        var voices = await provider.GetVoicesAsync(cancellationToken);
        var configuredVoice = voices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, voice.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Voice '{voice.Id}' is not configured for provider '{voice.ProviderId}'.");

        return new AudioSynthesisSession(provider, configuredVoice);
    }

    private async Task<ISpeechProvider> ResolveProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        if (string.Equals(providerId, TtsSettings.WindowsProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return windowsProvider;
        }

        var settings = await settingsStore.LoadAsync(cancellationToken);
        var profile = settings.OpenAiCompatibleProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"TTS provider '{providerId}' is not configured.");

        return new OpenAiCompatibleSpeechProvider(
            profile,
            openAiClient);
    }

    private sealed class AudioSynthesisSession(
        ISpeechProvider provider,
        SpeechVoice voice) : IAudioSynthesisSession
    {
        public SpeechVoice Voice { get; } = voice;

        public int MaximumInputCharacters => provider.MaximumInputCharacters;

        public Task<Stream> SynthesizeWavAsync(string content, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(content);
            return provider.SynthesizeWavAsync(content, Voice, cancellationToken);
        }
    }
}

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

internal sealed class OpenAiCompatibleSpeechProvider(
    OpenAiCompatibleTtsProfile profile,
    OpenAiCompatibleHttpClient openAiClient) : ISpeechProvider
{
    public SpeechProviderInfo Info { get; } = new(
        profile.Id,
        profile.DisplayName,
        SpeechProviderKind.OpenAiCompatible);

    public int MaximumInputCharacters => profile.MaximumInputCharacters;

    public Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SpeechVoice> voices = [.. profile.Voices.Select(voice => new SpeechVoice(
            profile.Id,
            voice.Id,
            voice.DisplayName,
            voice.Culture,
            voice.Gender))];
        return Task.FromResult(voices);
    }

    public async Task<Stream> SynthesizeWavAsync(string content, SpeechVoice voice, CancellationToken cancellationToken)
    {
        var bytes = await openAiClient.PostJsonForBytesAsync(
            new OpenAiEndpointRequestOptions(
                profile.Id,
                profile.DisplayName,
                profile.BaseUrl,
                profile.TimeoutSeconds,
                profile.ApiKeyEnvironmentVariable),
            "audio/speech",
            new SpeechRequest(profile.Model, content, voice.Id, "wav", profile.Speed),
            cancellationToken);
        var output = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
        ValidateWave(output);
        output.Position = 0;
        return output;
    }

    private static void ValidateWave(MemoryStream stream)
    {
        if (stream.Length < 12)
        {
            stream.Dispose();
            throw new InvalidDataException("The TTS provider returned empty or incomplete audio.");
        }

        var header = stream.GetBuffer().AsSpan(0, 12);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
        {
            stream.Dispose();
            throw new InvalidDataException("The TTS provider did not return WAV audio.");
        }
    }

    private sealed record SpeechRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("response_format")] string ResponseFormat,
        [property: JsonPropertyName("speed")] double Speed);
}

internal sealed class WaveAudioPreviewService(
    IAudioSynthesizer synthesizer,
    ITextChunker textChunker) : IAudioPreviewService, IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _currentRequest;
    private SoundPlayer? _player;
    private Stream? _audio;

    public async Task PlayAsync(string content, SpeechVoice voice, CancellationToken cancellationToken)
    {
        CancellationTokenSource request;
        lock (_gate)
        {
            StopCore();
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _currentRequest = request;
        }

        Stream? audio = null;
        SoundPlayer? player = null;
        try
        {
            var session = await synthesizer.CreateSessionAsync(voice, request.Token);
            var previewContent = textChunker.Split(content, session.MaximumInputCharacters)[0];
            audio = await session.SynthesizeWavAsync(previewContent, request.Token);
            request.Token.ThrowIfCancellationRequested();
            player = new SoundPlayer(audio);
            player.Load();

            lock (_gate)
            {
                if (!ReferenceEquals(_currentRequest, request))
                {
                    throw new OperationCanceledException(request.Token);
                }

                _audio = audio;
                _player = player;
                audio = null;
                player = null;
                _player.Play();
            }
        }
        finally
        {
            player?.Dispose();
            audio?.Dispose();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    public void Dispose() => Stop();

    private void StopCore()
    {
        _currentRequest?.Cancel();
        _currentRequest?.Dispose();
        _currentRequest = null;
        _player?.Stop();
        _player?.Dispose();
        _player = null;
        _audio?.Dispose();
        _audio = null;
    }
}
