using System.Speech.Synthesis;
using FFMpegCore;
using FFMpegCore.Pipes;
using FFMpegCore.Enums;
using System.Diagnostics;
using Microsoft.Playwright;
using TagLib;
using VersOne.Epub;
using VersOne.Epub.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace YewCone.AudiobookGenerator.Core;

public interface IBookConverter
{
    Task ConvertAsync(FileInfo input, DirectoryInfo output, string language, CancellationToken cancellationToken);
}

public interface IBookConverterProvider
{
    Task<IBookConverter> GetConverterAsync(CancellationToken cancellationToken);
}

public static class AudioBookConverterDependencyInjectionExtensions
{
    public static IServiceCollection AddBookConverter(this IServiceCollection services)
    {
        return services
            .AddWithIntitalizer<IHtmlConverter, PlaywrightHtmlConverter>()
            .AddWithIntitalizer<IAudioConverter, FfmpegAudioCopnverter>()
            .AddSingleton<IEpubBookParser, VersOneEpubBookParser>()
            .AddSingleton<IAudioSynthesizer, LocalAudioSynthesizer>()
            .AddSingleton<BookConverter>()
            .AddSingleton<IBookConverterProvider, BookConverterProvider>();
    }

    internal static IServiceCollection AddWithIntitalizer<TService, TImplementation>(this IServiceCollection services)
        where TImplementation : class, IInitializer, TService
        where TService : class
    {
        return services
            .AddSingleton<TImplementation>()
            .AddSingleton<TService>(sp => sp.GetRequiredService<TImplementation>())
            .AddSingleton<IInitializer>(sp => sp.GetRequiredService<TImplementation>());
    }
}

public record BookChapter(string FileName, string Name, string Content);

public record BookImage(string FileName, byte[] Content);

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

internal interface IInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IAudioSynthesizer
{
    IEnumerable<VoiceInfo> GetVoices();

    Task<Stream> SynthesizeWavFromTextAsync(string name, string content, VoiceInfo voice, CancellationToken cancellationToken);
}

internal interface IAudioConverter
{
    Task ConvertWavToAacAsync(Stream wavStream, FileInfo outputFile, CancellationToken cancellationToken);

    Task CreateM4bAsync(IEnumerable<FileInfo> aacChapters, FileInfo outputFile, CancellationToken cancellationToken);

    Task AddImagesAndTagsToM4bAsync(FileInfo m4bFile, Book bookInfo, CancellationToken cancellationToken);
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

internal class FfmpegAudioCopnverter : IAudioConverter, IInitializer
{
    public Task AddImagesAndTagsToM4bAsync(FileInfo m4bFile, Book bookInfo, CancellationToken cancellationToken)
    {
        static Picture ByteToPicture(byte[] bytes) => new Picture(new ByteVector(bytes));

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

    public async Task ConvertWavToAacAsync(Stream wavStream, FileInfo outputFile, CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromPipeInput(new StreamPipeSource(wavStream))
            .OutputToFile(outputFile.FullName, true, options => options.WithAudioCodec(AudioCodec.Aac))
            .ProcessAsynchronously();
    }

    public async Task CreateM4bAsync(IEnumerable<FileInfo> aacChapters, FileInfo outputFile, CancellationToken cancellationToken)
    {
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

        await FFMpegArguments
            .FromConcatInput(files)
            .AddFileInput(chaptersFile)
            .OutputToFile(outputFile.FullName, true)
            .ProcessAsynchronously();
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        Process process = new();
        process.StartInfo.FileName = "cmd.exe";
        process.StartInfo.Arguments = "/c winget install ffmpeg --accept-source-agreements";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        return process.WaitForExitAsync(cancellationToken);
    }
}

public class LocalAudioSynthesizer(ILogger<LocalAudioSynthesizer> logger) : IAudioSynthesizer
{
    private readonly SpeechSynthesizer _speechSynthesizer = new();

    public IEnumerable<VoiceInfo> GetVoices() => _speechSynthesizer.GetInstalledVoices().Select(voice => voice.VoiceInfo);

    public Task<Stream> SynthesizeWavFromTextAsync(string name, string content, VoiceInfo voice, CancellationToken cancellationToken)
    {
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

public class PlaywrightHtmlConverter : IHtmlConverter, IInitializer, IDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Microsoft.Playwright.Program.Main(["install"]);
        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "msedge", Headless = true }).ConfigureAwait(false);
    }

