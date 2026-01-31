using System.Speech.Synthesis;

using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Models;
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
    public async Task RunAsync(BookEditSession session, IAudioSynthesizer synthesizer, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Select Voice[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var voices = synthesizer.GetVoices().ToList();

        if (voices.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No TTS voices found on this system.[/]");
            AnsiConsole.MarkupLine("[dim]Install additional voices via Windows Settings > Time & Language > Speech.[/]");
            return;
        }

        // Display voice info table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("#")
            .AddColumn("Name")
            .AddColumn("Culture")
            .AddColumn("Gender")
            .AddColumn("Age");

        for (var i = 0; i < voices.Count; i++)
        {
            var voice = voices[i];
            var isSelected = session.SelectedVoice?.Name == voice.Name;
            var marker = isSelected ? "[green]★[/]" : (i + 1).ToString();

            table.AddRow(
                marker,
                isSelected ? $"[green]{Markup.Escape(voice.Name)}[/]" : Markup.Escape(voice.Name),
                voice.Culture.DisplayName,
                voice.Gender.ToString(),
                voice.Age.ToString());
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (session.SelectedVoice != null)
        {
            AnsiConsole.MarkupLine($"[dim]Currently selected: [green]{Markup.Escape(session.SelectedVoice.Name)}[/][/]");
            AnsiConsole.WriteLine();
        }

        var choices = voices
            .Select(v => $"{Markup.Escape(v.Name)} ({v.Culture.Name}, {v.Gender})")
            .Append("← Back to Main Menu")
            .ToList();

        var selection = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Select a voice for conversion:")
                .PageSize(10)
                .HighlightStyle(Style.Parse("blue bold"))
                .AddChoices(choices),
            cancellationToken);

        if (selection != "← Back to Main Menu")
        {
            var selectedVoice = voices.First(v => selection.StartsWith(Markup.Escape(v.Name)));
            session.SelectedVoice = selectedVoice;
            AnsiConsole.MarkupLine($"[green]✓ Voice set to '{Markup.Escape(selectedVoice.Name)}'.[/]");
        }
    }

    /// <summary>
    /// Prompts for voice selection if not already selected, used during direct conversion.
    /// </summary>
    public async Task<VoiceInfo?> EnsureVoiceSelectedAsync(BookEditSession session, IAudioSynthesizer synthesizer, CancellationToken cancellationToken)
    {
        if (session.SelectedVoice != null)
        {
            return session.SelectedVoice;
        }

        await RunAsync(session, synthesizer, cancellationToken);
        return session.SelectedVoice;
    }
}
