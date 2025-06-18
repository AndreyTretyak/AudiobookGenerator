using Microsoft.Extensions.Logging.Abstractions;

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;

using YewCone.AudiobookGenerator.Core;
using YewCone.AudiobookGenerator.Wpf.ViewModels;

namespace YewCone.AudiobookGenerator.Wpf
{
    public partial class MainWindow : Window
    {
        private AudiobookGeneratorViewModel? viewModel;

        public MainWindow()
        {
            IHtmlConverter converter = true
                ? new HtmlAgilityPackHtmlConverter(new NullLogger<HtmlAgilityPackHtmlConverter>())
                : new PlaywrightHtmlConverter(new NullLogger<PlaywrightHtmlConverter>());

            var parser = new VersOneEpubBookParser(converter, new NullLogger<VersOneEpubBookParser>());
            var synthesizer = new LocalAudioSynthesizer(new NullLogger<LocalAudioSynthesizer>());

            DataContext = viewModel = new AudiobookGeneratorViewModel(parser, synthesizer);
            InitializeComponent();
        }

        private void HandleDragOver(object sender, DragEventArgs e)
        {
            e.Effects = viewModel != null && IsDragSupported(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void HandleDrop(object sender, DragEventArgs e)
        {
            if (viewModel != null && IsDragSupported(e, out var file))
            {
                await viewModel.OpenBookAsync(file);
            }
        }

        private void PassThroughDrag(object sender, DragEventArgs e) => e.Handled = true;

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
}