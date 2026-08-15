using System.Globalization;
using System.Text.RegularExpressions;

namespace YewCone.AudiobookGenerator.Core;

internal sealed partial class JsonTtsSettingsStore(TtsSettingsStoreOptions options) : ITtsSettingsStore
{
    private const string EnvironmentPrefix = "AUDIOBOOKGENERATOR_TTS_";

    public string SettingsPath => options.SettingsPath;

    public async Task<TtsSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var settings = await AtomicJsonSettingsFile.LoadAsync(
            SettingsPath,
            "TTS",
            static () => new TtsSettings(),
            cancellationToken);
        ApplyEnvironmentOverrides(settings);
        Validate(settings);
        return settings;
    }

    public async Task SaveAsync(TtsSettings settings, CancellationToken cancellationToken)
    {
        Validate(settings);

        await AtomicJsonSettingsFile.SaveAsync(SettingsPath, settings, cancellationToken);
    }

    private static void ApplyEnvironmentOverrides(TtsSettings settings)
    {
        var defaultProvider = Environment.GetEnvironmentVariable($"{EnvironmentPrefix}DEFAULT_PROVIDER");
        if (!string.IsNullOrWhiteSpace(defaultProvider))
        {
            settings.DefaultProviderId = defaultProvider.Trim();
        }

        var environmentProfileIds = Environment.GetEnvironmentVariable($"{EnvironmentPrefix}PROFILE_IDS");
        if (!string.IsNullOrWhiteSpace(environmentProfileIds))
        {
            foreach (var id in environmentProfileIds.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!settings.OpenAiCompatibleProfiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.OpenAiCompatibleProfiles.Add(new OpenAiCompatibleTtsProfile { Id = id, DisplayName = id });
                }
            }
        }

        foreach (var profile in settings.OpenAiCompatibleProfiles)
        {
            var profilePrefix = $"{EnvironmentPrefix}{NormalizeForEnvironment(profile.Id)}_";
            OverrideString($"{profilePrefix}DISPLAY_NAME", value => profile.DisplayName = value);
            OverrideString($"{profilePrefix}BASE_URL", value => profile.BaseUrl = value);
            OverrideString($"{profilePrefix}MODEL", value => profile.Model = value);
            OverrideString($"{profilePrefix}API_KEY_ENVIRONMENT_VARIABLE", value => profile.ApiKeyEnvironmentVariable = value);
            OverrideDouble($"{profilePrefix}SPEED", value => profile.Speed = value);
            OverrideInteger($"{profilePrefix}MAXIMUM_INPUT_CHARACTERS", value => profile.MaximumInputCharacters = value);
            OverrideInteger($"{profilePrefix}TIMEOUT_SECONDS", value => profile.TimeoutSeconds = value);

            var voices = Environment.GetEnvironmentVariable($"{profilePrefix}VOICES");
            if (!string.IsNullOrWhiteSpace(voices))
            {
                profile.Voices = [.. voices
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseVoice)];
            }
        }

        static void OverrideString(string name, Action<string> apply)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                apply(value.Trim());
            }
        }

        static void OverrideDouble(string name, Action<double> apply)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new InvalidDataException($"Environment variable '{name}' must contain a number.");
            }

            apply(parsed);
        }

        static void OverrideInteger(string name, Action<int> apply)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new InvalidDataException($"Environment variable '{name}' must contain an integer.");
            }

            apply(parsed);
        }
    }

    private static ConfiguredSpeechVoice ParseVoice(string value)
    {
        var parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
        return new ConfiguredSpeechVoice
        {
            Id = parts[0],
            DisplayName = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : parts[0]
        };
    }

    private static string NormalizeForEnvironment(string value) =>
        EnvironmentNameRegex().Replace(value.ToUpperInvariant(), "_");

    private static void Validate(TtsSettings settings)
    {
        settings.OpenAiCompatibleProfiles ??= [];

        if (string.IsNullOrWhiteSpace(settings.DefaultProviderId))
        {
            throw new InvalidDataException("The default TTS provider ID is required.");
        }

        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TtsSettings.WindowsProviderId
        };

        foreach (var profile in settings.OpenAiCompatibleProfiles)
        {
            profile.Voices ??= [];

            _ = OpenAiEndpointValidation.Validate(
                "TTS",
                profile.Id,
                profile.DisplayName,
                profile.BaseUrl,
                profile.Model,
                profile.TimeoutSeconds);

            if (!providerIds.Add(profile.Id))
            {
                throw new InvalidDataException($"TTS provider ID '{profile.Id}' is duplicated.");
            }

            if (profile.Voices.Count == 0)
            {
                throw new InvalidDataException($"TTS profile '{profile.Id}' requires at least one configured voice.");
            }

            var voiceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var voice in profile.Voices)
            {
                if (string.IsNullOrWhiteSpace(voice.Id))
                {
                    throw new InvalidDataException($"TTS profile '{profile.Id}' contains a voice without an ID.");
                }

                if (!voiceIds.Add(voice.Id))
                {
                    throw new InvalidDataException($"TTS profile '{profile.Id}' contains duplicate voice ID '{voice.Id}'.");
                }

                if (string.IsNullOrWhiteSpace(voice.DisplayName))
                {
                    voice.DisplayName = voice.Id;
                }
            }

            if (profile.Speed is < 0.25 or > 4)
            {
                throw new InvalidDataException($"TTS profile '{profile.Id}' speed must be between 0.25 and 4.");
            }

            if (profile.MaximumInputCharacters is < 100 or > 100000)
            {
                throw new InvalidDataException($"TTS profile '{profile.Id}' maximum input characters must be between 100 and 100000.");
            }

        }

        if (!providerIds.Contains(settings.DefaultProviderId))
        {
            throw new InvalidDataException($"Default TTS provider '{settings.DefaultProviderId}' is not configured.");
        }
    }

    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex EnvironmentNameRegex();

}
