namespace YewCone.AudiobookGenerator.Core.Tests;

internal static class TestExecutableResolver
{
    public static string FindDotnet() => FindExistingExecutable(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
        "dotnet.exe",
        "dotnet");

    public static string FindPowerShell() => FindExistingExecutable(
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        "pwsh.exe",
        "pwsh",
        "powershell.exe",
        "powershell");

    public static string FindFfmpeg() => FindExistingExecutable(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "ffmpeg.exe"),
        "ffmpeg.exe",
        "ffmpeg");

    public static string FindFfprobe() => FindExistingExecutable(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "ffprobe.exe"),
        "ffprobe.exe",
        "ffprobe");

    public static EnvironmentVariableScope PrependExecutableDirectories(params string[] executablePaths)
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH");
        var directories = executablePaths
            .Select(Path.GetDirectoryName)
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>();
        var updatedPath = string.Join(
            Path.PathSeparator,
            currentPath == null
                ? directories
                : directories.Append(currentPath));
        return new EnvironmentVariableScope("PATH", updatedPath);
    }

    private static string FindExistingExecutable(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var resolved = FindOnPath(candidate);
            if (resolved != null)
            {
                return resolved;
            }

            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Required test executable was not found. Tried: {string.Join(", ", candidates)}");
    }

    private static string? FindOnPath(string executable)
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensions = Path.GetExtension(executable).Length > 0 || !OperatingSystem.IsWindows()
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in pathEntries)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(
                    entry,
                    Path.GetExtension(executable).Length > 0 ? executable : $"{executable}{extension}");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string name;
    private readonly string? originalValue;

    public EnvironmentVariableScope(string name, string? value)
    {
        this.name = name;
        originalValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(name, originalValue);
}
