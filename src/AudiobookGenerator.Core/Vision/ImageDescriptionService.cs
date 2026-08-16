using System.Text;
using System.Text.Json;

namespace YewCone.AudiobookGenerator.Core;

public sealed record VisionProfileInfo(string Id, string DisplayName, string Model);

public sealed record ImageDescriptionResult(string Description, string? FinishReason);

public interface IImageDescriptionSession
{
    VisionProfileInfo Profile { get; }

    Task<ImageDescriptionResult> DescribeAsync(
        Book book,
        BookImage image,
        bool includeExistingDescription,
        CancellationToken cancellationToken);
}

public interface IImageDescriptionService
{
    Task<IReadOnlyList<VisionProfileInfo>> GetProfilesAsync(CancellationToken cancellationToken);

    Task<string?> GetDefaultProfileIdAsync(CancellationToken cancellationToken);

    Task<IImageDescriptionSession> CreateSessionAsync(
        string profileId,
        CancellationToken cancellationToken);
}

internal sealed class ImageDescriptionService(
    IVisionSettingsStore settingsStore,
    IImageNormalizer imageNormalizer,
    OpenAiCompatibleHttpClient openAiClient) : IImageDescriptionService
{
    public async Task<IReadOnlyList<VisionProfileInfo>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        return [.. settings.Profiles.Select(static profile =>
            new VisionProfileInfo(profile.Id, profile.DisplayName, profile.Model))];
    }

    public async Task<string?> GetDefaultProfileIdAsync(CancellationToken cancellationToken) =>
        (await settingsStore.LoadAsync(cancellationToken)).DefaultProfileId;

    public async Task<IImageDescriptionSession> CreateSessionAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var profile = settings.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Vision profile '{profileId}' is not configured.");
        return new OpenAiCompatibleImageDescriptionSession(
            Clone(profile),
            imageNormalizer,
            openAiClient);
    }

    private static OpenAiCompatibleVisionProfile Clone(OpenAiCompatibleVisionProfile profile) => new()
    {
        Id = profile.Id,
        DisplayName = profile.DisplayName,
        BaseUrl = profile.BaseUrl,
        Model = profile.Model,
        Prompt = profile.Prompt,
        Temperature = profile.Temperature,
        MaximumOutputTokens = profile.MaximumOutputTokens,
        TimeoutSeconds = profile.TimeoutSeconds,
        MaximumImageDimension = profile.MaximumImageDimension,
        MaximumImageBytes = profile.MaximumImageBytes,
        MaximumContextCharacters = profile.MaximumContextCharacters,
        ApiKeyEnvironmentVariable = profile.ApiKeyEnvironmentVariable
    };
}

