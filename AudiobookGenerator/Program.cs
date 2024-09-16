using EpubSharp;
using System.Reflection;
using System.Speech.Synthesis;
using FFMpegCore;
using FFMpegCore.Pipes;
using FFMpegCore.Enums;
using UnDotNet.HtmlToText;
using System.IO;
namespace YewCore.AudiobookGenerator;

internal class Program
{
    static async Task Main()
    {
        // sample input
        await RunAsync(@"F:\Downloads\Long Chills and Case Dough by Brandon Sanderson.epub", @"E:\Downloads\", "en-US");
    }

    /// <summary>
    /// Generate .wav files from .txt
    /// </summary>
    /// <param name="input">Directory that contains .epub files.</param>
    /// <param name="output">Directory to put corresponding .wav files.</param>
    /// <param name="language">Language of the voice to use. Defaults to English.</param>
    static async Task RunAsync(string input, string output, string language = "en-US")
    {
        var getContent = typeof(EpubReader).Assembly.GetType("EpubSharp.HtmlProcessor", true)
            ?.GetMethod("GetContentAsPlainText", BindingFlags.Static | BindingFlags.Public, [typeof(string)]) 
            ?? throw new InvalidOperationException("internals of EpubSharp changed");

        //var t = VersOne.Epub.EpubReader.ReadBook(input);
        //var converter = new HtmlToTextConverter();
        //var options = new HtmlToTextOptions()
        //{
        //    Formatters = 
        //    {
        //        { Selectors.A, static (elem, walk, builder, formatOptions) => builder.AddLiteral(elem.NodeValue) },
        //        { Selectors.Img, static (elem, walk, builder, formatOptions) => builder.AddLiteral($"Book image showing {elem.Attributes["alt"]}")}
        //    }
        //};

        //options.Img.Options.IgnoreHref = true;
        //options.A.Options.IgnoreHref = true;
        //options.Img.Options.IgnoreHref = true;
        //options.Img.Options.NoAnchorUrl = true;

        //foreach (var c in t.ReadingOrder) 
        //{
        //    var r1 = converter.Convert(c.Content, options);
        //}

        var book = EpubReader.Read(input);
        var bookName = Path.GetFileNameWithoutExtension(input);
        var outDir = Directory.CreateDirectory(Path.Join(output, bookName));
        var aacDir = outDir.CreateSubdirectory("aac");
        var imageDir = outDir.CreateSubdirectory("images");

        var chapterNumber = 1;
        foreach (var chapter in book.SpecialResources.HtmlInReadingOrder) 
        {
            var content = getContent.Invoke(null, [chapter.TextContent]) as string ?? throw new InvalidOperationException($"Failed to get content of {chapter.FileName}");
            if (string.IsNullOrEmpty(content)) 
            {
                Log($"Skipping generation for ${chapter.FileName} since content is empty.", ConsoleColor.Yellow);
                continue;
            }
            var name = $"{chapterNumber:0000}_{Path.GetFileNameWithoutExtension(chapter.FileName)}";
            await ConvertTextToAacAsync(name, content, Path.Join(aacDir.FullName, $"{name}.aac"), language);
            chapterNumber++;
        }

        var images = book.Resources.Images;
        if (images.Count > 0) 
        {
            var imageNumber = 1;
            foreach (var image in images)
            {
                var imageName = $"{imageNumber:0000}_{Path.GetFileName(image.FileName)}";
                var path = Path.Join(imageDir.FullName, imageName);
                Log($"Saving image {imageName}", ConsoleColor.Green);
                File.WriteAllBytes(path, image.Content);
                imageNumber++;
            }
        }

        var outputBookPath = Path.Combine(outDir.FullName, $"{bookName}.m4b");
        var coverImage = imageDir.EnumerateFiles().FirstOrDefault();

        Log("Joining", ConsoleColor.Yellow);
        await ConcatAccToM4bAsync(aacDir, outputBookPath, bookName);
        Log("Done joining", ConsoleColor.Green);

        //if (coverImage != null) 
        //{
        //    Log($"Adding cover image {coverImage.FullName}", ConsoleColor.Yellow);
        //    await AddCoverImageAsync(outputBookPath, coverImage.FullName);
        //    Log("Done adding cover", ConsoleColor.Green);
        //}

        Log("Done", ConsoleColor.Green);
        Console.ReadLine();
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

    private static async Task<bool> ConcatAccToM4bAsync(DirectoryInfo directoryInfo, string output, string title)
    {
        var files = directoryInfo.GetFiles().Select(f => f.FullName).ToArray();

        using var memoryStream = new MemoryStream();
        using var writer = new StreamWriter(memoryStream);
        writer.WriteLine(";FFMETADATA1");

        long start = 0;
        foreach (var file in files)
        {
            var mediaInfo = await FFProbe.AnalyseAsync(file);
            var end = start + (long)mediaInfo.Duration.TotalMilliseconds;

            writer.WriteLine("[CHAPTER]");
            writer.WriteLine("TIMEBASE=1/1000");
            writer.WriteLine($"START={start}");
            writer.WriteLine($"END={end}");
            writer.WriteLine($"title={Path.GetFileNameWithoutExtension(file)}");
            writer.WriteLine("");

            start = end + 1;
        }

        writer.Flush();
        memoryStream.Position = 0;

        var chaptersFile = Path.Combine(directoryInfo.FullName, "chapters.txt");
        using (FileStream fileStream = new FileStream(chaptersFile, FileMode.Create, FileAccess.Write))
        {
            memoryStream.Position = 0; // Reset the position to the beginning of the stream
            memoryStream.CopyTo(fileStream);
        }

        var intermediateFile = output.Replace(".m4b", ".aac");

        var result = await FFMpegArguments
            .FromConcatInput(files)
            .OutputToFile(intermediateFile, true)
            .ProcessAsynchronously();

        if (!result) return result;

        return await FFMpegArguments
            .FromFileInput(intermediateFile)
            .AddFileInput(chaptersFile)
            .OutputToFile(output, true, options => options.WithCustomArgument("-metadata:s:v title=\"Album cover\" -metadata:s:v comment=\"Need to add description\""))
            .ProcessAsynchronously();
    }

    private static Task<bool> AddCoverImageAsync(string file, string imagePath) 
    {
        return FFMpegArguments
            .FromFileInput(imagePath, verifyExists: true, options => options.Loop(1).ForceFormat("image2"))
            .AddFileInput(file)
            .OutputToFile(file, overwrite: true)
            .ProcessAsynchronously();
    }

    static void Log(string message, ConsoleColor color)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = currentColor;
    }
}
