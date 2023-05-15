using System.Speech.Synthesis;
namespace AudiobookGenerator;

internal class Program
{
    /// <summary>
    /// Generate .wav files from .txt
    /// </summary>
    /// <param name="input">Directory that contains .txt files.</param>
    /// <param name="output">Directory to put corresponding .wav files.</param>
    /// <param name="language">Language of the voice to use. Defaults to English.</param>
    static void Main(string input, string output, string language = "en-EN")
    {
        foreach (var file in Directory.GetFiles(input, "*.txt").OrderBy(file => file))
        {
            ConvertToWav(file, output, language);
        }
        Log("Done", ConsoleColor.Green);
        Console.ReadLine();

    }

    static bool ConvertToWav(string file, string outDir, string language)
    {
        try
        {
            Log($"Starting for ${file}", ConsoleColor.Yellow);
            var synth = new SpeechSynthesizer();
            var voices = synth.GetInstalledVoices();
            var voice = voices.Select(v => v.VoiceInfo).Single(v => v.Gender == VoiceGender.Female && v.Culture.Name == language);
            synth.SetOutputToWaveFile(Path.Join(outDir, $"{Path.GetFileNameWithoutExtension(file)}.wav"));
            synth.SelectVoice(voice.Name);

            var builder = new PromptBuilder(voice.Culture);
            var text = File.ReadAllText(file);
            builder.AppendText(text);

            synth.Speak(builder);

            Log($"Succeeded for ${file}", ConsoleColor.Green);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed for ${file} with ex: {ex}", ConsoleColor.Red);
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
