using System.Text.Json.Serialization;

namespace YewCone.AudiobookGenerator.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ImageDescriptionCoverState>))]
public enum ImageDescriptionCoverState
{
    SourceDefault,
    Cleared,
    SourceImage,
    ImportedNotPersisted
}

public sealed class ImageDescriptionProject
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string SourceEpubPath { get; set; } = string.Empty;

    public string SourceEpubSha256 { get; set; } = string.Empty;

    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public VisionGenerationSnapshot? Generation { get; set; }

    public string? CoverImageContentHash { get; set; }

    public ImageDescriptionCoverState CoverState { get; set; }

    public int SkippedImportedImageCount { get; set; }

    public List<ImageDescriptionAnnotation> Images { get; set; } = [];
}

public sealed class VisionGenerationSnapshot
{
    public string? ProfileId { get; set; }

    public string? Model { get; set; }

    public string? Prompt { get; set; }

    public double? Temperature { get; set; }

    public int? MaximumOutputTokens { get; set; }

    public int? MaximumImageDimension { get; set; }

    public int? MaximumImageBytes { get; set; }

    public int? MaximumContextCharacters { get; set; }
}

public sealed class ImageDescriptionAnnotation
{
    public string ImageId { get; set; } = string.Empty;

    public string? SourcePath { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public string? ApprovedDescription { get; set; }

    public string? CandidateDescription { get; set; }

    public ImageDescriptionOrigin DescriptionOrigin { get; set; }

    public bool IsDecorative { get; set; }
}

public sealed record ImageDescriptionProjectApplyResult(
    Book Book,
    bool SourceFingerprintMatches,
    IReadOnlyList<string> UnmatchedImageIds);

public sealed record ImageDescriptionProjectSaveResult(
    int SkippedImportedImageCount,
    bool CoverSelectionNotPersisted);

public interface IImageDescriptionProjectStore
{
    string GetSidecarPath(FileInfo audiobookFile);

    FileInfo ResolveSourceEpub(
        FileInfo sidecarFile,
        ImageDescriptionProject project);

    Task<ImageDescriptionProjectSaveResult> SaveAsync(
        FileInfo sidecarFile,
        FileInfo sourceEpub,
        Book book,
        string? visionProfileId,
        CancellationToken cancellationToken);

    Task<ImageDescriptionProject> LoadAsync(
        FileInfo sidecarFile,
        CancellationToken cancellationToken);

