using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Console.Resources;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

/// <summary>
/// Interactive TTS voice selector.
/// </summary>
internal sealed class VoiceSelector
{
    /// <summary>
    /// Runs the voice selection menu.
    /// </summary>
    public async Task RunAsync(
        BookEditSession session,
        IAudioSynthesizer synthesizer,
        CancellationToken cancellationToken,
        string? preferredProviderId = null)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[yellow]{Strings.HeaderSelectVoice}[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var providers = await synthesizer.GetProvidersAsync(cancellationToken);
        if (providers.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]{Strings.ErrorNoVoicesFound}[/]");
            return;
        }

        var selectedProviderId = session.SelectedVoice?.ProviderId
            ?? preferredProviderId
            ?? await synthesizer.GetDefaultProviderIdAsync(cancellationToken);
        var provider = await SelectProviderAsync(providers, selectedProviderId, cancellationToken);
        if (provider == null)
        {
            return;
        }

        var voices = (await synthesizer.GetVoicesAsync(provider.Id, cancellationToken)).ToList();

        if (voices.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]{Strings.ErrorNoVoicesFound}[/]");
            AnsiConsole.MarkupLine($"[dim]{Strings.HintInstallVoices}[/]");
            return;
        }

        // Display voice info table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(Strings.ColumnNumber)
            .AddColumn(Strings.ColumnName)
            .AddColumn("ID")
            .AddColumn(Strings.ColumnCulture)
            .AddColumn(Strings.ColumnGender)
            .AddColumn(Strings.ColumnAge);

        for (var i = 0; i < voices.Count; i++)
        {
            var voice = voices[i];
            var isSelected = session.SelectedVoice?.Name == voice.Name;
            var marker = isSelected ? "[green]★[/]" : (i + 1).ToString();

            _ = table.AddRow(
                marker,
                isSelected ? $"[green]{Markup.Escape(voice.Name)}[/]" : Markup.Escape(voice.Name),
                Markup.Escape(voice.Id),
                Markup.Escape(voice.Culture ?? "-"),
                Markup.Escape(voice.Gender ?? "-"),
                Markup.Escape(voice.Age ?? "-"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (session.SelectedVoice != null)
        {
            AnsiConsole.MarkupLine($"[dim]{string.Format(Strings.StatusCurrentlySelected, $"[green]{Markup.Escape(session.SelectedVoice.Name)}[/]")}[/]");
            AnsiConsole.WriteLine();
        }

        var choices = voices
            .Select((voice, index) => $"{index + 1}. {Markup.Escape(voice.Name)} [{Markup.Escape(voice.Id)}]")
            .Append(Strings.MenuBackToMainMenu)
            .ToList();

        var selection = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title(Strings.PromptSelectVoice)
                .PageSize(10)
                .HighlightStyle(Style.Parse("blue bold"))
                .AddChoices(choices),
            cancellationToken);

        if (selection != Strings.MenuBackToMainMenu)
        {
            var voiceIndex = int.Parse(selection[..selection.IndexOf('.', StringComparison.Ordinal)]) - 1;
            var selectedVoice = voices[voiceIndex];
            session.SelectedVoice = selectedVoice;
            AnsiConsole.MarkupLine($"[green]{string.Format(Strings.StatusVoiceSet, Markup.Escape(selectedVoice.Name))}[/]");
        }
    }

    /// <summary>
    /// Prompts for voice selection if not already selected, used during direct conversion.
    /// </summary>
    public async Task<SpeechVoice?> EnsureVoiceSelectedAsync(BookEditSession session, IAudioSynthesizer synthesizer, CancellationToken cancellationToken)
    {
        if (session.SelectedVoice != null)
        {
            return session.SelectedVoice;
        }

        await RunAsync(session, synthesizer, cancellationToken);
        return session.SelectedVoice;
    }

    private static async Task<SpeechProviderInfo?> SelectProviderAsync(
        IReadOnlyList<SpeechProviderInfo> providers,
        string selectedProviderId,
        CancellationToken cancellationToken)
    {
        if (providers.Count == 1)
        {
            return providers[0];
        }

        var choices = providers
            .Select((provider, index) =>
                $"{index + 1}. {Markup.Escape(provider.DisplayName)} [{Markup.Escape(provider.Id)}]")
            .Append(Strings.MenuBackToMainMenu)
            .ToList();

        var selectedIndex = providers
            .Select((provider, index) => (provider, index))
            .FirstOrDefault(item => string.Equals(item.provider.Id, selectedProviderId, StringComparison.OrdinalIgnoreCase))
            .index;

        var selection = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title(Strings.PromptSelectProvider)
                .PageSize(10)
                .HighlightStyle(Style.Parse("blue bold"))
                .AddChoices(choices)
                .MoreChoicesText($"Current provider: {providers[selectedIndex].DisplayName}"),
            cancellationToken);

        if (selection == Strings.MenuBackToMainMenu)
        {
            return null;
        }

        var providerIndex = int.Parse(selection[..selection.IndexOf('.', StringComparison.Ordinal)]) - 1;
        return providers[providerIndex];
    }
}
