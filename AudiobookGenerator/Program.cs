using System.Speech.Synthesis;
using FFMpegCore;
using FFMpegCore.Pipes;
using FFMpegCore.Enums;
using System.Diagnostics;
using Microsoft.Playwright;
using TagLib;
using VersOne.Epub;
using VersOne.Epub.Options;
using System.Net.Http;
using System.Threading;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Collections;
namespace YewCone.AudiobookGenerator;


public readonly record struct BookChapter(string FileName, string Name, string Content);

public readonly record struct BookImage(string FileName, byte[] Content);

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

public interface IInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IAudioSynthesizer
{
    IEnumerable<VoiceInfo> GetVoices();

    Task<Stream> SynthesizeWavFromTextAsync(string name, string content, VoiceInfo voice, CancellationToken cancellationToken);
}

public interface IAudioConverter
{
    Task<Stream> ConvertWavToAacAsync(Stream wavStream, CancellationToken cancellationToken);

    Task<Stream> CreateM4bAsync(IEnumerable<Stream> aacChapters, CancellationToken cancellationToken);

    Task AddImagesAndTagsToM4bAsync(CancellationToken cancellationToken);
}

internal class FfmpegAudioCopnverter : IInitializer
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

    public async Task<FileInfo> ConvertWavToAacAsync(Stream wavStream, FileInfo outputFile, CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromPipeInput(new StreamPipeSource(wavStream))
            .OutputToFile(outputFile.FullName, true, options => options.WithAudioCodec(AudioCodec.Aac))
            .ProcessAsynchronously();

        return outputFile;
    }

    public async Task<FileInfo> CreateM4bAsync(IEnumerable<FileInfo> aacChapters, FileInfo outputFile, CancellationToken cancellationToken)
    {
        var files = aacChapters.Select(f => f.FullName).ToArray();

        if (outputFile.Directory == null) 
        {
            throw new InvalidOperationException($"Output directory for {outputFile} not found.");
        }
        var outputDir = outputFile.Directory.FullName;

        var chaptersFile = Path.Combine(outputDir, "chapters.txt");
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

        var output = Path.Join(outputDir, outputFile.Name);

        await FFMpegArguments
            .FromConcatInput(files)
            .AddFileInput(chaptersFile)
            .OutputToFile(output, true)
            .ProcessAsynchronously();

        return outputFile;
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

internal class LocalAudioSynthesizer : IAudioSynthesizer
{
    private readonly SpeechSynthesizer _speechSynthesizer = new ();

    public IEnumerable<VoiceInfo> GetVoices() => _speechSynthesizer.GetInstalledVoices().Select(voice => voice.VoiceInfo);

    public Task<Stream> SynthesizeWavFromTextAsync(string name, string content, VoiceInfo voice, CancellationToken cancellationToken)
    {
        try
        {
            Program.Log($"Starting for {name}", ConsoleColor.Yellow);
            var builder = new PromptBuilder(voice.Culture);
            builder.AppendText(content);
            _speechSynthesizer.SelectVoice(voice.Name);

            var speechStream = new MemoryStream();
            _speechSynthesizer.SetOutputToWaveStream(speechStream);
            _speechSynthesizer.Speak(builder);
            speechStream.Position = 0;

            Program.Log($"Succeeded for {name}", ConsoleColor.Green);

            return Task.FromResult<Stream>(speechStream);
        }
        catch (Exception ex)
        {
            Program.Log($"Failed for {name} with ex: {ex}", ConsoleColor.Red);
            return Task.FromResult(Stream.Null);
        }
    }

    //static async Task Main(string[] args)
    //{
    //    var config = SpeechConfig.FromSubscription("YourSubscriptionKey", "YourServiceRegion");

    //    var audioConfig = AudioConfig.FromWavFileOutput("output.wav");

    //    using var synthesizer = new SpeechSynthesizer(config, audioConfig);
    //    var result = await synthesizer.SpeakTextAsync("Hello, world!");

    //    if (result.Reason == ResultReason.SynthesizingAudioCompleted)
    //    {
    //        Console.WriteLine("Speech synthesized to file successfully.");
    //    }
    //    else if (result.Reason == ResultReason.Canceled)
    //    {
    //        var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
    //        Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");
    //        Console.WriteLine($"CANCELED: ErrorDetails={cancellation.ErrorDetails}");
    //    }
    //}

    //public static async Task Main(string[] args)
    //{
    //    var playwright = await Playwright.CreateAsync();
    //    var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    //    {
    //        Channel = "msedge", // Use the Edge browser
    //        Headless = false
    //    });
    //    var page = await browser.NewPageAsync();
    //    await page.GotoAsync("https://example.com");

    //    // Inject JavaScript to use the Web Speech API for TTS
    //    await page.EvaluateAsync(@"() => {
    //        const utterance = new SpeechSynthesisUtterance('Hello, world!');
    //        speechSynthesis.speak(utterance);
    //    }");

    //    // Wait for the speech to finish
    //    await Task.Delay(5000);

    //    await browser.CloseAsync();
    //}
}

internal class PlaywrightHtmlConverter : IHtmlConverter, IInitializer, IDisposable
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

internal class VersOneEpubBookParser(IHtmlConverter converter) : IEpubBookParser
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

internal class Program
{
    static async Task Main()
    {
        // sample input
        await RunAsync(@"F:\Downloads\Long Chills and Case Dough by Brandon Sanderson.epub", @"E:\Downloads\", "en-US");
    }

    static async Task RunAsync(string input, string output, string language = "en-US")
    {
        CancellationToken cancellationToken = default;
        var htmlConverter = new PlaywrightHtmlConverter();
        var audioConverter = new FfmpegAudioCopnverter();
        await htmlConverter.InitializeAsync(cancellationToken);
        await audioConverter.InitializeAsync(cancellationToken);
        var bookParser = new VersOneEpubBookParser(htmlConverter);
        var synthesizer = new LocalAudioSynthesizer();
        var book = await bookParser.ParseAsync(new FileInfo(input), cancellationToken);

        var outDir = Directory.CreateDirectory(Path.Join(output, book.FileName));
        var aacDir = outDir.CreateSubdirectory("aac");
        var imageDir = outDir.CreateSubdirectory("images");

        foreach (var chapter in book.Chapters)
        {
            var voice = synthesizer.GetVoices().Single(v => v.Gender == VoiceGender.Female && v.Culture.Name == language);
            using var stream = await synthesizer.SynthesizeWavFromTextAsync(chapter.Name, chapter.Content, voice, cancellationToken);
            var chapterAacOutput = new FileInfo(Path.Join(aacDir.FullName, $"{chapter.FileName}.aac"));
            await audioConverter.ConvertWavToAacAsync(stream, chapterAacOutput, cancellationToken);
        }

        foreach (var image in book.Images)
        {
            var path = Path.Join(imageDir.FullName, image.FileName);
            Log($"Saving file {image.FileName}", ConsoleColor.Green);
            System.IO.File.WriteAllBytes(path, image.Content);
        }

        Log("Joining", ConsoleColor.Yellow);
        var bookFileName = $"{book.FileName}.m4b";
        var bookPath = Path.Combine(outDir.FullName, bookFileName);
        var bookFile = new FileInfo(bookPath);
        await audioConverter.CreateM4bAsync(aacDir.GetFiles(), bookFile, cancellationToken);
        Log("Done joining", ConsoleColor.Green);

        Log($"Adding cover image", ConsoleColor.Yellow);
        await audioConverter.AddImagesAndTagsToM4bAsync(bookFile, book, cancellationToken);
        Log("Done adding cover", ConsoleColor.Green);

        Log("Done", ConsoleColor.Green);
        Console.ReadLine();
    }

    internal static void Log(string message, ConsoleColor color)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = currentColor;
    }
}
