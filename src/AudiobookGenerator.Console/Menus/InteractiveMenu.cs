using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

/// <summary>
/// Main interactive menu for editing and converting audiobooks.
/// </summary>
internal sealed class InteractiveMenu(
    BookEditSession session,
    BookConverter converter,
    ChapterEditor chapterEditor,
    ImageManager imageManager,
    MetadataEditor metadataEditor,
    VoiceSelector voiceSelector,
    AudioPreviewer audioPreviewer,
    ConversionRunner conversionRunner)
{
    private const string EditChapters = "📖 Edit Chapters";
    private const string ManageImages = "🖼️  Manage Images";
    private const string EditMetadata = "📝 Edit Metadata";
    private const string SelectVoice = "🎤 Select Voice";
    private const string PreviewAudio = "🔊 Preview Audio";
    private const string GenerateAudiobook = "🎧 Generate Audiobook";
    private const string ShowBookInfo = "ℹ️  Show Book Info";
    private const string Exit = "🚪 Exit";

    /// <summary>
    /// Runs the interactive menu loop.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        DisplayBookInfo();

        while (true)
        {
            var voiceStatus = session.SelectedVoice != null
                ? $"[green]{Markup.Escape(session.SelectedVoice.Name)}[/]"
                : "[yellow]Not selected[/]";

            var editStatus = session.HasEdits ? " [dim](modified)[/]" : "";

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold blue]{Markup.Escape(session.Title)}[/]{editStatus}").LeftJustified());
            AnsiConsole.MarkupLine($"[dim]Voice: {voiceStatus}[/]");
            AnsiConsole.WriteLine();

            var choice = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices([
                        EditChapters,
                        ManageImages,
                        EditMetadata,
                        SelectVoice,
                        PreviewAudio,
                        GenerateAudiobook,
                        ShowBookInfo,
                        Exit
                    ]),
                cancellationToken);

            switch (choice)
            {
                case EditChapters:
                    await chapterEditor.RunAsync(session, cancellationToken);
                    break;

                case ManageImages:
                    await imageManager.RunAsync(session, cancellationToken);
                    break;

                case EditMetadata:
                    await metadataEditor.RunAsync(session, cancellationToken);
                    break;

                case SelectVoice:
                    await voiceSelector.RunAsync(session, converter.Synthesizer, cancellationToken);
                    break;

                case PreviewAudio:
                    await audioPreviewer.RunAsync(session, converter.Synthesizer, cancellationToken);
                    break;

                case GenerateAudiobook:
                    if (session.SelectedVoice == null)
                    {
                        AnsiConsole.MarkupLine("[red]Please select a voice first.[/]");
                        await voiceSelector.RunAsync(session, converter.Synthesizer, cancellationToken);
                    }

                    if (session.SelectedVoice != null)
                    {
                        await conversionRunner.RunAsync(session, converter, cancellationToken);
                    }
                    break;

                case ShowBookInfo:
                    DisplayBookInfo();
                    break;

                case Exit:
                    if (session.HasEdits)
                    {
                        var confirmExit = await AnsiConsole.PromptAsync(
                            new ConfirmationPrompt("You have unsaved changes. Are you sure you want to exit?"),
                            cancellationToken);

                        if (!confirmExit)
                        {
                            continue;
                        }
                    }
                    AnsiConsole.MarkupLine("[dim]Goodbye![/]");
                    return;
            }
        }
    }

    private void DisplayBookInfo()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold green]{Markup.Escape(session.Title)}[/]"));
        AnsiConsole.WriteLine();

        var chapters = new Panel(
            string.Join("\n", session.Chapters.Select((c, i) => $"[dim]{i + 1}.[/] {Markup.Escape(c.Name)}")))
            .Header("[yellow]Chapters[/]")
            .Expand();

        var authors = new Panel(
            string.Join("\n", session.Authors))
            .Header("[yellow]Authors[/]")
            .Expand();

        var images = new Panel(
            string.Join("\n", session.Images.Select(i =>
                session.IsCoverImage(i) ? $"{Markup.Escape(i.FileName)} [yellow](Cover)[/]" : Markup.Escape(i.FileName))))
            .Header("[yellow]Images[/]")
            .Expand();

        var description = new Panel(
            Markup.Escape(session.Description.Length > 500
                ? session.Description[..500] + "..."
                : session.Description))
            .Header("[yellow]Description[/]")
            .Expand();

        var layout = new Layout("BookStructure")
            .SplitColumns(
                new Layout("Chapters", chapters),
                new Layout("OtherData")
                    .SplitRows(
                        new Layout("Authors", authors),
                        new Layout("Description", description),
                        new Layout("Images", images)));

        AnsiConsole.Write(layout);
    }
}
