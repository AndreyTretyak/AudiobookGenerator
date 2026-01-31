using System.Speech.Synthesis;

using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

/// <summary>
/// Interactive audio previewer for hearing chapter content with the selected voice.
/// </summary>
internal sealed class AudioPreviewer
{
    /// <summary>
    /// Runs the audio preview menu.
    /// </summary>
    public async Task RunAsync(BookEditSession session, IAudioSynthesizer synthesizer, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Preview Audio[/]").LeftJustified());
        AnsiConsole.WriteLine();

        if (session.SelectedVoice == null)
        {
            AnsiConsole.MarkupLine("[yellow]No voice selected. Please select a voice first.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Using voice: [blue]{Markup.Escape(session.SelectedVoice.Name)}[/][/]");
        AnsiConsole.WriteLine();

        while (true)
        {
            var chapters = session.Chapters;
            var choices = chapters
                .Select((c, i) => $"{i + 1}. {Markup.Escape(c.Name)}")
                .Prepend("🔊 Preview custom text")
                .Append("⏹️  Stop playback")
                .Append("← Back to Main Menu")
                .ToList();

            var choice = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("Select a chapter to preview:")
                    .PageSize(15)
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices(choices),
                cancellationToken);

            if (choice == "← Back to Main Menu")
            {
                synthesizer.StopSpeaking();
                return;
            }

            if (choice == "⏹️  Stop playback")
            {
                synthesizer.StopSpeaking();
                AnsiConsole.MarkupLine("[dim]Playback stopped.[/]");
                continue;
            }

            if (choice == "🔊 Preview custom text")
            {
                await PreviewCustomTextAsync(session, synthesizer, cancellationToken);
                continue;
            }

            // Parse chapter index
            var chapterIndex = int.Parse(choice.Split('.')[0]) - 1;
            var chapter = chapters[chapterIndex];

            await PreviewChapterAsync(session, synthesizer, chapter, cancellationToken);
        }
    }

    private static async Task PreviewChapterAsync(BookEditSession session, IAudioSynthesizer synthesizer, BookChapter chapter, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]Previewing:[/] {Markup.Escape(chapter.Name)}");

        // Show preview options
        var previewLength = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("How much to preview?")
                .AddChoices([
                    "📝 First 500 characters",
                    "📄 First 2000 characters",
                    "📖 Entire chapter (may take a while)",
                    "← Cancel"
                ]),
            cancellationToken);

        if (previewLength == "← Cancel")
        {
            return;
        }

        var textToSpeak = previewLength switch
        {
            "📝 First 500 characters" => TruncateAtSentence(chapter.Content, 500),
            "📄 First 2000 characters" => TruncateAtSentence(chapter.Content, 2000),
            _ => chapter.Content
        };

        PlayText(synthesizer, textToSpeak, session.SelectedVoice!);
    }

    private static async Task PreviewCustomTextAsync(BookEditSession session, IAudioSynthesizer synthesizer, CancellationToken cancellationToken)
    {
        var text = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Enter text to preview:")
                .Validate(t => !string.IsNullOrWhiteSpace(t)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Text cannot be empty.")),
            cancellationToken);

        PlayText(synthesizer, text, session.SelectedVoice!);
    }

    private static void PlayText(IAudioSynthesizer synthesizer, string text, VoiceInfo voice)
    {
        // Stop any current playback
        synthesizer.StopSpeaking();

        AnsiConsole.MarkupLine("[dim]Playing audio... (select 'Stop playback' to stop)[/]");

        // Start speaking (runs in background)
        try
        {
            synthesizer.Speak(text, voice);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error playing audio: {Markup.Escape(ex.Message)}[/]");
        }
    }

    /// <summary>
    /// Truncates text at approximately the given length, ending at a sentence boundary if possible.
    /// </summary>
    private static string TruncateAtSentence(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var truncated = text[..maxLength];

        // Try to find the last sentence ending
        var lastPeriod = truncated.LastIndexOf('.');
        var lastQuestion = truncated.LastIndexOf('?');
        var lastExclamation = truncated.LastIndexOf('!');

        var lastSentenceEnd = Math.Max(lastPeriod, Math.Max(lastQuestion, lastExclamation));

        if (lastSentenceEnd > maxLength / 2)
        {
            return text[..(lastSentenceEnd + 1)];
        }

        // Fall back to truncating at word boundary
        var lastSpace = truncated.LastIndexOf(' ');
        return lastSpace > 0 ? text[..lastSpace] + "..." : truncated + "...";
    }
}
