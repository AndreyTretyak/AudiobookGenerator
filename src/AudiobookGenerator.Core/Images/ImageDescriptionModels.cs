using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using System.Text.Json.Serialization;

namespace YewCone.AudiobookGenerator.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ImageDescriptionOrigin>))]
public enum ImageDescriptionOrigin
{
    None,
    EpubAltText,
    Generated,
    UserEdited
}

public sealed record BookImage(string FileName, byte[] Content)
{
    public string Id { get; init; } = BookImageIdentity.CreateContentId(FileName, Content);

    public string ContentHash { get; init; } = BookImageIdentity.CreateContentHash(Content);

    public string? SourcePath { get; init; }

    public string[] SourceAltTexts { get; init; } = [];

    public string? ApprovedDescription { get; init; }

    public string? CandidateDescription { get; init; }

    public ImageDescriptionOrigin DescriptionOrigin { get; init; }

    public bool IsDecorative { get; init; }
}

public sealed record BookImageOccurrence(
    string Id,
    string? ImageId,
    string Source,
    string? OriginalAltText);

public sealed record HtmlImageReference(
    string Placeholder,
    string Source,
    string? AltText);

public sealed record HtmlTextConversionResult(
    string Title,
    string Content,
    HtmlImageReference[] Images);

public static class BookImageIdentity
{
    public static string CreateSourceId(string normalizedSourcePath) =>
        $"image-{HashText(normalizedSourcePath)[..24]}";

    public static string CreateContentId(string fileName, byte[] content) =>
        $"added-{HashText($"{fileName}\n{CreateContentHash(content)}")[..24]}";

    public static string CreateOccurrenceId(string normalizedChapterPath, int imageIndex) =>
        $"occurrence-{HashText($"{normalizedChapterPath}\n{imageIndex}")[..24]}";

    public static string CreateContentHash(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static partial class EpubImagePath
{
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var withoutSuffix = PathSuffixRegex().Replace(path, string.Empty).Replace('\\', '/');
        try
        {
            withoutSuffix = Uri.UnescapeDataString(withoutSuffix);
        }
        catch (UriFormatException)
        {
            // Keep the source spelling so malformed-but-readable EPUB paths can still be matched.
        }

        var segments = new List<string>();
        foreach (var segment in withoutSuffix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    public static string? Resolve(string chapterPath, string source)
    {
        if (string.IsNullOrWhiteSpace(source)
            || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || Uri.TryCreate(source, UriKind.Absolute, out _))
        {
            return null;
        }

        var normalizedSource = source.Replace('\\', '/');
        if (normalizedSource.StartsWith('/'))
        {
            return Normalize(normalizedSource);
        }

        var normalizedChapter = Normalize(chapterPath);
        var chapterDirectory = normalizedChapter.Contains('/')
            ? normalizedChapter[..normalizedChapter.LastIndexOf('/')]
            : string.Empty;
        return Normalize(string.IsNullOrEmpty(chapterDirectory)
            ? normalizedSource
            : $"{chapterDirectory}/{normalizedSource}");
    }

    [GeneratedRegex("[?#].*$")]
    private static partial Regex PathSuffixRegex();
}

public static partial class ImageNarrationMarker
{
    public static string Create(string occurrenceId) => $"[[image-ref:{occurrenceId}]]";

    public static IReadOnlyList<string> ExtractOccurrenceIds(string content) =>
        [.. MarkerRegex().Matches(content).Select(static match => match.Groups["id"].Value)];

    public static bool HasSameOccurrences(string first, string second) =>
        ExtractOccurrenceIds(first).Order(StringComparer.Ordinal)
            .SequenceEqual(ExtractOccurrenceIds(second).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    public static string Replace(
        string content,
        Func<string, string> replacement) =>
        MarkerRegex().Replace(content, match => replacement(match.Groups["id"].Value));

    [GeneratedRegex(@"\[\[image-ref:(?<id>[a-z0-9-]+)\]\]")]
    private static partial Regex MarkerRegex();
}
