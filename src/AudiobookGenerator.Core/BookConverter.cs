using FFMpegCore;
using FFMpegCore.Enums;

using HtmlAgilityPack;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text.RegularExpressions;

using TagLib;

using VersOne.Epub;
using VersOne.Epub.Options;

namespace YewCone.AudiobookGenerator.Core;

public static class AudioBookConverterDependencyInjectionExtensions
{
    public static IServiceCollection AddBookConverter(this IServiceCollection services, string? ttsSettingsPath = null)
    {
        ttsSettingsPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YewCone",
            "AudiobookGenerator",
            "tts-settings.json");
        var visionSettingsPath = Path.Combine(
            Path.GetDirectoryName(ttsSettingsPath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "vision-settings.json");

        return services
            .AddSingleton<IAudioConverter, FfmpegAudioConverter>()
            .AddSingleton<IHtmlConverter, HtmlAgilityPackHtmlConverter>()
            .AddSingleton<IEpubBookParser, VersOneEpubBookParser>()
            .AddSingleton(new TtsSettingsStoreOptions(ttsSettingsPath))
            .AddSingleton<ITtsSettingsStore, JsonTtsSettingsStore>()
            .AddSingleton(new VisionSettingsStoreOptions(visionSettingsPath))
            .AddSingleton<IVisionSettingsStore, JsonVisionSettingsStore>()
            .AddSingleton<OpenAiCompatibleHttpClient>()
            .AddSingleton<WindowsSpeechProvider>()
            .AddSingleton<IAudioSynthesizer, AudioSynthesizer>()
            .AddSingleton<IAudioPreviewService, WaveAudioPreviewService>()
            .AddSingleton<ITextChunker, NaturalTextChunker>()
            .AddSingleton<IImageNarrationRenderer, ImageNarrationRenderer>()
            .AddSingleton<IImageNormalizer, SkiaImageNormalizer>()
            .AddSingleton<IImageDescriptionService, ImageDescriptionService>()
            .AddSingleton<IImageDescriptionWorkflow, ImageDescriptionWorkflow>()
            .AddSingleton<IImageDescriptionProjectStore, ImageDescriptionProjectStore>()
            .AddHttpClient()
            .AddSingleton<BookConverter>();
    }
}

public sealed record BookChapter
{
    private string content;
    private readonly string[] requiredOccurrenceIds;

    public BookChapter(
        string fileName,
        string name,
        string content,
        BookImageOccurrence[]? imageOccurrences = null)
    {
        FileName = fileName;
        Name = name;
        this.content = content;
        ImageOccurrences = imageOccurrences;
        requiredOccurrenceIds = [.. ImageNarrationMarker
            .ExtractOccurrenceIds(content)
            .Order(StringComparer.Ordinal)];
    }

    public string FileName { get; init; }

    public string Name { get; init; }

    public string Content
    {
        get => content;
        set
        {
            var updatedIds = ImageNarrationMarker
                .ExtractOccurrenceIds(value)
                .Order(StringComparer.Ordinal);
            if (!requiredOccurrenceIds.SequenceEqual(updatedIds, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Chapter edits must preserve every [[image-ref:...]] marker.");
            }

            content = value;
        }
    }

    public BookImageOccurrence[]? ImageOccurrences { get; init; }
}

public record Book(
    string FileName,
    string Title,
    string Description,
    List<string> AuthorList,
    byte[]? CoverImage,
    BookChapter[] Chapters,
    BookImage[] Images,
    string? Language = null);

public interface IEpubBookParser
{
    Task<Book> ParseAsync(FileInfo fileInfo, CancellationToken token);
}

public interface IHtmlConverter
{
    Task<HtmlTextConversionResult> HtmlToPlaineTextAsync(string htmlContent, CancellationToken cancellationToken);
}

public interface IAudioConverter
{
    Task ConvertWavToAacAsync(IEnumerable<FileInfo> wavFiles, FileInfo outputFile, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken);

    Task CreateM4bAsync(IEnumerable<FileInfo> aacChapters, FileInfo outputFile, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken);

