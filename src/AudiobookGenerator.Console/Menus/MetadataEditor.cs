using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Console.Resources;

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
            AnsiConsole.Write(new Rule($"[yellow]{Strings.HeaderEditMetadata}[/]").LeftJustified());
            AnsiConsole.WriteLine();

            DisplayCurrentMetadata(session);

            var action = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title(Strings.PromptWhatToEdit)
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices([
                        Strings.MenuEditTitle,
                        Strings.MenuEditDescription,
                        Strings.MenuEditAuthors,
                        Strings.MenuBackToMainMenu
                    ]),
                cancellationToken);

            if (action == Strings.MenuEditTitle)
            {
                await EditTitleAsync(session, cancellationToken);
            }
            else if (action == Strings.MenuEditDescription)
            {
                await EditDescriptionAsync(session, cancellationToken);
            }
            else if (action == Strings.MenuEditAuthors)
            {
                await EditAuthorsAsync(session, cancellationToken);
            }
            else if (action == Strings.MenuBackToMainMenu)
            {
                return;
            }
        }
    }

    private static void DisplayCurrentMetadata(BookEditSession session)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(Strings.ColumnField)
            .AddColumn(Strings.ColumnValue);

        _ = table.AddRow($"[blue]{Strings.LabelTitle}[/]", Markup.Escape(session.Title));
        _ = table.AddRow($"[blue]{Strings.LabelAuthors}[/]", Markup.Escape(string.Join(", ", session.Authors)));

        var descPreview = session.Description.Length > 200
            ? session.Description[..200] + "..."
            : session.Description;
        _ = table.AddRow($"[blue]{Strings.LabelDescription}[/]", Markup.Escape(descPreview));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static async Task EditTitleAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        var newTitle = await AnsiConsole.PromptAsync(
            new TextPrompt<string>(Strings.PromptEnterNewTitle)
                .DefaultValue(session.Title)
                .Validate(title => !string.IsNullOrWhiteSpace(title)
                    ? ValidationResult.Success()
                    : ValidationResult.Error(Strings.ErrorTitleEmpty)),
            cancellationToken);

        session.Title = newTitle.Trim();
        AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusTitleUpdated, Markup.Escape(session.Title))}[/]");
    }

    private static async Task EditDescriptionAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[dim]{Strings.StatusCurrentDescription}[/]");
        AnsiConsole.WriteLine(session.Description);
        AnsiConsole.WriteLine();

        var editMode = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title(Strings.PromptHowToEdit)
                .AddChoices([
                    Strings.MenuEnterDescriptionSingleLine,
                    Strings.MenuReplaceMultiline,
                    Strings.MenuCancel
                ]),
            cancellationToken);

        if (editMode == Strings.MenuEnterDescriptionSingleLine)
        {
            var newDesc = await AnsiConsole.PromptAsync(
                new TextPrompt<string>(Strings.PromptEnterNewDescription)
                    .AllowEmpty(),
                cancellationToken);

            session.Description = newDesc;
            AnsiConsole.MarkupLine($"[green]{Strings.StatusDescriptionUpdated}[/]");
        }
        else if (editMode == Strings.MenuReplaceMultiline)
        {
            AnsiConsole.MarkupLine($"[yellow]{Strings.PromptEnterDescriptionEndEmpty}[/]");
            var lines = new List<string>();
            string? line;
            while (!string.IsNullOrEmpty(line = System.Console.ReadLine()))
            {
                lines.Add(line);
            }
            session.Description = string.Join(Environment.NewLine, lines);
            AnsiConsole.MarkupLine($"[green]{Strings.StatusDescriptionUpdated}[/]");
        }
    }

    private static async Task EditAuthorsAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[dim]{string.Format(Strings.StatusCurrentAuthors, string.Join(", ", session.Authors))}[/]");
        AnsiConsole.WriteLine();

        var action = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title(Strings.PromptWhatToDo)
                .AddChoices([
                    Strings.MenuReplaceAllAuthors,
                    Strings.MenuAddAuthor,
                    Strings.MenuRemoveAuthor,
                    Strings.MenuCancel
                ]),
            cancellationToken);

        if (action == Strings.MenuReplaceAllAuthors)
        {
            var authorsInput = await AnsiConsole.PromptAsync(
                new TextPrompt<string>(Strings.PromptEnterAuthors)
                    .DefaultValue(string.Join(", ", session.Authors))
                    .Validate(input => !string.IsNullOrWhiteSpace(input)
                        ? ValidationResult.Success()
                        : ValidationResult.Error(Strings.ErrorAtLeastOneAuthor)),
                cancellationToken);

            session.Authors = [.. authorsInput
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

            AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusAuthorsUpdated, string.Join(", ", session.Authors))}[/]");
        }
        else if (action == Strings.MenuAddAuthor)
        {
            var newAuthor = await AnsiConsole.PromptAsync(
                new TextPrompt<string>(Strings.PromptEnterAuthorName)
                    .Validate(name => !string.IsNullOrWhiteSpace(name)
                        ? ValidationResult.Success()
                        : ValidationResult.Error(Strings.ErrorNameEmpty)),
                cancellationToken);

            session.Authors.Add(newAuthor.Trim());
            AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusAuthorAdded, newAuthor.Trim())}[/]");
        }
        else if (action == Strings.MenuRemoveAuthor)
        {
            if (session.Authors.Count <= 1)
            {
                AnsiConsole.MarkupLine($"[yellow]{Strings.ErrorCannotRemoveLastAuthor}[/]");
                return;
            }

            var authorToRemove = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title(Strings.PromptSelectAuthorToRemove)
                    .AddChoices([.. session.Authors, Strings.MenuCancel]),
                cancellationToken);

            if (authorToRemove != Strings.MenuCancel)
            {
                _ = session.Authors.Remove(authorToRemove);
                AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusAuthorRemoved, authorToRemove)}[/]");
            }
        }
    }
}
