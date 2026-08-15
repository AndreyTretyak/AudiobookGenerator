using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using System.Windows;

using YewCone.AudiobookGenerator.Core;
using YewCone.AudiobookGenerator.Wpf.ViewModels;

namespace YewCone.AudiobookGenerator.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceProvider? serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        serviceProvider = new ServiceCollection()
            .AddLogging(static logging => logging.AddEventSourceLogger())
            .AddBookConverter()
            .AddTransient<MainWindow>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        serviceProvider.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