    Task AddImagesAndTagsToM4bAsync(FileInfo m4bFile, Book bookInfo, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken);
}

internal static class DirectoryInfoExtension
{
    public static string GetSubPath(this DirectoryInfo directoryInfo, string fileName) => Path.Combine(directoryInfo.FullName, fileName);

    public static FileInfo GetSubFile(this DirectoryInfo directoryInfo, string fileName) => new FileInfo(directoryInfo.GetSubPath(fileName));

    public static string GetFileInSameDir(this FileInfo fileInfo, string fileName)
    {
        _ = fileInfo.Directory ?? throw new InvalidOperationException($"Output directory for {fileInfo} not found.");
        return fileInfo.Directory.GetSubPath(fileName);
    }
}

public class FfmpegAudioConverter : IAudioConverter
{
    private readonly object initializeGate = new();
    private Task? initializeTask;

    public Task AddImagesAndTagsToM4bAsync(FileInfo m4bFile, Book bookInfo, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        static Picture ByteToPicture(byte[] bytes) => new(new ByteVector(bytes));

        using var state = progress.Start(Path.GetFileNameWithoutExtension(m4bFile.Name), StageType.UpdatingM4bMetadata);

        using var file = TagLib.File.Create(m4bFile.FullName);

        IPicture? coverImage = null;
        if (bookInfo.CoverImage != null)
        {
            coverImage = ByteToPicture(bookInfo.CoverImage);
            coverImage.Type = TagLib.PictureType.FrontCover;
        }

        file.Tag.Title = bookInfo.Title;
        file.Tag.TitleSort = bookInfo.Title;
        file.Tag.Album = bookInfo.Title;
        file.Tag.Comment = bookInfo.Description;
        file.Tag.Performers = [.. bookInfo.AuthorList];

        var allImages = bookInfo.Images.Select(i => ByteToPicture(i.Content));
        file.Tag.Pictures = coverImage != null ? [coverImage, .. allImages] : allImages.ToArray();

        file.Save();

        return Task.CompletedTask;
    }

    public async Task ConvertWavToAacAsync(IEnumerable<FileInfo> wavFiles, FileInfo outputFile, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var inputs = wavFiles.Select(static file => file.FullName).ToArray();
        if (inputs.Length == 0)
        {
            throw new InvalidOperationException($"No synthesized audio was produced for '{outputFile.Name}'.");
        }

        await EnsureInitializedAsync(progress, cancellationToken).ConfigureAwait(false);
        using var state = progress.Start(Path.GetFileNameWithoutExtension(outputFile.Name), StageType.ConvertWavToAac);
        _ = await FFMpegArguments
            .FromConcatInput(inputs)
            .OutputToFile(outputFile.FullName, true, options => options.WithAudioCodec(AudioCodec.Aac))
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously()
            .ConfigureAwait(false);
    }

    public async Task CreateM4bAsync(IEnumerable<FileInfo> aacChapters, FileInfo outputFile, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        using var state = progress.Start(Path.GetFileNameWithoutExtension(outputFile.Name), StageType.MergingIntoM4b);
        var files = aacChapters.Select(static file => file.FullName).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"No chapter audio was produced for '{outputFile.Name}'.");
        }

