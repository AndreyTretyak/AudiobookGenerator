namespace YewCone.AudiobookGenerator.Core;

public sealed class TtsSettings
{
    public const string WindowsProviderId = "windows";

    public string DefaultProviderId { get; set; } = WindowsProviderId;

    public List<OpenAiCompatibleTtsProfile> OpenAiCompatibleProfiles { get; set; } = [];
}

public sealed class OpenAiCompatibleTtsProfile
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "http://127.0.0.1:8880/v1/";

    public string Model { get; set; } = string.Empty;

    public List<ConfiguredSpeechVoice> Voices { get; set; } = [];

    public double Speed { get; set; } = 1;

    public int MaximumInputCharacters { get; set; } = 4000;

    public int TimeoutSeconds { get; set; } = 120;

    public string? ApiKeyEnvironmentVariable { get; set; }
}

public sealed class ConfiguredSpeechVoice
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Culture { get; set; }

    public string? Gender { get; set; }
}

public static class TtsVoiceListCodec
{
    public static List<ConfiguredSpeechVoice> Parse(string value)
    {
        var entries = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var voices = new List<ConfiguredSpeechVoice>(entries.Length);

        foreach (var entry in entries)
        {
            var fields = entry.Split('|', 4, StringSplitOptions.TrimEntries);
            voices.Add(new ConfiguredSpeechVoice
            {
                Id = fields[0],
                DisplayName = fields.Length > 1 && !string.IsNullOrWhiteSpace(fields[1]) ? fields[1] : fields[0],
                Culture = fields.Length > 2 && !string.IsNullOrWhiteSpace(fields[2]) ? fields[2] : null,
                Gender = fields.Length > 3 && !string.IsNullOrWhiteSpace(fields[3]) ? fields[3] : null
            });
        }

        return voices;
    }

    public static string Format(IEnumerable<ConfiguredSpeechVoice> voices) =>
        string.Join(
            Environment.NewLine,
            voices.Select(static voice =>
                $"{voice.Id}|{voice.DisplayName}|{voice.Culture ?? string.Empty}|{voice.Gender ?? string.Empty}"));
}

public interface ITtsSettingsStore
{
    string SettingsPath { get; }

    Task<TtsSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(TtsSettings settings, CancellationToken cancellationToken);
}

internal sealed record TtsSettingsStoreOptions(string SettingsPath);
