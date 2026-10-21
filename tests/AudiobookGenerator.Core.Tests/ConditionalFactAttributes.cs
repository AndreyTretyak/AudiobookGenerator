using System.Diagnostics;

using Xunit;

namespace YewCone.AudiobookGenerator.Core.Tests;

internal sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        Skip = GetMissingToolReason("ffmpeg", "ffprobe");
    }

    private static string? GetMissingToolReason(params string[] executables)
    {
        foreach (var executable in executables)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
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
                return $"Skipping FFmpeg-dependent test because '{executable}' is unavailable.";
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return $"Skipping FFmpeg-dependent test because '{executable} -version' failed.";
            }
        }

        return null;
    }
}

#if AUDIOBOOKGENERATOR_WINDOWS_CORE
internal sealed class RequiresWindowsVoiceFactAttribute : FactAttribute
{
    public RequiresWindowsVoiceFactAttribute()
    {
        Skip = GetSkipReason();
    }

    private static string? GetSkipReason()
    {
        try
        {
            using var synthesizer = new System.Speech.Synthesis.SpeechSynthesizer();
            return synthesizer.GetInstalledVoices().Any(static voice => voice.Enabled)
                ? null
                : "No Windows speech voices are installed on this machine.";
        }
        catch (Exception ex)
        {
            return $"Windows speech synthesis is unavailable: {ex.Message}";
        }
    }
}
#endif