        await EnsureInitializedAsync(progress, cancellationToken).ConfigureAwait(false);
        var chaptersFile = outputFile.GetFileInSameDir(
            $".{Path.GetFileNameWithoutExtension(outputFile.Name)}.{Guid.NewGuid():N}.chapters.txt");
        try
        {
            using (StreamWriter stream = new StreamWriter(chaptersFile))
            {
                stream.WriteLine(";FFMETADATA1");

                long start = 0;
                foreach (var file in files)
                {
                    var mediaInfo = await FFProbe.AnalyseAsync(file, cancellationToken: cancellationToken);
                    var end = start + (long)mediaInfo.Duration.TotalMilliseconds;

                    stream.WriteLine("[CHAPTER]");
                    stream.WriteLine("TIMEBASE=1/1000");
                    stream.WriteLine($"START={start}");
                    stream.WriteLine($"END={end}");
                    stream.WriteLine($"title={Path.GetFileNameWithoutExtension(file)}");
                    stream.WriteLine("");

                    start = end + 1;
                }
            }

            _ = await FFMpegArguments
                .FromConcatInput(files)
                .AddFileInput(chaptersFile)
                .OutputToFile(outputFile.FullName, true)
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously()
                .ConfigureAwait(false);
        }
        finally
        {
            System.IO.File.Delete(chaptersFile);
        }
    }

    public async Task InitializeAsync(IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var state = progress.Start("FFmpeg", StageType.Installing);
        if (await IsFfmpegAvailableAsync(cancellationToken))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using Process process = new();
        process.StartInfo.FileName = "winget";
        process.StartInfo.ArgumentList.Add("install");
        process.StartInfo.ArgumentList.Add("ffmpeg");
        process.StartInfo.ArgumentList.Add("--accept-source-agreements");
        process.StartInfo.ArgumentList.Add("--accept-package-agreements");
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        _ = process.Start();
        await WaitForExitOrTerminateAsync(process, cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg installation failed with exit code {process.ExitCode}.");
        }
    }

    private async Task EnsureInitializedAsync(IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        Task initialization;
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
            await initialization.WaitAsync(cancellationToken);
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

    private static async Task<bool> IsFfmpegAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process process = new();
        process.StartInfo.FileName = "ffmpeg";
        process.StartInfo.ArgumentList.Add("-version");
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        try
        {
            _ = process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }

        await WaitForExitOrTerminateAsync(process, cancellationToken);
        return process.ExitCode == 0;
    }

    private static async Task WaitForExitOrTerminateAsync(Process process, CancellationToken cancellationToken)
    {
        using var cancellationRegistration = cancellationToken.Register(
            static state => TerminateProcess((Process)state!),
            process);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }
    }

    private static void TerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exited or became inaccessible during cancellation.
        }
    }
}

public class HtmlAgilityPackHtmlConverter(ILogger<HtmlAgilityPackHtmlConverter> logger) : IHtmlConverter
{
    public Task<HtmlTextConversionResult> HtmlToPlaineTextAsync(string htmlContent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // title can't be self closing tag in order for parsing to work, but epub allos it
        // TODO: it would be nice to have nicer workaround, but this may require using diffirent way of converting.
        var selfClosingRegex = new Regex(@"<title\b[^>]*\s*\/>");
        if (selfClosingRegex.IsMatch(htmlContent))
        {
            logger.LogWarning("Epub contains self closing title tag that breaks parsing, replacing it.");
            htmlContent = selfClosingRegex.Replace(htmlContent, "<title></title>");
        }

        HtmlDocument htmlDocument = new();
        htmlDocument.LoadHtml(htmlContent);

        var imageReferences = new List<HtmlImageReference>();
        var images = htmlDocument.DocumentNode.SelectNodes("//img");
        if (images != null)
        {
            for (var imageIndex = 0; imageIndex < images.Count; imageIndex++)
            {
                var image = images[imageIndex];
                if (image == null)
                {
                    continue;
                }

                var source = (HtmlEntity.DeEntitize(image.GetAttributeValue("src", string.Empty)) ?? string.Empty).Trim();
                var altText = (HtmlEntity.DeEntitize(image.GetAttributeValue("alt", string.Empty)) ?? string.Empty).Trim();
                var placeholder = $"[[_epub_image_{imageIndex:0000}_]]";
                imageReferences.Add(new(
                    placeholder,
                    source,
                    string.IsNullOrWhiteSpace(altText) ? null : altText));
                _ = image.ParentNode.ReplaceChild(HtmlTextNode.CreateNode(placeholder), image);
            }
        }

        static string GetText(HtmlDocument document, string xpath)
        {
            var nodes = document.DocumentNode.SelectNodes(xpath);

            return nodes == null
                ? string.Empty
                : string.Join(" ", nodes.Select(n => n.InnerText)).Trim();
        }

        var title = GetText(htmlDocument, "//title//text()");
        var content = GetText(htmlDocument, "//body//text()");  // if we use //body//text() then title chapter won't be anounced during narration 
        return Task.FromResult(new HtmlTextConversionResult(title, content, [.. imageReferences]));
    }
}

