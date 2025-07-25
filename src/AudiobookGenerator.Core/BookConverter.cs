﻿using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;

using HtmlAgilityPack;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;

using TagLib;

using VersOne.Epub;
using VersOne.Epub.Options;

namespace YewCone.AudiobookGenerator.Core;

public static class AudioBookConverterDependencyInjectionExtensions
{
    public static IServiceCollection AddBookConverter(this IServiceCollection services)
    {
        return services
            .AddSingleton<IAudioConverter, FfmpegAudioConverter>()
            .AddSingleton<IHtmlConverter, HtmlAgilityPackHtmlConverter>()
            .AddSingleton<IEpubBookParser, VersOneEpubBookParser>()
            .AddSingleton<IAudioSynthesizer, LocalAudioSynthesizer>()
            .AddSingleton<BookConverter>();
    }
}

public record BookChapter(string FileName, string Name, string Content);

public record BookImage(string FileName, byte[] Content);

public record Book(
    string FileName,
    string Title,
    string Description,
    List<string> AuthorList,
    byte[]? CoverImage,
    BookChapter[] Chapters,
    BookImage[] Images);

public interface IEpubBookParser
{
    Task<Book> ParseAsync(FileInfo fileInfo, CancellationToken token);
}

public interface IHtmlConverter
{
    Task<(string Title, string Content)> HtmlToPlaineTextAsync(string htmlContent, CancellationToken cancellationToken);
}

public interface IAudioSynthesizer
{
    IEnumerable<VoiceInfo> GetVoices();

    void Speak(string text, VoiceInfo voice);

    void StopSpeaking();

    Task<Stream> SynthesizeWavFromTextAsync(string name, string content, VoiceInfo voice, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken);
}

public interface IAudioConverter
{
    Task ConvertWavToAacAsync(Stream wavStream, FileInfo outputFile, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken);

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
    private Task? initializeTask;

    public Task AddImagesAndTagsToM4bAsync(FileInfo m4bFile, Book bookInfo, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        static Picture ByteToPicture(byte[] bytes) => new(new ByteVector(bytes));

        using var state = progress.Start(Path.GetFileNameWithoutExtension(m4bFile.Name), StageType.UpdatingM4bMetadata);

        var file = TagLib.File.Create(m4bFile.FullName);

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

    public async Task ConvertWavToAacAsync(Stream wavStream, FileInfo outputFile, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(progress, cancellationToken).ConfigureAwait(false);
        using var state = progress.Start(Path.GetFileNameWithoutExtension(outputFile.Name), StageType.ConvertWavToAac);
        _ = await FFMpegArguments
            .FromPipeInput(new StreamPipeSource(wavStream))
            .OutputToFile(outputFile.FullName, true, options => options.WithAudioCodec(AudioCodec.Aac))
            .ProcessAsynchronously()
            .ConfigureAwait(false);
    }

    public async Task CreateM4bAsync(IEnumerable<FileInfo> aacChapters, FileInfo outputFile, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        using var state = progress.Start(Path.GetFileNameWithoutExtension(outputFile.Name), StageType.MergingIntoM4b);

        var files = aacChapters.Select(f => f.FullName);

        var chaptersFile = outputFile.GetFileInSameDir("chapters.txt");
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

        await EnsureInitializedAsync(progress, cancellationToken).ConfigureAwait(false);
        _ = await FFMpegArguments
            .FromConcatInput(files)
            .AddFileInput(chaptersFile)
            .OutputToFile(outputFile.FullName, true)
            .ProcessAsynchronously()
            .ConfigureAwait(false);
    }

    public async Task InitializeAsync(IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        using var state = progress.Start("FFmpeg", StageType.Installing);
        Process process = new();
        process.StartInfo.FileName = "cmd.exe";
        process.StartInfo.Arguments = "/c winget install ffmpeg --accept-source-agreements";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        _ = process.Start();
        await process.WaitForExitAsync(cancellationToken);
    }

    private Task EnsureInitializedAsync(IProgress<ProgressUpdate> progress, CancellationToken cancellationToken) => initializeTask ??= InitializeAsync(progress, cancellationToken);
}

public class LocalAudioSynthesizer(ILogger<LocalAudioSynthesizer> logger) : IAudioSynthesizer
{
    private readonly SpeechSynthesizer _speechSynthesizer = new();

    public IEnumerable<VoiceInfo> GetVoices() => _speechSynthesizer.GetInstalledVoices().Select(voice => voice.VoiceInfo);

    public void Speak(string text, VoiceInfo voice)
    {
        var builder = new PromptBuilder(voice.Culture);
        builder.AppendText(text);
        _speechSynthesizer.SelectVoice(voice.Name);
        _speechSynthesizer.SpeakAsyncCancelAll();
        _ = _speechSynthesizer.SpeakAsync(builder);
    }

    public void StopSpeaking() => _speechSynthesizer.SpeakAsyncCancelAll();