    public async Task<(string Title, string Content)> HtmlToPlaineTextAsync(string htmlContent, CancellationToken cancellationToken)
    {
        if (_browser == null)
        {
            throw new InvalidOperationException($"{nameof(InitializeAsync)} should be called before using this method");
        }

        var page = await _browser.NewPageAsync().ConfigureAwait(false);

        await page.SetContentAsync(htmlContent).ConfigureAwait(false);

        await page.EvaluateAsync(@"() => {
                const images = document.querySelectorAll('img');
                images.forEach(img => {
                    const altText = 'book image:' + img.getAttribute('alt');
                    const textNode = document.createTextNode(altText);
                    img.parentNode.replaceChild(textNode, img);
                });
            }");

        var title = await page.InnerTextAsync("title").ConfigureAwait(false);
        var content = await page.InnerTextAsync("body").ConfigureAwait(false);
        return (title, content);
    }

    public void Dispose() => _playwright?.Dispose();
}

public class VersOneEpubBookParser(IHtmlConverter converter) : IEpubBookParser
{
    public EpubReaderOptions EpubReaderOptions { get; set; } = new EpubReaderOptions();

    public async Task<Book> ParseAsync(FileInfo fileInfo, CancellationToken cancellationToken)
    {
        var stream = fileInfo.OpenRead();
        var book = EpubReader.ReadBook(stream, EpubReaderOptions);
        var convertTask = book.ReadingOrder.Select(chapter => ChapterToPlainTextAsync(chapter, cancellationToken));
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

    private async Task<(string Title, string Content)> ChapterToPlainTextAsync(EpubLocalTextContentFile chapter, CancellationToken cancellationToken)
    {
        var (title, content) = await converter.HtmlToPlaineTextAsync(chapter.Content, cancellationToken).ConfigureAwait(false);
        title = string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(chapter.FilePath) : title;
        return (title, content);
    }

    private static BookChapter ConvertChapter((string Title, string Content) chapter, int index) =>
        new BookChapter(
            $"{(index + 1):0000} {chapter.Title}",
            chapter.Title,
            chapter.Content);

    private static BookImage ConvertImage(EpubLocalByteContentFile imageFile, int index) =>
        new BookImage(
            $"{(index + 1):0000} {Path.GetFileName(imageFile.FilePath)}",
            imageFile.Content);
}

internal class BookConverter(
    IEpubBookParser bookParser,
    IAudioSynthesizer synthesizer,
    IAudioConverter audioConverter,
    ILogger<BookConverter> logger) : IBookConverter
{
    public async Task ConvertAsync(FileInfo input, DirectoryInfo output, string language, CancellationToken cancellationToken)
    {
        var book = await bookParser.ParseAsync(input, cancellationToken);

        var bookOutDir = output.CreateSubdirectory(book.FileName);
        var aacDir = bookOutDir.CreateSubdirectory("aac");
        var imageDir = bookOutDir.CreateSubdirectory("images");

        foreach (var chapter in book.Chapters)
        {
            var voice = synthesizer.GetVoices().Single(v => v.Gender == VoiceGender.Female && v.Culture.Name == language);
            using var stream = await synthesizer.SynthesizeWavFromTextAsync(chapter.Name, chapter.Content, voice, cancellationToken);

            var chapterAacOutput = aacDir.GetSubFile($"{chapter.FileName}.aac");
            await audioConverter.ConvertWavToAacAsync(stream, chapterAacOutput, cancellationToken);
        }

        foreach (var image in book.Images)
        {
            var path = imageDir.GetSubPath(image.FileName);
            logger.LogInformation($"Saving file {image.FileName}", ConsoleColor.Green);
            System.IO.File.WriteAllBytes(path, image.Content);
        }

        logger.LogInformation("Joining");
        var bookFile = bookOutDir.GetSubFile($"{book.FileName}.m4b");
        await audioConverter.CreateM4bAsync(aacDir.GetFiles(), bookFile, cancellationToken);
        logger.LogInformation("Done joining");

        logger.LogInformation($"Adding cover image");
        await audioConverter.AddImagesAndTagsToM4bAsync(bookFile, book, cancellationToken);
        logger.LogInformation("Done adding cover");

        logger.LogInformation("Done");
    }
}

internal class BookConverterProvider(IEnumerable<IInitializer> initializers, BookConverter converter) : IBookConverterProvider
{
    private Task? initTask;

    public async Task<IBookConverter> GetConverterAsync(CancellationToken cancellationToken)
    {
        initTask ??= Task.WhenAll(initializers.Select(initializer => initializer.InitializeAsync(cancellationToken)));
        await initTask.ConfigureAwait(false);
        return converter;
    }
}
