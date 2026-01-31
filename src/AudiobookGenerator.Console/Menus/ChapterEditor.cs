using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

/// <summary>
/// Interactive chapter browser and editor.
/// </summary>
internal sealed class ChapterEditor
{
    private const string Back = "← Back to Main Menu";

    /// <summary>
    /// Runs the chapter editor menu.
    /// </summary>
    public async Task RunAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Edit Chapters[/]").LeftJustified());
            AnsiConsole.WriteLine();

            var chapters = session.Chapters;
            var choices = chapters
                .Select((c, i) => $"{i + 1}. {Markup.Escape(c.Name)}")
                .Append(Back)
                .ToList();

            var choice = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("Select a chapter to view/edit:")
                    .PageSize(15)
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices(choices),
                cancellationToken);

            if (choice == Back)
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
                ? chapter.Content[..2000] + "\n[dim]... (content truncated for display)[/]"
                : chapter.Content;

            AnsiConsole.Write(new Panel(Markup.Escape(contentPreview))
                .Header($"[blue]Content[/] [dim]({chapter.Content.Length} characters)[/]")
                .Expand()
                .Border(BoxBorder.Rounded));

            var action = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices([
                        "✏️  Edit Content",
                        "📋 View Full Content",
                        "← Back to Chapter List"
                    ]),
                cancellationToken);

            switch (action)
            {
                case "✏️  Edit Content":
                    await EditContentAsync(session, chapter, cancellationToken);
                    // Refresh chapter reference after edit
                    chapter = session.Chapters.First(c => c.FileName == chapter.FileName);
                    break;

                case "📋 View Full Content":
                    ViewFullContent(chapter);
                    break;

                case "← Back to Chapter List":
                    return;
            }
        }
    }

    private static async Task EditContentAsync(BookEditSession session, BookChapter chapter, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Editing chapter content. The current content will be shown.[/]");
        AnsiConsole.MarkupLine("[dim]You can modify it and press Enter when done.[/]");
        AnsiConsole.MarkupLine("[dim]Tip: For long content, consider using an external editor.[/]");
        AnsiConsole.WriteLine();

        var editMode = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("How would you like to edit?")
                .AddChoices([
                    "📝 Edit in console (for small changes)",
                    "🔄 Replace entire content",
                    "✂️  Find and replace text",
                    "← Cancel"
                ]),
            cancellationToken);

        switch (editMode)
        {
            case "📝 Edit in console (for small changes)":
                var newContent = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>("Enter new content:")
                        .DefaultValue(chapter.Content.Length > 500 ? chapter.Content[..500] + "..." : chapter.Content)
                        .AllowEmpty(),
                    cancellationToken);

                if (!string.IsNullOrEmpty(newContent) && newContent != chapter.Content)
                {
                    session.UpdateChapterContent(chapter.FileName, newContent);
                    AnsiConsole.MarkupLine("[green]✓ Chapter content updated.[/]");
                }
                break;

            case "🔄 Replace entire content":
                AnsiConsole.MarkupLine("[yellow]Paste your new content (end with an empty line):[/]");
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
                    AnsiConsole.MarkupLine("[green]✓ Chapter content replaced.[/]");
                }
                break;

            case "✂️  Find and replace text":
                var findText = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>("Text to find:")
                        .AllowEmpty(),
                    cancellationToken);

                if (!string.IsNullOrEmpty(findText) && chapter.Content.Contains(findText, StringComparison.OrdinalIgnoreCase))
                {
                    var occurrences = CountOccurrences(chapter.Content, findText);
                    AnsiConsole.MarkupLine($"[dim]Found {occurrences} occurrence(s).[/]");

                    var replaceText = await AnsiConsole.PromptAsync(
                        new TextPrompt<string>("Replace with:")
                            .AllowEmpty(),
                        cancellationToken);

                    var updatedContent = chapter.Content.Replace(findText, replaceText, StringComparison.OrdinalIgnoreCase);
                    session.UpdateChapterContent(chapter.FileName, updatedContent);
                    AnsiConsole.MarkupLine($"[green]✓ Replaced {occurrences} occurrence(s).[/]");
                }
                else if (!string.IsNullOrEmpty(findText))
                {
                    AnsiConsole.MarkupLine("[yellow]Text not found in chapter.[/]");
                }
                break;
        }
    }

    private static void ViewFullContent(BookChapter chapter)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]{Markup.Escape(chapter.Name)} - Full Content[/]"));
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
                AnsiConsole.MarkupLine($"[dim]--- Page {page + 1}/{totalPages} (Press Enter to continue, 'q' to quit) ---[/]");
                var input = System.Console.ReadLine();
                if (input?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true)
                {
                    break;
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]--- End of content ---[/]");
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
