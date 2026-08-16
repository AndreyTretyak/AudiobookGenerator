namespace YewCone.AudiobookGenerator.Core;

internal sealed class AudioSynthesizer : IAudioSynthesizer
{
    private readonly IReadOnlyList<ISpeechProvider> builtInProviders;
    private readonly IReadOnlyDictionary<string, ISpeechProvider> builtInProvidersById;
    private readonly ITtsSettingsStore settingsStore;
    private readonly OpenAiCompatibleHttpClient openAiClient;

    public AudioSynthesizer(
        IEnumerable<ISpeechProvider> builtInProviders,
        ITtsSettingsStore settingsStore,
        OpenAiCompatibleHttpClient openAiClient)
    {
        this.settingsStore = settingsStore;
        this.openAiClient = openAiClient;
        this.builtInProviders = [.. builtInProviders];
        builtInProvidersById = this.builtInProviders
            .GroupBy(static provider => provider.Info.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<SpeechProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var providers = new List<SpeechProviderInfo>(builtInProviders.Count + settings.OpenAiCompatibleProfiles.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in builtInProviders)
        {
            if (seenIds.Add(provider.Info.Id))
            {
                providers.Add(provider.Info);
            }
        }

        foreach (var profile in settings.OpenAiCompatibleProfiles)
        {
            if (seenIds.Add(profile.Id))
            {
                providers.Add(new SpeechProviderInfo(
                    profile.Id,
                    profile.DisplayName,
                    SpeechProviderKind.OpenAiCompatible));
            }
        }

        return providers;
    }

    public async Task<string> GetDefaultProviderIdAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        return ResolveDefaultProviderId(settings);
    }

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

    private string ResolveDefaultProviderId(TtsSettings settings)
    {
        if (IsBuiltInProviderAvailable(settings.DefaultProviderId)
            || settings.OpenAiCompatibleProfiles.Any(profile =>
                string.Equals(profile.Id, settings.DefaultProviderId, StringComparison.OrdinalIgnoreCase)))
        {
            return settings.DefaultProviderId;
        }

        if (string.Equals(settings.DefaultProviderId, TtsSettings.WindowsProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return settings.OpenAiCompatibleProfiles.FirstOrDefault()?.Id
                ?? throw CreateWindowsProviderUnavailableException();
        }

        throw new InvalidOperationException($"TTS provider '{settings.DefaultProviderId}' is not configured.");
    }

    private async Task<ISpeechProvider> ResolveProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        if (builtInProvidersById.TryGetValue(providerId, out var builtInProvider))
        {
            return builtInProvider;
        }

        var settings = await settingsStore.LoadAsync(cancellationToken);
        var profile = settings.OpenAiCompatibleProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (profile != null)
        {
            return new OpenAiCompatibleSpeechProvider(profile, openAiClient);
        }

        if (string.Equals(providerId, TtsSettings.WindowsProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateWindowsProviderUnavailableException();
        }

        throw new InvalidOperationException($"TTS provider '{providerId}' is not configured.");
    }

    private bool IsBuiltInProviderAvailable(string providerId) =>
        builtInProvidersById.ContainsKey(providerId);

    private static InvalidOperationException CreateWindowsProviderUnavailableException() =>
        new("The Windows TTS provider is not available in this build. Configure at least one OpenAI-compatible TTS profile and use it as the default provider.");

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
            new OpenAiSpeechRequest(profile.Model, content, voice.Id, "wav", profile.Speed),
            OpenAiRequestJsonContext.Default.OpenAiSpeechRequest,
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
}
