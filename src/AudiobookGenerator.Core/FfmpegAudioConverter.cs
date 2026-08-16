using System.Globalization;
using System.Text;

namespace YewCone.AudiobookGenerator.Core;

public sealed class FfmpegAudioConverter(
    IProcessRunner processRunner,
    IImageNormalizer imageNormalizer,
    string ffmpegExecutable = "ffmpeg",
    string ffprobeExecutable = "ffprobe") : IAudioConverter
{
    private const int MaximumCapturedCharacters = 256 * 1024;
    private const int AttachmentMaximumDimension = 4096;
    private const int AttachmentMaximumBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object initializeGate = new();
    private Task<FfmpegToolPaths>? initializeTask;

    public async Task ConvertWavToAacAsync(
        IEnumerable<FileInfo> wavFiles,
        FileInfo outputFile,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wavFiles);
        ArgumentNullException.ThrowIfNull(outputFile);
        ArgumentNullException.ThrowIfNull(progress);

        var inputs = wavFiles.ToArray();
        if (inputs.Length == 0)
        {
            throw new InvalidOperationException($"No synthesized audio was produced for '{outputFile.Name}'.");
        }

        var tools = await EnsureInitializedAsync(progress, cancellationToken).ConfigureAwait(false);
        using var state = progress.Start(Path.GetFileNameWithoutExtension(outputFile.Name), StageType.ConvertWavToAac);
        var concatPath = CreateSiblingTempPath(outputFile, ".ffconcat");
        var tempOutput = new FileInfo(CreateSiblingTempPath(outputFile, outputFile.Extension));
        var preserveTemporaryOutput = false;

        try
        {
            await WriteConcatFileAsync(concatPath, inputs.Select(static file => file.FullName), cancellationToken).ConfigureAwait(false);
            await RunFfmpegAsync(
                tools.FfmpegPath,
                [
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-y",
                    "-f",
                    "concat",
                    "-safe",
                    "0",
                    "-i",
                    concatPath,
                    "-vn",
                    "-acodec",
                    "aac",
                    tempOutput.FullName
                ],
                "AAC conversion",
                cancellationToken).ConfigureAwait(false);
            ReplaceFile(tempOutput.FullName, outputFile.FullName, ref preserveTemporaryOutput);
        }
        finally
        {
            DeleteIfExists(concatPath);
            if (!preserveTemporaryOutput)
            {
                DeleteIfExists(tempOutput.FullName);
            }
        }
    }

    public async Task CreateM4bAsync(
        IEnumerable<AudioChapter> aacChapters,
        FileInfo outputFile,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aacChapters);
        ArgumentNullException.ThrowIfNull(outputFile);
        ArgumentNullException.ThrowIfNull(progress);

        using var state = progress.Start(Path.GetFileNameWithoutExtension(outputFile.Name), StageType.MergingIntoM4b);
        var chapters = aacChapters.ToArray();
        if (chapters.Length == 0)
        {
            throw new InvalidOperationException($"No chapter audio was produced for '{outputFile.Name}'.");
        }

        var tools = await EnsureInitializedAsync(progress, cancellationToken).ConfigureAwait(false);
        var concatPath = CreateSiblingTempPath(outputFile, ".ffconcat");
        var metadataPath = CreateSiblingTempPath(outputFile, ".chapters.ffmetadata");
        var tempOutput = new FileInfo(CreateSiblingTempPath(outputFile, outputFile.Extension));
        var preserveTemporaryOutput = false;

        try
        {
            await WriteConcatFileAsync(concatPath, chapters.Select(static chapter => chapter.AudioFile.FullName), cancellationToken).ConfigureAwait(false);
            var chapterTimings = new List<ChapterTiming>(chapters.Length);
            long currentStart = 0;
            foreach (var chapter in chapters)
            {
                var duration = await ProbeDurationMillisecondsAsync(tools.FfprobePath, chapter.AudioFile, cancellationToken).ConfigureAwait(false);
                var chapterEnd = checked(currentStart + duration);
                chapterTimings.Add(new ChapterTiming(chapter.Title, currentStart, chapterEnd));
                currentStart = chapterEnd;
            }

            await WriteChapterMetadataFileAsync(metadataPath, chapterTimings, cancellationToken).ConfigureAwait(false);
            await RunFfmpegAsync(
                tools.FfmpegPath,
                [
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-y",
                    "-f",
                    "concat",
                    "-safe",
                    "0",
                    "-i",
                    concatPath,
                    "-f",
                    "ffmetadata",
                    "-i",
                    metadataPath,
                    "-map",
                    "0:a:0",
                    "-map_metadata",
                    "1",
                    "-map_chapters",
                    "1",
                    "-c",
                    "copy",
                    tempOutput.FullName
                ],
                "M4B merge",
                cancellationToken).ConfigureAwait(false);
            ReplaceFile(tempOutput.FullName, outputFile.FullName, ref preserveTemporaryOutput);
        }
        finally
        {
            DeleteIfExists(concatPath);
            DeleteIfExists(metadataPath);
            if (!preserveTemporaryOutput)
            {
                DeleteIfExists(tempOutput.FullName);
            }
        }
    }

    public async Task AddImagesAndTagsToM4bAsync(
        FileInfo m4bFile,
        Book bookInfo,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(m4bFile);
        ArgumentNullException.ThrowIfNull(bookInfo);
        ArgumentNullException.ThrowIfNull(progress);

        var tools = await EnsureInitializedAsync(progress, cancellationToken).ConfigureAwait(false);
        using var state = progress.Start(Path.GetFileNameWithoutExtension(m4bFile.Name), StageType.UpdatingM4bMetadata);
        var metadataPath = CreateSiblingTempPath(m4bFile, ".metadata.ffmetadata");
        var tempOutput = new FileInfo(CreateSiblingTempPath(m4bFile, m4bFile.Extension));
        var preserveTemporaryOutput = false;
        var normalizedAttachments = new List<NormalizedAttachment>();

        try
        {
            foreach (var (image, index) in EnumerateAttachments(bookInfo).Select((image, index) => (image, index)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = await imageNormalizer.NormalizeAsync(
                    image,
                    AttachmentMaximumDimension,
                    AttachmentMaximumBytes,
                    cancellationToken).ConfigureAwait(false);
                var extension = normalized.MimeType switch
                {
                    "image/png" => ".png",
                    "image/jpeg" => ".jpg",
                    _ => throw new InvalidDataException(
                        $"Normalized image '{image.FileName}' produced unsupported MIME type '{normalized.MimeType}'.")
                };
                var normalizedPath = CreateSiblingTempPath(m4bFile, $".{index:0000}{extension}");
                await File.WriteAllBytesAsync(normalizedPath, normalized.Content, cancellationToken).ConfigureAwait(false);
                normalizedAttachments.Add(new(normalizedPath));
            }

            await WriteGlobalMetadataFileAsync(metadataPath, bookInfo, cancellationToken).ConfigureAwait(false);
            var arguments = new List<string>
            {
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-i",
                m4bFile.FullName
            };

            foreach (var attachment in normalizedAttachments)
            {
                arguments.Add("-i");
                arguments.Add(attachment.Path);
            }

            arguments.AddRange(
            [
                "-f",
                "ffmetadata",
                "-i",
                metadataPath,
                "-map",
                "0:a:0"
            ]);

            for (var index = 0; index < normalizedAttachments.Count; index++)
            {
                arguments.Add("-map");
                arguments.Add($"{index + 1}:v:0");
            }

            arguments.AddRange(
            [
                "-map_metadata",
                (normalizedAttachments.Count + 1).ToString(CultureInfo.InvariantCulture),
                "-map_chapters",
                "0",
                "-c",
                "copy"
            ]);

            for (var index = 0; index < normalizedAttachments.Count; index++)
            {
                arguments.Add($"-disposition:v:{index}");
                arguments.Add("attached_pic");
            }

            arguments.Add(tempOutput.FullName);
            await RunFfmpegAsync(
                tools.FfmpegPath,
                arguments,
                "M4B metadata update",
                cancellationToken).ConfigureAwait(false);
            ReplaceFile(tempOutput.FullName, m4bFile.FullName, ref preserveTemporaryOutput);
        }
        finally
        {
            DeleteIfExists(metadataPath);
            if (!preserveTemporaryOutput)
            {
                DeleteIfExists(tempOutput.FullName);
            }
            foreach (var attachment in normalizedAttachments)
            {
                DeleteIfExists(attachment.Path);
            }
        }
    }

    private async Task<FfmpegToolPaths> EnsureInitializedAsync(
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        Task<FfmpegToolPaths> initialization;
        lock (initializeGate)
        {
            if (initializeTask is null || initializeTask.IsCanceled || initializeTask.IsFaulted)
            {
                initializeTask = InitializeAsync(progress, cancellationToken);
            }

            initialization = initializeTask;
        }

        try
        {
            return await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (initializeGate)
            {
                if (ReferenceEquals(initializeTask, initialization)
                    && (initialization.IsCanceled || initialization.IsFaulted))
                {
                    initializeTask = null;
                }
            }

            throw;
        }
    }

    private Task<FfmpegToolPaths> InitializeAsync(
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var state = progress.Start("FFmpeg", StageType.Installing);
        return Task.FromResult(new FfmpegToolPaths(
            LocateRequiredTool(ffmpegExecutable, "ffmpeg"),
            LocateRequiredTool(ffprobeExecutable, "ffprobe")));
    }

    private static IEnumerable<BookImage> EnumerateAttachments(Book book)
    {
        string? coverHash = null;
        if (book.CoverImage is { Length: > 0 } coverImage)
        {
            coverHash = BookImageIdentity.CreateContentHash(coverImage);
            yield return new BookImage("cover", coverImage)
            {
                Id = coverHash
            };
        }

        foreach (var image in book.Images)
        {
            if (coverHash != null && string.Equals(image.ContentHash, coverHash, StringComparison.Ordinal))
            {
                continue;
            }

            yield return image;
        }
    }

    private static string LocateRequiredTool(string configuredName, string displayName)
    {
        var located = ExecutableLocator.Find(configuredName);
        if (!string.IsNullOrWhiteSpace(located))
        {
            return located;
        }

        throw CreateToolNotFoundException(displayName);
    }

    private async Task<long> ProbeDurationMillisecondsAsync(
        string ffprobePath,
        FileInfo inputFile,
        CancellationToken cancellationToken)
    {
        var result = await RunRequiredToolAsync(
            ffprobePath,
            "ffprobe",
            [
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
                inputFile.FullName
            ],
            "chapter duration probe",
            cancellationToken).ConfigureAwait(false);

        var text = result.StandardOutput.Trim();
        if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds)
            || double.IsNaN(seconds)
            || double.IsInfinity(seconds)
            || seconds <= 0)
        {
            throw new InvalidDataException(
                $"ffprobe returned an invalid duration for '{inputFile.Name}': '{text}'.");
        }

        return Math.Max(1L, (long)Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero));
    }

    private async Task RunFfmpegAsync(
        string ffmpegPath,
        IEnumerable<string> arguments,
        string operation,
        CancellationToken cancellationToken)
    {
        _ = await RunRequiredToolAsync(ffmpegPath, "ffmpeg", arguments, operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessRunResult> RunRequiredToolAsync(
        string executable,
        string displayName,
        IEnumerable<string> arguments,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunAsync(
                executable,
                arguments,
                cancellationToken,
                maximumCapturedCharacters: MaximumCapturedCharacters).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{operation} failed with exit code {result.ExitCode}. {FormatToolOutput(result)}");
            }

            return result;
        }
        catch (FileNotFoundException ex)
        {
            throw CreateToolNotFoundException(displayName, ex);
        }
    }

    private static string FormatToolOutput(ProcessRunResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        output = output.Trim();
        return string.IsNullOrEmpty(output)
            ? "The tool did not provide any additional diagnostics."
            : output;
    }

    private static async Task WriteConcatFileAsync(
        string path,
        IEnumerable<string> inputPaths,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine("ffconcat version 1.0");
        foreach (var inputPath in inputPaths)
        {
            _ = builder.Append("file '")
                .Append(EscapeConcatPath(inputPath))
                .AppendLine("'");
        }

        await WriteTextFileAsync(path, builder, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteChapterMetadataFileAsync(
        string path,
        IReadOnlyList<ChapterTiming> chapters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine(";FFMETADATA1");
        foreach (var chapter in chapters)
        {
            _ = builder.AppendLine("[CHAPTER]")
                .AppendLine("TIMEBASE=1/1000")
                .Append("START=")
                .AppendLine(chapter.StartMilliseconds.ToString(CultureInfo.InvariantCulture))
                .Append("END=")
                .AppendLine(chapter.EndMilliseconds.ToString(CultureInfo.InvariantCulture))
                .Append("title=")
                .AppendLine(EscapeMetadataValue(chapter.Title))
                .AppendLine();
        }

        await WriteTextFileAsync(path, builder, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteGlobalMetadataFileAsync(
        string path,
        Book book,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine(";FFMETADATA1");
        AppendMetadataEntry(builder, "title", book.Title);
        AppendMetadataEntry(builder, "album", book.Title);
        AppendMetadataEntry(builder, "comment", book.Description);
        if (book.AuthorList.Count > 0)
        {
            AppendMetadataEntry(builder, "artist", string.Join(", ", book.AuthorList));
        }

        await WriteTextFileAsync(path, builder, cancellationToken).ConfigureAwait(false);
    }

    private static void AppendMetadataEntry(StringBuilder builder, string key, string value)
    {
        _ = builder.Append(key)
            .Append('=')
            .AppendLine(EscapeMetadataValue(value));
    }

    private static string EscapeConcatPath(string path)
    {
        if (path.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidOperationException("FFmpeg concat manifests do not support file paths containing newlines.");
        }

        return path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private static string EscapeMetadataValue(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    _ = builder.Append("\\\\");
                    break;
                case '=':
                case ';':
                case '#':
                    _ = builder.Append('\\').Append(character);
                    break;
                case '\r':
                    break;
                case '\n':
                    _ = builder.Append("\\\n");
                    break;
                default:
                    _ = builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string CreateSiblingTempPath(FileInfo relatedFile, string suffix)
    {
        var stem = Path.GetFileNameWithoutExtension(relatedFile.Name);
        return relatedFile.GetFileInSameDir($".{stem}.{Guid.NewGuid():N}{suffix}");
    }

    private static Task WriteTextFileAsync(string path, StringBuilder builder, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            path,
            builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal),
            Utf8WithoutBom,
            cancellationToken);

    private static void ReplaceFile(
        string sourcePath,
        string destinationPath,
        ref bool preserveSourceOnFailure)
    {
        try
        {
            File.Move(sourcePath, destinationPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            preserveSourceOnFailure = File.Exists(sourcePath);
            var recoveryMessage = preserveSourceOnFailure
                ? $" The newly generated file remains at '{sourcePath}' for recovery."
                : string.Empty;
            throw new IOException(
                $"Could not replace '{destinationPath}'. The previous file was left unchanged.{recoveryMessage}",
                ex);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static InvalidOperationException CreateToolNotFoundException(string toolName, Exception? innerException = null) =>
        new(
            $"Audiobook conversion requires '{toolName}' on PATH. Install FFmpeg for your platform so both 'ffmpeg' and 'ffprobe' are available."
            + (OperatingSystem.IsWindows()
                ? " On Windows, 'winget install ffmpeg' installs both tools."
                : string.Empty),
            innerException);

    private sealed record FfmpegToolPaths(string FfmpegPath, string FfprobePath);

    private sealed record ChapterTiming(string Title, long StartMilliseconds, long EndMilliseconds);

    private sealed record NormalizedAttachment(string Path);
}
