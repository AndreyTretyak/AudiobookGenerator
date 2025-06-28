using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;

using YewCone.AudiobookGenerator.Core;
using YewCone.AudiobookGenerator.Wpf.ViewModels;

namespace YewCone.AudiobookGenerator.Wpf;

public partial class MainWindow : Window
{
    private readonly AudiobookGeneratorViewModel viewModel;

    public MainWindow()
    {
        DataContext = viewModel = new ServiceCollection()
            .AddLogging(static p => p.AddEventSourceLogger())
            .AddBookConverter()
            .AddTransient<AudiobookGeneratorViewModel>()
            .BuildServiceProvider(new ServiceProviderOptions() { ValidateOnBuild = true, ValidateScopes = true })
            .GetRequiredService<AudiobookGeneratorViewModel>();

        InitializeComponent();
    }

    private void HandleDragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsDragSupported(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void HandleDrop(object sender, DragEventArgs e)
    {
        if (IsDragSupported(e, out var file))
        {
            await viewModel.OpenBookAsync(file);
        }
    }

    private static bool IsDragSupported(DragEventArgs e, [NotNullWhen(true)] out FileInfo? file)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files
            && files is [var filePath, ..]
            && AudiobookGeneratorViewModel.IsEbookExtensionSupported(filePath))
        {
            file = new FileInfo(filePath);
            return true;
        }
        file = null;
        return false;
    }
}