    Task<ImageDescriptionProjectApplyResult> ApplyAsync(
        ImageDescriptionProject project,
        FileInfo sourceEpub,
        Book book,
        CancellationToken cancellationToken);
}

internal sealed class ImageDescriptionProjectStore(
    IVisionSettingsStore visionSettingsStore) : IImageDescriptionProjectStore
{
    public string GetSidecarPath(FileInfo audiobookFile) =>
        Path.ChangeExtension(audiobookFile.FullName, ".audiobook.json");

    public FileInfo ResolveSourceEpub(
        FileInfo sidecarFile,
        ImageDescriptionProject project)
    {
        var path = project.SourceEpubPath;
        if (!OperatingSystem.IsWindows()
            && ((path.Length >= 3
                    && char.IsAsciiLetter(path[0])
                    && path[1] == ':'
                    && path[2] is '\\' or '/')
                || path.StartsWith(@"\\", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The sidecar contains an absolute Windows source path that cannot be resolved on this platform.");
        }
        if (OperatingSystem.IsWindows() && path.StartsWith('/'))
        {
            throw new InvalidDataException(
                "The sidecar contains an absolute Unix source path that cannot be resolved on Windows.");
        }

        return Path.IsPathRooted(path)
            ? new FileInfo(path)
            : new FileInfo(Path.GetFullPath(
                path.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar),
                sidecarFile.DirectoryName ?? Environment.CurrentDirectory));
    }

    public async Task<ImageDescriptionProjectSaveResult> SaveAsync(
        FileInfo sidecarFile,
        FileInfo sourceEpub,
        Book book,
        string? visionProfileId,
        CancellationToken cancellationToken)
    {
        var sourceImages = book.Images
            .Where(static image => image.SourcePath != null)
            .ToArray();
        var skippedImportedImageCount = book.Images.Length - sourceImages.Length;
        var coverHash = book.CoverImage == null
            ? null
            : BookImageIdentity.CreateContentHash(book.CoverImage);
        var persistedCover = coverHash == null
            ? null
            : sourceImages.FirstOrDefault(image =>
                string.Equals(image.ContentHash, coverHash, StringComparison.OrdinalIgnoreCase));
        var coverSelectionNotPersisted = coverHash != null && persistedCover == null;
        var coverState = coverHash == null
            ? ImageDescriptionCoverState.Cleared
            : persistedCover != null
                ? ImageDescriptionCoverState.SourceImage
                : ImageDescriptionCoverState.ImportedNotPersisted;

        var project = new ImageDescriptionProject
        {
            SourceEpubPath = CreatePortableSourcePath(sidecarFile, sourceEpub),
            SourceEpubSha256 = await ComputeHashAsync(sourceEpub, cancellationToken),
            SavedAtUtc = DateTimeOffset.UtcNow,
            Generation = await CreateGenerationSnapshotAsync(visionProfileId, cancellationToken),
            CoverImageContentHash = persistedCover?.ContentHash,
            CoverState = coverState,
            SkippedImportedImageCount = skippedImportedImageCount,
            Images = [.. sourceImages.Select(static image => new ImageDescriptionAnnotation
            {
                ImageId = image.Id,
                SourcePath = image.SourcePath,
                ContentHash = image.ContentHash,
                ApprovedDescription = image.ApprovedDescription,
                CandidateDescription = image.CandidateDescription,
                DescriptionOrigin = image.DescriptionOrigin,
                IsDecorative = image.IsDecorative
            })]
        };

        await AtomicJsonSettingsFile.SaveAsync(
            sidecarFile.FullName,
            project,
            AudiobookFileJsonContext.Default.ImageDescriptionProject,
            cancellationToken);
        return new(skippedImportedImageCount, coverSelectionNotPersisted);
    }

    public async Task<ImageDescriptionProject> LoadAsync(
        FileInfo sidecarFile,
        CancellationToken cancellationToken)
    {
        var project = await AtomicJsonSettingsFile.LoadAsync<ImageDescriptionProject>(
            sidecarFile.FullName,
            "Audiobook image-description project",
            static () => throw new InvalidDataException("A sidecar project file is required."),
            AudiobookFileJsonContext.Default.ImageDescriptionProject,
            cancellationToken);
        if (project.SchemaVersion != ImageDescriptionProject.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Sidecar schema version {project.SchemaVersion} is not supported. Expected {ImageDescriptionProject.CurrentSchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(project.SourceEpubPath)
            || string.IsNullOrWhiteSpace(project.SourceEpubSha256))
        {
            throw new InvalidDataException("The sidecar does not identify its source EPUB.");
        }
        if (project.CoverState == ImageDescriptionCoverState.SourceImage
            && string.IsNullOrWhiteSpace(project.CoverImageContentHash))
        {
            throw new InvalidDataException("The sidecar identifies a source cover without its image hash.");
        }

        project.Images ??= [];
        return project;
    }

    public async Task<ImageDescriptionProjectApplyResult> ApplyAsync(
        ImageDescriptionProject project,
        FileInfo sourceEpub,
        Book book,
        CancellationToken cancellationToken)
    {
        var sourceHash = await ComputeHashAsync(sourceEpub, cancellationToken);
        var sourceMatches = string.Equals(
            sourceHash,
            project.SourceEpubSha256,
            StringComparison.OrdinalIgnoreCase);
        var remaining = new List<ImageDescriptionAnnotation>(project.Images);
        var updatedImages = new List<BookImage>(book.Images.Length);
        var appliedIds = new HashSet<string>(StringComparer.Ordinal);
        var currentHashCounts = book.Images
            .GroupBy(static image => image.ContentHash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var image in book.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var annotation = remaining.FirstOrDefault(candidate =>
                string.Equals(candidate.ImageId, image.Id, StringComparison.Ordinal)
                && string.Equals(candidate.ContentHash, image.ContentHash, StringComparison.OrdinalIgnoreCase));
            if (annotation == null)
            {
                var hashMatches = remaining.Where(candidate =>
                    string.Equals(candidate.ContentHash, image.ContentHash, StringComparison.OrdinalIgnoreCase)).ToArray();
                annotation = hashMatches.Length == 1
                    && currentHashCounts.GetValueOrDefault(image.ContentHash) == 1
                        ? hashMatches[0]
                        : null;
            }

            if (annotation == null)
            {
                updatedImages.Add(image);
                continue;
            }

            _ = remaining.Remove(annotation);
            appliedIds.Add(annotation.ImageId);
            updatedImages.Add(image with
            {
                ApprovedDescription = annotation.ApprovedDescription,
                CandidateDescription = annotation.CandidateDescription,
                DescriptionOrigin = annotation.DescriptionOrigin,
                IsDecorative = annotation.IsDecorative
            });
        }

        var unmatched = project.Images
            .Where(annotation => !appliedIds.Contains(annotation.ImageId))
            .Select(static annotation => annotation.ImageId)
            .ToList();
        var coverImage = project.CoverState == ImageDescriptionCoverState.Cleared
            ? null
            : book.CoverImage;
        if (project.CoverState == ImageDescriptionCoverState.SourceImage
            && project.CoverImageContentHash != null)
        {
            var coverMatches = updatedImages.Where(image => string.Equals(
                image.ContentHash,
                project.CoverImageContentHash,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (coverMatches.Length == 1)
            {
                coverImage = coverMatches[0].Content;
            }
            else
            {
                unmatched.Add($"cover:{project.CoverImageContentHash}");
            }
        }

        return new(
            book with { Images = [.. updatedImages], CoverImage = coverImage },
            sourceMatches,
            unmatched);
    }

    private async Task<VisionGenerationSnapshot?> CreateGenerationSnapshotAsync(
        string? profileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        var settings = await visionSettingsStore.LoadAsync(cancellationToken);
        var profile = settings.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase));
        return profile == null
            ? new VisionGenerationSnapshot { ProfileId = profileId }
            : new VisionGenerationSnapshot
            {
                ProfileId = profile.Id,
                Model = profile.Model,
                Prompt = profile.Prompt,
                Temperature = profile.Temperature,
                MaximumOutputTokens = profile.MaximumOutputTokens,
                MaximumImageDimension = profile.MaximumImageDimension,
                MaximumImageBytes = profile.MaximumImageBytes,
                MaximumContextCharacters = profile.MaximumContextCharacters
            };
    }

    private static async Task<string> ComputeHashAsync(
        FileInfo file,
        CancellationToken cancellationToken)
    {
        await using var stream = file.OpenRead();
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static string CreatePortableSourcePath(FileInfo sidecarFile, FileInfo sourceEpub)
    {
        var baseDirectory = sidecarFile.DirectoryName ?? Environment.CurrentDirectory;
        var relative = Path.GetRelativePath(baseDirectory, sourceEpub.FullName);
        return Path.IsPathRooted(relative)
            ? sourceEpub.FullName
            : relative.Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
