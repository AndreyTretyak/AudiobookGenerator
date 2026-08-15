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
    private readonly ITtsSettingsStore ttsSettings;
    private readonly IAudioSynthesizer synthesizer;
    private readonly IAudioPreviewService preview;

    public MainWindow(
        BookConverter converter,
        ILoggerFactory loggerFactory,
        ITtsSettingsStore ttsSettingsStore,
        IAudioSynthesizer audioSynthesizer,
        IAudioPreviewService previewService)
    {
        DataContext = viewModel = new AudiobookGeneratorViewModel(
            converter,
            loggerFactory.CreateLogger<AudiobookGeneratorViewModel>());
        ttsSettings = ttsSettingsStore;
        synthesizer = audioSynthesizer;
        preview = previewService;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.InitializeAsync();
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

    private async void HandleTtsSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TtsSettingsWindow(ttsSettings, synthesizer, preview)
        {
            Owner = this
        };
        _ = dialog.ShowDialog();
        await viewModel.InitializeAsync();
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