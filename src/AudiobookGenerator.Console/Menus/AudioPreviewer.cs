using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Console.Resources;
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
    public async Task RunAsync(BookEditSession session, IAudioPreviewService preview, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[yellow]{Strings.HeaderPreviewAudio}[/]").LeftJustified());
        AnsiConsole.WriteLine();

        if (session.SelectedVoice == null)
        {
            AnsiConsole.MarkupLine($"[yellow]{Strings.ErrorSelectVoiceFirst}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]{string.Format(Strings.StatusUsingVoice, $"[blue]{Markup.Escape(session.SelectedVoice.Name)}[/]")}[/]");
        AnsiConsole.WriteLine();

        while (true)
        {
            var chapters = session.Chapters;
            var choices = chapters
                .Select((c, i) => $"{i + 1}. {Markup.Escape(c.Name)}")
                .Prepend(Strings.MenuPreviewCustomText)
                .Append(Strings.MenuStopPlayback)
                .Append(Strings.MenuBackToMainMenu)
                .ToList();

            var choice = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title(Strings.PromptSelectChapterToPreview)
                    .PageSize(15)
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices(choices),
                cancellationToken);

            if (choice == Strings.MenuBackToMainMenu)
            {
                preview.Stop();
                return;
            }

            if (choice == Strings.MenuStopPlayback)
            {
                preview.Stop();
                AnsiConsole.MarkupLine($"[dim]{Strings.StatusPlaybackStopped}[/]");
                continue;
            }

            if (choice == Strings.MenuPreviewCustomText)
            {
                await PreviewCustomTextAsync(session, preview, cancellationToken);
                continue;
            }

            // Parse chapter index
            var chapterIndex = int.Parse(choice.Split('.')[0]) - 1;
            var chapter = chapters[chapterIndex];

            await PreviewChapterAsync(session, preview, chapter, cancellationToken);
        }
    }

    private static async Task PreviewChapterAsync(BookEditSession session, IAudioPreviewService preview, BookChapter chapter, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]{string.Format(Strings.StatusPreviewing, Markup.Escape(chapter.Name))}[/]");

        // Show preview options
        var previewLength = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title(Strings.PromptHowMuchToPreview)
                .AddChoices([
                    Strings.MenuFirst500Chars,
                    Strings.MenuFirst2000Chars,
                    Strings.MenuEntireChapter,
                    Strings.MenuCancel
                ]),
            cancellationToken);

        if (previewLength == Strings.MenuCancel)
        {
            return;
        }

        var textToSpeak = previewLength == Strings.MenuFirst500Chars
            ? TruncateAtSentence(chapter.Content, 500)
            : previewLength == Strings.MenuFirst2000Chars
                ? TruncateAtSentence(chapter.Content, 2000)
                : chapter.Content;

        await PlayTextAsync(preview, textToSpeak, session.SelectedVoice!, cancellationToken);
    }

    private static async Task PreviewCustomTextAsync(BookEditSession session, IAudioPreviewService preview, CancellationToken cancellationToken)
    {
        var text = await AnsiConsole.PromptAsync(
            new TextPrompt<string>(Strings.PromptEnterTextToPreview)
                .Validate(t => !string.IsNullOrWhiteSpace(t)
                    ? ValidationResult.Success()
                    : ValidationResult.Error(Strings.ErrorTextEmpty)),
            cancellationToken);

        await PlayTextAsync(preview, text, session.SelectedVoice!, cancellationToken);
    }

    private static async Task PlayTextAsync(
        IAudioPreviewService preview,
        string text,
        SpeechVoice voice,
        CancellationToken cancellationToken)
    {
        preview.Stop();

        AnsiConsole.MarkupLine($"[dim]{Strings.StatusPlayingAudio}[/]");

        try
        {
            await preview.PlayAsync(text, voice, cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{string.Format(Strings.ErrorPlayingAudio, Markup.Escape(ex.Message))}[/]");
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
