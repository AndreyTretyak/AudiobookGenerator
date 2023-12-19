using EpubSharp;

using System.Reflection;
using System.Speech.Synthesis;
namespace AudiobookGenerator;

internal class Program
{
    static void Main()
    {
        // sample input
        Main(@"E:\Downloads\Long Chills and Case Dough by Brandon Sanderson.epub", @"E:\Downloads\", "en-US");
    }

    /// <summary>
    /// Generate .wav files from .txt
    /// </summary>
    /// <param name="input">Directory that contains .epub files.</param>
    /// <param name="output">Directory to put corresponding .wav files.</param>
    /// <param name="language">Language of the voice to use. Defaults to English.</param>
    static void Main(string input, string output, string language = "en-US")
    {
        var getContent = typeof(EpubReader).Assembly.GetType("EpubSharp.HtmlProcessor", true)
            .GetMethod("GetContentAsPlainText", BindingFlags.Static | BindingFlags.Public, [typeof(string)]) 
            ?? throw new InvalidOperationException("internals of EpubSharp changed");

        var book = EpubReader.Read(input);
        var outDir = Directory.CreateDirectory(Path.Join(output, Path.GetFileNameWithoutExtension(input)));
        
        var number = 1;
        foreach (var chapter in book.SpecialResources.HtmlInReadingOrder) 
        {
            var content = getContent.Invoke(null, [chapter.TextContent]) as string ?? throw new InvalidOperationException($"Failed to get content of {chapter.FileName}");
            if (string.IsNullOrEmpty(content)) 
            {
                Log($"Skipping generation for ${chapter.FileName} since content is empty.", ConsoleColor.Yellow);
                continue;
            }
            var name = $"{number.ToString("0000")}_{Path.GetFileNameWithoutExtension(chapter.FileName)}";
            ConvertToWav(name, content, Path.Join(outDir.FullName, $"{name}.wav"), language);
            number++;
        }

        var images = book.Resources.Images;
        if (images.Any()) 
        {
            var imageDir = Directory.CreateDirectory(Path.Combine(outDir.FullName, "images"));
            foreach (var image in images)
            {
                var path = Path.Join(imageDir.FullName, Path.GetFileName(image.FileName));
                Log($"Saving image {image.FileName}", ConsoleColor.Green);
                File.WriteAllBytes(path, image.Content);
            }
        }

        Log("Done", ConsoleColor.Green);
        Console.ReadLine();
    }

    static bool ConvertToWav(string name, string content, string outputFile, string language)
    {
        try
        {
            Log($"Starting for {name}", ConsoleColor.Yellow);
            var synth = new SpeechSynthesizer();
            var voices = synth.GetInstalledVoices();
            var voice = voices.Select(v => v.VoiceInfo).Single(v => v.Gender == VoiceGender.Female && v.Culture.Name == language);
            synth.SetOutputToWaveFile(outputFile);
            synth.SelectVoice(voice.Name);

            var builder = new PromptBuilder(voice.Culture);
            builder.AppendText(content);

            synth.Speak(builder);

            Log($"Succeeded for {name}", ConsoleColor.Green);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed for {name} with ex: {ex}", ConsoleColor.Red);
            return false;
        }
    }

    static void Log(string message, ConsoleColor color)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = currentColor;
    }
}
