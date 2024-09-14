using EpubSharp;
using NAudio.Wave;
using NAudio.Lame;
using System.Reflection;
using System.Speech.Synthesis;
using FFMpegCore;
using FFMpegCore.Pipes;
using FFMpegCore.Enums;
namespace AudiobookGenerator;

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
        await ConcatAccToM4bAsync(aacDir, outputBookPath);
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

    private static Task<bool> ConcatAccToM4bAsync(DirectoryInfo directoryInfo, string output)
    {
        return FFMpegArguments.FromConcatInput(directoryInfo.GetFiles().Select(f => f.FullName))
            .OutputToFile(output, true)
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
