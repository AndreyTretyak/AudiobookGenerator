using SkiaSharp;
using Svg.Skia;

using System.Xml;
using System.Xml.Linq;

namespace YewCone.AudiobookGenerator.Core;

public sealed record NormalizedImage(
    byte[] Content,
    string MimeType,
    int Width,
    int Height);

public interface IImageNormalizer
{
    Task<NormalizedImage> NormalizeAsync(
        BookImage image,
        int maximumDimension,
        int maximumBytes,
        CancellationToken cancellationToken);
}

internal sealed class SkiaImageNormalizer : IImageNormalizer
{
    public Task<NormalizedImage> NormalizeAsync(
        BookImage image,
        int maximumDimension,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (maximumDimension < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        }
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        return Task.Run(
            () => Normalize(image, maximumDimension, maximumBytes, cancellationToken),
            cancellationToken);
    }

    private static NormalizedImage Normalize(
        BookImage image,
        int maximumDimension,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var decoded = LooksLikeSvg(image)
            ? DecodeSvg(image.Content, maximumDimension)
            : DecodeRaster(image.Content);

        var targetDimension = Math.Min(maximumDimension, Math.Max(decoded.Width, decoded.Height));
        while (targetDimension >= 128)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var resized = Resize(decoded, targetDimension);
            var hasAlpha = resized.AlphaType != SKAlphaType.Opaque;

            if (hasAlpha)
            {
                var png = Encode(resized, SKEncodedImageFormat.Png, quality: 100);
                if (png.Length <= maximumBytes)
                {
                    return new(png, "image/png", resized.Width, resized.Height);
                }
            }
            else
            {
                foreach (var quality in new[] { 90, 80, 70, 60, 50, 40 })
                {
                    var jpeg = Encode(resized, SKEncodedImageFormat.Jpeg, quality);
                    if (jpeg.Length <= maximumBytes)
                    {
                        return new(jpeg, "image/jpeg", resized.Width, resized.Height);
                    }
                }
            }

            targetDimension = (int)Math.Floor(targetDimension * 0.8);
        }

        using var minimum = Resize(decoded, Math.Min(128, Math.Max(decoded.Width, decoded.Height)));
        using var flattened = FlattenOnWhite(minimum);
        var fallback = Encode(flattened, SKEncodedImageFormat.Jpeg, quality: 35);
        if (fallback.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"Image '{image.FileName}' cannot be normalized below {maximumBytes} bytes.");
        }

        return new(fallback, "image/jpeg", flattened.Width, flattened.Height);
    }

    private static SKBitmap DecodeRaster(byte[] content)
    {
        using var data = SKData.CreateCopy(content);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("The image format is not supported.");
        var decodeInfo = new SKImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            SKColorType.Rgba8888,
            codec.Info.AlphaType == SKAlphaType.Opaque
                ? SKAlphaType.Opaque
                : SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels(), new SKCodecOptions(frameIndex: 0));
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"The image could not be decoded ({result}).");
        }

        return AutoOrient(decoded, codec.EncodedOrigin);
    }

    private static SKBitmap DecodeSvg(byte[] content, int maximumDimension)
    {
        using var input = new MemoryStream(content, writable: false);
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 20 * 1024 * 1024
        };
        using var reader = XmlReader.Create(input, readerSettings);
        var document = XDocument.Load(reader, LoadOptions.None);
        ValidateSvgResources(document);

        using var stream = new MemoryStream();
        document.Save(stream, SaveOptions.DisableFormatting);
        stream.Position = 0;
        using var svg = new SKSvg();
        var picture = svg.Load(stream)
            ?? throw new InvalidDataException("The SVG image could not be parsed.");
        var bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException("The SVG image has no renderable dimensions.");
        }

        var scale = Math.Min(1f, maximumDimension / Math.Max(bounds.Width, bounds.Height));
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();
        return bitmap;
    }

    private static void ValidateSvgResources(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName is "script" or "foreignObject")
            {
                throw new InvalidDataException(
                    $"SVG element '{element.Name.LocalName}' is not supported for secure image normalization.");
            }

            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName is "href" or "src"
                    && !string.IsNullOrWhiteSpace(attribute.Value)
                    && !attribute.Value.StartsWith('#')
                    && !attribute.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SVG images may not reference external resources.");
                }
            }

            var style = element.Attribute("style")?.Value;
            if (!string.IsNullOrWhiteSpace(style)
                && style.Contains("url(", StringComparison.OrdinalIgnoreCase)
                && !style.Contains("url(#", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SVG styles may not reference external resources.");
            }
        }
    }

    private static SKBitmap AutoOrient(SKBitmap original, SKEncodedOrigin origin)
    {
        var width = original.Width;
        var height = original.Height;
        Action<SKCanvas> transform = static _ => { };

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                transform = canvas => canvas.Scale(-1, 1, width / 2f, height / 2f);
                break;
            case SKEncodedOrigin.BottomRight:
                transform = canvas => canvas.RotateDegrees(180, width / 2f, height / 2f);
                break;
            case SKEncodedOrigin.BottomLeft:
                transform = canvas => canvas.Scale(1, -1, width / 2f, height / 2f);
                break;
            case SKEncodedOrigin.LeftTop:
                width = original.Height;
                height = original.Width;
                transform = canvas =>
                {
                    canvas.RotateDegrees(90, width / 2f, height / 2f);
                    canvas.Scale(
                        height / (float)width,
                        -width / (float)height,
                        width / 2f,
                        height / 2f);
                };
                break;
            case SKEncodedOrigin.RightTop:
                width = original.Height;
                height = original.Width;
                transform = canvas =>
                {
                    canvas.Translate(width, 0);
                    canvas.RotateDegrees(90);
                };
                break;
            case SKEncodedOrigin.RightBottom:
                width = original.Height;
                height = original.Width;
                transform = canvas =>
                {
                    canvas.RotateDegrees(90, width / 2f, height / 2f);
                    canvas.Scale(
                        -height / (float)width,
                        width / (float)height,
                        width / 2f,
                        height / 2f);
                };
                break;
            case SKEncodedOrigin.LeftBottom:
                width = original.Height;
                height = original.Width;
                transform = canvas =>
                {
                    canvas.Translate(0, height);
                    canvas.RotateDegrees(270);
                };
                break;
        }

        var oriented = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, original.AlphaType));
        using var orientedCanvas = new SKCanvas(oriented);
        orientedCanvas.Clear(SKColors.Transparent);
        transform(orientedCanvas);
        orientedCanvas.DrawBitmap(original, 0, 0, SKSamplingOptions.Default);
        orientedCanvas.Flush();
        return oriented;
    }

    private static SKBitmap Resize(SKBitmap source, int maximumDimension)
    {
        var scale = Math.Min(1d, maximumDimension / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, source.AlphaType));
        _ = source.ScalePixels(resized.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return resized;
    }

    private static SKBitmap FlattenOnWhite(SKBitmap source)
    {
        var flattened = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgb888x, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(flattened);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        canvas.Flush();
        return flattened;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality)
            ?? throw new InvalidDataException($"The image could not be encoded as {format}.");
        return data.ToArray();
    }

    private static bool LooksLikeSvg(BookImage image)
    {
        var prefixLength = Math.Min(image.Content.Length, 4096);
        var prefix = System.Text.Encoding.UTF8.GetString(image.Content, 0, prefixLength).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return prefix.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
}
