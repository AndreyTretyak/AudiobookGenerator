using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Console.Resources;
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
    TtsSettingsMenu ttsSettingsMenu,
    ConversionRunner conversionRunner)
{
    /// <summary>
    /// Runs the interactive menu loop.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        DisplayBookInfo();

        while (true)
        {
            var voiceStatus = session.SelectedVoice != null
                ? $"[green]{Markup.Escape(session.SelectedVoice.Name)}[/] [dim]({Markup.Escape(session.SelectedVoice.ProviderId)})[/]"
                : $"[yellow]{Strings.StatusVoiceNotSelected}[/]";

            var editStatus = session.HasEdits ? $" [dim]{Strings.StatusModified}[/]" : "";

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold blue]{Markup.Escape(session.Title)}[/]{editStatus}").LeftJustified());
            AnsiConsole.MarkupLine($"[dim]{Strings.LabelVoice}: {voiceStatus}[/]");
            AnsiConsole.WriteLine();

            var choice = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title(Strings.PromptWhatToDo)
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices([
                        Strings.MenuEditChapters,
                        Strings.MenuManageImages,
                        Strings.MenuEditMetadata,
                        Strings.MenuSelectVoice,
                        Strings.MenuConfigureTtsProviders,
                        Strings.MenuPreviewAudio,
                        Strings.MenuGenerateAudiobook,
                        Strings.MenuShowBookInfo,
                        Strings.MenuExit
                    ]),
                cancellationToken);

            if (choice == Strings.MenuEditChapters)
            {
                await chapterEditor.RunAsync(session, cancellationToken);
            }
            else if (choice == Strings.MenuManageImages)
            {
                await imageManager.RunAsync(session, converter, cancellationToken);
            }
            else if (choice == Strings.MenuEditMetadata)
            {
                await metadataEditor.RunAsync(session, cancellationToken);
            }
            else if (choice == Strings.MenuSelectVoice)
            {
                await voiceSelector.RunAsync(session, converter.Synthesizer, cancellationToken);
            }
            else if (choice == Strings.MenuConfigureTtsProviders)
            {
                await ttsSettingsMenu.RunAsync(cancellationToken);
                if (session.SelectedVoice != null)
                {
                    try
                    {
                        var voices = await converter.Synthesizer.GetVoicesAsync(
                            session.SelectedVoice.ProviderId,
                            cancellationToken);
                        if (!voices.Any(voice => string.Equals(voice.Id, session.SelectedVoice.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            session.SelectedVoice = null;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        session.SelectedVoice = null;
                    }
                }
            }
            else if (choice == Strings.MenuPreviewAudio)
            {
                await audioPreviewer.RunAsync(session, converter, cancellationToken);
            }
            else if (choice == Strings.MenuGenerateAudiobook)
            {
                if (session.SelectedVoice == null)
                {
                    AnsiConsole.MarkupLine($"[red]{Strings.ErrorSelectVoiceFirst}[/]");
                    await voiceSelector.RunAsync(session, converter.Synthesizer, cancellationToken);
                }

                if (session.SelectedVoice != null)
                {
                    await conversionRunner.RunAsync(session, converter, cancellationToken);
                }
            }
            else if (choice == Strings.MenuShowBookInfo)
            {
                DisplayBookInfo();
            }
            else if (choice == Strings.MenuExit)
            {
                if (session.HasEdits)
                {
                    var confirmExit = await AnsiConsole.PromptAsync(
                        new ConfirmationPrompt(Strings.PromptConfirmExitUnsaved),
                        cancellationToken);

                    if (!confirmExit)
                    {
                        continue;
                    }
                }
                AnsiConsole.MarkupLine($"[dim]{Strings.StatusGoodbye}[/]");
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
            .Header($"[yellow]{Strings.LabelChapters}[/]")
            .Expand();

        var authors = new Panel(
            string.Join("\n", session.Authors))
            .Header($"[yellow]{Strings.LabelAuthors}[/]")
            .Expand();

        var images = new Panel(
            string.Join("\n", session.Images.Select(i =>
                session.IsCoverImage(i) ? $"{Markup.Escape(i.FileName)} [yellow]{Strings.LabelCover}[/]" : Markup.Escape(i.FileName))))
            .Header($"[yellow]{Strings.LabelImages}[/]")
            .Expand();

        var description = new Panel(
            Markup.Escape(session.Description.Length > 500
                ? session.Description[..500] + "..."
                : session.Description))
            .Header($"[yellow]{Strings.LabelDescription}[/]")
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