    public Task<Stream> SynthesizeWavFromTextAsync(
        string name,
        string content,
        VoiceInfo voice,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        using var state = progress.Start(name, StageType.ConvertTextToWav);
        try
        {
            logger.LogInformation($"Starting for {name}", ConsoleColor.Yellow);
            var builder = new PromptBuilder(voice.Culture);
            builder.AppendText(content);
            _speechSynthesizer.SelectVoice(voice.Name);

            var speechStream = new MemoryStream();
            _speechSynthesizer.SetOutputToWaveStream(speechStream);
            _speechSynthesizer.Speak(builder);
            speechStream.Position = 0;

            logger.LogInformation($"Succeeded for {name}", ConsoleColor.Green);

            return Task.FromResult<Stream>(speechStream);
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed for {name} with ex: {ex}", ConsoleColor.Red);
            return Task.FromResult(Stream.Null);
        }
    }
}

public class HtmlAgilityPackHtmlConverter(ILogger<HtmlAgilityPackHtmlConverter> logger) : IHtmlConverter
{
    public Task<(string Title, string Content)> HtmlToPlaineTextAsync(string htmlContent, CancellationToken cancellationToken)
    {
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

        var images = htmlDocument.DocumentNode.SelectNodes("//img");
        if (images != null)
        {
            var replaceNodes = htmlDocument.DocumentNode.SelectNodes("//img")?.Select(img =>
            {
                var altText = img.Attributes["alt"]?.Value ?? string.Empty;
                var fileName = img.Attributes["src"].Value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Last();
                return (Original: img, Replacement: HtmlTextNode.CreateNode($"book image: {altText} file name {fileName}"));
            }) ?? [];

            foreach (var (original, replacement) in replaceNodes)
            {
                _ = original.ParentNode.ReplaceChild(replacement, original);
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
        return Task.FromResult((title, content));
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

        var chapterMapping = CollectChapterNames(book);

        var chapters = book.ReadingOrder.Select(c => new Chapter(c.FilePath, chapterMapping.GetValueOrDefault(c.FilePath, ""), c.Content));

        var convertTask = chapters
            .Where(chapters => !string.IsNullOrWhiteSpace(chapters.Content))
            .Select(chapter => ChapterToPlainTextAsync(chapter, cancellationToken));

        var plainTextChapters = await Task.WhenAll(convertTask).ConfigureAwait(false);
        return new Book(
            Path.GetFileNameWithoutExtension(fileInfo.Name),
            book.Title,
            book.Description ?? string.Empty,
            book.AuthorList,
            book.CoverImage,
            plainTextChapters.Where(chapter => !string.IsNullOrEmpty(chapter.Content)).Select(ConvertChapter).ToArray(),
            book.Content.Images.Local.Select(ConvertImage).ToArray());
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

    private async Task<Chapter> ChapterToPlainTextAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        var (parsedTitle, content) = await converter.HtmlToPlaineTextAsync(chapter.Content, cancellationToken).ConfigureAwait(false);
        var fileName = Path.GetFileNameWithoutExtension(chapter.FileName);
        var title = string.IsNullOrEmpty(chapter.Title)
            ? string.IsNullOrEmpty(parsedTitle)
                ? fileName
                : parsedTitle
            : chapter.Title;

        return new Chapter(fileName, title, content);
    }

    private static BookChapter ConvertChapter(Chapter chapter, int index) =>
        new BookChapter(
           $"{(index + 1):0000} {chapter.FileName}", // TODO: do we need to add index here and in images?
            chapter.Title,
            chapter.Content);

    private static BookImage ConvertImage(EpubLocalByteContentFile imageFile, int index) =>
        new BookImage(
            $"{(index + 1):0000} {Path.GetFileName(imageFile.FilePath)}",
            imageFile.Content);

    private readonly record struct Chapter(string FileName, string Title, string Content);
}

public class BookConverter(
    IEpubBookParser bookParser,
    IAudioSynthesizer synthesizer,
    IAudioConverter audioConverter,
    ILogger<BookConverter> logger)
{
    public async Task ConvertAsync(
        VoiceInfo voice,
        Book book,
        FileInfo output,
        DirectoryInfo tmpFileDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var bookOutDir = tmpFileDir.CreateSubdirectory(Path.GetFileNameWithoutExtension(book.FileName));
        var aacDir = bookOutDir.CreateSubdirectory("aac");
        var imageDir = bookOutDir.CreateSubdirectory("images");

        foreach (var chapter in book.Chapters)
        {
            Stream? stream = await synthesizer.SynthesizeWavFromTextAsync(chapter.Name, chapter.Content, voice, progress, cancellationToken);
            var chapterAacOutput = aacDir.GetSubFile($"{chapter.FileName}.aac");
            await audioConverter.ConvertWavToAacAsync(stream, chapterAacOutput, progress, cancellationToken); // TODO: consider passing Name, for better reporting
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
        await audioConverter.CreateM4bAsync(aacDir.GetFiles(), output, progress, cancellationToken);
        logger.LogInformation("Done joining");

        logger.LogInformation($"Adding cover image");
        await audioConverter.AddImagesAndTagsToM4bAsync(output, book, progress, cancellationToken);
        logger.LogInformation("Done adding cover");

        logger.LogInformation("Done");
    }

    public async Task ConvertAsync(FileInfo input, DirectoryInfo output, string language, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var book = await bookParser.ParseAsync(input, cancellationToken);
        var voice = synthesizer.GetVoices().Single(v => v.Gender == VoiceGender.Female && v.Culture.Name == language);
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
