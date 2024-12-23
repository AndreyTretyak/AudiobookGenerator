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

        var converter = await host.Services.GetRequiredService<IBookConverterProvider>().GetConverterAsync(cancelationToken);

        // sample input
        await converter.ConvertAsync(
            new FileInfo(@"F:\Downloads\Long Chills and Case Dough by Brandon Sanderson.epub"),
            new DirectoryInfo(@"E:\Downloads\"),
            "en-US",
            cancelationToken);
    }
}
