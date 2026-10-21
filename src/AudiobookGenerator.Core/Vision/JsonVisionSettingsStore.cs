using System.Globalization;
using System.Text.RegularExpressions;

namespace YewCone.AudiobookGenerator.Core;

internal sealed partial class JsonVisionSettingsStore(VisionSettingsStoreOptions options) : IVisionSettingsStore
{
    private const string EnvironmentPrefix = "AUDIOBOOKGENERATOR_VISION_";

    public string SettingsPath => options.SettingsPath;

    public async Task<VisionSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var settings = await AtomicJsonSettingsFile.LoadAsync(
            SettingsPath,
            "Vision",
            static () => new VisionSettings(),
            AudiobookFileJsonContext.Default.VisionSettings,
            cancellationToken);
        ApplyEnvironmentOverrides(settings);
        Validate(settings);
        return settings;
    }

    public async Task SaveAsync(VisionSettings settings, CancellationToken cancellationToken)
    {
        Validate(settings);
        await AtomicJsonSettingsFile.SaveAsync(
            SettingsPath,
            settings,
            AudiobookFileJsonContext.Default.VisionSettings,
            cancellationToken);
    }

    private static void ApplyEnvironmentOverrides(VisionSettings settings)
    {
        var defaultProfile = Environment.GetEnvironmentVariable($"{EnvironmentPrefix}DEFAULT_PROFILE");
        if (!string.IsNullOrWhiteSpace(defaultProfile))
        {
            settings.DefaultProfileId = defaultProfile.Trim();
        }

        var environmentProfileIds = Environment.GetEnvironmentVariable($"{EnvironmentPrefix}PROFILE_IDS");
        if (!string.IsNullOrWhiteSpace(environmentProfileIds))
        {
            foreach (var id in environmentProfileIds.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!settings.Profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.Profiles.Add(new OpenAiCompatibleVisionProfile { Id = id, DisplayName = id });
                }
            }
        }

        foreach (var profile in settings.Profiles)
        {
            var prefix = $"{EnvironmentPrefix}{NormalizeForEnvironment(profile.Id)}_";
            OverrideString($"{prefix}DISPLAY_NAME", value => profile.DisplayName = value);
            OverrideString($"{prefix}BASE_URL", value => profile.BaseUrl = value);
            OverrideString($"{prefix}MODEL", value => profile.Model = value);
            OverrideString($"{prefix}PROMPT", value => profile.Prompt = value);
            OverrideString($"{prefix}API_KEY_ENVIRONMENT_VARIABLE", value => profile.ApiKeyEnvironmentVariable = value);
            OverrideDouble($"{prefix}TEMPERATURE", value => profile.Temperature = value);
            OverrideInteger($"{prefix}MAXIMUM_OUTPUT_TOKENS", value => profile.MaximumOutputTokens = value);
            OverrideInteger($"{prefix}TIMEOUT_SECONDS", value => profile.TimeoutSeconds = value);
            OverrideInteger($"{prefix}MAXIMUM_IMAGE_DIMENSION", value => profile.MaximumImageDimension = value);
            OverrideInteger($"{prefix}MAXIMUM_IMAGE_BYTES", value => profile.MaximumImageBytes = value);
            OverrideInteger($"{prefix}MAXIMUM_CONTEXT_CHARACTERS", value => profile.MaximumContextCharacters = value);
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

    private static void Validate(VisionSettings settings)
    {
        settings.Profiles ??= [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Profiles)
        {
            _ = OpenAiEndpointValidation.Validate(
                "Vision",
                profile.Id,
                profile.DisplayName,
                profile.BaseUrl,
                profile.Model,
                profile.TimeoutSeconds);

            if (!ids.Add(profile.Id))
            {
                throw new InvalidDataException($"Vision profile ID '{profile.Id}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(profile.Prompt))
            {
                throw new InvalidDataException($"Vision profile '{profile.Id}' requires a prompt.");
            }

            if (profile.Temperature is < 0 or > 2)
            {
                throw new InvalidDataException($"Vision profile '{profile.Id}' temperature must be between 0 and 2.");
            }

            if (profile.MaximumOutputTokens is < 16 or > 4096)
            {
                throw new InvalidDataException(
                    $"Vision profile '{profile.Id}' maximum output tokens must be between 16 and 4096.");
            }

            if (profile.MaximumImageDimension is < 256 or > 8192)
            {
                throw new InvalidDataException(
                    $"Vision profile '{profile.Id}' maximum image dimension must be between 256 and 8192.");
            }

            if (profile.MaximumImageBytes is < 65536 or > 50 * 1024 * 1024)
            {
                throw new InvalidDataException(
                    $"Vision profile '{profile.Id}' maximum image bytes must be between 65536 and 52428800.");
            }

            if (profile.MaximumContextCharacters is < 0 or > 10000)
            {
                throw new InvalidDataException(
                    $"Vision profile '{profile.Id}' maximum context characters must be between 0 and 10000.");
            }
        }

        if (settings.DefaultProfileId != null && !ids.Contains(settings.DefaultProfileId))
        {
            throw new InvalidDataException($"Default vision profile '{settings.DefaultProfileId}' is not configured.");
        }
    }

    private static string NormalizeForEnvironment(string value) =>
        EnvironmentNameRegex().Replace(value.ToUpperInvariant(), "_");

    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex EnvironmentNameRegex();
}
