using EpubSharp;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using System;
using System.Reflection;
using System.Speech.Synthesis;
namespace AudiobookGenerator;

internal class Program
{
    static async Task Main()
    {
        // sample input
        await Main(@"F:\Downloads\Long Chills and Case Dough by Brandon Sanderson.epub", @"F:\Downloads\Test", "en-US");
    }

    /// <summary>
    /// Generate .wav files from .txt
    /// </summary>
    /// <param name="input">Directory that contains .epub files.</param>
    /// <param name="output">Directory to put corresponding .wav files.</param>
    /// <param name="language">Language of the voice to use. Defaults to English.</param>
    static async Task Main(string input, string output, string language = "en-US")
    {
        var book = EpubReader.Read(input);
        var bookName = Path.GetFileNameWithoutExtension(input);
        var safeBookName = MakeNameFfmpegCompatible(bookName);
        var tempDir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), safeBookName));

        try
        {
            var outDir = Directory.CreateDirectory(Path.Join(output, Path.GetFileNameWithoutExtension(input)));

            var wavOutDir = tempDir.CreateSubdirectory("wav");
            var wavFiles = book.SpecialResources.HtmlInReadingOrder
                .Select((chapter, index) => ConvertChapterWavFile(chapter, index + 1, wavOutDir, language))
                .Where(path => path != null)
                .Cast<FileInfo>()
                .ToArray();

            FileInfo? coverImage = null;
            var images = book.Resources.Images;
            if (images.Any())
            {
                var imageDir = outDir.CreateSubdirectory("images");
                foreach (var image in images)
                {
                    var imageName = Path.GetFileName(image.FileName);
                    var imagePath = new FileInfo(Path.Join(imageDir.FullName, imageName));
                    Log($"Saving image {image.FileName}", ConsoleColor.Green);
                    await File.WriteAllBytesAsync(imagePath.FullName, image.Content);

                    if (coverImage == null || imageName.Contains("cover", StringComparison.OrdinalIgnoreCase))
                    {
                        coverImage = imagePath.CopyTo(Path.Join(tempDir.FullName, MakeNameFfmpegCompatible(imageName)), overwrite: true);
                        Log($"Using {coverImage.Name} as cover image", ConsoleColor.Green);
                    }
                }
            }

            Log("Done creating WAV", ConsoleColor.Green);
            Log("Generating m4b", ConsoleColor.Green);

            var convertedBook = await WavToM4bAsync(wavFiles, coverImage, new FileInfo(Path.Combine(outDir.FullName, $"{safeBookName}.m4b")));

            if (convertedBook != null)
            {
                Log("Done", ConsoleColor.Green);
            }
            else
            {
                Log("ffmpeg failed", ConsoleColor.Red);
            }
        }
        finally
        {
            Log($"Removing temp dir {tempDir}.", ConsoleColor.Yellow);
        }

        Console.ReadLine();
    }

    private static FileInfo? ConvertChapterWavFile(EpubTextFile chapter, int number, DirectoryInfo outDir, string language) 
    {
        var content = GetContentMethod.Invoke(null, [chapter.TextContent]) as string ?? throw new InvalidOperationException($"Failed to get content of {chapter.FileName}");
        if (string.IsNullOrEmpty(content))
        {
            Log($"Skipping generation for ${chapter.FileName} since content is empty.", ConsoleColor.Yellow);
            return null;
        }

        var name = $"{number.ToString("0000")}_{Path.GetFileNameWithoutExtension(chapter.FileName)}";
        var filePath = new FileInfo(Path.Join(outDir.FullName, $"{name}.wav"));

        ConvertToWav(name, content, filePath.FullName, language);

        return filePath;
    }

    private static async Task<FileInfo?> WavToM4bAsync(IEnumerable<FileInfo> files, FileInfo? coverFile, FileInfo output) 
    {
        var success = await FFMpegArguments
            .FromDemuxConcatInput(files.Select(file => file.FullName),
                options =>
                {
                    options
                        .UsingMultithreading(true)
                        .ForceFormat("mp4");

                    //if (coverFile != null)
                    //{
                    //    options.WithCustomArgument($"-i {coverFile.FullName}");
                    //}
                })
            .OutputToFile(output.FullName,
                overwrite: true,
                static options => options
                    //.WithAudioCodec("libfaac")
                    .WithAudioCodec(AudioCodec.Aac)
                    .WithAudioBitrate(AudioQuality.Normal))
            .NotifyOnOutput(message => Log(message, ConsoleColor.Gray))
            .WithLogLevel(FFMpegLogLevel.Verbose)
            .ProcessAsynchronously();

        return success ? output : null;
    }

    private static bool ConvertToWav(string name, string content, string outputFile, string language)
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

    private static void Log(string message, ConsoleColor color)
    {
        var currentColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = currentColor;
    }

    private static string MakeNameFfmpegCompatible(string name) => name.Replace(" ", "_");

    private static readonly MethodInfo GetContentMethod = typeof(EpubReader).Assembly.GetType("EpubSharp.HtmlProcessor", true)
       .GetMethod("GetContentAsPlainText", BindingFlags.Static | BindingFlags.Public, [typeof(string)])
        ?? throw new InvalidOperationException("internals of EpubSharp changed");
}
