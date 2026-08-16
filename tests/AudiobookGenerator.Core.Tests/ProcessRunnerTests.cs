using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using System.Diagnostics;

using Xunit;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Core.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunsArgumentsWithoutShellAndBoundsCapturedOutput()
    {
        using var services = CreateServices();
        var runner = services.GetRequiredService<IProcessRunner>();
        var powerShell = TestExecutableResolver.FindPowerShell();

        var result = await runner.RunAsync(
            powerShell,
            [
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[Console]::Out.Write('0123456789ABCDEFGHIJ'); [Console]::Error.Write('abcdefghijABCDEFGHIJ')"
            ],
            CancellationToken.None,
            maximumCapturedCharacters: 12);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0123456789AB", result.StandardOutput);
        Assert.Equal("abcdefghijAB", result.StandardError);
    }

    [Fact]
    public async Task ReportsMissingExecutable()
    {
        using var services = CreateServices();
        var runner = services.GetRequiredService<IProcessRunner>();
        var executable = $"missing-audiobook-tool-{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            runner.RunAsync(executable, [], CancellationToken.None));

        Assert.Contains(executable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HonorsPreCancelledToken()
    {
        using var services = CreateServices();
        var runner = services.GetRequiredService<IProcessRunner>();
        var powerShell = TestExecutableResolver.FindPowerShell();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(powerShell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 5"], cancellation.Token));
    }

    [Fact]
    public async Task CancellationTerminatesEntireProcessTree()
    {
        using var services = CreateServices();
        var runner = services.GetRequiredService<IProcessRunner>();
        using var temporary = new TemporaryDirectory("AudiobookProcessTreeTests");
        var childPidPath = Path.Combine(temporary.Path, "child.pid");
        var powerShell = TestExecutableResolver.FindPowerShell();
        using var cancellation = new CancellationTokenSource();

        var runTask = runner.RunAsync(
            powerShell,
            [
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                CreateChildProcessCommand(childPidPath, powerShell)
            ],
            cancellation.Token);
        var childProcessId = await WaitForChildProcessIdAsync(childPidPath);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        await AssertProcessExitedAsync(childProcessId);
    }

    private static string CreateChildProcessCommand(string childPidPath, string powerShellPath)
    {
        var escapedPath = childPidPath.Replace("'", "''", StringComparison.Ordinal);
        var escapedPowerShell = powerShellPath.Replace("'", "''", StringComparison.Ordinal);
        var windowStyle = OperatingSystem.IsWindows() ? "-WindowStyle Hidden " : string.Empty;
        return
            $"$child = Start-Process -FilePath '{escapedPowerShell}' "
            + "-ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60' "
            + windowStyle
            + "-PassThru; "
            + $"Set-Content -LiteralPath '{escapedPath}' -Value $child.Id -NoNewline; "
            + "Start-Sleep -Seconds 60";
    }

    private static async Task<int> WaitForChildProcessIdAsync(string path)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path)
                && int.TryParse(await File.ReadAllTextAsync(path), out var childProcessId))
            {
                return childProcessId;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Timed out waiting for the PowerShell child-process fixture to start.");
        return 0;
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Child process {processId} was still running after cancellation.");
    }

    private static ServiceProvider CreateServices()
    {
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"AudiobookProcessTests-{Guid.NewGuid():N}",
            "tts-settings.json");
        return new ServiceCollection()
            .AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug))
            .AddBookConverter(settingsPath)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
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
