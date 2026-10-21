namespace YewCone.AudiobookGenerator.Core;

public enum SpeechProviderKind
{
    Windows,
    OpenAiCompatible
}

public sealed record SpeechProviderInfo(
    string Id,
    string DisplayName,
    SpeechProviderKind Kind);

public sealed record SpeechVoice(
    string ProviderId,
    string Id,
    string Name,
    string? Culture = null,
    string? Gender = null,
    string? Age = null)
{
    public string QualifiedId => $"{ProviderId}:{Id}";
}

public interface IAudioSynthesizer
{
    Task<IReadOnlyList<SpeechProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken);

    Task<string> GetDefaultProviderIdAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(string providerId, CancellationToken cancellationToken);

    Task<int> GetMaximumInputCharactersAsync(SpeechVoice voice, CancellationToken cancellationToken);

    Task<Stream> SynthesizeWavAsync(string content, SpeechVoice voice, CancellationToken cancellationToken);

    Task<IAudioSynthesisSession> CreateSessionAsync(SpeechVoice voice, CancellationToken cancellationToken);
}

public interface IAudioSynthesisSession
{
    SpeechVoice Voice { get; }

    int MaximumInputCharacters { get; }

    Task<Stream> SynthesizeWavAsync(string content, CancellationToken cancellationToken);
}

public interface IAudioPreviewService
{
    Task PlayAsync(string content, SpeechVoice voice, CancellationToken cancellationToken);

    void Stop();
}

public interface ITextChunker
{
    IReadOnlyList<string> Split(string text, int maximumCharacters);
}

internal interface ISpeechProvider
{
    SpeechProviderInfo Info { get; }

    int MaximumInputCharacters { get; }

    Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken cancellationToken);

    Task<Stream> SynthesizeWavAsync(string content, SpeechVoice voice, CancellationToken cancellationToken);
}
