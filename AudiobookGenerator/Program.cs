using System.Speech.Synthesis;
using FFMpegCore;
using FFMpegCore.Pipes;
using FFMpegCore.Enums;
using System.Diagnostics;
using Microsoft.Playwright;
using TagLib;
using VersOne.Epub;
namespace YewCone.AudiobookGenerator;

internal class Program
{
    static async Task Main()
    {
        // sample input
        await RunAsync(@"F:\Downloads\Long Chills and Case Dough by Brandon Sanderson.epub", @"E:\Downloads\", "en-US");
    }

    private static Task InstallDependenciesAsync(CancellationToken cancellationToken) 
    {
        Microsoft.Playwright.Program.Main(["install"]);

        Process process = new Process();
        process.StartInfo.FileName = "cmd.exe";
        process.StartInfo.Arguments = "/c winget install ffmpeg --accept-source-agreements";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        process.Start();

        return process.WaitForExitAsync(cancellationToken);
    }

    /// <summary>
    /// Generate .wav files from .txt
    /// </summary>
    /// <param name="input">Directory that contains .epub files.</param>
    /// <param name="output">Directory to put corresponding .wav files.</param>
    /// <param name="language">Language of the voice to use. Defaults to English.</param>
    static async Task RunAsync(string input, string output, string language = "en-US")
    {
        CancellationToken cancellationToken = default;

        await InstallDependenciesAsync(cancellationToken);

        var book = VersOne.Epub.EpubReader.ReadBook(input);
        var bookName = Path.GetFileNameWithoutExtension(input);
        var outDir = Directory.CreateDirectory(Path.Join(output, bookName));
        var aacDir = outDir.CreateSubdirectory("aac");
        var imageDir = outDir.CreateSubdirectory("images");

        var chapterNumber = 1;
        foreach (var chapter in book.ReadingOrder)
        {
            var (title, content) = await RenderPageAsync(chapter.Content);
            if (string.IsNullOrEmpty(content))
            {
                Log($"Skipping generation for ${chapter.FilePath} since content is empty.", ConsoleColor.Yellow);
                continue;
            }

            title = string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(chapter.FilePath) : title;
            var name = $"{chapterNumber:0000} {title}";
            await ConvertTextToAacAsync(name, content, Path.Join(aacDir.FullName, $"{name}.aac"), language);
            chapterNumber++;
        }

        var fileNumber = 1;
        foreach (var file in book.Content.Images.Local)
        {
            var name = $"{fileNumber:0000} {Path.GetFileName(file.FilePath)}";
            var path = Path.Join(imageDir.FullName, name);
            Log($"Saving file {name}", ConsoleColor.Green);
            System.IO.File.WriteAllBytes(path, file.Content);
            fileNumber++;
        }

        Log("Joining", ConsoleColor.Yellow);
        var bookFileName = $"{bookName}.m4b";
        await ConcatAccToM4bAsync(aacDir, outDir, bookFileName);
        Log("Done joining", ConsoleColor.Green);

        if (book.CoverImage != null)
        {
            Log($"Adding cover image", ConsoleColor.Yellow);
            await AddCoverImageAndTagsAsync(Path.Combine(outDir.FullName, bookFileName), book);
            Log("Done adding cover", ConsoleColor.Green);
        }

        Log("Done", ConsoleColor.Green);
        Console.ReadLine();
    }

    private static async Task<(string title, string content)> RenderPageAsync(string htmlContent) 
    {
        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "msedge", Headless = true });
        var page = await browser.NewPageAsync();

        await page.SetContentAsync(htmlContent);

        await page.EvaluateAsync(@"() => {
                const images = document.querySelectorAll('img');
                images.forEach(img => {
                    const altText = 'book image:' + img.getAttribute('alt');
                    const textNode = document.createTextNode(altText);
                    img.parentNode.replaceChild(textNode, img);
                });
            }");

        var title = await page.InnerTextAsync("title");
        var content = await page.InnerTextAsync("body");
        return (title, content);
    }

    private static async Task<bool> ConvertTextToAacAsync(string name, string content, string outputFile, string language)
    {
        try
        {
            Log($"Starting for {name}", ConsoleColor.Yellow);
            var synth = new SpeechSynthesizer();
            var voices = synth.GetInstalledVoices();
            var voice = voices.Select(v => v.VoiceInfo).Single(v => v.Gender == VoiceGender.Female && v.Culture.Name == language);
            var builder = new PromptBuilder(voice.Culture);
            builder.AppendText(content);
            synth.SelectVoice(voice.Name);

            using var speechStream = new MemoryStream();
            synth.SetOutputToWaveStream(speechStream);
            synth.Speak(builder);
            speechStream.Position = 0;
            var result = await ConvertWavToAccAsync(speechStream, outputFile);

            Log($"Succeeded for {name}", ConsoleColor.Green);
            return result;
        }
        catch (Exception ex)
        {
            Log($"Failed for {name} with ex: {ex}", ConsoleColor.Red);
            return false;
        }
    }

    private static Task<bool> ConvertWavToAccAsync(Stream wavStream, string output)
    {
        return FFMpegArguments
            .FromPipeInput(new StreamPipeSource(wavStream))
            .OutputToFile(output, true, options => options.WithAudioCodec(AudioCodec.Aac))
            .ProcessAsynchronously();
    }

    private static async Task<bool> ConcatAccToM4bAsync(DirectoryInfo inputDir, DirectoryInfo outputDir, string outputFileName)
    {
        var files = inputDir.GetFiles().Select(f => f.FullName).ToArray();


        var chaptersFile = Path.Combine(outputDir.FullName, "chapters.txt");
        using (StreamWriter stream = new StreamWriter(chaptersFile))
        {
            stream.WriteLine(";FFMETADATA1");

            long start = 0;
            foreach (var file in files)
            {
                var mediaInfo = await FFProbe.AnalyseAsync(file);
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

        var output = Path.Join(outputDir.FullName, outputFileName);


        return await FFMpegArguments
            .FromConcatInput(files)
            .AddFileInput(chaptersFile)
            .OutputToFile(output, true)
            .ProcessAsynchronously();
    }

    private static Task<bool> AddCoverImageAndTagsAsync(string filePath, EpubBook book) 
    {
        static Picture ByteToPicture(byte[] bytes) => new TagLib.Picture(new ByteVector(bytes));

        var file = TagLib.File.Create(filePath);

        IPicture? coverImage = null;
        if (book.CoverImage != null) 
        {
            coverImage = ByteToPicture(book.CoverImage);
            coverImage.Type = TagLib.PictureType.FrontCover;
        } 

        file.Tag.Title = book.Title;
        file.Tag.TitleSort = book.Title;
        file.Tag.Album = book.Title;
        file.Tag.Comment = book.Description;
        file.Tag.Performers = [.. book.AuthorList];

        var allImages = book.Content.Images.Local.Select(i => ByteToPicture(i.Content));
        file.Tag.Pictures = coverImage != null ? [coverImage, ..allImages] : allImages.ToArray();

        file.Save();

        return Task.FromResult(true);
    }

    static void Log(string message, ConsoleColor color)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = currentColor;
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
