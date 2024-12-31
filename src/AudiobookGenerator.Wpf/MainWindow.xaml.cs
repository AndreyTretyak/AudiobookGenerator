using Microsoft.Extensions.Logging.Abstractions;
using System.Windows;
using YewCone.AudiobookGenerator.Core;
using YewCone.AudiobookGenerator.Wpf.ViewModels;

namespace YewCone.AudiobookGenerator.Wpf
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            IHtmlConverter converter = true
                ? new HtmlAgilityPackHtmlConverter(new NullLogger<HtmlAgilityPackHtmlConverter>())
                : new PlaywrightHtmlConverter(new NullLogger<PlaywrightHtmlConverter>());

            var parser = new VersOneEpubBookParser(converter, new NullLogger<VersOneEpubBookParser>());
            var synthesizer = new LocalAudioSynthesizer(new NullLogger<LocalAudioSynthesizer>());

            DataContext = new AudiobookGeneratorViewModel(parser, synthesizer);
            InitializeComponent();
        }
    }
}