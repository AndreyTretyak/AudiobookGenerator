using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Spectre.Console;

using System.Speech.Synthesis;

using YewCone.AudiobookGenerator.Console.Menus;
using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var cancellationToken = cts.Token;

        var builder = Host.CreateApplicationBuilder();

        _ = builder.Services
            .AddLogging(l => l.ClearProviders())
            .AddBookConverter();

        using var host = builder.Build();

        var converter = host.Services.GetRequiredService<BookConverter>();

        // Parse command line arguments
        return await ParseAndRunAsync(args, converter, cancellationToken);
    }

    private static async Task<int> ParseAndRunAsync(string[] args, BookConverter converter, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            ShowHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        return command switch
        {
            "open" => await RunInteractiveAsync(args, converter, cancellationToken),
            "convert" => await RunDirectConvertAsync(args, converter, cancellationToken),
            "voices" => RunListVoices(converter),
            "info" => await RunInfoAsync(args, converter, cancellationToken),
            "help" or "--help" or "-h" or "/?" => ShowHelp(),
            _ when File.Exists(args[0].Trim('\"')) => await RunInteractiveAsync(["open", args[0]], converter, cancellationToken),
            _ => ShowHelp($"Unknown command: {command}")
        };
    }

    private static int ShowHelp(string? error = null)
    {
        if (error != null)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(error)}[/]");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.Write(new FigletText("Audiobook Generator").Color(Color.Blue));
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[yellow]Command[/]")
            .AddColumn("[yellow]Description[/]")
            .AddColumn("[yellow]Example[/]");

        table.AddRow(
            "[blue]open[/] [dim]<file>[/]",
            "Open EPUB in interactive editor",
            "[dim]audiobook open book.epub[/]");

        table.AddRow(
            "[blue]convert[/] [dim]<file> [[options]][/]",
            "Convert EPUB directly to M4B",
            "[dim]audiobook convert book.epub --voice \"David\" --output \"C:\\Books\"[/]");

        table.AddRow(
            "[blue]voices[/]",
            "List available TTS voices",
            "[dim]audiobook voices[/]");

        table.AddRow(
            "[blue]info[/] [dim]<file>[/]",
            "Display book information",
            "[dim]audiobook info book.epub[/]");

        table.AddRow(
            "[blue]help[/]",
            "Show this help message",
            "[dim]audiobook help[/]");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Convert options:[/]");
        AnsiConsole.MarkupLine("  [blue]--voice[/] [dim]<name>[/]    TTS voice name (partial match)");
        AnsiConsole.MarkupLine("  [blue]--output[/] [dim]<dir>[/]    Output directory (default: book's directory)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Tip: You can also drag and drop an EPUB file onto the executable.[/]");

        return error != null ? 1 : 0;
    }

    private static async Task<int> RunInteractiveAsync(string[] args, BookConverter converter, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            // Prompt for file
            var filePath = await PromptForFileAsync(cancellationToken);
            if (filePath == null) return 1;
            args = ["open", filePath];
        }

        var bookPath = args[1].Trim('\"');

        if (!File.Exists(bookPath))
        {
            AnsiConsole.MarkupLine($"[red]File not found: {Markup.Escape(bookPath)}[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]Audiobook Generator[/]"));
        AnsiConsole.WriteLine();

        var book = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Loading book...", async _ =>
                await converter.Parser.ParseAsync(new FileInfo(bookPath), cancellationToken));

        var session = new BookEditSession(book);

        // Create menu components
        var chapterEditor = new ChapterEditor();
        var imageManager = new ImageManager();
        var metadataEditor = new MetadataEditor();
        var voiceSelector = new VoiceSelector();
        var audioPreviewer = new AudioPreviewer();
        var conversionRunner = new ConversionRunner();

        var menu = new InteractiveMenu(
            session,
            converter,
            chapterEditor,
            imageManager,
            metadataEditor,
            voiceSelector,
            audioPreviewer,
            conversionRunner);

        await menu.RunAsync(cancellationToken);

        return 0;
    }

    private static async Task<int> RunDirectConvertAsync(string[] args, BookConverter converter, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            return ShowHelp("convert command requires a file path");
        }

        var bookPath = args[1].Trim('\"');

        if (!File.Exists(bookPath))
        {
            AnsiConsole.MarkupLine($"[red]File not found: {Markup.Escape(bookPath)}[/]");
            return 1;
        }

        // Parse options
        string? voiceName = null;
        string? outputDir = null;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--voice" or "-v" when i + 1 < args.Length:
                    voiceName = args[++i];
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    outputDir = args[++i].Trim('\"');
                    break;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]Audiobook Generator - Direct Conversion[/]"));
        AnsiConsole.WriteLine();

        // Load book
        var book = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Loading book...", async _ =>
                await converter.Parser.ParseAsync(new FileInfo(bookPath), cancellationToken));

        var session = new BookEditSession(book);

        // Find voice
        var voices = converter.Synthesizer.GetVoices().ToList();

        if (voiceName != null)
        {
            var matchedVoice = voices.FirstOrDefault(v =>
                v.Name.Contains(voiceName, StringComparison.OrdinalIgnoreCase));

            if (matchedVoice == null)
            {
                AnsiConsole.MarkupLine($"[red]Voice not found: {Markup.Escape(voiceName)}[/]");
                AnsiConsole.MarkupLine("[dim]Available voices:[/]");
                foreach (var v in voices)
                {
                    AnsiConsole.MarkupLine($"  - {Markup.Escape(v.Name)}");
                }
                return 1;
            }

            session.SelectedVoice = matchedVoice;
        }
        else
        {
            // Prompt for voice
            var voiceSelector = new VoiceSelector();
            await voiceSelector.RunAsync(session, converter.Synthesizer, cancellationToken);

            if (session.SelectedVoice == null)
            {
                AnsiConsole.MarkupLine("[red]No voice selected.[/]");
                return 1;
            }
        }

        // Set output directory
        outputDir ??= Path.GetDirectoryName(bookPath) ?? Environment.CurrentDirectory;

        if (!Directory.Exists(outputDir))
        {
            AnsiConsole.MarkupLine($"[red]Output directory not found: {Markup.Escape(outputDir)}[/]");
            return 1;
        }

        var outputFile = new FileInfo(Path.Combine(outputDir, $"{book.FileName}.m4b"));

        AnsiConsole.MarkupLine($"[blue]Book:[/] {Markup.Escape(book.Title)}");
        AnsiConsole.MarkupLine($"[blue]Voice:[/] {Markup.Escape(session.SelectedVoice.Name)}");
        AnsiConsole.MarkupLine($"[blue]Output:[/] {Markup.Escape(outputFile.FullName)}");
        AnsiConsole.WriteLine();

        // Run conversion
        var conversionRunner = new ConversionRunner();
        await conversionRunner.RunAsync(session, converter, cancellationToken);

        return 0;
    }

    private static int RunListVoices(BookConverter converter)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]Available TTS Voices[/]"));
        AnsiConsole.WriteLine();

        var voices = converter.Synthesizer.GetVoices().ToList();

        if (voices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No TTS voices found on this system.[/]");
            AnsiConsole.MarkupLine("[dim]Install additional voices via Windows Settings > Time & Language > Speech.[/]");
            return 1;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Name")
            .AddColumn("Culture")
            .AddColumn("Gender")
            .AddColumn("Age");

        foreach (var voice in voices)
        {
            table.AddRow(
                voice.Name,
                voice.Culture.DisplayName,
                voice.Gender.ToString(),
                voice.Age.ToString());
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Total: {voices.Count} voice(s)[/]");

        return 0;
    }

    private static async Task<int> RunInfoAsync(string[] args, BookConverter converter, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            return ShowHelp("info command requires a file path");
        }

        var bookPath = args[1].Trim('\"');

        if (!File.Exists(bookPath))
        {
            AnsiConsole.MarkupLine($"[red]File not found: {Markup.Escape(bookPath)}[/]");
            return 1;
        }

        var book = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Loading book...", async _ =>
                await converter.Parser.ParseAsync(new FileInfo(bookPath), cancellationToken));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold green]{Markup.Escape(book.Title)}[/]"));
        AnsiConsole.WriteLine();

        var chapters = new Panel(
            string.Join("\n", book.Chapters.Select((c, i) => $"[dim]{i + 1}.[/] {Markup.Escape(c.Name)}")))
            .Header("[yellow]Chapters[/]")
            .Expand();

        var authors = new Panel(
            string.Join("\n", book.AuthorList.Select(a => Markup.Escape(a))))
            .Header("[yellow]Authors[/]")
            .Expand();

        var images = new Panel(
            string.Join("\n", book.Images.Select(i =>
                book.CoverImage != null && i.Content.Take(200).SequenceEqual(book.CoverImage.Take(200))
                    ? $"{Markup.Escape(i.FileName)} [yellow](Cover)[/]"
                    : Markup.Escape(i.FileName))))
            .Header("[yellow]Images[/]")
            .Expand();

        var description = new Panel(
            Markup.Escape(book.Description.Length > 500 ? book.Description[..500] + "..." : book.Description))
            .Header("[yellow]Description[/]")
            .Expand();

        var layout = new Layout("BookStructure")
            .SplitColumns(
                new Layout("Chapters", chapters),
                new Layout("OtherData")
                    .SplitRows(
                        new Layout("Authors", authors),
                        new Layout("Description", description),
                        new Layout("Images", images)));

        AnsiConsole.Write(layout);

        return 0;
    }

    private static async Task<string?> PromptForFileAsync(CancellationToken cancellationToken)
    {
        var path = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Enter path to EPUB file:")
                .Validate(value =>
                {
                    var trimmed = value?.Trim('\"');
                    if (string.IsNullOrWhiteSpace(trimmed))
                        return ValidationResult.Error("Path cannot be empty.");
                    if (!File.Exists(trimmed))
                        return ValidationResult.Error("File does not exist.");
                    if (!trimmed.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                        return ValidationResult.Error("File must be an EPUB.");
                    return ValidationResult.Success();
                }),
            cancellationToken);

        return path?.Trim('\"');
    }
}
