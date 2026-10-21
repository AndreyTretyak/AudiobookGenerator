namespace YewCone.AudiobookGenerator.Core;

public interface IImageNarrationRenderer
{
    string Render(
        BookChapter chapter,
        IReadOnlyCollection<BookImage> images,
        string unresolvedImageText = "Image.");
}

internal sealed class ImageNarrationRenderer : IImageNarrationRenderer
{
    public string Render(
        BookChapter chapter,
        IReadOnlyCollection<BookImage> images,
        string unresolvedImageText = "Image.")
    {
        var occurrences = (chapter.ImageOccurrences ?? [])
            .ToDictionary(static occurrence => occurrence.Id, StringComparer.Ordinal);
        var imagesById = images.ToDictionary(static image => image.Id, StringComparer.Ordinal);

        return ImageNarrationMarker.Replace(chapter.Content, occurrenceId =>
        {
            if (!occurrences.TryGetValue(occurrenceId, out var occurrence))
            {
                return FormatDescription(unresolvedImageText);
            }

            if (occurrence.ImageId != null
                && imagesById.TryGetValue(occurrence.ImageId, out var image))
            {
                if (image.IsDecorative)
                {
                    return string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(image.ApprovedDescription))
                {
                    return FormatDescription(image.ApprovedDescription);
                }
            }

            return !string.IsNullOrWhiteSpace(occurrence.OriginalAltText)
                ? FormatDescription(occurrence.OriginalAltText)
                : FormatDescription(unresolvedImageText);
        });
    }

    private static string FormatDescription(string value)
    {
        var description = value.Trim();
        return description.Length == 0 || description[^1] is '.' or '!' or '?'
            ? description
            : $"{description}.";
    }
}

public static class ImageNarrationFallback
{
    public static string ForLanguage(string? language)
    {
        var primaryLanguage = language?.Split('-', '_')[0].ToLowerInvariant();
        return primaryLanguage switch
        {
            "de" => "Bild.",
            "es" => "Imagen.",
            "fr" => "Image.",
            "hi" => "चित्र।",
            "it" => "Immagine.",
            "ja" => "画像。",
            "pt" => "Imagem.",
            "ru" => "Изображение.",
            "zh" => "图像。",
            _ => "Image."
        };
    }
}
