using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Console.Resources;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

/// <summary>
/// Interactive image manager for viewing, adding, and managing book images.
/// </summary>
internal sealed class ImageManager
{
    /// <summary>
    /// Runs the image manager menu.
    /// </summary>
    public async Task RunAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[yellow]{Strings.HeaderManageImages}[/]").LeftJustified());
            AnsiConsole.WriteLine();

            DisplayImagesTable(session);

            var action = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title(Strings.PromptWhatToDo)
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices([
                        Strings.MenuSetCoverImage,
                        Strings.MenuAddImageFromFile,
                        Strings.MenuSaveImageToDisk,
                        Strings.MenuClearCoverImage,
                        Strings.MenuBackToMainMenu
                    ]),
                cancellationToken);

            if (action == Strings.MenuSetCoverImage)
            {
                await SetCoverImageAsync(session, cancellationToken);
            }
            else if (action == Strings.MenuAddImageFromFile)
            {
                await AddImageAsync(session, cancellationToken);
            }
            else if (action == Strings.MenuSaveImageToDisk)
            {
                await SaveImageAsync(session, cancellationToken);
            }
            else if (action == Strings.MenuClearCoverImage)
            {
                session.ClearCoverImage();
                AnsiConsole.MarkupLine($"[green]{Strings.StatusCoverImageCleared}[/]");
            }
            else if (action == Strings.MenuBackToMainMenu)
            {
                return;
            }
        }
    }

    private static void DisplayImagesTable(BookEditSession session)
    {
        var images = session.Images;

        if (images.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{Strings.StatusNoImagesInBook}[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn(Strings.ColumnNumber).Centered())
            .AddColumn(Strings.ColumnFilename)
            .AddColumn(new TableColumn(Strings.ColumnSize).RightAligned())
            .AddColumn(new TableColumn(Strings.ColumnCover).Centered());

        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var isCover = session.IsCoverImage(image);
            var sizeKb = image.Content.Length / 1024.0;

            _ = table.AddRow(
                (i + 1).ToString(),
                image.FileName,
                $"{sizeKb:F1} KB",
                isCover ? "[yellow]★[/]" : "");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static async Task SetCoverImageAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        var images = session.Images;

        if (images.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Strings.ErrorNoImagesAvailable}[/]");
            return;
        }

        var choices = images
            .Select((img, i) => $"{i + 1}. {img.FileName}")
            .Append(Strings.MenuCancel)
            .ToList();

        var choice = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title(Strings.PromptSelectImageAsCover)
                .HighlightStyle(Style.Parse("blue bold"))
                .AddChoices(choices),
            cancellationToken);

        if (choice == Strings.MenuCancel)
        {
            return;
        }

        var index = int.Parse(choice.Split('.')[0]) - 1;
        var selectedImage = images[index];

        session.SetCoverImage(selectedImage);
        AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusImageSetAsCover, selectedImage.FileName)}[/]");
    }

    private static async Task AddImageAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        var filePath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>(Strings.PromptEnterImagePath)
                .Validate(path =>
                {
                    var trimmed = path.Trim('\"');
                    if (!File.Exists(trimmed))
                    {
                        return ValidationResult.Error(Strings.ErrorFileDoesNotExist);
                    }

                    var ext = Path.GetExtension(trimmed).ToLowerInvariant();
                    return ext is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp")
                        ? ValidationResult.Error(Strings.ErrorUnsupportedImageFormat)
                        : ValidationResult.Success();
                }),
            cancellationToken);

        var trimmedPath = filePath.Trim('\"');
        var fileName = Path.GetFileName(trimmedPath);
        var content = await File.ReadAllBytesAsync(trimmedPath, cancellationToken);

        var image = new BookImage(fileName, content);
        session.AddImage(image);

        AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusImageAdded, fileName, $"{content.Length / 1024.0:F1}")}[/]");

        var setAsCover = await AnsiConsole.PromptAsync(
            new ConfirmationPrompt(Strings.PromptSetAsCover)
                .ShowDefaultValue(),
            cancellationToken);

        if (setAsCover)
        {
            session.SetCoverImage(image);
            AnsiConsole.MarkupLine($"[green]{Strings.StatusImageSetAsCoverShort}[/]");
        }
    }

    private static async Task SaveImageAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        var images = session.Images;

        if (images.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Strings.ErrorNoImagesToSave}[/]");
            return;
        }

        var choices = images
            .Select((img, i) => $"{i + 1}. {img.FileName}")
            .Append(Strings.MenuCancel)
            .ToList();

        var choice = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title(Strings.PromptSelectImageToSave)
                .HighlightStyle(Style.Parse("blue bold"))
                .AddChoices(choices),
            cancellationToken);

        if (choice == Strings.MenuCancel)
        {
            return;
        }

        var index = int.Parse(choice.Split('.')[0]) - 1;
        var selectedImage = images[index];

        var outputPath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>(Strings.PromptSaveToDirectory)
                .DefaultValue(Environment.GetFolderPath(Environment.SpecialFolder.Desktop))
                .Validate(path =>
                {
                    var trimmed = path.Trim('\"');
                    return Directory.Exists(trimmed)
                        ? ValidationResult.Success()
                        : ValidationResult.Error(Strings.ErrorDirectoryDoesNotExist);
                }),
            cancellationToken);

        var fullPath = Path.Combine(outputPath.Trim('\"'), selectedImage.FileName);

        await File.WriteAllBytesAsync(fullPath, selectedImage.Content, cancellationToken);
        AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusImageSaved, fullPath)}[/]");
    }
}
