using Spectre.Console;

using System.Globalization;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Console.Resources;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

internal sealed class ImageManager
{
    private const string GenerateMissing = "✨ Generate missing referenced descriptions";
    private const string ManageDescription = "📝 Manage an image description";
    private const string ConfigureVision = "⚙ Configure vision models";
    private const string SaveProject = "💾 Save description project as...";

    public async Task RunAsync(
        BookEditSession session,
        BookConverter converter,
        CancellationToken cancellationToken)
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
                        GenerateMissing,
                        ManageDescription,
                        ConfigureVision,
                        SaveProject,
                        Strings.MenuSetCoverImage,
                        Strings.MenuAddImageFromFile,
                        Strings.MenuSaveImageToDisk,
                        Strings.MenuClearCoverImage,
                        Strings.MenuBackToMainMenu
                    ]),
                cancellationToken);

            if (action == GenerateMissing)
            {
                await GenerateMissingAsync(session, converter, cancellationToken);
            }
            else if (action == ManageDescription)
            {
                await ManageDescriptionAsync(session, converter, cancellationToken);
            }
            else if (action == ConfigureVision)
            {
                var menu = new VisionSettingsMenu(converter.VisionSettingsStore, converter.ImageDescriptions);
                await menu.RunAsync(session.Images.FirstOrDefault(), cancellationToken);
            }
            else if (action == SaveProject)
            {
                await SaveProjectAsync(session, converter, cancellationToken);
            }
            else if (action == Strings.MenuSetCoverImage)
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
            .AddColumn(new TableColumn("Refs").RightAligned())
            .AddColumn("Description")
            .AddColumn(new TableColumn(Strings.ColumnCover).Centered());

        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            _ = table.AddRow(
                (index + 1).ToString(CultureInfo.InvariantCulture),
                Markup.Escape(image.FileName),
                session.GetReferenceCount(image).ToString(CultureInfo.InvariantCulture),
                GetDescriptionStatus(image),
                session.IsCoverImage(image) ? "[yellow]★[/]" : string.Empty);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static string GetDescriptionStatus(BookImage image)
    {
        if (image.IsDecorative)
        {
            return "[dim]Decorative[/]";
        }
        if (!string.IsNullOrWhiteSpace(image.CandidateDescription))
        {
            return "[yellow]Review pending[/]";
        }
        if (!string.IsNullOrWhiteSpace(image.ApprovedDescription))
        {
            return $"[green]Approved ({image.DescriptionOrigin})[/]";
        }
        return "[red]Missing[/]";
    }

    private static async Task GenerateMissingAsync(
        BookEditSession session,
        BookConverter converter,
        CancellationToken cancellationToken)
    {
        var profileId = await SelectVisionProfileAsync(session, converter, cancellationToken);
        if (profileId == null)
        {
            return;
        }

        var reporter = new ActionProgress<ImageDescriptionProgress>(update =>
        {
            var color = update.State switch
            {
                ImageDescriptionProgressState.Completed => "green",
                ImageDescriptionProgressState.Failed => "red",
                _ => "blue"
            };
            AnsiConsole.MarkupLine(
                $"[{color}]{update.Current}/{update.Total} {Markup.Escape(update.FileName)}: {update.State}[/]");
        });
        var result = await converter.ImageDescriptionWorkflow.GenerateMissingReferencedAsync(
            session.BuildBook(),
            profileId,
            reporter,
            cancellationToken);
        ApplyCandidates(session, result);
        DisplayBatchResult(result);
    }

    private static async Task ManageDescriptionAsync(
        BookEditSession session,
        BookConverter converter,
        CancellationToken cancellationToken)
    {
        var image = await SelectImageAsync(session.Images, "Select an image:", cancellationToken);
        if (image == null)
        {
            return;
        }

        while (true)
        {
            DisplayDescription(image, session.GetReferenceCount(image));
            var decorativeAction = image.IsDecorative ? "Narrate this image" : "Mark decorative";
            var choices = new List<string>();
            if (!image.IsDecorative && string.IsNullOrWhiteSpace(image.CandidateDescription))
            {
                choices.Add(string.IsNullOrWhiteSpace(image.ApprovedDescription)
                    ? "Generate description"
                    : "Regenerate description");
            }
            choices.Add("Edit approved description");
            choices.Add(decorativeAction);
            choices.Add("Restore original EPUB alt text");
            if (!string.IsNullOrWhiteSpace(image.CandidateDescription))
            {
                choices.Insert(0, "Review generated candidate");
            }
            choices.Add(Strings.MenuBackToMainMenu);

            var action = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>().Title(Strings.PromptWhatToDo).AddChoices(choices),
                cancellationToken);
            if (action == Strings.MenuBackToMainMenu)
            {
                return;
            }
            if (action == "Review generated candidate")
            {
                image = await ReviewCandidateAsync(session, image, cancellationToken);
            }
            else if (action is "Generate description" or "Regenerate description")
            {
                image = await GenerateSelectedAsync(session, converter, image, cancellationToken);
            }
            else if (action == "Edit approved description")
            {
                image = await EditApprovedAsync(session, image, cancellationToken);
            }
            else if (action == decorativeAction)
            {
                image = ImageDescriptionTransitions.SetDecorative(image, !image.IsDecorative);
                session.UpdateImage(image);
            }
            else if (action == "Restore original EPUB alt text")
            {
                image = ImageDescriptionTransitions.RestoreOriginalAltText(image);
                session.UpdateImage(image);
            }
        }
    }

    private static async Task<BookImage> GenerateSelectedAsync(
        BookEditSession session,
        BookConverter converter,
        BookImage image,
        CancellationToken cancellationToken)
    {
        var profileId = await SelectVisionProfileAsync(session, converter, cancellationToken);
        if (profileId == null)
        {
            return image;
        }

        var result = await converter.ImageDescriptionWorkflow.GenerateSelectedAsync(
            session.BuildBook(),
            image.Id,
            profileId,
            regenerate: !string.IsNullOrWhiteSpace(image.ApprovedDescription),
            progress: null,
            cancellationToken);
        ApplyCandidates(session, result);
        DisplayBatchResult(result);
        return session.Images.First(candidate => string.Equals(candidate.Id, image.Id, StringComparison.Ordinal));
    }

    private static async Task<BookImage> ReviewCandidateAsync(
        BookEditSession session,
        BookImage image,
        CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Panel(Markup.Escape(image.ApprovedDescription ?? "(none)")).Header("Approved"));
        AnsiConsole.Write(new Panel(Markup.Escape(image.CandidateDescription ?? "(none)")).Header("Candidate"));
        var action = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Review candidate:")
                .AddChoices(["Approve", "Edit and approve", "Reject", Strings.MenuCancel]),
            cancellationToken);
        if (action == "Approve")
        {
            image = ImageDescriptionTransitions.ApproveCandidate(image);
        }
        else if (action == "Edit and approve")
        {
            var text = await AnsiConsole.PromptAsync(
                new TextPrompt<string>("Approved description:")
                    .DefaultValue(image.CandidateDescription ?? string.Empty),
                cancellationToken);
            image = ImageDescriptionTransitions.ApproveCandidate(image, text);
        }
        else if (action == "Reject")
        {
            image = ImageDescriptionTransitions.RejectCandidate(image);
        }
        session.UpdateImage(image);
        return image;
    }

    private static async Task<BookImage> EditApprovedAsync(
        BookEditSession session,
        BookImage image,
        CancellationToken cancellationToken)
    {
        var description = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Approved description:")
                .DefaultValue(image.ApprovedDescription ?? string.Empty)
                .Validate(static value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("Description cannot be empty.")
                    : ValidationResult.Success()),
            cancellationToken);
        image = ImageDescriptionTransitions.EditApproved(image, description);
        session.UpdateImage(image);
        return image;
    }

    private static void DisplayDescription(BookImage image, int referenceCount)
    {
        AnsiConsole.MarkupLine(
            $"[blue]{Markup.Escape(image.FileName)}[/] [dim]({referenceCount} chapter reference(s))[/]");
        AnsiConsole.MarkupLine($"[dim]Status: {GetDescriptionStatus(image)}[/]");
        if (!string.IsNullOrWhiteSpace(image.ApprovedDescription))
        {
            AnsiConsole.Write(new Panel(Markup.Escape(image.ApprovedDescription)).Header("Approved description"));
        }
    }

    private static void ApplyCandidates(
        BookEditSession session,
        ImageDescriptionBatchResult result)
    {
        foreach (var candidate in result.Candidates)
        {
            var image = session.Images.First(item =>
                string.Equals(item.Id, candidate.ImageId, StringComparison.Ordinal));
            session.UpdateImage(ImageDescriptionTransitions.WithCandidate(image, candidate.Description));
        }
    }

    private static void DisplayBatchResult(ImageDescriptionBatchResult result)
    {
        AnsiConsole.MarkupLine(
            $"[green]{result.Candidates.Count} description candidate(s) generated.[/]");
        foreach (var failure in result.Failures)
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(failure.FileName)}: {Markup.Escape(failure.Message)}[/]");
        }
    }

    private static async Task<string?> SelectVisionProfileAsync(
        BookEditSession session,
        BookConverter converter,
        CancellationToken cancellationToken)
    {
        var profiles = await converter.ImageDescriptions.GetProfilesAsync(cancellationToken);
        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Configure a vision profile first.[/]");
            var settingsMenu = new VisionSettingsMenu(converter.VisionSettingsStore, converter.ImageDescriptions);
            await settingsMenu.RunAsync(session.Images.FirstOrDefault(), cancellationToken);
            profiles = await converter.ImageDescriptions.GetProfilesAsync(cancellationToken);
            if (profiles.Count == 0)
            {
                return null;
            }
        }

        var defaultId = session.SelectedVisionProfileId
            ?? await converter.ImageDescriptions.GetDefaultProfileIdAsync(cancellationToken);
        if (profiles.Count == 1)
        {
            session.SelectedVisionProfileId = profiles[0].Id;
            return profiles[0].Id;
        }

        var choices = profiles
            .Select((profile, index) => $"{index + 1}. {Markup.Escape(profile.DisplayName)} [{Markup.Escape(profile.Id)}]")
            .ToArray();
        var selection = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title($"Vision profile (default: {Markup.Escape(defaultId ?? "none")}):")
                .AddChoices(choices),
            cancellationToken);
        var selectedIndex = int.Parse(
            selection[..selection.IndexOf('.', StringComparison.Ordinal)],
            CultureInfo.InvariantCulture) - 1;
        session.SelectedVisionProfileId = profiles[selectedIndex].Id;
        return session.SelectedVisionProfileId;
    }

    private static async Task<BookImage?> SelectImageAsync(
        IReadOnlyList<BookImage> images,
        string title,
        CancellationToken cancellationToken)
    {
        if (images.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Strings.ErrorNoImagesAvailable}[/]");
            return null;
        }

        var choices = images
            .Select((image, index) => $"{index + 1}. {Markup.Escape(image.FileName)}")
            .Append(Strings.MenuCancel)
            .ToArray();
        var choice = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>().Title(title).AddChoices(choices),
            cancellationToken);
        if (choice == Strings.MenuCancel)
        {
            return null;
        }
        var index = int.Parse(
            choice[..choice.IndexOf('.', StringComparison.Ordinal)],
            CultureInfo.InvariantCulture) - 1;
        return images[index];
    }

    private static async Task SetCoverImageAsync(
        BookEditSession session,
        CancellationToken cancellationToken)
    {
        var selectedImage = await SelectImageAsync(session.Images, Strings.PromptSelectImageAsCover, cancellationToken);
        if (selectedImage == null)
        {
            return;
        }

        session.SetCoverImage(selectedImage);
        AnsiConsole.MarkupLine(
            $"[green]{string.Format(Strings.StatusImageSetAsCover, selectedImage.FileName)}[/]");
    }

    private static async Task AddImageAsync(
        BookEditSession session,
        CancellationToken cancellationToken)
    {
        var filePath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>(Strings.PromptEnterImagePath)
                .Validate(path =>
                {
                    var trimmed = path.Trim('"');
                    if (!File.Exists(trimmed))
                    {
                        return ValidationResult.Error(Strings.ErrorFileDoesNotExist);
                    }
                    var extension = Path.GetExtension(trimmed).ToLowerInvariant();
                    return extension is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".svg")
                        ? ValidationResult.Error(Strings.ErrorUnsupportedImageFormat)
                        : ValidationResult.Success();
                }),
            cancellationToken);
        var trimmedPath = filePath.Trim('"');
        var fileName = Path.GetFileName(trimmedPath);
        var image = new BookImage(
            fileName,
            await File.ReadAllBytesAsync(trimmedPath, cancellationToken));
        session.AddImage(image);
        AnsiConsole.MarkupLine(
            $"[green]{string.Format(Strings.StatusImageAdded, fileName, $"{image.Content.Length / 1024.0:F1}")}[/]");
        if (await AnsiConsole.PromptAsync(
                new ConfirmationPrompt(Strings.PromptSetAsCover).ShowDefaultValue(),
                cancellationToken))
        {
            session.SetCoverImage(image);
            AnsiConsole.MarkupLine($"[green]{Strings.StatusImageSetAsCoverShort}[/]");
        }
    }

    private static async Task SaveImageAsync(
        BookEditSession session,
        CancellationToken cancellationToken)
    {
        var selectedImage = await SelectImageAsync(session.Images, Strings.PromptSelectImageToSave, cancellationToken);
        if (selectedImage == null)
        {
            return;
        }

        var outputPath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>(Strings.PromptSaveToDirectory)
                .DefaultValue(Environment.GetFolderPath(Environment.SpecialFolder.Desktop))
                .Validate(path => Directory.Exists(path.Trim('"'))
                    ? ValidationResult.Success()
                    : ValidationResult.Error(Strings.ErrorDirectoryDoesNotExist)),
            cancellationToken);
        var fullPath = Path.Combine(outputPath.Trim('"'), selectedImage.FileName);
        await File.WriteAllBytesAsync(fullPath, selectedImage.Content, cancellationToken);
        AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusImageSaved, fullPath)}[/]");
    }

    private static async Task SaveProjectAsync(
        BookEditSession session,
        BookConverter converter,
        CancellationToken cancellationToken)
    {
        if (session.SourceFile == null)
        {
            throw new InvalidOperationException("This session has no source EPUB to identify in a sidecar.");
        }

        var defaultPath = Path.Combine(
            session.SourceFile.DirectoryName ?? Environment.CurrentDirectory,
            $"{session.FileName}.audiobook.json");
        var path = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Sidecar project path:")
                .DefaultValue(defaultPath)
                .Validate(static value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("A path is required.")
                    : ValidationResult.Success()),
            cancellationToken);
        if (!path.EndsWith(".audiobook.json", StringComparison.OrdinalIgnoreCase))
        {
            path += ".audiobook.json";
        }

        var saveResult = await converter.ImageDescriptionProjects.SaveAsync(
            new FileInfo(path),
            session.SourceFile,
            session.BuildBook(),
            session.SelectedVisionProfileId,
            cancellationToken);
        AnsiConsole.MarkupLine($"[green]Saved {Markup.Escape(path)}[/]");
        if (saveResult.SkippedImportedImageCount > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]The sidecar excludes {saveResult.SkippedImportedImageCount} imported image(s).[/]");
        }
        if (saveResult.CoverSelectionNotPersisted)
        {
            AnsiConsole.MarkupLine(
                "[yellow]The imported cover selection cannot be restored from this annotation-only sidecar.[/]");
        }
    }
}
