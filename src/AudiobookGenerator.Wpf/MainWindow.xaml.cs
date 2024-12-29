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
            var parser = new VersOneEpubBookParser(new PlaywrightHtmlConverter());
            var synthesizer = new LocalAudioSynthesizer(new NullLogger<LocalAudioSynthesizer>());
            DataContext = new AudiobookGeneratorViewModel(parser, synthesizer);
            InitializeComponent();
        }
    }
}