public class VersOneEpubBookParser(IHtmlConverter converter, ILogger<VersOneEpubBookParser> logger) : IEpubBookParser
{
    public async Task<Book> ParseAsync(FileInfo fileInfo, CancellationToken cancellationToken)
    {
        // https://os.vers.one/EpubReader/malformed-epub/index.html

        var options = new EpubReaderOptions
        {
            PackageReaderOptions = new PackageReaderOptions
            {
                IgnoreMissingToc = true,
                SkipInvalidManifestItems = true,
            },
            Epub2NcxReaderOptions = new Epub2NcxReaderOptions
            {
                IgnoreMissingContentForNavigationPoints = true
            },
            XmlReaderOptions = new XmlReaderOptions
            {
                SkipXmlHeaders = true
            }
        };

        options.ContentReaderOptions.ContentFileMissing += (sender, e) =>
        {
            // TODO Report error about e.FilePath missing
            logger.LogError($"Content file '{e.FilePath}' is missing in the epub.");
            e.SuppressException = true;
        };

        using var stream = fileInfo.OpenRead();
        var book = EpubReader.ReadBook(stream, options);

        var images = book.Content.Images.Local.Select(ConvertImage).ToArray();
        var imagesByPath = new Dictionary<string, BookImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            if (image.SourcePath != null && !imagesByPath.TryAdd(image.SourcePath, image))
            {
                logger.LogWarning("Multiple EPUB images resolve to source path {SourcePath}.", image.SourcePath);
            }
        }

        var chapterMapping = CollectChapterNames(book);

        var chapters = book.ReadingOrder.Select(c => new RawChapter(
            c.FilePath,
            chapterMapping.GetValueOrDefault(c.FilePath, ""),
            c.Content));

        var convertTask = chapters
            .Where(chapters => !string.IsNullOrWhiteSpace(chapters.Content))
            .Select(chapter => ChapterToPlainTextAsync(chapter, imagesByPath, cancellationToken));

        var plainTextChapters = await Task.WhenAll(convertTask).ConfigureAwait(false);
        var altTextByImageId = plainTextChapters
            .SelectMany(static chapter => chapter.ImageOccurrences)
            .Where(static occurrence => occurrence.ImageId != null && !string.IsNullOrWhiteSpace(occurrence.OriginalAltText))
            .GroupBy(static occurrence => occurrence.ImageId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static occurrence => occurrence.OriginalAltText!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        images = [.. images.Select(image =>
        {
            var altTexts = altTextByImageId.GetValueOrDefault(image.Id) ?? [];
            if (altTexts.Length > 1)
            {
                logger.LogWarning(
                    "Image {ImagePath} has {AltTextCount} different EPUB alt texts; preserving all and using the first as approved.",
                    image.SourcePath,
                    altTexts.Length);
            }

            return image with
            {
                SourceAltTexts = altTexts,
                ApprovedDescription = altTexts.FirstOrDefault(),
                DescriptionOrigin = altTexts.Length == 0
                    ? ImageDescriptionOrigin.None
                    : ImageDescriptionOrigin.EpubAltText
            };
        })];

        return new Book(
            Path.GetFileNameWithoutExtension(fileInfo.Name),
            book.Title,
            book.Description ?? string.Empty,
            book.AuthorList,
            book.CoverImage,
            plainTextChapters.Where(chapter => !string.IsNullOrEmpty(chapter.Content)).Select(ConvertChapter).ToArray(),
            images,
            book.Schema.Package.Metadata.Languages.FirstOrDefault()?.Language);
    }

    private Dictionary<string, string> CollectChapterNames(EpubBook book)
    {
        var mapping = new Dictionary<string, string>();

        if (book.Navigation == null)
        {
            return mapping;
        }

        void ExtractChapterMapping(EpubNavigationItem item, Dictionary<string, string> mapping)
        {
            if (item.HtmlContentFile != null)
            {
                mapping[item.HtmlContentFile.FilePath] = item.Title;
            }

            foreach (var nested in item.NestedItems)
            {
                ExtractChapterMapping(nested, mapping);
            }
        }

        foreach (var item in book.Navigation)
        {
            ExtractChapterMapping(item, mapping);
        }

        return mapping;
    }

