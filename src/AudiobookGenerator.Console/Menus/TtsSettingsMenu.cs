using Spectre.Console;

using System.Globalization;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

internal sealed class TtsSettingsMenu(
    ITtsSettingsStore settingsStore,
    IAudioSynthesizer synthesizer,
    IAudioPreviewService preview)
{
    private const string AddProfile = "Add OpenAI-compatible profile";
    private const string EditProfile = "Edit profile";
    private const string RemoveProfile = "Remove profile";
    private const string SetDefaultProvider = "Set default provider";
    private const string TestProfile = "Test profile and voice";
    private const string StopPreview = "Stop preview";
    private const string Back = "Back";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TtsSettings settings;
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

            await DisplayProfilesAsync(settings, cancellationToken);

            var choice = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("TTS provider settings")
                    .AddChoices([
                        AddProfile,
                        EditProfile,
                        RemoveProfile,
                        SetDefaultProvider,
                        TestProfile,
                        StopPreview,
                        Back
                    ]),
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
                    case SetDefaultProvider:
                        await SetDefaultProviderAsync(settings, cancellationToken);
                        break;
                    case TestProfile:
                        await TestProfileAsync(settings, cancellationToken);
                        break;
                    case StopPreview:
                        preview.Stop();
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

    private async Task DisplayProfilesAsync(TtsSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]TTS providers[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[dim]Settings: {Markup.Escape(settingsStore.SettingsPath)}[/]");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Default")
            .AddColumn("ID")
            .AddColumn("Name")
            .AddColumn("Endpoint")
            .AddColumn("Model")
            .AddColumn("Voices");

        IReadOnlyList<SpeechProviderInfo> providers;
        string? defaultProviderId;
        string? configurationError = null;
        try
        {
            providers = await synthesizer.GetProvidersAsync(cancellationToken);
            defaultProviderId = providers.Count == 0
                ? null
                : await synthesizer.GetDefaultProviderIdAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            providers = await synthesizer.GetProvidersAsync(cancellationToken);
            defaultProviderId = null;
            configurationError = ex.Message;
        }

        foreach (var provider in providers)
        {
            if (provider.Kind == SpeechProviderKind.Windows)
            {
                _ = table.AddRow(
                    string.Equals(defaultProviderId, provider.Id, StringComparison.OrdinalIgnoreCase) ? "[green]Yes[/]" : string.Empty,
                    Markup.Escape(provider.Id),
                    Markup.Escape(provider.DisplayName),
                    "Local Windows API",
                    "-",
                    "Installed");
                continue;
            }

            var profile = settings.OpenAiCompatibleProfiles.First(profile =>
                string.Equals(profile.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
            _ = table.AddRow(
                string.Equals(defaultProviderId, provider.Id, StringComparison.OrdinalIgnoreCase) ? "[green]Yes[/]" : string.Empty,
                Markup.Escape(profile.Id),
                Markup.Escape(profile.DisplayName),
                Markup.Escape(profile.BaseUrl),
                Markup.Escape(profile.Model),
                profile.Voices.Count.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        if (providers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No TTS providers are currently available. Add an OpenAI-compatible profile to enable speech synthesis.[/]");
        }

        if (configurationError != null)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(configurationError)}[/]");
        }
    }

    private async Task AddProfileAsync(TtsSettings settings, CancellationToken cancellationToken)
    {
        var id = await PromptAsync("Profile ID:", "local-tts", cancellationToken);
        if (settings.OpenAiCompatibleProfiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
            || string.Equals(id, TtsSettings.WindowsProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"TTS provider ID '{id}' already exists.");
        }

        var profile = new OpenAiCompatibleTtsProfile
        {
            Id = id,
            DisplayName = "Local TTS",
            Model = "kokoro",
            Voices = [new ConfiguredSpeechVoice { Id = "af_heart", DisplayName = "Heart", Culture = "en-US" }]
        };

        await PromptForProfileAsync(profile, cancellationToken);
        settings.OpenAiCompatibleProfiles.Add(profile);
        await settingsStore.SaveAsync(settings, cancellationToken);
        AnsiConsole.MarkupLine($"[green]Saved TTS profile '{Markup.Escape(profile.DisplayName)}'.[/]");
    }

    private async Task EditProfileAsync(TtsSettings settings, CancellationToken cancellationToken)
    {
        var profile = await SelectProfileAsync(settings, "Select a profile to edit:", cancellationToken);
        if (profile == null)
        {
            return;
        }

        await PromptForProfileAsync(profile, cancellationToken);
        await settingsStore.SaveAsync(settings, cancellationToken);
        AnsiConsole.MarkupLine($"[green]Saved TTS profile '{Markup.Escape(profile.DisplayName)}'.[/]");
    }

    private async Task RemoveProfileAsync(TtsSettings settings, CancellationToken cancellationToken)
    {
        var profile = await SelectProfileAsync(settings, "Select a profile to remove:", cancellationToken);
        if (profile == null)
        {
            return;
        }

        var confirmed = await AnsiConsole.PromptAsync(
            new ConfirmationPrompt($"Remove '{Markup.Escape(profile.DisplayName)}'?"),
            cancellationToken);
        if (!confirmed)
        {
            return;
        }

        _ = settings.OpenAiCompatibleProfiles.Remove(profile);
        if (string.Equals(settings.DefaultProviderId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultProviderId = settings.OpenAiCompatibleProfiles.FirstOrDefault()?.Id ?? TtsSettings.WindowsProviderId;
        }

        await settingsStore.SaveAsync(settings, cancellationToken);
        AnsiConsole.MarkupLine($"[green]Removed TTS profile '{Markup.Escape(profile.DisplayName)}'.[/]");
    }

    private async Task SetDefaultProviderAsync(TtsSettings settings, CancellationToken cancellationToken)
    {
        var providers = await synthesizer.GetProvidersAsync(cancellationToken);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException("No TTS providers are currently available. Add an OpenAI-compatible profile first.");
        }

        var choices = providers
            .Select((provider, index) => $"{index + 1}. {Markup.Escape(provider.DisplayName)} [{Markup.Escape(provider.Id)}]")
            .ToArray();
        var selection = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Select the default TTS provider:")
                .AddChoices(choices),
            cancellationToken);
        var index = int.Parse(selection[..selection.IndexOf('.', StringComparison.Ordinal)], CultureInfo.InvariantCulture) - 1;
        settings.DefaultProviderId = providers[index].Id;
        await settingsStore.SaveAsync(settings, cancellationToken);
    }

    private async Task TestProfileAsync(TtsSettings settings, CancellationToken cancellationToken)
    {
        var profile = await SelectProfileAsync(settings, "Select a profile to test:", cancellationToken);
        if (profile == null)
        {
            return;
        }

        var voices = await synthesizer.GetVoicesAsync(profile.Id, cancellationToken);
        var choices = voices
            .Select((voice, index) => $"{index + 1}. {Markup.Escape(voice.Name)} [{Markup.Escape(voice.Id)}]")
            .ToArray();
        var selection = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Select a voice:")
                .AddChoices(choices),
            cancellationToken);
        var voiceIndex = int.Parse(selection[..selection.IndexOf('.', StringComparison.Ordinal)], CultureInfo.InvariantCulture) - 1;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(
                "Requesting test speech...",
                async _ => await preview.PlayAsync(
                    "This is a test of the configured audiobook voice.",
                    voices[voiceIndex],
                    cancellationToken));
        AnsiConsole.MarkupLine("[green]Test audio is playing.[/]");
    }

    private static async Task PromptForProfileAsync(OpenAiCompatibleTtsProfile profile, CancellationToken cancellationToken)
    {
        profile.DisplayName = await PromptAsync("Display name:", profile.DisplayName, cancellationToken);
        profile.BaseUrl = await PromptAsync("Base URL (ending in /v1/):", profile.BaseUrl, cancellationToken);
        profile.Model = await PromptAsync("Model:", profile.Model, cancellationToken);

        var voiceText = TtsVoiceListCodec.Format(profile.Voices).Replace(Environment.NewLine, ";", StringComparison.Ordinal);
        voiceText = await PromptAsync(
            "Voices (id|display name|culture|gender; ...):",
            voiceText,
            cancellationToken);
        profile.Voices = TtsVoiceListCodec.Parse(voiceText);

        profile.Speed = await PromptDoubleAsync("Speech speed:", profile.Speed, cancellationToken);
        profile.MaximumInputCharacters = await PromptIntegerAsync(
            "Maximum characters per request:",
            profile.MaximumInputCharacters,
            cancellationToken);
        profile.TimeoutSeconds = await PromptIntegerAsync(
            "Request timeout in seconds:",
            profile.TimeoutSeconds,
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

    private static async Task<OpenAiCompatibleTtsProfile?> SelectProfileAsync(
        TtsSettings settings,
        string title,
        CancellationToken cancellationToken)
    {
        if (settings.OpenAiCompatibleProfiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No OpenAI-compatible TTS profiles are configured.[/]");
            return null;
        }

        var choices = settings.OpenAiCompatibleProfiles
            .Select((profile, index) => $"{index + 1}. {Markup.Escape(profile.DisplayName)} [{Markup.Escape(profile.Id)}]")
            .Append(Back)
            .ToArray();
        var selection = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title(title)
                .AddChoices(choices),
            cancellationToken);
        if (selection == Back)
        {
            return null;
        }

        var index = int.Parse(selection[..selection.IndexOf('.', StringComparison.Ordinal)], CultureInfo.InvariantCulture) - 1;
        return settings.OpenAiCompatibleProfiles[index];
    }

    private static Task<string> PromptAsync(string prompt, string defaultValue, CancellationToken cancellationToken) =>
        AnsiConsole.PromptAsync(
            new TextPrompt<string>(prompt)
                .DefaultValue(defaultValue)
                .Validate(static value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("A value is required.")
                    : ValidationResult.Success()),
            cancellationToken);

    private static async Task<int> PromptIntegerAsync(string prompt, int defaultValue, CancellationToken cancellationToken)
    {
        var value = await PromptAsync(prompt, defaultValue.ToString(CultureInfo.InvariantCulture), cancellationToken);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{prompt} must be an integer.");
    }

    private static async Task<double> PromptDoubleAsync(string prompt, double defaultValue, CancellationToken cancellationToken)
    {
        var value = await PromptAsync(prompt, defaultValue.ToString(CultureInfo.InvariantCulture), cancellationToken);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{prompt} must be a number.");
    }
}
