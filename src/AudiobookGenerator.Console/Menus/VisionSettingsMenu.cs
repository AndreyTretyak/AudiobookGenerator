using Spectre.Console;

using System.Globalization;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

internal sealed class VisionSettingsMenu(
    IVisionSettingsStore settingsStore,
    IImageDescriptionService descriptions)
{
    private const string AddProfile = "Add OpenAI-compatible vision profile";
    private const string EditProfile = "Edit profile";
    private const string RemoveProfile = "Remove profile";
    private const string SetDefaultProfile = "Set default profile";
    private const string TestProfile = "Test profile";
    private const string Back = "Back";

    public async Task RunAsync(BookImage? testImage, CancellationToken cancellationToken)
    {
        while (true)
        {
            VisionSettings settings;
            try
            {
                settings = await settingsStore.LoadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine($"[dim]Settings: {Markup.Escape(settingsStore.SettingsPath)}[/]");
                return;
            }

            DisplayProfiles(settings);
            var choice = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("Vision model settings")
                    .AddChoices([AddProfile, EditProfile, RemoveProfile, SetDefaultProfile, TestProfile, Back]),
                cancellationToken);
            if (choice == Back)
            {
                return;
            }

            try
            {
                switch (choice)
                {
                    case AddProfile:
                        await AddProfileAsync(settings, cancellationToken);
                        break;
                    case EditProfile:
                        await EditProfileAsync(settings, cancellationToken);
                        break;
                    case RemoveProfile:
                        await RemoveProfileAsync(settings, cancellationToken);
                        break;
                    case SetDefaultProfile:
                        await SetDefaultProfileAsync(settings, cancellationToken);
                        break;
                    case TestProfile:
                        await TestProfileAsync(settings, testImage, cancellationToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    private void DisplayProfiles(VisionSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Vision profiles[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[dim]Settings: {Markup.Escape(settingsStore.SettingsPath)}[/]");
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Default")
            .AddColumn("ID")
            .AddColumn("Name")
            .AddColumn("Endpoint")
            .AddColumn("Model");
        foreach (var profile in settings.Profiles)
        {
            _ = table.AddRow(
                string.Equals(settings.DefaultProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)
                    ? "[green]Yes[/]"
                    : string.Empty,
                Markup.Escape(profile.Id),
                Markup.Escape(profile.DisplayName),
                Markup.Escape(profile.BaseUrl),
                Markup.Escape(profile.Model));
        }
        AnsiConsole.Write(table);
    }

    private async Task AddProfileAsync(VisionSettings settings, CancellationToken cancellationToken)
    {
        var id = await PromptAsync("Profile ID:", "local-vision", cancellationToken);
        if (settings.Profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Vision profile ID '{id}' already exists.");
        }

        var profile = new OpenAiCompatibleVisionProfile
        {
            Id = id,
            DisplayName = "Local Vision",
            Model = "qwen3-vl:8b"
        };
        await PromptForProfileAsync(profile, cancellationToken);
        settings.Profiles.Add(profile);
        settings.DefaultProfileId ??= profile.Id;
        await settingsStore.SaveAsync(settings, cancellationToken);
    }

    private async Task EditProfileAsync(VisionSettings settings, CancellationToken cancellationToken)
    {
        var profile = await SelectProfileAsync(settings, "Select a profile to edit:", cancellationToken);
        if (profile == null)
        {
            return;
        }

        await PromptForProfileAsync(profile, cancellationToken);
        await settingsStore.SaveAsync(settings, cancellationToken);
    }

    private async Task RemoveProfileAsync(VisionSettings settings, CancellationToken cancellationToken)
    {
        var profile = await SelectProfileAsync(settings, "Select a profile to remove:", cancellationToken);
        if (profile == null)
        {
            return;
        }

        if (!await AnsiConsole.PromptAsync(
                new ConfirmationPrompt($"Remove '{Markup.Escape(profile.DisplayName)}'?"),
                cancellationToken))
        {
            return;
        }

        _ = settings.Profiles.Remove(profile);
        if (string.Equals(settings.DefaultProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultProfileId = settings.Profiles.FirstOrDefault()?.Id;
        }
        await settingsStore.SaveAsync(settings, cancellationToken);
    }

    private async Task SetDefaultProfileAsync(VisionSettings settings, CancellationToken cancellationToken)
    {
        var profile = await SelectProfileAsync(settings, "Select the default vision profile:", cancellationToken);
        if (profile == null)
        {
            return;
        }

        settings.DefaultProfileId = profile.Id;
        await settingsStore.SaveAsync(settings, cancellationToken);
    }

    private async Task TestProfileAsync(
        VisionSettings settings,
        BookImage? suppliedImage,
        CancellationToken cancellationToken)
    {
        var profile = await SelectProfileAsync(settings, "Select a profile to test:", cancellationToken);
        if (profile == null)
        {
            return;
        }

        var image = suppliedImage ?? await PromptForImageAsync(cancellationToken);
        var session = await descriptions.CreateSessionAsync(profile.Id, cancellationToken);
        var book = new Book("test", "Vision profile test", string.Empty, [], null, [], [image]);
        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(
                "Generating a test description...",
                async _ => await session.DescribeAsync(book, image, false, cancellationToken));
        AnsiConsole.Write(new Panel(Markup.Escape(result.Description)).Header("[green]Description[/]"));
    }

    private static async Task PromptForProfileAsync(
        OpenAiCompatibleVisionProfile profile,
        CancellationToken cancellationToken)
    {
        profile.DisplayName = await PromptAsync("Display name:", profile.DisplayName, cancellationToken);
        profile.BaseUrl = await PromptAsync("Base URL (ending in /v1/):", profile.BaseUrl, cancellationToken);
        profile.Model = await PromptAsync("Vision model:", profile.Model, cancellationToken);
        profile.Prompt = await PromptAsync("Description prompt:", profile.Prompt, cancellationToken);
        profile.Temperature = await PromptDoubleAsync("Temperature:", profile.Temperature, cancellationToken);
        profile.MaximumOutputTokens = await PromptIntegerAsync(
            "Maximum output tokens:",
            profile.MaximumOutputTokens,
            cancellationToken);
        profile.TimeoutSeconds = await PromptIntegerAsync("Timeout seconds:", profile.TimeoutSeconds, cancellationToken);
        profile.MaximumImageDimension = await PromptIntegerAsync(
            "Maximum image dimension:",
            profile.MaximumImageDimension,
            cancellationToken);
        profile.MaximumImageBytes = await PromptIntegerAsync(
            "Maximum encoded image bytes:",
            profile.MaximumImageBytes,
            cancellationToken);
        profile.MaximumContextCharacters = await PromptIntegerAsync(
            "Maximum nearby context characters:",
            profile.MaximumContextCharacters,
            cancellationToken);
        profile.ApiKeyEnvironmentVariable = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("API-key environment variable (optional):")
                .DefaultValue(profile.ApiKeyEnvironmentVariable ?? string.Empty)
                .AllowEmpty(),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable))
        {
            profile.ApiKeyEnvironmentVariable = null;
        }
    }

    private static async Task<BookImage> PromptForImageAsync(CancellationToken cancellationToken)
    {
        var path = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Image file for the test:")
                .Validate(value => File.Exists(value.Trim('"'))
                    ? ValidationResult.Success()
                    : ValidationResult.Error("File does not exist.")),
            cancellationToken);
        var fullPath = path.Trim('"');
        return new BookImage(
            Path.GetFileName(fullPath),
            await File.ReadAllBytesAsync(fullPath, cancellationToken));
    }

    private static async Task<OpenAiCompatibleVisionProfile?> SelectProfileAsync(
        VisionSettings settings,
        string title,
        CancellationToken cancellationToken)
    {
        if (settings.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No vision profiles are configured.[/]");
            return null;
        }

        var choices = settings.Profiles
            .Select((profile, index) => $"{index + 1}. {Markup.Escape(profile.DisplayName)} [{Markup.Escape(profile.Id)}]")
            .Append(Back)
            .ToArray();
        var selection = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>().Title(title).AddChoices(choices),
            cancellationToken);
        if (selection == Back)
        {
            return null;
        }

        var index = int.Parse(
            selection[..selection.IndexOf('.', StringComparison.Ordinal)],
            CultureInfo.InvariantCulture) - 1;
        return settings.Profiles[index];
    }

    private static Task<string> PromptAsync(
        string prompt,
        string defaultValue,
        CancellationToken cancellationToken) =>
        AnsiConsole.PromptAsync(
            new TextPrompt<string>(prompt)
                .DefaultValue(defaultValue)
                .Validate(static value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("A value is required.")
                    : ValidationResult.Success()),
            cancellationToken);

    private static async Task<int> PromptIntegerAsync(
        string prompt,
        int defaultValue,
        CancellationToken cancellationToken)
    {
        var value = await PromptAsync(prompt, defaultValue.ToString(CultureInfo.InvariantCulture), cancellationToken);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{prompt} must be an integer.");
    }

    private static async Task<double> PromptDoubleAsync(
        string prompt,
        double defaultValue,
        CancellationToken cancellationToken)
    {
        var value = await PromptAsync(prompt, defaultValue.ToString(CultureInfo.InvariantCulture), cancellationToken);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{prompt} must be a number.");
    }
}
