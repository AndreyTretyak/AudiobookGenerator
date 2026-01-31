using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

/// <summary>
/// Interactive image manager for viewing, adding, and managing book images.
/// </summary>
internal sealed class ImageManager
{
    private const string Back = "← Back to Main Menu";

    /// <summary>
    /// Runs the image manager menu.
    /// </summary>
    public async Task RunAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Manage Images[/]").LeftJustified());
            AnsiConsole.WriteLine();

            DisplayImagesTable(session);

            var action = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .HighlightStyle(Style.Parse("blue bold"))
                    .AddChoices([
                        "🖼️  Set Cover Image",
                        "➕ Add Image from File",
                        "💾 Save Image to Disk",
                        "❌ Clear Cover Image",
                        Back
                    ]),
                cancellationToken);

            switch (action)
            {
                case "🖼️  Set Cover Image":
                    await SetCoverImageAsync(session, cancellationToken);
                    break;

                case "➕ Add Image from File":
                    await AddImageAsync(session, cancellationToken);
                    break;

                case "💾 Save Image to Disk":
                    await SaveImageAsync(session, cancellationToken);
                    break;

                case "❌ Clear Cover Image":
                    session.ClearCoverImage();
                    AnsiConsole.MarkupLine("[green]✓ Cover image cleared.[/]");
                    break;

                case Back:
                    return;
            }
        }
    }

    private static void DisplayImagesTable(BookEditSession session)
    {
        var images = session.Images;

        if (images.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No images in book.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").Centered())
            .AddColumn("Filename")
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn("Cover").Centered());

        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var isCover = session.IsCoverImage(image);
            var sizeKb = image.Content.Length / 1024.0;

            table.AddRow(
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
            AnsiConsole.MarkupLine("[yellow]No images available. Add an image first.[/]");
            return;
        }

        var choices = images
            .Select((img, i) => $"{i + 1}. {img.FileName}")
            .Append("← Cancel")
            .ToList();

        var choice = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Select image to set as cover:")
                .HighlightStyle(Style.Parse("blue bold"))
                .AddChoices(choices),
            cancellationToken);

        if (choice == "← Cancel")
        {
            return;
        }

        var index = int.Parse(choice.Split('.')[0]) - 1;
        var selectedImage = images[index];

        session.SetCoverImage(selectedImage);
        AnsiConsole.MarkupLine($"[green]✓ '{selectedImage.FileName}' set as cover image.[/]");
    }

    private static async Task AddImageAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        var filePath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Enter path to image file:")
                .Validate(path =>
                {
                    var trimmed = path.Trim('\"');
                    if (!File.Exists(trimmed))
                    {
                        return ValidationResult.Error("File does not exist.");
                    }

                    var ext = Path.GetExtension(trimmed).ToLowerInvariant();
                    if (ext is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp"))
                    {
                        return ValidationResult.Error("Unsupported image format. Use JPG, PNG, GIF, or WebP.");
                    }

                    return ValidationResult.Success();
                }),
            cancellationToken);

        var trimmedPath = filePath.Trim('\"');
        var fileName = Path.GetFileName(trimmedPath);
        var content = await File.ReadAllBytesAsync(trimmedPath, cancellationToken);

        var image = new BookImage(fileName, content);
        session.AddImage(image);

        AnsiConsole.MarkupLine($"[green]✓ Image '{fileName}' added ({content.Length / 1024.0:F1} KB).[/]");

        var setAsCover = await AnsiConsole.PromptAsync(
            new ConfirmationPrompt("Set this image as the cover?")
                .ShowDefaultValue(),
            cancellationToken);

        if (setAsCover)
        {
            session.SetCoverImage(image);
            AnsiConsole.MarkupLine("[green]✓ Image set as cover.[/]");
        }
    }

    private static async Task SaveImageAsync(BookEditSession session, CancellationToken cancellationToken)
    {
        var images = session.Images;

        if (images.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No images to save.[/]");
            return;
        }

        var choices = images
            .Select((img, i) => $"{i + 1}. {img.FileName}")
            .Append("← Cancel")
            .ToList();

        var choice = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Select image to save:")
                .HighlightStyle(Style.Parse("blue bold"))
                .AddChoices(choices),
            cancellationToken);

        if (choice == "← Cancel")
        {
            return;
        }

        var index = int.Parse(choice.Split('.')[0]) - 1;
        var selectedImage = images[index];

        var outputPath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Save to directory:")
                .DefaultValue(Environment.GetFolderPath(Environment.SpecialFolder.Desktop))
                .Validate(path =>
                {
                    var trimmed = path.Trim('\"');
                    return Directory.Exists(trimmed)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Directory does not exist.");
                }),
            cancellationToken);

        var fullPath = Path.Combine(outputPath.Trim('\"'), selectedImage.FileName);

        await File.WriteAllBytesAsync(fullPath, selectedImage.Content, cancellationToken);
        AnsiConsole.MarkupLine($"[green]✓ Image saved to '{fullPath}'.[/]");
    }
}
