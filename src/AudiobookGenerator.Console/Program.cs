using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Spectre.Console;
using Spectre.Console.Cli;

using System.ComponentModel;
using System.Speech.Synthesis;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var app = new CommandApp<ConvertCommand>();
        app.Configure(config => config.PropagateExceptions());
        return await app.RunAsync(args);
    }
}

public class ConvertCommand : AsyncCommand<ConvertCommand.Settings>
{
    private static readonly IProgress<ProgressUpdate> reporter = new ConsoleProgressReporter();

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var cancellationToken = CancellationToken.None;

        var builder = Host.CreateApplicationBuilder();

        _ = builder.Services
            .AddLogging(l => l.AddConsole())
            .AddBookConverter();

        using var host = builder.Build();

        var stringPath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Please enter path to the epub file:\n") { Validator = PathValidator },
            cancellationToken);

        var converter = host.Services.GetRequiredService<BookConverter>();

        var book = await converter.Parser.ParseAsync(new FileInfo(stringPath), cancellationToken);

        var voises = converter.Synthesizer.GetVoices();

        var voice = await AnsiConsole.PromptAsync(
            new SelectionPrompt<VoiceInfo>()
                .Title("Please select voise? (To get more voices check <add link>)")
                .AddChoices(voises)
                .UseConverter(voice => $"{voice.Name} ({voice.Culture}, {voice.Gender})"),
            cancellationToken);

        var outputPath = await AnsiConsole.PromptAsync(
            new TextPrompt<string>("Please select output directory:\n") { Validator = PathValidator },
            cancellationToken);

        await converter.ConvertAsync(
            voice,
            book,
            new FileInfo(Path.Combine(outputPath, "A.m4b")),
            new DirectoryInfo(outputPath),
            reporter,
            cancellationToken);

        return 0;
    }


    private static ValidationResult PathValidator(string value) =>
        Path.Exists(value?.Trim('\"'))
            ? ValidationResult.Success()
            : ValidationResult.Error("The specified path does not exist.");

    public class ConsoleProgressReporter : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value)
        {
            throw new NotImplementedException();
        }
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<INPUT>")]
        public string? InputFile { get; init; }

        [CommandArgument(1, "<OUTPUT>")]
        public string? OutputDirectory { get; init; }

        [CommandOption("-l|--language")]
        [DefaultValue("en-US")]
        public string Language { get; init; } = "en-US";
    }
}
