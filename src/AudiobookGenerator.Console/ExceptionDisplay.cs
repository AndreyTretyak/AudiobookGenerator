using Spectre.Console;

namespace YewCone.AudiobookGenerator.Console;

internal static class ExceptionDisplay
{
    public static void Write(Exception exception)
    {
#if AUDIOBOOKGENERATOR_PORTABLE
        foreach (var line in exception.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(line)}[/]");
        }
#else
        AnsiConsole.WriteException(exception);
#endif
    }
}
