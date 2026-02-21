using Spectre.Console;

using System.Collections.Concurrent;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Menus;

/// <summary>
/// Runs the audiobook conversion process with progress display.
/// </summary>
internal sealed class ConversionRunner
{
    /// <summary>
    /// Runs the conversion workflow.
    /// </summary>
    public async Task RunAsync(BookEditSession session, BookConverter converter, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Generate Audiobook[/]").LeftJustified());
        AnsiConsole.WriteLine();

        if (session.SelectedVoice == null)
        {
            AnsiConsole.MarkupLine("[red]No voice selected. Please select a voice first.[/]");
            return;
        }

        // Prompt for output directory
        var outputPath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Output directory:")
                .DefaultValue(Environment.GetFolderPath(Environment.SpecialFolder.Desktop))
                .Validate(path =>
                {
                    var trimmed = path.Trim('\"');
                    return Directory.Exists(trimmed)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Directory does not exist.");
                }),
            cancellationToken);

        var outputDir = new DirectoryInfo(outputPath.Trim('\"'));
        var outputFile = new FileInfo(Path.Combine(outputDir.FullName, $"{session.FileName}.m4b"));

        // Check if file exists
        if (outputFile.Exists)
        {
            var overwrite = await AnsiConsole.PromptAsync(
                new ConfirmationPrompt($"File '{outputFile.Name}' already exists. Overwrite?"),
                cancellationToken);

            if (!overwrite)
            {
                return;
            }
        }

        // Show summary before conversion
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Setting")
            .AddColumn("Value");

        _ = table.AddRow("[blue]Title[/]", Markup.Escape(session.Title));
        _ = table.AddRow("[blue]Authors[/]", Markup.Escape(string.Join(", ", session.Authors)));
        _ = table.AddRow("[blue]Chapters[/]", session.Chapters.Count.ToString());
        _ = table.AddRow("[blue]Voice[/]", session.SelectedVoice.Name);
        _ = table.AddRow("[blue]Output[/]", Markup.Escape(outputFile.FullName));
        _ = table.AddRow("[blue]Has Cover[/]", session.CoverImage != null ? "[green]Yes[/]" : "[yellow]No[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var confirm = await AnsiConsole.PromptAsync(
            new ConfirmationPrompt("Start conversion?"),
            cancellationToken);

        if (!confirm)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Starting conversion... This may take a while depending on book length.[/]");
        AnsiConsole.WriteLine();

        var book = session.BuildBook();

        try
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .AutoRefresh(true)
                .HideCompleted(false)
                .Columns([
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn()
                ])
                .StartAsync(async ctx =>
                {
                    var progress = new SpectreProgressReporter(ctx);

                    await converter.ConvertAsync(
                        session.SelectedVoice,
                        book,
                        outputFile,
                        outputDir,
                        progress,
                        cancellationToken);
                });

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[green]Conversion Complete![/]"));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]✓ Audiobook saved to:[/] {Markup.Escape(outputFile.FullName)}");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Conversion cancelled.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Conversion failed: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.WriteException(ex);
        }
    }

    /// <summary>
    /// Progress reporter that integrates with Spectre.Console's Progress display.
    /// </summary>
    private sealed class SpectreProgressReporter(ProgressContext context) : IProgress<ProgressUpdate>
    {
        private readonly ConcurrentDictionary<(StageType, string), ProgressTask> _tasks = new();

        public void Report(ProgressUpdate value)
        {
            var key = (value.CurrentStage, value.Scope);
            var description = GetStageDescription(value.CurrentStage, value.Scope);

            var task = _tasks.GetOrAdd(key, _ =>
            {
                var t = context.AddTask(description);
                t.MaxValue = 100;
                return t;
            });

            if (value.State == Core.Progress.Started)
            {
                task.StartTask();
                task.Value = 10; // Show some initial progress
            }
            else if (value.State == Core.Progress.Done)
            {
                task.Value = 100;
                task.StopTask();
            }
            else if (value.State == Core.Progress.Failed)
            {
                task.Description = $"[red]{description} (Failed)[/]";
                task.StopTask();
            }
        }

        private static string GetStageDescription(StageType stage, string scope) => stage switch
        {
            StageType.ConvertTextToWav => $"[blue]Converting text to audio:[/] {scope}",
            StageType.ConvertWavToAac => $"[cyan]Encoding audio:[/] {scope}",
            StageType.SavingImage => $"[yellow]Saving image:[/] {scope}",
            StageType.MergingIntoM4b => $"[magenta]Creating audiobook file[/]",
            StageType.UpdatingM4bMetadata => $"[green]Adding metadata and cover[/]",
            StageType.Installing => $"[dim]Installing dependencies[/]",
            _ => scope
        };
    }
}
