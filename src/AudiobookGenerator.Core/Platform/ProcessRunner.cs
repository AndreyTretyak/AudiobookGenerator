using System.Diagnostics;
using System.Text;

namespace YewCone.AudiobookGenerator.Core;

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        int maximumCapturedCharacters = 64 * 1024);

    IExternalProcess Start(
        string executable,
        IEnumerable<string> arguments);
}

public interface IExternalProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    void Terminate();
}

internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        int maximumCapturedCharacters = 64 * 1024)
    {
        if (maximumCapturedCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCapturedCharacters));
        }

        using var process = CreateProcess(
            executable,
            arguments,
            redirectOutput: true);
        StartProcess(process, executable);
        using var cancellationRegistration = cancellationToken.Register(
            static state => TerminateProcess((Process)state!),
            process);
        var standardOutput = ReadBoundedAsync(
            process.StandardOutput,
            maximumCapturedCharacters,
            CancellationToken.None);
        var standardError = ReadBoundedAsync(
            process.StandardError,
            maximumCapturedCharacters,
            CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TerminateProcess(process);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await Task.WhenAll(standardOutput, standardError);
            throw;
        }

        return new(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    public IExternalProcess Start(
        string executable,
        IEnumerable<string> arguments)
    {
        var process = CreateProcess(
            executable,
            arguments,
            redirectOutput: false);
        try
        {
            StartProcess(process, executable);
            return new ExternalProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static Process CreateProcess(
        string executable,
        IEnumerable<string> arguments,
        bool redirectOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = redirectOutput,
                RedirectStandardError = redirectOutput,
                StandardOutputEncoding = redirectOutput ? Encoding.UTF8 : null,
                StandardErrorEncoding = redirectOutput ? Encoding.UTF8 : null
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    private static void StartProcess(Process process, string executable)
    {
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Process '{executable}' did not start.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FileNotFoundException(
                $"Required executable '{executable}' was not found on PATH.",
                executable,
                ex);
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var remaining = maximumCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return output.ToString();
    }

    private static void TerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exited or became inaccessible during cancellation.
        }
    }

    private sealed class ExternalProcess(Process process) : IExternalProcess
    {
        public int Id => process.Id;

        public bool HasExited => process.HasExited;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((ExternalProcess)state!).Terminate(),
                this);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return process.ExitCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Terminate();
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
        }

        public void Terminate() => TerminateProcess(process);

        public void Dispose()
        {
            Terminate();
            process.Dispose();
        }
    }
}

internal static class ExecutableLocator
{
    public static string? Find(params string[] executableNames)
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var executableName in executableNames)
        {
            if (Path.IsPathRooted(executableName) && File.Exists(executableName))
            {
                return executableName;
            }

            foreach (var directory in pathEntries)
            {
                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(
                        directory,
                        OperatingSystem.IsWindows()
                            && Path.GetExtension(executableName).Length == 0
                                ? $"{executableName}{extension}"
                                : executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }
}
