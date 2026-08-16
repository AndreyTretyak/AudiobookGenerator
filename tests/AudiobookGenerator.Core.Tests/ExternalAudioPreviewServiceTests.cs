#if AUDIOBOOKGENERATOR_PORTABLE
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using System.Net;
using System.Text;

using Xunit;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Core.Tests;

public sealed class ExternalAudioPreviewServiceTests
{
    [Fact]
    public async Task PreviewUsesResolvedExternalPlayerAndDeletesTemporaryWaveAfterPlayback()
    {
        using var temporary = new TemporaryDirectory();
        var fakePlayerPath = CreateFakeExecutable(
            temporary.Path,
            OperatingSystem.IsWindows() ? "ffplay.cmd" : "ffplay");
        using var environment = new EnvironmentVariableScope("PATH", temporary.Path);
        var process = new FakeExternalProcess();
        var runner = new RecordingProcessRunner(process);
        using var services = CreateServices(temporary.SettingsPath, runner);
        await services.GetRequiredService<ITtsSettingsStore>().SaveAsync(CreateSettings(), CancellationToken.None);
        var preview = services.GetRequiredService<IAudioPreviewService>();
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));

        await preview.PlayAsync("Preview from the portable player.", voice, CancellationToken.None);

        Assert.Equal(fakePlayerPath, runner.StartedExecutable, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["-nodisp", "-autoexit", "-loglevel", "error"], runner.StartedArguments[..4]);
        var temporaryWavePath = Assert.Single(runner.StartedArguments[4..]);
        Assert.True(File.Exists(temporaryWavePath));

        process.Complete();

        await WaitUntilAsync(() => !File.Exists(temporaryWavePath));
        Assert.False(File.Exists(temporaryWavePath));
    }

    [Fact]
    public async Task PreviewStopTerminatesProcessAndDeletesTemporaryWave()
    {
        using var temporary = new TemporaryDirectory();
        _ = CreateFakeExecutable(
            temporary.Path,
            OperatingSystem.IsWindows() ? "ffplay.cmd" : "ffplay");
        using var environment = new EnvironmentVariableScope("PATH", temporary.Path);
        var process = new FakeExternalProcess();
        var runner = new RecordingProcessRunner(process);
        using var services = CreateServices(temporary.SettingsPath, runner);
        await services.GetRequiredService<ITtsSettingsStore>().SaveAsync(CreateSettings(), CancellationToken.None);
        var preview = services.GetRequiredService<IAudioPreviewService>();
        var synthesizer = services.GetRequiredService<IAudioSynthesizer>();
        var voice = Assert.Single(await synthesizer.GetVoicesAsync("local", CancellationToken.None));

        await preview.PlayAsync("Portable preview stop.", voice, CancellationToken.None);

        var temporaryWavePath = Assert.Single(runner.StartedArguments[4..]);
        Assert.True(File.Exists(temporaryWavePath));

        preview.Stop();

        await WaitUntilAsync(() => process.TerminateCount > 0 && !File.Exists(temporaryWavePath));
        Assert.True(process.TerminateCount > 0);
        Assert.False(File.Exists(temporaryWavePath));
    }

    [Fact]
    public void PreviewReportsMissingPlayerWithActionableError()
    {
        using var temporary = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope("PATH", temporary.Path);
        using var environmentMixedCase = new EnvironmentVariableScope("Path", temporary.Path);
        var previewType = typeof(BookConverter).Assembly.GetType(
            "YewCone.AudiobookGenerator.Core.ExternalAudioPreviewService",
            throwOnError: true)!;
        var resolvePlayer = previewType.GetMethod(
            "ResolvePlayer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            resolvePlayer.Invoke(null, [Path.Combine(temporary.Path, "preview.wav")]));
        var exception = Assert.IsType<InvalidOperationException>(invocation.InnerException);

        Assert.Contains("ffplay", exception.Message, StringComparison.Ordinal);
        Assert.Contains("pw-play", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateServices(string settingsPath, IProcessRunner processRunner)
    {
        var services = new ServiceCollection()
            .AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug))
            .AddBookConverter(settingsPath);
        services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(processRunner));
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new RecordingHandler(static (_, _) =>
            Task.FromResult(WaveResponse()))));
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
                        DisplayName = "Heart"
                    }
                ]
            }
        ]
    };

    private static HttpResponseMessage WaveResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(CreateWave())
    };

    private static byte[] CreateWave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(0);
        writer.Flush();
        return stream.ToArray();
    }

    private static string CreateFakeExecutable(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "@echo off");
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Timed out waiting for the preview process fixture.");
    }

    private sealed class RecordingProcessRunner(FakeExternalProcess process) : IProcessRunner
    {
        public string? StartedExecutable { get; private set; }

        public string[] StartedArguments { get; private set; } = [];

        public Task<ProcessRunResult> RunAsync(
            string executable,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken,
            int maximumCapturedCharacters = 64 * 1024) =>
            throw new NotSupportedException();

        public IExternalProcess Start(string executable, IEnumerable<string> arguments)
        {
            StartedExecutable = executable;
            StartedArguments = [.. arguments];
            return process;
        }
    }

    private sealed class FakeExternalProcess : IExternalProcess
    {
        private readonly TaskCompletionSource<int> exitCode =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => 42;

        public bool HasExited => exitCode.Task.IsCompleted;

        public int TerminateCount { get; private set; }

        public void Complete(int code = 0) => exitCode.TrySetResult(code);

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            exitCode.Task.WaitAsync(cancellationToken);

        public void Terminate()
        {
            TerminateCount++;
            exitCode.TrySetResult(-1);
        }

        public void Dispose()
        {
        }
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AudiobookPreviewTests-{Guid.NewGuid():N}");
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
#endif
