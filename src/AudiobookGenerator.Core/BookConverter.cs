using System.Speech.Synthesis;
using FFMpegCore;
using FFMpegCore.Pipes;
using FFMpegCore.Enums;
using System.Diagnostics;
using TagLib;
using VersOne.Epub;
using VersOne.Epub.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using System.Collections.Frozen;
using System.Drawing;

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

public abstract record BookElement(string FileName)
{
    public abstract int Size { get; }
}

public record BookChapter(string FileName, string Name, string Content) : BookElement(FileName)
{
    public override int Size => Content.Length;
}

public record BookImage(string FileName, byte[] Content) : BookElement(FileName) 
{
    public override int Size => Content.Length;
}

public readonly record struct Book(
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
    private Task?  initializeTask;

    public Task AddImagesAndTagsToM4bAsync(FileInfo m4bFile, Book bookInfo, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        static Picture ByteToPicture(byte[] bytes) => new (new ByteVector(bytes));

        using var state = progress.Start(m4bFile.Name, StageType.UpdatingM4bMetadata);

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
        using var state = progress.Start(outputFile.Name, StageType.ConvertWavToAac);
        await FFMpegArguments
            .FromPipeInput(new StreamPipeSource(wavStream))
            .OutputToFile(outputFile.FullName, true, options => options.WithAudioCodec(AudioCodec.Aac))
            .ProcessAsynchronously()
            .ConfigureAwait(false);
    }

    public async Task CreateM4bAsync(IEnumerable<FileInfo> aacChapters, FileInfo outputFile, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        using var state = progress.Start(outputFile.Name, StageType.MergingIntoM4b);

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
        await FFMpegArguments
            .FromConcatInput(files)
            .AddFileInput(chaptersFile)
            .OutputToFile(outputFile.FullName, true)
            .ProcessAsynchronously()
            .ConfigureAwait(false);
    }

    public Task InitializeAsync(IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        using var state = progress.Start("FFmpeg", StageType.Installing);
        Process process = new();
        process.StartInfo.FileName = "cmd.exe";
        process.StartInfo.Arguments = "/c winget install ffmpeg --accept-source-agreements";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        return process.WaitForExitAsync(cancellationToken);
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
        _speechSynthesizer.SpeakAsync(builder);
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

//public class PlaywrightHtmlConverter(ILogger<PlaywrightHtmlConverter> logger) : IHtmlConverter, IDisposable
//{
//    private IPlaywright? _playwright;
//    private IBrowser? _browser;

//    private async Task<IBrowser> InitializeAsync(CancellationToken cancellationToken)
//    {
//        if (_browser == null)
//        {
//            Microsoft.Playwright.Program.Main(["install"]);
//            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
//            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "msedge", Headless = true }).ConfigureAwait(false);
//        }

//        return _browser;
//    }

//    public async Task<(string Title, string Content)> HtmlToPlaineTextAsync(string htmlContent, CancellationToken cancellationToken)
//    {
//        var browser = await InitializeAsync(cancellationToken).ConfigureAwait(false);

//        var page = await browser.NewPageAsync().ConfigureAwait(false);

//        // title can't be self closing tag in order for parsing to work, but epub allos it
//        // TODO: it would be nice to have nicer workaround, but this may require using diffirent way of converting.
//        var selfClosingRegex = new Regex(@"<title\b[^>]*\s*\/>");
//        if (selfClosingRegex.IsMatch(htmlContent))
//        {
//            logger.LogWarning("Epub contains self closing title tag that breaks parsing, replacing it.");
//            htmlContent = selfClosingRegex.Replace(htmlContent, "<title></title>");
//        }

//        await page.SetContentAsync(htmlContent).ConfigureAwait(false);

//        await page.EvaluateAsync(@"() => {
//                const images = document.querySelectorAll('img');
//                images.forEach(img => {
//                    const altText = 'book image:' + img.getAttribute('alt') + ' file name ' + img.getAttribute('src')?.split('/').pop();
//                    const textNode = document.createTextNode(altText);
//                    img.parentNode.replaceChild(textNode, img);
//                });
//            }");

//        var pageText = await page.EvaluateAsync(@"() => document.body.innerText");

//        var title = await page.InnerTextAsync("title").ConfigureAwait(false);
//        var content = await page.InnerTextAsync("body").ConfigureAwait(false);
//        return (title, content);
//    }

//    public void Dispose() => _playwright?.Dispose();
//}

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
            var replaceNodes = htmlDocument.DocumentNode.SelectNodes("//img").Select(img =>
            {
                var altText = img.Attributes["alt"]?.Value ?? string.Empty;
                var fileName = img.Attributes["src"].Value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Last();
                return (Original: img, Replacement: HtmlTextNode.CreateNode($"book image: {altText} file name {fileName}"));
            });

            foreach (var (original, replacement) in replaceNodes)
            {
                original.ParentNode.ReplaceChild(replacement, original);
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
            $"{(index + 1):0000} {chapter.FileName}",
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
            await audioConverter.ConvertWavToAacAsync(stream, chapterAacOutput, progress, cancellationToken);
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
        progress.Report(new (scope, currentStage, Progress.Started));
        return new DisposeAction(progress, scope, currentStage);
    }

    private class DisposeAction(IProgress<ProgressUpdate> progress, string scope, StageType stage) : IDisposable
    {
        public void Dispose() => progress.Report(new (scope, stage, Progress.Done));
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
            StageType.ConvertTextToWav or StageType.ConvertWavToAac => StageProgrees(Scope, book.Chapters, isPartCompleted),
            StageType.SavingImage => StageProgrees(Scope, book.Images, isPartCompleted),
            _ => isPartCompleted ? currentStageValue : 0
        };

        return ToPercentage(progress);
    }

    private static double StageProgrees<T>(string scope, IEnumerable<T> parts, bool isPartCompleted) where T : BookElement
    {
        bool afterCurrent = false;
        double progress = 0;
        double total = 0;

        foreach (var part in parts)
        {
            total += part.Size;
            if (afterCurrent)
            {
                continue;
            }
            else if (part.FileName == scope)
            {
                afterCurrent = true;
                if (isPartCompleted)
                {
                    progress += part.Size;
                }
            }
        }

        Debug.Assert(!afterCurrent, "Current scope was not found.");

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

//public class Progress<TStage> : IProgress<ProgressState<TStage>> where TStage : class
//{
//    private readonly FrozenDictionary<TStage, StageState> stages;

//    public int Total { get; private set; }

//    public int Current { get; private set; }

//    public Progress(params IEnumerable<(TStage stage, int doneAt)> stages) 
//    {
//        this.stages = stages.ToFrozenDictionary(
//            static pair => pair.stage, 
//            static pair => 
//            {
//                Debug.Assert(pair.doneAt < 1);
//                return new StageState(Math.Max(pair.doneAt, 0), 0);
//            });
//        this.Total = this.stages.Values.Sum(static v => v.DoneAt);
//        this.Current = 0;
//    }

//    public void Report(ProgressState<TStage> state)
//    {
//        var stage = stages[state.CurrentStage];
//        var previousProgress = stage.Current;
//        stage.Current = state.Progress;
//        Current += state.Progress - previousProgress;
//    }

//    private class StageState(int doneAt, int current) 
//    {
//        private int current = current;

//        public int DoneAt { get; } = doneAt;

//        public int Current
//        { 
//            get => current;
//            set
//            {
//                Debug.Assert(DoneAt >= value);
//                current = Math.Min(value, DoneAt);
//            }
//        } 
//    }
//}
