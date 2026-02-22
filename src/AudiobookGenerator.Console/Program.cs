using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Spectre.Console;

using YewCone.AudiobookGenerator.Console.Menus;
using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Console.Resources;
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
            _ = ShowHelp();
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
            _ => ShowHelp(string.Format(Strings.ErrorUnknownCommand, command))
        };
    }

    private static int ShowHelp(string? error = null)
    {
        if (error != null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.Write(new FigletText(Strings.HeaderAudiobookGenerator).Color(Color.Blue));
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn($"[yellow]{Strings.ColumnCommand}[/]")
            .AddColumn($"[yellow]{Strings.ColumnDescription}[/]")
            .AddColumn($"[yellow]{Strings.ColumnExample}[/]");

        _ = table.AddRow(
            "[blue]open[/] [dim]<file>[/]",
            Strings.HelpOpenDescription,
            "[dim]audiobook open book.epub[/]");

        _ = table.AddRow(
            "[blue]convert[/] [dim]<file> [[options]][/]",
            Strings.HelpConvertDescription,
            "[dim]audiobook convert book.epub --voice \"David\" --output \"C:\\Books\"[/]");

        _ = table.AddRow(
            "[blue]voices[/]",
            Strings.HelpVoicesDescription,
            "[dim]audiobook voices[/]");

        _ = table.AddRow(
            "[blue]info[/] [dim]<file>[/]",
            Strings.HelpInfoDescription,
            "[dim]audiobook info book.epub[/]");

        _ = table.AddRow(
            "[blue]help[/]",
            Strings.HelpHelpDescription,
            "[dim]audiobook help[/]");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{Strings.HelpConvertOptions}[/]");
        AnsiConsole.MarkupLine($"  [blue]--voice[/] [dim]<name>[/]    {Strings.HelpVoiceOption}");
        AnsiConsole.MarkupLine($"  [blue]--output[/] [dim]<dir>[/]    {Strings.HelpOutputOption}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{Strings.HelpTipDragDrop}[/]");

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
            AnsiConsole.MarkupLine($"[red]{string.Format(Strings.ErrorFileNotFound, Markup.Escape(bookPath))}[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold green]{Strings.HeaderAudiobookGenerator}[/]"));
        AnsiConsole.WriteLine();

        var book = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(Strings.StatusLoadingBook, async _ =>
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
            return ShowHelp(Strings.ErrorConvertRequiresFile);
        }

        var bookPath = args[1].Trim('\"');

        if (!File.Exists(bookPath))
        {
            AnsiConsole.MarkupLine($"[red]{string.Format(Strings.ErrorFileNotFound, Markup.Escape(bookPath))}[/]");
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
        AnsiConsole.Write(new Rule($"[bold green]{Strings.HeaderDirectConversion}[/]"));
        AnsiConsole.WriteLine();

        // Load book
        var book = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(Strings.StatusLoadingBook, async _ =>
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
                AnsiConsole.MarkupLine($"[red]{string.Format(Strings.ErrorVoiceNotFound, Markup.Escape(voiceName))}[/]");
                AnsiConsole.MarkupLine($"[dim]{Strings.StatusAvailableVoices}[/]");
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
                AnsiConsole.MarkupLine($"[red]{Strings.ErrorNoVoiceSelectedShort}[/]");
                return 1;
            }
        }

        // Set output directory
        outputDir ??= Path.GetDirectoryName(bookPath) ?? Environment.CurrentDirectory;

        if (!Directory.Exists(outputDir))
        {
            AnsiConsole.MarkupLine($"[red]{string.Format(Strings.ErrorOutputDirNotFound, Markup.Escape(outputDir))}[/]");
            return 1;
        }

        var outputFile = new FileInfo(Path.Combine(outputDir, $"{book.FileName}.m4b"));

        AnsiConsole.MarkupLine($"[blue]{Strings.LabelBook}:[/] {Markup.Escape(book.Title)}");
        AnsiConsole.MarkupLine($"[blue]{Strings.LabelVoice}:[/] {Markup.Escape(session.SelectedVoice.Name)}");
        AnsiConsole.MarkupLine($"[blue]{Strings.LabelOutput}:[/] {Markup.Escape(outputFile.FullName)}");
        AnsiConsole.WriteLine();

        // Run conversion
        var conversionRunner = new ConversionRunner();
        await conversionRunner.RunAsync(session, converter, cancellationToken);

        return 0;
    }

    private static int RunListVoices(BookConverter converter)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold green]{Strings.HeaderAvailableTTSVoices}[/]"));
        AnsiConsole.WriteLine();

        var voices = converter.Synthesizer.GetVoices().ToList();

        if (voices.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Strings.ErrorNoVoicesFound}[/]");
            AnsiConsole.MarkupLine($"[dim]{Strings.HintInstallVoices}[/]");
            return 1;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(Strings.ColumnName)
            .AddColumn(Strings.ColumnCulture)
            .AddColumn(Strings.ColumnGender)
            .AddColumn(Strings.ColumnAge);

        foreach (var voice in voices)
        {
            _ = table.AddRow(
                voice.Name,
                voice.Culture.DisplayName,
                voice.Gender.ToString(),
                voice.Age.ToString());
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{string.Format(Strings.StatusTotalVoices, voices.Count)}[/]");

        return 0;
    }

    private static async Task<int> RunInfoAsync(string[] args, BookConverter converter, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            return ShowHelp(Strings.ErrorInfoRequiresFile);
        }

        var bookPath = args[1].Trim('\"');

        if (!File.Exists(bookPath))
        {
            AnsiConsole.MarkupLine($"[red]{string.Format(Strings.ErrorFileNotFound, Markup.Escape(bookPath))}[/]");
            return 1;
        }

        var book = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(Strings.StatusLoadingBook, async _ =>
                await converter.Parser.ParseAsync(new FileInfo(bookPath), cancellationToken));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold green]{Markup.Escape(book.Title)}[/]"));
        AnsiConsole.WriteLine();

        var chapters = new Panel(
            string.Join("\n", book.Chapters.Select((c, i) => $"[dim]{i + 1}.[/] {Markup.Escape(c.Name)}")))
            .Header($"[yellow]{Strings.LabelChapters}[/]")
            .Expand();

        var authors = new Panel(
            string.Join("\n", book.AuthorList.Select(a => Markup.Escape(a))))
            .Header($"[yellow]{Strings.LabelAuthors}[/]")
            .Expand();

        var images = new Panel(
            string.Join("\n", book.Images.Select(i =>
                book.CoverImage != null && i.Content.Take(200).SequenceEqual(book.CoverImage.Take(200))
                    ? $"{Markup.Escape(i.FileName)} [yellow]{Strings.LabelCover}[/]"
                    : Markup.Escape(i.FileName))))
            .Header($"[yellow]{Strings.LabelImages}[/]")
            .Expand();

        var description = new Panel(
            Markup.Escape(book.Description.Length > 500 ? book.Description[..500] + "..." : book.Description))
            .Header($"[yellow]{Strings.LabelDescription}[/]")
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
            new TextPrompt<string>(Strings.PromptEnterEpubPath)
                .Validate(value =>
                {
                    var trimmed = value?.Trim('\"');
                    return trimmed switch
                    {
                        _ when string.IsNullOrWhiteSpace(trimmed) => ValidationResult.Error(Strings.ErrorPathEmpty),
                        _ when !File.Exists(trimmed) => ValidationResult.Error(Strings.ErrorFileDoesNotExist),
                        _ when !trimmed.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) => ValidationResult.Error(Strings.ErrorFileMustBeEpub),
                        _ => ValidationResult.Success()
                    };
                }),
            cancellationToken);

        return path?.Trim('\"');
    }
}
