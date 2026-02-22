using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Console.Resources;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

/// <summary>
/// Interactive chapter browser and editor.
/// </summary>
internal sealed class ChapterEditor
{
    /// <summary>
    /// Runs the chapter editor menu.
    /// </summary>
    public async Task RunAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[yellow]{Strings.HeaderEditChapters}[/]").LeftJustified());
            AnsiConsole.WriteLine();

            var chapters = session.Chapters;
            var choices = chapters
                .Select((c, i) => $"{i + 1}. {Markup.Escape(c.Name)}")
                .Append(Strings.MenuBackToMainMenu)
                .ToList();

            var choice = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title(Strings.PromptSelectChapter)
                    .PageSize(15)
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices(choices),
                cancellationToken);

            if (choice == Strings.MenuBackToMainMenu)
            {
                return;
            }

            // Parse chapter index from choice
            var chapterIndex = int.Parse(choice.Split('.')[0]) - 1;
            var chapter = chapters[chapterIndex];

            await EditChapterAsync(session, chapter, cancellationToken);
        }
    }

    private static async Task EditChapterAsync(BookEditSession session, BookChapter chapter, CancellationToken cancellationToken)
    {
        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[yellow]{Markup.Escape(chapter.Name)}[/]").LeftJustified());
            AnsiConsole.WriteLine();

            // Display chapter content in a panel
            var contentPreview = chapter.Content.Length > 2000
                ? chapter.Content[..2000] + $"\n[dim]{Strings.ContentTruncated}[/]"
                : chapter.Content;

            AnsiConsole.Write(new Panel(Markup.Escape(contentPreview))
                .Header($"[blue]{Strings.LabelContent}[/] [dim]({string.Format(Strings.LabelCharactersCount, chapter.Content.Length)})[/]")
                .Expand()
                .Border(BoxBorder.Rounded));

            var action = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title(Strings.PromptWhatToDo)
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices([
                        Strings.MenuEditContent,
                        Strings.MenuViewFullContent,
                        Strings.MenuBackToChapterList
                    ]),
                cancellationToken);

            if (action == Strings.MenuEditContent)
            {
                await EditContentAsync(session, chapter, cancellationToken);
                // Refresh chapter reference after edit
                chapter = session.Chapters.First(c => c.FileName == chapter.FileName);
            }
            else if (action == Strings.MenuViewFullContent)
            {
                ViewFullContent(chapter);
            }
            else if (action == Strings.MenuBackToChapterList)
            {
                return;
            }
        }
    }

    private static async Task EditContentAsync(BookEditSession session, BookChapter chapter, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{Strings.HintEditingChapter1}[/]");
        AnsiConsole.MarkupLine($"[dim]{Strings.HintEditingChapter2}[/]");
        AnsiConsole.MarkupLine($"[dim]{Strings.HintEditingChapter3}[/]");
        AnsiConsole.WriteLine();

        var editMode = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title(Strings.PromptHowToEdit)
                .AddChoices([
                    Strings.MenuEditInConsole,
                    Strings.MenuReplaceEntireContent,
                    Strings.MenuFindAndReplace,
                    Strings.MenuCancel
                ]),
            cancellationToken);

        if (editMode == Strings.MenuEditInConsole)
        {
            var newContent = await AnsiConsole.PromptAsync(
                new TextPrompt<string>(Strings.PromptEnterNewContent)
                    .DefaultValue(chapter.Content.Length > 500 ? chapter.Content[..500] + "..." : chapter.Content)
                    .AllowEmpty(),
                cancellationToken);

            if (!string.IsNullOrEmpty(newContent) && newContent != chapter.Content)
            {
                session.UpdateChapterContent(chapter.FileName, newContent);
                AnsiConsole.MarkupLine($"[green]{Strings.StatusChapterContentUpdated}[/]");
            }
        }
        else if (editMode == Strings.MenuReplaceEntireContent)
        {
            AnsiConsole.MarkupLine($"[yellow]{Strings.PromptPasteNewContent}[/]");
            var lines = new List<string>();
            string? line;
            while (!string.IsNullOrEmpty(line = System.Console.ReadLine()))
            {
                lines.Add(line);
            }
            if (lines.Count > 0)
            {
                var replacedContent = string.Join(Environment.NewLine, lines);
                session.UpdateChapterContent(chapter.FileName, replacedContent);
                AnsiConsole.MarkupLine($"[green]{Strings.StatusChapterContentReplaced}[/]");
            }
        }
        else if (editMode == Strings.MenuFindAndReplace)
        {
            var findText = await AnsiConsole.PromptAsync(
                new TextPrompt<string>(Strings.PromptTextToFind)
                    .AllowEmpty(),
                cancellationToken);

            if (!string.IsNullOrEmpty(findText) && chapter.Content.Contains(findText, StringComparison.OrdinalIgnoreCase))
            {
                var occurrences = CountOccurrences(chapter.Content, findText);
                AnsiConsole.MarkupLine($"[dim]{string.Format(Strings.StatusFoundOccurrences, occurrences)}[/]");

                var replaceText = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>(Strings.PromptReplaceWith)
                        .AllowEmpty(),
                    cancellationToken);

                var updatedContent = chapter.Content.Replace(findText, replaceText, StringComparison.OrdinalIgnoreCase);
                session.UpdateChapterContent(chapter.FileName, updatedContent);
                AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusReplacedOccurrences, occurrences)}[/]");
            }
            else if (!string.IsNullOrEmpty(findText))
            {
                AnsiConsole.MarkupLine($"[yellow]{Strings.ErrorTextNotFound}[/]");
            }
        }
    }

    private static void ViewFullContent(BookChapter chapter)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]{string.Format(Strings.HeaderFullContent, Markup.Escape(chapter.Name))}[/]"));
        AnsiConsole.WriteLine();

        // Split into pages for readability
        const int charsPerPage = 3000;
        var content = chapter.Content;
        var totalPages = (int)Math.Ceiling(content.Length / (double)charsPerPage);

        for (var page = 0; page < totalPages; page++)
        {
            var start = page * charsPerPage;
            var length = Math.Min(charsPerPage, content.Length - start);
            var pageContent = content.Substring(start, length);

            AnsiConsole.WriteLine(pageContent);

            if (page < totalPages - 1)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[dim]{string.Format(Strings.StatusPageProgress, page + 1, totalPages)}[/]");
                var input = System.Console.ReadLine();
                if (input?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true)
                {
                    break;
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{Strings.StatusEndOfContent}[/]");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
