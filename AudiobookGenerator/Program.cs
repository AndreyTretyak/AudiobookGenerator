using EpubSharp;
using NAudio.Wave;
using NAudio.Lame;
using System.Reflection;
using System.Speech.Synthesis;
namespace AudiobookGenerator;

internal class Program
{
    static void Main()
    {
        // sample input
        Main(@"F:\Downloads\Long Chills and Case Dough by Brandon Sanderson.epub", @"E:\Downloads\", "en-US");
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
            ?.GetMethod("GetContentAsPlainText", BindingFlags.Static | BindingFlags.Public, [typeof(string)]) 
            ?? throw new InvalidOperationException("internals of EpubSharp changed");

        var book = EpubReader.Read(input);
        var bookName = Path.GetFileNameWithoutExtension(input);
        var outDir = Directory.CreateDirectory(Path.Join(output, bookName));
        var mp3Dir = outDir.CreateSubdirectory("mp3");
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
            ConvertTextToMp3(name, content, Path.Join(mp3Dir.FullName, $"{name}.mp3"), language);
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

        Log("Done", ConsoleColor.Green);
        Console.ReadLine();
    }

    private static bool ConvertTextToMp3(string name, string content, string outputFile, string language)
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
            // synth.SetOutputToWaveFile(outputFile);

            synth.SetOutputToWaveStream(speechStream);
            synth.Speak(builder);
            speechStream.Position = 0;
            ConvertWavToMp3(speechStream, outputFile);

            Log($"Succeeded for {name}", ConsoleColor.Green);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed for {name} with ex: {ex}", ConsoleColor.Red);
            return false;
        }
    }

    private static void ConvertWavToMp3(Stream wavStream, string mp3File)
    {
        using var reader = new WaveFileReader(wavStream);
        using var writer = new LameMP3FileWriter(mp3File, reader.WaveFormat, LAMEPreset.VBR_90);
        reader.CopyTo(writer);
    }

    static void Log(string message, ConsoleColor color)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = currentColor;
    }
}