internal sealed class OpenAiCompatibleImageDescriptionSession(
    OpenAiCompatibleVisionProfile profile,
    IImageNormalizer imageNormalizer,
    OpenAiCompatibleHttpClient openAiClient) : IImageDescriptionSession
{
    public VisionProfileInfo Profile { get; } = new(profile.Id, profile.DisplayName, profile.Model);

    public async Task<ImageDescriptionResult> DescribeAsync(
        Book book,
        BookImage image,
        bool includeExistingDescription,
        CancellationToken cancellationToken)
    {
        var normalized = await imageNormalizer.NormalizeAsync(
            image,
            profile.MaximumImageDimension,
            profile.MaximumImageBytes,
            cancellationToken);
        var context = BuildContext(book, image, includeExistingDescription, profile.MaximumContextCharacters);
        var dataUrl = $"data:{normalized.MimeType};base64,{Convert.ToBase64String(normalized.Content)}";
        var payload = new OpenAiChatCompletionsRequest(
            profile.Model,
            [
                new OpenAiSystemChatMessage(profile.Prompt),
                new OpenAiUserChatMessage(
                [
                    new OpenAiUserTextContentPart(context),
                    new OpenAiUserImageUrlContentPart(new OpenAiImageUrl(dataUrl))
                ])
            ],
            profile.Temperature,
            profile.MaximumOutputTokens);

        var responseBytes = await openAiClient.PostJsonForBytesAsync(
            new OpenAiEndpointRequestOptions(
                profile.Id,
                profile.DisplayName,
                profile.BaseUrl,
                profile.TimeoutSeconds,
                profile.ApiKeyEnvironmentVariable),
            "chat/completions",
            payload,
            OpenAiRequestJsonContext.Default.OpenAiChatCompletionsRequest,
            cancellationToken,
            includeErrorBody: false,
            maximumResponseBytes: 1024 * 1024);
        return ParseResponse(responseBytes);
    }

    private static string BuildContext(
        Book book,
        BookImage image,
        bool includeExistingDescription,
        int maximumContextCharacters)
    {
        var chapter = book.Chapters.FirstOrDefault(candidate =>
            (candidate.ImageOccurrences ?? []).Any(occurrence =>
                string.Equals(occurrence.ImageId, image.Id, StringComparison.Ordinal)));
        var builder = new StringBuilder();
        builder.AppendLine($"Book title: {book.Title}");
        if (!string.IsNullOrWhiteSpace(book.Language))
        {
            builder.AppendLine($"Book language: {book.Language}");
        }
        if (chapter != null)
        {
            builder.AppendLine($"Chapter title: {chapter.Name}");
            var nearbyText = ExtractNearbyText(chapter, image.Id, maximumContextCharacters);
            if (!string.IsNullOrWhiteSpace(nearbyText))
            {
                builder.AppendLine("Nearby narration context:");
                builder.AppendLine(nearbyText);
            }
        }
        if (includeExistingDescription && !string.IsNullOrWhiteSpace(image.ApprovedDescription))
        {
            builder.AppendLine($"Existing description to improve: {image.ApprovedDescription}");
        }

        builder.Append("Return only the proposed audiobook narration for this image.");
        return builder.ToString();
    }

    private static string ExtractNearbyText(
        BookChapter chapter,
        string imageId,
        int maximumCharacters)
    {
        if (maximumCharacters == 0)
        {
            return string.Empty;
        }

        var occurrence = (chapter.ImageOccurrences ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.ImageId, imageId, StringComparison.Ordinal));
        if (occurrence == null)
        {
            return string.Empty;
        }

        var marker = ImageNarrationMarker.Create(occurrence.Id);
        var markerIndex = chapter.Content.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var radius = maximumCharacters / 2;
        var start = Math.Max(0, markerIndex - radius);
        var end = Math.Min(chapter.Content.Length, markerIndex + marker.Length + radius);
        var context = chapter.Content[start..end];
        context = ImageNarrationMarker.Replace(context, static _ => "[Image]");
        return context.Length <= maximumCharacters
            ? context.Trim()
            : context[..maximumCharacters].Trim();
    }

    private static ImageDescriptionResult ParseResponse(byte[] responseBytes)
    {
        using var document = JsonDocument.Parse(responseBytes);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidDataException("The vision provider response did not contain a choice.");
        }

        var choice = choices[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var finish)
            && finish.ValueKind == JsonValueKind.String
                ? finish.GetString()
                : null;
        if (!choice.TryGetProperty("message", out var message))
        {
            throw new InvalidDataException("The vision provider response did not contain a message.");
        }

        var description = message.TryGetProperty("content", out var content)
            ? ReadContent(content)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(description)
            && message.TryGetProperty("refusal", out var refusal)
            && refusal.ValueKind == JsonValueKind.String)
        {
            throw new InvalidDataException($"The vision provider refused the image: {refusal.GetString()}");
        }

        description = CleanDescription(description);
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidDataException("The vision provider returned an empty description.");
        }

        return new(description, finishReason);
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            content.EnumerateArray()
                .Where(static part =>
                    part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                .Select(static part => part.GetProperty("text").GetString())
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string CleanDescription(string value)
    {
        var description = value.Trim();
        if (description.StartsWith("```", StringComparison.Ordinal)
            && description.EndsWith("```", StringComparison.Ordinal))
        {
            description = description[3..^3].Trim();
            if (description.StartsWith("text", StringComparison.OrdinalIgnoreCase))
            {
                description = description[4..].TrimStart('\r', '\n', ' ');
            }
        }

        if (description.Length >= 2
            && description[0] == '"'
            && description[^1] == '"')
        {
            description = description[1..^1].Trim();
        }

        return description;
    }
}
