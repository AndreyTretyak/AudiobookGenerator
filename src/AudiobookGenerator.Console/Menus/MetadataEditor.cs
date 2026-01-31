using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;

namespace YewCone.AudiobookGenerator.Console.Menus;

/// <summary>
/// Interactive metadata editor for title, description, and authors.
/// </summary>
internal sealed class MetadataEditor
{
    /// <summary>
    /// Runs the metadata editor menu.
    /// </summary>
    public async Task RunAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Edit Metadata[/]").LeftJustified());
            AnsiConsole.WriteLine();

            DisplayCurrentMetadata(session);

            var action = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("What would you like to edit?")
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices([
                        "📕 Edit Title",
                        "📝 Edit Description",
                        "👤 Edit Authors",
                        "← Back to Main Menu"
                    ]),
                cancellationToken);

            switch (action)
            {
                case "📕 Edit Title":
                    await EditTitleAsync(session, cancellationToken);
                    break;

                case "📝 Edit Description":
                    await EditDescriptionAsync(session, cancellationToken);
                    break;

                case "👤 Edit Authors":
                    await EditAuthorsAsync(session, cancellationToken);
                    break;

                case "← Back to Main Menu":
                    return;
            }
        }
    }

    private static void DisplayCurrentMetadata(BookEditSession session)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Field")
            .AddColumn("Value");

        table.AddRow("[blue]Title[/]", Markup.Escape(session.Title));
        table.AddRow("[blue]Authors[/]", Markup.Escape(string.Join(", ", session.Authors)));

        var descPreview = session.Description.Length > 200
            ? session.Description[..200] + "..."
            : session.Description;
        table.AddRow("[blue]Description[/]", Markup.Escape(descPreview));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static async Task EditTitleAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        var newTitle = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Enter new title:")
                .DefaultValue(session.Title)
                .Validate(title => !string.IsNullOrWhiteSpace(title)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Title cannot be empty.")),
            cancellationToken);

        session.Title = newTitle.Trim();
        AnsiConsole.MarkupLine($"[green]✓ Title updated to '{Markup.Escape(session.Title)}'.[/]");
    }

    private static async Task EditDescriptionAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[dim]Current description:[/]");
        AnsiConsole.WriteLine(session.Description);
        AnsiConsole.WriteLine();

        var editMode = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("How would you like to edit?")
                .AddChoices([
                    "📝 Enter new description (single line)",
                    "🔄 Replace with multiline input",
                    "← Cancel"
                ]),
            cancellationToken);

        switch (editMode)
        {
            case "📝 Enter new description (single line)":
                var newDesc = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>("Enter new description:")
                        .AllowEmpty(),
                    cancellationToken);

                session.Description = newDesc;
                AnsiConsole.MarkupLine("[green]✓ Description updated.[/]");
                break;

            case "🔄 Replace with multiline input":
                AnsiConsole.MarkupLine("[yellow]Enter new description (end with an empty line):[/]");
                var lines = new List<string>();
                string? line;
                while (!string.IsNullOrEmpty(line = System.Console.ReadLine()))
                {
                    lines.Add(line);
                }
                session.Description = string.Join(Environment.NewLine, lines);
                AnsiConsole.MarkupLine("[green]✓ Description updated.[/]");
                break;
        }
    }

    private static async Task EditAuthorsAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[dim]Current authors: {string.Join(", ", session.Authors)}[/]");
        AnsiConsole.WriteLine();

        var action = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("What would you like to do?")
                .AddChoices([
                    "🔄 Replace all authors",
                    "➕ Add author",
                    "➖ Remove author",
                    "← Cancel"
                ]),
            cancellationToken);

        switch (action)
        {
            case "🔄 Replace all authors":
                var authorsInput = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>("Enter authors (comma-separated):")
                        .DefaultValue(string.Join(", ", session.Authors))
                        .Validate(input => !string.IsNullOrWhiteSpace(input)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("At least one author is required.")),
                    cancellationToken);

                session.Authors = [.. authorsInput
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

                AnsiConsole.MarkupLine($"[green]✓ Authors updated: {string.Join(", ", session.Authors)}[/]");
                break;

            case "➕ Add author":
                var newAuthor = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>("Enter author name:")
                        .Validate(name => !string.IsNullOrWhiteSpace(name)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Name cannot be empty.")),
                    cancellationToken);

                session.Authors.Add(newAuthor.Trim());
                AnsiConsole.MarkupLine($"[green]✓ Author '{newAuthor.Trim()}' added.[/]");
                break;

            case "➖ Remove author":
                if (session.Authors.Count <= 1)
                {
                    AnsiConsole.MarkupLine("[yellow]Cannot remove the last author.[/]");
                    break;
                }

                var authorToRemove = await AnsiConsole.PromptAsync(
                    new SelectionPrompt<string>()
                        .Title("Select author to remove:")
                        .AddChoices([.. session.Authors, "← Cancel"]),
                    cancellationToken);

                if (authorToRemove != "← Cancel")
                {
                    session.Authors.Remove(authorToRemove);
                    AnsiConsole.MarkupLine($"[green]✓ Author '{authorToRemove}' removed.[/]");
                }
                break;
        }
    }
}
