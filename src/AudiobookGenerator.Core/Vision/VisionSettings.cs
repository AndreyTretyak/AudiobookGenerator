namespace YewCone.AudiobookGenerator.Core;

public sealed class VisionSettings
{
    public string? DefaultProfileId { get; set; }

    public List<OpenAiCompatibleVisionProfile> Profiles { get; set; } = [];
}

public sealed class OpenAiCompatibleVisionProfile
{
    public const string DefaultDescriptionPrompt =
        "Describe the image for audiobook narration in 1 to 3 concise, objective sentences. " +
        "Use the language of the supplied book context. Do not begin with phrases such as " +
        "\"image of\", \"picture of\", or \"this image shows\". Return only the narration text.";

    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1/";

    public string Model { get; set; } = string.Empty;

    public string Prompt { get; set; } = DefaultDescriptionPrompt;

    public double Temperature { get; set; } = 0.2;

    public int MaximumOutputTokens { get; set; } = 200;

    public int TimeoutSeconds { get; set; } = 180;

    public int MaximumImageDimension { get; set; } = 2048;

    public int MaximumImageBytes { get; set; } = 5 * 1024 * 1024;

    public int MaximumContextCharacters { get; set; } = 1200;

    public string? ApiKeyEnvironmentVariable { get; set; }
}

public interface IVisionSettingsStore
{
    string SettingsPath { get; }

    Task<VisionSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(VisionSettings settings, CancellationToken cancellationToken);
}

internal sealed record VisionSettingsStoreOptions(string SettingsPath);
