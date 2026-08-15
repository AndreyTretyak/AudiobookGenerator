using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using Xunit;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Core.Tests;

public sealed class TtsIntegrationTests
{
    [Fact]
    public async Task MissingSettingsUseWindowsDefaults()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(WaveResponse())));
        var store = services.GetRequiredService<ITtsSettingsStore>();

        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(TtsSettings.WindowsProviderId, settings.DefaultProviderId);
        Assert.Empty(settings.OpenAiCompatibleProfiles);
        Assert.Equal(temporary.SettingsPath, store.SettingsPath);
    }

    [Fact]
    public async Task WindowsProviderProducesWaveWhenVoiceIsInstalled()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(WaveResponse())));
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = (await synthesizer.GetVoicesAsync(TtsSettings.WindowsProviderId, CancellationToken.None)).FirstOrDefault();
        if (voice == null)
        {
            return;
        }

        await using var audio = await synthesizer.SynthesizeWavAsync("Windows text to speech test.", voice, CancellationToken.None);
        var header = new byte[12];
        var bytesRead = await audio.ReadAsync(header);

        Assert.Equal(header.Length, bytesRead);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(header, 8, 4));
    }

    [Fact]
    public async Task FfmpegConcatenatesSynthesizedWaveChunksWhenAvailable()
    {
        if (!await CanRunFfmpegAsync())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(WaveResponse())));
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = (await synthesizer.GetVoicesAsync(TtsSettings.WindowsProviderId, CancellationToken.None)).FirstOrDefault();
        if (voice == null)
        {
            return;
        }

        Directory.CreateDirectory(temporary.Path);
        var waveFiles = new List<FileInfo>();
        foreach (var (text, index) in new[] { "First chunk.", "Second chunk." }.Select((text, index) => (text, index)))
        {
            await using var audio = await synthesizer.SynthesizeWavAsync(text, voice, CancellationToken.None);
            var waveFile = new FileInfo(System.IO.Path.Combine(temporary.Path, $"{index}.wav"));
            await using var output = waveFile.Create();
            await audio.CopyToAsync(output);
            waveFiles.Add(waveFile);
        }

        var aacFile = new FileInfo(System.IO.Path.Combine(temporary.Path, "joined.aac"));
        var converter = services.GetRequiredService<IAudioConverter>();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                converter.ConvertWavToAacAsync(
                    waveFiles,
                    aacFile,
                    new ActionProgress<ProgressUpdate>(static _ => { }),
                    cancelled.Token));
        }

        await converter.ConvertWavToAacAsync(
            waveFiles,
            aacFile,
            new ActionProgress<ProgressUpdate>(static _ => { }),
            CancellationToken.None);

        Assert.True(aacFile.Exists);
        Assert.True(aacFile.Length > 0);
    }

    [Fact]
    public async Task ConversionRejectsEmptyChaptersBeforeSynthesis()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(WaveResponse())));
        var converter = services.GetRequiredService<BookConverter>();
        var book = new Book(
            "empty-book",
            "Empty Book",
            string.Empty,
            [],
            null,
            [new BookChapter("chapter-1", "Chapter One", "  ")],
            []);
        var voice = new SpeechVoice("local", "af_heart", "Heart");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            converter.ConvertAsync(
                voice,
                book,
                new FileInfo(System.IO.Path.Combine(temporary.Path, "empty.m4b")),
                new DirectoryInfo(temporary.Path),
                new ActionProgress<ProgressUpdate>(static _ => { }),
                CancellationToken.None));

        Assert.Contains("Chapter One", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no narration text", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullConversionCleansGeneratedWorkFilesWhenDependenciesAreAvailable()
    {
        if (!await CanRunFfmpegAsync())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(WaveResponse())));
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = (await synthesizer.GetVoicesAsync(TtsSettings.WindowsProviderId, CancellationToken.None)).FirstOrDefault();
        if (voice == null)
        {
            return;
        }

        Directory.CreateDirectory(temporary.Path);
        var converter = services.GetRequiredService<BookConverter>();
        var output = new FileInfo(System.IO.Path.Combine(temporary.Path, "complete.m4b"));
        var book = new Book(
            "complete",
            "Complete Book",
            "Test conversion",
            ["Test Author"],
            null,
            [new BookChapter("0001 chapter", "Chapter One", "A short chapter for conversion testing.")],
            []);

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
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.chapters.txt"));
    }

    [Fact]
    public async Task ConversionCleansGeneratedWorkFilesAfterProviderFailure()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(
            temporary.SettingsPath,
            new RecordingHandler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("invalid audio"u8.ToArray())
            })));
        var store = services.GetRequiredService<ITtsSettingsStore>();
        await store.SaveAsync(CreateSettings(), CancellationToken.None);
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));
        var converter = services.GetRequiredService<BookConverter>();
        Directory.CreateDirectory(temporary.Path);
        var book = new Book(
            "failure",
            "Failure",
            string.Empty,
            [],
            null,
            [new BookChapter("0001 chapter", "Chapter One", "Provider failure cleanup.")],
            []);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            converter.ConvertAsync(
                voice,
                book,
                new FileInfo(System.IO.Path.Combine(temporary.Path, "failure.m4b")),
                new DirectoryInfo(temporary.Path),
                new ActionProgress<ProgressUpdate>(static _ => { }),
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateDirectories(temporary.Path, ".audiobookgenerator-*"));
    }

    [Fact]
    public async Task SettingsPersistProfilesWithoutSecretValues()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(WaveResponse())));
        var store = services.GetRequiredService<ITtsSettingsStore>();
        var secretVariable = $"AUDIOBOOKGENERATOR_TEST_KEY_{Guid.NewGuid():N}";
        var secretValue = $"secret-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(secretVariable, secretValue);

        try
        {
            var settings = CreateSettings(secretVariable);
            await store.SaveAsync(settings, CancellationToken.None);

            var reloaded = await store.LoadAsync(CancellationToken.None);
            var fileContent = await File.ReadAllTextAsync(temporary.SettingsPath);

            Assert.Equal("local", reloaded.DefaultProviderId);
            Assert.Equal(secretVariable, Assert.Single(reloaded.OpenAiCompatibleProfiles).ApiKeyEnvironmentVariable);
            Assert.DoesNotContain(secretValue, fileContent, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretVariable, null);
        }
    }

    [Fact]
    public async Task OpenAiProviderPostsExpectedRequestAndAuthorization()
    {
        using var temporary = new TemporaryDirectory();
        HttpRequestMessage? observedRequest = null;
        string? observedJson = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            observedRequest = request;
            observedJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return WaveResponse();
        });
        using var services = CreateServices(temporary.SettingsPath, handler);
        var store = services.GetRequiredService<ITtsSettingsStore>();
        var secretVariable = $"AUDIOBOOKGENERATOR_TEST_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(secretVariable, "test-token");

        try
        {
            await store.SaveAsync(CreateSettings(secretVariable), CancellationToken.None);
            var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
            var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));

            await using var audio = await synthesizer.SynthesizeWavAsync("Hello from a local model.", voice, CancellationToken.None);

            Assert.NotNull(observedRequest);
            Assert.Equal("http://127.0.0.1:8880/v1/audio/speech", observedRequest.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", observedRequest.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", observedRequest.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(observedJson!);
            Assert.Equal("kokoro", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("af_heart", json.RootElement.GetProperty("voice").GetString());
            Assert.Equal("wav", json.RootElement.GetProperty("response_format").GetString());
            Assert.Equal("Hello from a local model.", json.RootElement.GetProperty("input").GetString());
            Assert.True(audio.Length >= 44);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretVariable, null);
        }
    }

    [Fact]
    public async Task OpenAiProviderRejectsMalformedAudio()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(
            temporary.SettingsPath,
            new RecordingHandler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("not wave audio"u8.ToArray())
            })));
        var store = services.GetRequiredService<ITtsSettingsStore>();
        await store.SaveAsync(CreateSettings(), CancellationToken.None);
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => synthesizer.SynthesizeWavAsync("Hello.", voice, CancellationToken.None));

        Assert.Contains("WAV", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiProviderRequiresConfiguredSecretEnvironmentVariable()
    {
        using var temporary = new TemporaryDirectory();
        var requests = 0;
        using var services = CreateServices(
            temporary.SettingsPath,
            new RecordingHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(WaveResponse());
            }));
        var missingVariable = $"AUDIOBOOKGENERATOR_MISSING_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(missingVariable, null);
        var store = services.GetRequiredService<ITtsSettingsStore>();
        await store.SaveAsync(CreateSettings(missingVariable), CancellationToken.None);
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => synthesizer.SynthesizeWavAsync("Hello.", voice, CancellationToken.None));

        Assert.Contains(missingVariable, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task SettingsRejectPlainHttpForRemoteEndpoints()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(WaveResponse())));
        var store = services.GetRequiredService<ITtsSettingsStore>();
        var settings = CreateSettings();
        settings.OpenAiCompatibleProfiles[0].BaseUrl = "http://example.com/v1/";

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(settings, CancellationToken.None));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(temporary.SettingsPath));
    }

    [Fact]
    public async Task OpenAiProviderRetriesTransientResponses()
    {
        using var temporary = new TemporaryDirectory();
        var attempts = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : WaveResponse());
        });
        using var services = CreateServices(temporary.SettingsPath, handler);
        var store = services.GetRequiredService<ITtsSettingsStore>();
        await store.SaveAsync(CreateSettings(), CancellationToken.None);
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));

        await using var audio = await synthesizer.SynthesizeWavAsync("Hello.", voice, CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.True(audio.Length > 0);
    }

    [Fact]
    public async Task OpenAiProviderHonorsCallerCancellation()
    {
        using var temporary = new TemporaryDirectory();
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return WaveResponse();
        });
        using var services = CreateServices(temporary.SettingsPath, handler);
        var store = services.GetRequiredService<ITtsSettingsStore>();
        await store.SaveAsync(CreateSettings(), CancellationToken.None);
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => synthesizer.SynthesizeWavAsync("Hello.", voice, cancellation.Token));
    }

    [Fact]
    public void ChunkerPreservesTextAndNaturalBoundaries()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(WaveResponse())));
        var chunker = services.GetRequiredService<ITextChunker>();
        var text = "First sentence has several words. Second sentence is also here.\n\nThird paragraph remains intact.";

        var chunks = chunker.Split(text, 45);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 45));
        Assert.Equal(text, string.Concat(chunks));
        Assert.EndsWith(".", chunks[0].TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderRoutingUsesQualifiedVoiceIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var requestedModels = new List<string>();
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            requestedModels.Add(json.RootElement.GetProperty("model").GetString()!);
            return WaveResponse();
        });
        using var services = CreateServices(temporary.SettingsPath, handler);
        var settings = CreateSettings();
        settings.OpenAiCompatibleProfiles.Add(new OpenAiCompatibleTtsProfile
        {
            Id = "second",
            DisplayName = "Second",
            BaseUrl = "http://127.0.0.1:8881/v1/",
            Model = "second-model",
            Voices = [new ConfiguredSpeechVoice { Id = "af_heart", DisplayName = "Heart" }]
        });
        var store = services.GetRequiredService<ITtsSettingsStore>();
        await store.SaveAsync(settings, CancellationToken.None);
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("second", CancellationToken.None));

        await using var audio = await synthesizer.SynthesizeWavAsync("Hello.", voice, CancellationToken.None);

        Assert.Equal(["second-model"], requestedModels);
    }

    [Fact]
    public async Task SynthesisSessionKeepsProviderSnapshotAcrossSettingsChanges()
    {
        using var temporary = new TemporaryDirectory();
        string? requestedModel = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            requestedModel = json.RootElement.GetProperty("model").GetString();
            return WaveResponse();
        });
        using var services = CreateServices(temporary.SettingsPath, handler);
        var store = services.GetRequiredService<ITtsSettingsStore>();
        var settings = CreateSettings();
        await store.SaveAsync(settings, CancellationToken.None);
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));
        var session = await synthesizer.CreateSessionAsync(voice, CancellationToken.None);

        settings.OpenAiCompatibleProfiles[0].Model = "changed-after-start";
        await store.SaveAsync(settings, CancellationToken.None);
        await using var audio = await session.SynthesizeWavAsync("Keep the original model.", CancellationToken.None);

        Assert.Equal("kokoro", requestedModel);
    }

    private static ServiceProvider CreateServices(string settingsPath, HttpMessageHandler handler)
    {
        var services = new ServiceCollection()
            .AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug))
            .AddBookConverter(settingsPath);
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static TtsSettings CreateSettings(string? apiKeyEnvironmentVariable = null) => new()
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
                ],
                ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable
            }
        ]
    };

    private static HttpResponseMessage WaveResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(CreateWave())
    };

    private static byte[] CreateWave()
    {
        var bytes = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        return bytes;
    }

    private static async Task<bool> CanRunFfmpegAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                CreateNoWindow = true,
                UseShellExecute = false
            }
        };

        try
        {
            _ = process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }

        await process.WaitForExitAsync();
        return process.ExitCode == 0;
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AudiobookGeneratorTests-{Guid.NewGuid():N}");
            SettingsPath = System.IO.Path.Combine(Path, "tts-settings.json");
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
