using System.Text.RegularExpressions;

namespace YewCone.AudiobookGenerator.Core;

internal static partial class OpenAiEndpointValidation
{
    public static Uri Validate(
        string profileType,
        string id,
        string displayName,
        string baseUrl,
        string model,
        int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(id) || !ProfileIdRegex().IsMatch(id))
        {
            throw new InvalidDataException(
                $"{profileType} profile ID '{id}' is invalid. Use letters, digits, periods, underscores, or hyphens.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidDataException($"{profileType} profile '{id}' requires a display name.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException($"{profileType} profile '{id}' has an invalid HTTP(S) base URL.");
        }

        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
        {
            throw new InvalidDataException(
                $"{profileType} profile '{id}' must use HTTPS unless its endpoint is on this computer.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidDataException($"{profileType} profile '{id}' requires a model.");
        }

        if (timeoutSeconds is < 1 or > 3600)
        {
            throw new InvalidDataException(
                $"{profileType} profile '{id}' timeout must be between 1 and 3600 seconds.");
        }

        return baseUri;
    }

    public static Uri BuildEndpoint(string baseUrl, string relativePath)
    {
        var normalized = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/";
        return new Uri(new Uri(normalized, UriKind.Absolute), relativePath);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex ProfileIdRegex();
}
