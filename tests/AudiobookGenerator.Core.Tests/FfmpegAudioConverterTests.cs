using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SkiaSharp;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using Xunit;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Core.Tests;

public sealed class FfmpegAudioConverterTests
{
    [RequiresFfmpegFact]
    public async Task FullConversionProducesTaggedM4bWithOrderedChaptersAndAttachedImages()
    {
        var ffmpeg = TestExecutableResolver.FindFfmpeg();
        var ffprobe = TestExecutableResolver.FindFfprobe();
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(
            temporary.SettingsPath,
            new RecordingHandler(static (_, _) => Task.FromResult(WaveResponse())),
            ffmpeg,
            ffprobe);
        var store = services.GetRequiredService<ITtsSettingsStore>();
        await store.SaveAsync(CreateSettings(), CancellationToken.None);
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));
        var converter = services.GetRequiredService<BookConverter>();
        var cover = CreateRasterImage(320, 320, SKColors.Firebrick, SKEncodedImageFormat.Png);
        var imageTwo = CreateRasterImage(200, 120, SKColors.ForestGreen, SKEncodedImageFormat.Jpeg);
        var imageThree = CreateRasterImage(180, 260, SKColors.RoyalBlue, SKEncodedImageFormat.Png);
        var title = "Тест \"Title\" = #1 \\\\ 路径";
        var description = "Первый ряд;\nSecond line = #2 \\\\ 路径";
        var chapterOneTitle = "Первая \"глава\" = #1 C:\\Books\\One";
        var chapterTwoTitle = "第二章; fin #2";
        var authors = new List<string> { "Автор One", "作者二" };
        var output = new FileInfo(Path.Combine(temporary.Path, "unicode-book.m4b"));
        var book = new Book(
            "unicode-book",
            title,
            description,
            authors,
            cover,
            [
                new BookChapter("0001 chapter", chapterOneTitle, "First short chapter."),
                new BookChapter("0002 chapter", chapterTwoTitle, "Second short chapter.")
            ],
            [
                new BookImage("duplicate-cover.png", cover),
                new BookImage("image-two.jpg", imageTwo),
                new BookImage("image-three.png", imageThree)
            ]);

        await converter.ConvertAsync(
            voice,
            book,
            output,
            new DirectoryInfo(temporary.Path),
            new ActionProgress<ProgressUpdate>(static _ => { }),
            CancellationToken.None);

        Assert.True(output.Exists);
        Assert.True(output.Length > 0);
        Assert.Empty(Directory.EnumerateDirectories(temporary.Path, ".audiobookgenerator-*"));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.ffconcat"));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.ffmetadata"));

        using var probe = await ProbeFileAsync(output.FullName);
        var formatTags = probe.RootElement.GetProperty("format").GetProperty("tags");
        Assert.Equal(title, formatTags.GetProperty("title").GetString());
        Assert.Equal(title, formatTags.GetProperty("album").GetString());
        Assert.Equal(description, formatTags.GetProperty("comment").GetString());
        Assert.Equal(string.Join(", ", authors), formatTags.GetProperty("artist").GetString());

        var chapters = probe.RootElement.GetProperty("chapters").EnumerateArray().ToArray();
        Assert.Equal(2, chapters.Length);
        Assert.Equal(chapterOneTitle, chapters[0].GetProperty("tags").GetProperty("title").GetString());
        Assert.Equal(chapterTwoTitle, chapters[1].GetProperty("tags").GetProperty("title").GetString());
        Assert.True(
            double.Parse(chapters[1].GetProperty("start_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            >= double.Parse(chapters[0].GetProperty("end_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture));

        var attachedStreams = probe.RootElement.GetProperty("streams")
            .EnumerateArray()
            .Where(static stream => stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1)
            .ToArray();
        Assert.Equal(3, attachedStreams.Length);
        Assert.Equal((320, 320), GetDimensions(attachedStreams[0]));
        Assert.Equal((200, 120), GetDimensions(attachedStreams[1]));
        Assert.Equal((180, 260), GetDimensions(attachedStreams[2]));
    }

    [Fact]
    public async Task CreateM4bCleansTemporaryFilesWhenFfmpegFails()
    {
        using var temporary = new TemporaryDirectory();
        var chapterFile = CreatePlaceholderFile(temporary.Path, "chapter-1.aac");
        var existingExecutable = TestExecutableResolver.FindDotnet();
        var converter = new FfmpegAudioConverter(
            new FakeProcessRunner((_, arguments, _) =>
            {
                var args = arguments.ToArray();
                return Task.FromResult(args.Contains("-show_entries", StringComparer.Ordinal)
                    ? new ProcessRunResult(0, "0.250000\n", string.Empty)
                    : new ProcessRunResult(1, string.Empty, "boom"));
            }),
            new PassthroughImageNormalizer(),
            existingExecutable,
            existingExecutable);
        var output = new FileInfo(Path.Combine(temporary.Path, "failure.m4b"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            converter.CreateM4bAsync(
                [new AudioChapter(chapterFile, "Failure")],
                output,
                new ActionProgress<ProgressUpdate>(static _ => { }),
                CancellationToken.None));

        Assert.Contains("boom", exception.Message, StringComparison.Ordinal);
        Assert.False(output.Exists);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.ffconcat"));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.ffmetadata"));
    }

    [Fact]
    public async Task CreateM4bCleansTemporaryFilesWhenCanceled()
    {
        using var temporary = new TemporaryDirectory();
        var chapterFile = CreatePlaceholderFile(temporary.Path, "chapter-1.aac");
        var existingExecutable = TestExecutableResolver.FindDotnet();
        var converter = new FfmpegAudioConverter(
            new FakeProcessRunner(async (_, arguments, cancellationToken) =>
            {
                var args = arguments.ToArray();
                if (args.Contains("-show_entries", StringComparer.Ordinal))
                {
                    return new ProcessRunResult(0, "0.250000\n", string.Empty);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new UnreachableException();
            }),
            new PassthroughImageNormalizer(),
            existingExecutable,
            existingExecutable);
        var output = new FileInfo(Path.Combine(temporary.Path, "cancelled.m4b"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            converter.CreateM4bAsync(
                [new AudioChapter(chapterFile, "Cancelled")],
                output,
                new ActionProgress<ProgressUpdate>(static _ => { }),
                cancellation.Token));

        Assert.False(output.Exists);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.ffconcat"));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.ffmetadata"));
    }

    [Fact]
    public async Task MissingToolsProduceActionableError()
    {
        using var temporary = new TemporaryDirectory();
        var converter = new FfmpegAudioConverter(
            new FakeProcessRunner((_, _, _) => throw new UnreachableException()),
            new PassthroughImageNormalizer(),
            $"missing-ffmpeg-{Guid.NewGuid():N}",
            $"missing-ffprobe-{Guid.NewGuid():N}");
        var wavFile = CreatePlaceholderFile(temporary.Path, "chapter-1.wav");
        var output = new FileInfo(Path.Combine(temporary.Path, "missing.aac"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            converter.ConvertWavToAacAsync(
                [wavFile],
                output,
                new ActionProgress<ProgressUpdate>(static _ => { }),
                CancellationToken.None));

        Assert.Contains("ffmpeg", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ffprobe", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementFailurePreservesSourceForRecovery()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "new.m4b");
        var destinationDirectory = Path.Combine(temporary.Path, "existing-destination");
        File.WriteAllText(source, "new audiobook");
        Directory.CreateDirectory(destinationDirectory);
        var method = typeof(FfmpegAudioConverter).GetMethod(
            "ReplaceFile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object[] arguments = [source, destinationDirectory, false];

        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method.Invoke(null, arguments));
        var exception = Assert.IsType<IOException>(invocation.InnerException);

        Assert.True(File.Exists(source));
        Assert.Contains(source, exception.Message, StringComparison.Ordinal);
    }

    private static async Task<JsonDocument> ProbeFileAsync(string path)
    {
        var ffprobe = TestExecutableResolver.FindFfprobe();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in new[]
        {
            "-v",
            "error",
            "-print_format",
            "json",
            "-show_format",
            "-show_streams",
            "-show_chapters",
            path
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        _ = process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe failed with exit code {process.ExitCode}: {error}");
        }

        return JsonDocument.Parse(output);
    }

    private static FileInfo CreatePlaceholderFile(string directory, string name)
    {
        var file = new FileInfo(Path.Combine(directory, name));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(file.FullName, [1, 2, 3, 4]);
        return file;
    }

    private static (int Width, int Height) GetDimensions(JsonElement stream) =>
        (stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32());

    private static ServiceProvider CreateServices(
        string settingsPath,
        HttpMessageHandler handler,
        string? ffmpegExecutable = null,
        string? ffprobeExecutable = null)
    {
        var services = new ServiceCollection()
            .AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug))
            .AddBookConverter(settingsPath);
        if (ffmpegExecutable != null && ffprobeExecutable != null)
        {
            services.AddSingleton<IAudioConverter>(serviceProvider => new FfmpegAudioConverter(
                serviceProvider.GetRequiredService<IProcessRunner>(),
                serviceProvider.GetRequiredService<IImageNormalizer>(),
                ffmpegExecutable,
                ffprobeExecutable));
        }

        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static TtsSettings CreateSettings() => new()
    {
        DefaultProviderId = "local",
        OpenAiCompatibleProfiles =
        [
            new OpenAiCompatibleTtsProfile
            {
                Id = "local",
                DisplayName = "Local",
                BaseUrl = "http://127.0.0.1:8880/v1/",
                Model = "kokoro",
                Voices =
                [
                    new ConfiguredSpeechVoice
                    {
                        Id = "af_heart",
                        DisplayName = "Heart",
                        Culture = "en-US",
                        Gender = "Female"
                    }
                ]
            }
        ]
    };

    private static HttpResponseMessage WaveResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(CreateWave(durationMilliseconds: 250))
    };

    private static byte[] CreateWave(int durationMilliseconds)
    {
        const int sampleRate = 16000;
        const short bitsPerSample = 16;
        const short channels = 1;
        const double frequency = 440d;
        var sampleCount = sampleRate * durationMilliseconds / 1000;
        var blockAlign = channels * bitsPerSample / 8;
        var byteRate = sampleRate * blockAlign;
        var dataLength = sampleCount * blockAlign;

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (var sample = 0; sample < sampleCount; sample++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * frequency * sample / sampleRate) * short.MaxValue * 0.2);
            writer.Write(value);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] CreateRasterImage(int width, int height, SKColor color, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality: 95);
        return data.ToArray();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }

    private sealed class FakeProcessRunner(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessRunResult>> runAsync) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(
            string executable,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken,
            int maximumCapturedCharacters = 64 * 1024) =>
            runAsync(executable, arguments.ToArray(), cancellationToken);

        public IExternalProcess Start(string executable, IEnumerable<string> arguments) =>
            throw new NotSupportedException();
    }

    private sealed class PassthroughImageNormalizer : IImageNormalizer
    {
        public Task<NormalizedImage> NormalizeAsync(
            BookImage image,
            int maximumDimension,
            int maximumBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NormalizedImage(image.Content, "image/png", 1, 1));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AudiobookGeneratorTests-{Guid.NewGuid():N}");
            SettingsPath = System.IO.Path.Combine(Path, "tts-settings.json");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string SettingsPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
