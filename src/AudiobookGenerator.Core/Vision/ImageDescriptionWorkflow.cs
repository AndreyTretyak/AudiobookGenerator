namespace YewCone.AudiobookGenerator.Core;

public enum ImageDescriptionProgressState
{
    Started,
    Completed,
    Failed
}

public sealed record ImageDescriptionProgress(
    string ImageId,
    string FileName,
    int Current,
    int Total,
    ImageDescriptionProgressState State,
    string? Message = null);

public sealed record ImageDescriptionCandidate(
    string ImageId,
    string Description,
    string? FinishReason);

public sealed record ImageDescriptionFailure(
    string ImageId,
    string FileName,
    string Message);

public sealed record ImageDescriptionBatchResult(
    IReadOnlyList<ImageDescriptionCandidate> Candidates,
    IReadOnlyList<ImageDescriptionFailure> Failures);

public interface IImageDescriptionWorkflow
{
    Task<ImageDescriptionBatchResult> GenerateMissingReferencedAsync(
        Book book,
        string? profileId,
        IProgress<ImageDescriptionProgress>? progress,
        CancellationToken cancellationToken);

    Task<ImageDescriptionBatchResult> GenerateSelectedAsync(
        Book book,
        string imageId,
        string? profileId,
        bool regenerate,
        IProgress<ImageDescriptionProgress>? progress,
        CancellationToken cancellationToken);
}

public static class ImageDescriptionTransitions
{
    public static BookImage WithCandidate(BookImage image, string description) =>
        image with { CandidateDescription = RequireDescription(description) };

    public static BookImage ApproveCandidate(BookImage image, string? editedDescription = null)
    {
        var candidate = editedDescription ?? image.CandidateDescription;
        var approved = RequireDescription(candidate);
        return image with
        {
            ApprovedDescription = approved,
            CandidateDescription = null,
            DescriptionOrigin = editedDescription == null
                ? ImageDescriptionOrigin.Generated
                : ImageDescriptionOrigin.UserEdited,
            IsDecorative = false
        };
    }

    public static BookImage EditApproved(BookImage image, string description) =>
        image with
        {
            ApprovedDescription = RequireDescription(description),
            CandidateDescription = null,
            DescriptionOrigin = ImageDescriptionOrigin.UserEdited,
            IsDecorative = false
        };

    public static BookImage RejectCandidate(BookImage image) =>
        image with { CandidateDescription = null };

    public static BookImage RestoreOriginalAltText(BookImage image)
    {
        var original = image.SourceAltTexts.FirstOrDefault();
        return image with
        {
            ApprovedDescription = original,
            CandidateDescription = null,
            DescriptionOrigin = original == null
                ? ImageDescriptionOrigin.None
                : ImageDescriptionOrigin.EpubAltText,
            IsDecorative = false
        };
    }

    public static BookImage SetDecorative(BookImage image, bool isDecorative) =>
        image with { IsDecorative = isDecorative };

    private static string RequireDescription(string? value) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException("An image description cannot be empty.");
}

internal sealed class ImageDescriptionWorkflow(
    IImageDescriptionService imageDescriptionService) : IImageDescriptionWorkflow
{
    public Task<ImageDescriptionBatchResult> GenerateMissingReferencedAsync(
        Book book,
        string? profileId,
        IProgress<ImageDescriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var referencedIds = book.Chapters
            .SelectMany(static chapter => chapter.ImageOccurrences ?? [])
            .Where(static occurrence => occurrence.ImageId != null)
            .Select(static occurrence => occurrence.ImageId!)
            .ToHashSet(StringComparer.Ordinal);
        var images = book.Images.Where(image =>
            referencedIds.Contains(image.Id)
            && !image.IsDecorative
            && string.IsNullOrWhiteSpace(image.ApprovedDescription)
            && string.IsNullOrWhiteSpace(image.CandidateDescription));
        return GenerateAsync(book, images, profileId, regenerate: false, progress, cancellationToken);
    }

    public Task<ImageDescriptionBatchResult> GenerateSelectedAsync(
        Book book,
        string imageId,
        string? profileId,
        bool regenerate,
        IProgress<ImageDescriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var image = book.Images.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, imageId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Image '{imageId}' is not part of this book.");
        if (image.IsDecorative)
        {
            throw new InvalidOperationException($"Image '{image.FileName}' is marked decorative.");
        }
        if (!string.IsNullOrWhiteSpace(image.CandidateDescription))
        {
            throw new InvalidOperationException(
                $"Image '{image.FileName}' already has a generated candidate. Approve or reject it before generating another.");
        }

        return GenerateAsync(book, [image], profileId, regenerate, progress, cancellationToken);
    }

    private async Task<ImageDescriptionBatchResult> GenerateAsync(
        Book book,
        IEnumerable<BookImage> sourceImages,
        string? profileId,
        bool regenerate,
        IProgress<ImageDescriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var images = sourceImages.ToArray();
        if (images.Length == 0)
        {
            return new([], []);
        }

        profileId ??= await imageDescriptionService.GetDefaultProfileIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new InvalidOperationException("No default vision profile is configured.");
        }

        var session = await imageDescriptionService.CreateSessionAsync(profileId, cancellationToken);
        var candidates = new List<ImageDescriptionCandidate>(images.Length);
        var failures = new List<ImageDescriptionFailure>();

        for (var index = 0; index < images.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = images[index];
            progress?.Report(new(
                image.Id,
                image.FileName,
                index + 1,
                images.Length,
                ImageDescriptionProgressState.Started));
            try
            {
                var result = await session.DescribeAsync(book, image, regenerate, cancellationToken);
                candidates.Add(new(image.Id, result.Description, result.FinishReason));
                progress?.Report(new(
                    image.Id,
                    image.FileName,
                    index + 1,
                    images.Length,
                    ImageDescriptionProgressState.Completed));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new(image.Id, image.FileName, ex.Message));
                progress?.Report(new(
                    image.Id,
                    image.FileName,
                    index + 1,
                    images.Length,
                    ImageDescriptionProgressState.Failed,
                    ex.Message));
            }
        }

        return new(candidates, failures);
    }
}