    private async Task<Chapter> ChapterToPlainTextAsync(
        RawChapter chapter,
        IReadOnlyDictionary<string, BookImage> imagesByPath,
        CancellationToken cancellationToken)
    {
        var conversion = await converter.HtmlToPlaineTextAsync(chapter.Content, cancellationToken).ConfigureAwait(false);
        var content = conversion.Content;
        var normalizedChapterPath = EpubImagePath.Normalize(chapter.FilePath);
        var occurrences = new List<BookImageOccurrence>(conversion.Images.Length);

        for (var imageIndex = 0; imageIndex < conversion.Images.Length; imageIndex++)
        {
            var reference = conversion.Images[imageIndex];
            var resolvedPath = EpubImagePath.Resolve(chapter.FilePath, reference.Source);
            BookImage? image = null;
            if (resolvedPath != null && !imagesByPath.TryGetValue(resolvedPath, out image))
            {
                logger.LogWarning(
                    "Chapter image source {ImageSource} resolved to {ResolvedPath}, but no matching EPUB image was found.",
                    reference.Source,
                    resolvedPath);
            }

            var occurrenceId = BookImageIdentity.CreateOccurrenceId(normalizedChapterPath, imageIndex);
            occurrences.Add(new(occurrenceId, image?.Id, reference.Source, reference.AltText));
            content = content.Replace(
                reference.Placeholder,
                ImageNarrationMarker.Create(occurrenceId),
                StringComparison.Ordinal);
        }

        var fileName = Path.GetFileNameWithoutExtension(chapter.FilePath);
        var title = string.IsNullOrEmpty(chapter.Title)
            ? string.IsNullOrEmpty(conversion.Title)
                ? fileName
                : conversion.Title
            : chapter.Title;

        return new Chapter(fileName, title, content, [.. occurrences]);
    }

    private static BookChapter ConvertChapter(Chapter chapter, int index) =>
        new BookChapter(
           $"{(index + 1):0000} {chapter.FileName}", // TODO: do we need to add index here and in images?
            chapter.Title,
           chapter.Content,
           chapter.ImageOccurrences);

    private static BookImage ConvertImage(EpubLocalByteContentFile imageFile, int index) =>
        new BookImage(
           $"{(index + 1):0000} {Path.GetFileName(imageFile.FilePath)}",
           imageFile.Content)
        {
           SourcePath = EpubImagePath.Normalize(imageFile.FilePath),
           Id = BookImageIdentity.CreateSourceId(EpubImagePath.Normalize(imageFile.FilePath))
        };

    private readonly record struct RawChapter(string FilePath, string Title, string Content);

    private readonly record struct Chapter(
        string FileName,
        string Title,
        string Content,
        BookImageOccurrence[] ImageOccurrences);
}

