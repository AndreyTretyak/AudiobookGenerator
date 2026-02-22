using Spectre.Console;

using System.Collections.Concurrent;

using YewCone.AudiobookGenerator.Console.Models;
using YewCone.AudiobookGenerator.Console.Resources;
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
        AnsiConsole.Write(new Rule($"[yellow]{Strings.HeaderGenerateAudiobook}[/]").LeftJustified());
        AnsiConsole.WriteLine();

        if (session.SelectedVoice == null)
        {
            AnsiConsole.MarkupLine($"[red]{Strings.ErrorNoVoiceSelected}[/]");
            return;
        }

        // Prompt for output directory
        var outputPath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>(Strings.PromptOutputDirectory)
                .DefaultValue(Environment.GetFolderPath(Environment.SpecialFolder.Desktop))
                .Validate(path =>
                {
                    var trimmed = path.Trim('\"');
                    return Directory.Exists(trimmed)
                        ? ValidationResult.Success()
                        : ValidationResult.Error(Strings.ErrorDirectoryDoesNotExist);
                }),
            cancellationToken);

        var outputDir = new DirectoryInfo(outputPath.Trim('\"'));
        var outputFile = new FileInfo(Path.Combine(outputDir.FullName, $"{session.FileName}.m4b"));

        // Check if file exists
        if (outputFile.Exists)
        {
            var overwrite = await AnsiConsole.PromptAsync(
                new ConfirmationPrompt(string.Format(Strings.PromptConfirmOverwrite, outputFile.Name)),
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
            .AddColumn(Strings.ColumnSetting)
            .AddColumn(Strings.ColumnValue);

        _ = table.AddRow($"[blue]{Strings.LabelTitle}[/]", Markup.Escape(session.Title));
        _ = table.AddRow($"[blue]{Strings.LabelAuthors}[/]", Markup.Escape(string.Join(", ", session.Authors)));
        _ = table.AddRow($"[blue]{Strings.LabelChapters}[/]", session.Chapters.Count.ToString());
        _ = table.AddRow($"[blue]{Strings.LabelVoice}[/]", session.SelectedVoice.Name);
        _ = table.AddRow($"[blue]{Strings.LabelOutput}[/]", Markup.Escape(outputFile.FullName));
        _ = table.AddRow($"[blue]{Strings.LabelHasCover}[/]", session.CoverImage != null ? $"[green]{Strings.LabelYes}[/]" : $"[yellow]{Strings.LabelNo}[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var confirm = await AnsiConsole.PromptAsync(
            new ConfirmationPrompt(Strings.PromptStartConversion),
            cancellationToken);

        if (!confirm)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{Strings.StatusStartingConversion}[/]");
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
            AnsiConsole.Write(new Rule($"[green]{Strings.StatusConversionComplete}[/]"));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]{Strings.StatusAudiobookSaved}[/] {Markup.Escape(outputFile.FullName)}");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[yellow]{Strings.StatusConversionCancelled}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{string.Format(Strings.ErrorConversionFailed, Markup.Escape(ex.Message))}[/]");
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
                task.Description = $"[red]{description} {Strings.StageFailed}[/]";
                task.StopTask();
            }
        }

        private static string GetStageDescription(StageType stage, string scope) => stage switch
        {
            StageType.ConvertTextToWav => $"[blue]{Strings.StageConvertTextToAudio}[/] {scope}",
            StageType.ConvertWavToAac => $"[cyan]{Strings.StageEncodingAudio}[/] {scope}",
            StageType.SavingImage => $"[yellow]{Strings.StageSavingImage}[/] {scope}",
            StageType.MergingIntoM4b => $"[magenta]{Strings.StageCreatingAudiobook}[/]",
            StageType.UpdatingM4bMetadata => $"[green]{Strings.StageAddingMetadata}[/]",
            StageType.Installing => $"[dim]{Strings.StageInstallingDependencies}[/]",
            _ => scope
        };
    }
}
