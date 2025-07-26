using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Spectre.Console;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Speech.Synthesis;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var cancellationToken = CancellationToken.None;

        var builder = Host.CreateApplicationBuilder();

        _ = builder.Services
            .AddLogging(l => l.AddConsole())
            .AddBookConverter();

        using var host = builder.Build();

        StartSection("Book selection");

        var stringPath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Please enter path to the epub file:\n") { Validator = PathValidator },
            cancellationToken);

        var converter = host.Services.GetRequiredService<BookConverter>();

        var book = await converter.Parser.ParseAsync(new FileInfo(stringPath.Trim('\"')), cancellationToken);

        StartSection(book.Title);

        var chapters = new Panel(string.Join("\n", book.Chapters.Select(i => i.Name))).Header("Chapters").Expand();
        var authors = new Panel(string.Join("\n", book.AuthorList)).Header("Authors").Expand();
        var images = new Panel(string.Join("\n", book.Images.Select(i => book.CoverImage == i.Content ? $"{i.FileName} (Cover)" : i.FileName))).Header("Images").Expand();
        var description = new Panel(book.Description).Header("Description").Expand();

        var layout = new Layout("BookStructure")
            .SplitColumns(
                new Layout("Chapters", chapters),
                new Layout("OtherData")
                    .SplitRows(
                        new Layout("Authors", authors),
                        new Layout("Description", description),
                        new Layout("Images", images)));

        AnsiConsole.Write(layout);

        StartSection("Voice Selection");

        var voises = converter.Synthesizer.GetVoices();

        var voice = await AnsiConsole.PromptAsync(
            new SelectionPrompt<VoiceInfo>()
                .Title("Please select voice? (To get more voices check <add link>)")
                .AddChoices(voises)
                .UseConverter(voice => $"{voice.Name} ({voice.Culture}, {voice.Gender})"),
            cancellationToken);

        StartSection("Output Selection");

        var outputPath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Please select output directory:\n") { Validator = PathValidator },
            cancellationToken);

        StartSection("Generating");

        await AnsiConsole.Progress()
            .StartAsync(ctx =>
                converter.ConvertAsync(
                    voice,
                    book,
                    new FileInfo(Path.Combine(outputPath, $"{book.FileName}.m4b")),
                    new DirectoryInfo(outputPath),
                    new ConsoleProgressReporter(ctx),
                    cancellationToken));

        return 0;
    }

    private static void StartSection(string name)
    {
        AnsiConsole.Write(new Rule($"[bold][blue]{name}[/][/]"));
        AnsiConsole.WriteLine();
    }

    private static ValidationResult PathValidator(string value) =>
        Path.Exists(value?.Trim('\"'))
            ? ValidationResult.Success()
            : ValidationResult.Error("The specified path does not exist.");

    private class ConsoleProgressReporter(ProgressContext context) : IProgress<ProgressUpdate>
    {
        ConcurrentDictionary<ProgressUpdate, ProgressTask> tasks = new(comparer: ProgressUpdateComparer.Instances);

        public void Report(ProgressUpdate value)
        {
            var task = tasks.GetOrAdd(value, _ => context.AddTask($"{value.State} - {value.Scope}"));

            if (value.State == Core.Progress.Started)
            {
                task.StartTask();
            }
            else
            {
                task.StopTask();
            }
        }
    }

    private class ProgressUpdateComparer : IEqualityComparer<ProgressUpdate>
    {
        public static ProgressUpdateComparer Instances { get; } = new ProgressUpdateComparer();

        private ProgressUpdateComparer() { }

        public bool Equals(ProgressUpdate? x, ProgressUpdate? y) =>
            x != null
            && y != null
            && x.State == y.State
            && x.Scope == y.Scope;

        public int GetHashCode([DisallowNull] ProgressUpdate obj) => (obj.State.GetHashCode() * 7) + obj.Scope.GetHashCode();
    }
}
