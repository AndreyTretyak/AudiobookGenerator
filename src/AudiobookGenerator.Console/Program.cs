using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console;

internal class Program
{
    static async Task Main(string[] args)
    {
        var cancelationToken = CancellationToken.None;

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services
            .AddLogging(l => l.AddConsole())
            .AddBookConverter();

        using IHost host = builder.Build();

        // sample input
        await host.Services.GetRequiredService<BookConverter>()
            .ConvertAsync(
                new FileInfo(@"F:\Downloads\Long Chills and Case Dough by Brandon Sanderson.epub"),
                new DirectoryInfo(@"E:\Downloads\"),
                "en-US",
                new ActionProgress<ProgressUpdate>(static p => System.Console.WriteLine($"{p.Scope} - {p.CurrentStage} - {p.State}")),
                cancelationToken);
    }
}
