using EpubSharp;
using System.Reflection;
using System.Speech.Synthesis;
using FFMpegCore;
using FFMpegCore.Pipes;
using FFMpegCore.Enums;
using UnDotNet.HtmlToText;
using System.IO;
using System.Diagnostics;
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

        var t = VersOne.Epub.EpubReader.ReadBook(input);
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
        //    var r = HtmlToPlainText(c.Content);
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

        var coverImage = imageDir.EnumerateFiles().FirstOrDefault(i => i.Name.Contains("cover" , StringComparison.OrdinalIgnoreCase)) 
            ?? imageDir.EnumerateFiles().FirstOrDefault();

        Log("Joining", ConsoleColor.Yellow);
        var bookFileName = $"{bookName}.m4b";
        await ConcatAccToM4bAsync(aacDir, outDir, bookFileName);
        Log("Done joining", ConsoleColor.Green);

        if (coverImage != null)
        {
            Log($"Adding cover image {coverImage.FullName}", ConsoleColor.Yellow);
            await AddCoverImageAsync(Path.Combine(outDir.FullName, bookFileName), coverImage.FullName);
            Log("Done adding cover", ConsoleColor.Green);
        }

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

    private static Task<bool> AddCoverImageAsync(string filePath, string coverImagePath) 
    {
        var file = TagLib.File.Create(filePath);

        var coverImage = new TagLib.Picture(coverImagePath)
        {
            Type = TagLib.PictureType.FrontCover
        };

        file.Tag.Pictures = [coverImage];
        file.Save();

        return Task.FromResult(true);
    }

    static string HtmlToPlainText(string html)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.InnerText;
    }

    static void Log(string message, ConsoleColor color)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = currentColor;
    }
}