public sealed class BookConverter(
    IEpubBookParser bookParser,
    IAudioSynthesizer synthesizer,
    IAudioPreviewService previewService,
    ITtsSettingsStore ttsSettings,
    IVisionSettingsStore visionSettings,
    IImageDescriptionService imageDescriptions,
    IImageDescriptionWorkflow imageDescriptionWorkflow,
    IImageDescriptionProjectStore imageDescriptionProjects,
    ITextChunker textChunker,
    IImageNarrationRenderer narrationRenderer,
    IAudioConverter audioConverter,
    ILogger<BookConverter> logger)
{
    public IEpubBookParser Parser { get; } = bookParser;

    public IAudioSynthesizer Synthesizer { get; } = synthesizer;

    public IAudioPreviewService Preview { get; } = previewService;

    public ITtsSettingsStore TtsSettingsStore { get; } = ttsSettings;

    public IVisionSettingsStore VisionSettingsStore { get; } = visionSettings;

    public IImageDescriptionService ImageDescriptions { get; } = imageDescriptions;

    public IImageDescriptionWorkflow ImageDescriptionWorkflow { get; } = imageDescriptionWorkflow;

    public IImageDescriptionProjectStore ImageDescriptionProjects { get; } = imageDescriptionProjects;

    public IImageNarrationRenderer NarrationRenderer { get; } = narrationRenderer;

    public async Task ConvertAsync(
        SpeechVoice voice,
        Book book,
        FileInfo output,
        DirectoryInfo tmpFileDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var emptyChapter = book.Chapters.FirstOrDefault(static chapter => string.IsNullOrWhiteSpace(chapter.Content));
        if (emptyChapter != null)
        {
            throw new InvalidOperationException(
                $"Chapter '{emptyChapter.Name}' has no narration text. Add chapter text before generating the audiobook.");
        }

        var synthesisSession = await Synthesizer.CreateSessionAsync(voice, cancellationToken);
        var bookOutDir = tmpFileDir.CreateSubdirectory($".audiobookgenerator-{Guid.NewGuid():N}");
        try
        {
            await ConvertCoreAsync(synthesisSession, book, output, bookOutDir, progress, cancellationToken);
        }
        finally
        {
            if (bookOutDir.Exists)
            {
                bookOutDir.Delete(recursive: true);
            }
        }
    }

    private async Task ConvertCoreAsync(
        IAudioSynthesisSession synthesisSession,
        Book book,
        FileInfo output,
        DirectoryInfo bookOutDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var wavDir = bookOutDir.CreateSubdirectory("wav");
        var aacDir = bookOutDir.CreateSubdirectory("aac");
        var imageDir = bookOutDir.CreateSubdirectory("images");

        foreach (var chapter in book.Chapters)
        {
            var narrationContent = NarrationRenderer.Render(
                chapter,
                book.Images,
                ImageNarrationFallback.ForLanguage(book.Language));
            if (string.IsNullOrWhiteSpace(narrationContent))
            {
                continue;
            }

            List<FileInfo> chapterWavFiles;
            progress.Report(new(chapter.Name, StageType.ConvertTextToWav, Progress.Started));
            try
            {
                var chunks = textChunker.Split(narrationContent, synthesisSession.MaximumInputCharacters);
                chapterWavFiles = new List<FileInfo>(chunks.Count);

                for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var stream = await synthesisSession.SynthesizeWavAsync(chunks[chunkIndex], cancellationToken);
                    var wavFile = wavDir.GetSubFile($"{chapter.FileName}.{chunkIndex + 1:0000}.wav");
                    await using (var outputStream = new FileStream(
                        wavFile.FullName,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 81920,
                        FileOptions.Asynchronous))
                    {
                        await stream.CopyToAsync(outputStream, cancellationToken);
                    }

                    if (wavFile.Length == 0)
                    {
                        throw new InvalidDataException($"TTS provider returned empty audio for chapter '{chapter.Name}', chunk {chunkIndex + 1}.");
                    }

                    chapterWavFiles.Add(wavFile);
                }

            }
            catch
            {
                progress.Report(new(chapter.Name, StageType.ConvertTextToWav, Progress.Failed));
                throw;
            }

            progress.Report(new(chapter.Name, StageType.ConvertTextToWav, Progress.Done));
            var chapterAacOutput = aacDir.GetSubFile($"{chapter.FileName}.aac");
            await audioConverter.ConvertWavToAacAsync(chapterWavFiles, chapterAacOutput, progress, cancellationToken);
        }

        foreach (var image in book.Images)
        {
            var path = imageDir.GetSubPath(image.FileName);
            logger.LogInformation($"Saving file {image.FileName}", ConsoleColor.Green);
            using (var stage = progress.Start(image.FileName, StageType.SavingImage))
            {
                System.IO.File.WriteAllBytes(path, image.Content);
            }
        }

        logger.LogInformation("Joining");
        await audioConverter.CreateM4bAsync(
            aacDir.GetFiles().OrderBy(static file => file.Name, StringComparer.Ordinal),
            output,
            progress,
            cancellationToken);
        logger.LogInformation("Done joining");

        logger.LogInformation($"Adding cover image");
        await audioConverter.AddImagesAndTagsToM4bAsync(output, book, progress, cancellationToken);
        logger.LogInformation("Done adding cover");

        logger.LogInformation("Done");
    }

    public async Task ConvertAsync(FileInfo input, DirectoryInfo output, string language, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var book = await Parser.ParseAsync(input, cancellationToken);
        var providerId = TtsSettings.WindowsProviderId;
        var voices = await Synthesizer.GetVoicesAsync(providerId, cancellationToken);
        var voice = voices.FirstOrDefault(v =>
            string.Equals(v.Gender, "Female", StringComparison.OrdinalIgnoreCase)
            && string.Equals(v.Culture, language, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No female voice for language '{language}' was found in provider '{providerId}'.");
        var bookFile = output.GetSubFile($"{book.FileName}.m4b");

        await ConvertAsync(voice, book, bookFile, output, progress, cancellationToken);
    }
}

public class ActionProgress<T>(Action<T> reportAction) : IProgress<T>
{
    public void Report(T value) => reportAction(value);
}

internal static class ProgressExtensions
{
    public static IDisposable Start(this IProgress<ProgressUpdate> progress, string scope, StageType currentStage)
    {
        progress.Report(new(scope, currentStage, Progress.Started));
        return new DisposeAction(progress, scope, currentStage);
    }

    private class DisposeAction(IProgress<ProgressUpdate> progress, string scope, StageType stage) : IDisposable
    {
        public void Dispose() => progress.Report(new(scope, stage, Progress.Done));
    }
}

public record ProgressUpdate(string Scope, StageType CurrentStage, Progress State)
{
    public int GetPercentage(Book book)
    {
        double progress = 0;
        double currentStageValue = 0;
        foreach (var stage in stageValues)
        {
            currentStageValue = stage.Value;
            if (CurrentStage == stage.Type)
            {
                break;
            }
            else
            {
                progress += currentStageValue;
            }
        }

        var isPartCompleted = State == Progress.Done;
        progress += CurrentStage switch
        {
            StageType.ConvertTextToWav => StageProgress(Scope, book.Chapters, static c => c.Name, static c => c.Content.Length, isPartCompleted),
            StageType.ConvertWavToAac => StageProgress(Scope, book.Chapters, static c => c.FileName, static c => c.Content.Length, isPartCompleted),
            StageType.SavingImage => StageProgress(Scope, book.Images, static g => g.FileName, static g => g.Content.Length, isPartCompleted),
            _ => isPartCompleted ? currentStageValue : 0
        };

        return ToPercentage(progress);
    }

    private static double StageProgress<T>(string scope, IEnumerable<T> parts, Func<T, string> getScopeName, Func<T, int> getSize, bool isPartCompleted)
    {
        bool afterCurrent = false;
        double progress = 0;
        double total = 0;

        foreach (var part in parts)
        {
            var size = getSize(part);
            total += size;
            if (afterCurrent)
            {
                continue;
            }
            else if (getScopeName(part) == scope)
            {
                afterCurrent = true;
                if (isPartCompleted)
                {
                    progress += size;
                }
            }
        }

        Debug.Assert(afterCurrent, "Current scope was not found.");

        return progress / total;
    }

    private static int ToPercentage(double value) => (int)Math.Round(value * 100);

    // Sum of values should be 1
    private readonly (StageType Type, double Value)[] stageValues = [
        (StageType.Installing,           0.05),
        (StageType.ConvertTextToWav,     0.50),
        (StageType.ConvertWavToAac,      0.20),
        (StageType.MergingIntoM4b,       0.20),
        (StageType.SavingImage,          0.03),
        (StageType.UpdatingM4bMetadata,  0.02)];
}

public enum StageType
{
    ConvertTextToWav,
    ConvertWavToAac,
    SavingImage,
    MergingIntoM4b,
    UpdatingM4bMetadata,
    Installing
}

public enum Progress
{
    Started,
    Done,
    Failed
}

public interface IState<T>
{
    public int Current { get; }

    public int Total { get; }

    public void Report(T current)
    {

    }
}
