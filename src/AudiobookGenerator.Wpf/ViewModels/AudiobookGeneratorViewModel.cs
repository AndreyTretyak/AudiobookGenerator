using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Input;
using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Wpf.ViewModels;

internal class BaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Raise([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void SetAndRaise<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        field = value;
        Raise(propertyName);
    }

    public void SetAndRaise<T>(ref T field, T value, IEnumerable<string> additionalProperties, IEnumerable<DelegateCommand> commands, [CallerMemberName] string? propertyName = null)
    {
        field = value;
        Raise(propertyName);
        foreach (var property in additionalProperties)
        {
            Raise(property);
        }
        foreach (var command in commands) 
        {
            command.RaiseCanExecuteChanged();
        }
    }
}

internal class AudiobookGeneratorViewModel : BaseViewModel
{
    private const string supportedBookFormatFilter = "Electronic Publication Book (.epub)|*.epub";
    private const string supportedImageFormatsFilter = "PNG|*.png|JPeg Image|*.jpg|GIF Image|*.gif|Scalable Vector Graphics|*.svg";
    private readonly IAudioSynthesizer audioSynthesizer;

    private bool isGenerating;
    private bool isPlaying;
    private BookViewModel? book;
    private string? selectedVoice;

    public BookViewModel? Book { 
        get => book; 
        private set => SetAndRaise(
            ref book,
            value,
            [nameof(IsBookSelected), nameof(TextContentSectionHeader), nameof(ImagesSectionHeader), nameof(BookPath)],
            [SaveImageAsCommand, AddImageCommand, GenerateCommand]); }

    public bool IsGenerating { get => isGenerating; private set => SetAndRaise(ref isGenerating, value); }

    public bool IsPlaying { get => isPlaying; private set => SetAndRaise(ref isPlaying, value, [nameof(PlayStopIcon), nameof(PlayStopToolTip)], []); }

    public string PlayStopIcon { get => isPlaying ? "\xE769" /* pause icon */ : "\xE768"; /* play icon */ }

    public string BookPath { get => Book == null ? Resources.BookPathPlaceholder : Book.Path.FullName; }

    public string PlayStopToolTip { get => isPlaying ? Resources.StopToolTip : Resources.PlayTooltip; }

    public string? SelectedVoice { get => selectedVoice; set => SetAndRaise(ref selectedVoice, value, [], [PlayOrStopCommand, GenerateCommand]); }

    public ObservableCollection<VoiceInfo> Voices { get; }

    public ObservableCollection<LogMessage> Logs { get; } = new();

    public string TextContentSectionHeader => Resources.TextContentSectionHeader + (Book == null ? "" : string.Format(Resources.ChaptersLable, Book.Chapters.Count));

    public string ImagesSectionHeader => Resources.ImagesSectionHeader + (Book == null ? "" : string.Format(Resources.ImagesLable, Book.Images.Count));

    public bool IsBookSelected => Book != null;

    public bool IsVoiceSelected => SelectedVoice != null;

    public Uri AddVoiceLink { get; } = new Uri(Resources.AddVoiceLink);

    public DelegateCommand SelectBookCommand { get; }

    public DelegateCommand PlayOrStopCommand { get; }

    public DelegateCommand SaveImageAsCommand { get; }

    public DelegateCommand AddImageCommand { get; }

    public DelegateCommand GenerateCommand { get; }

    public AudiobookGeneratorViewModel(IAudioSynthesizer synthesizer)
    {
        audioSynthesizer = synthesizer;
        SelectBookCommand = new DelegateCommand(SelectBookAsync);
        PlayOrStopCommand = new DelegateCommand(PlayOrStopAsync, parameter => this.IsVoiceSelected);

        Func<object?, bool> canExecuteWhenBookSelected = parameter => this.IsBookSelected;
        SaveImageAsCommand = new DelegateCommand(SaveImageAsAsync, canExecuteWhenBookSelected);
        AddImageCommand = new DelegateCommand(AddImageAsync, canExecuteWhenBookSelected);

        GenerateCommand = new DelegateCommand(GenerateAsync, parameter => this.IsVoiceSelected && this.IsBookSelected);

        Voices = [..synthesizer.GetVoices()];
    }

    private async Task SelectBookAsync(object? parameter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        dialog.Filter = supportedBookFormatFilter;

        bool? result = dialog.ShowDialog();

        if (result != true)
        {
            return;
        }

        var file = new FileInfo(dialog.FileName);

        var converter = new PlaywrightHtmlConverter();

        await converter.InitializeAsync(CancellationToken.None);

        var parser = new VersOneEpubBookParser(converter);

        var book = await parser.ParseAsync(file, CancellationToken.None);

        Book = new BookViewModel(
            file,
            [],
            book.Chapters,
            book.Images,
            book.Images.FirstOrDefault(i => i.Content.Equals(book.CoverImage)));
    }

    private async Task PlayOrStopAsync(object? parameter)
    {
        if (SelectedVoice == null) 
        {
            return;
        }

        IsPlaying = !IsPlaying;

        await Task.CompletedTask;
    }

    private async Task SaveImageAsAsync(object? parameter)
    {
        if (Book == null || Book.Cover == null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog();
        
        dialog.FileName = Book.Cover.FileName;
        dialog.Filter = supportedImageFormatsFilter;
        dialog.DefaultExt = Path.GetExtension(Book.Cover.FileName);

        var result = dialog.ShowDialog();

        if (result != true)
        {
            return;
        }

        await File.WriteAllBytesAsync(dialog.FileName, Book.Cover.Content);
    }

    private async Task AddImageAsync(object? parameter)
    {
        if (Book == null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog();
        dialog.Filter = supportedImageFormatsFilter;

        bool? result = dialog.ShowDialog();

        if (result != true)
        {
            return;
        }

        var filename = dialog.FileName;
        var content = await File.ReadAllBytesAsync(filename);
        var image = new BookImage(Path.GetFileName(filename), content);

        this.Book.Images.Add(image);
        this.Book.Cover = image;
    }

    private async Task GenerateAsync(object? parameter)
    {
        IsGenerating = true;
        // TODO
        await Task.Delay(TimeSpan.FromSeconds(30));
        IsGenerating = false;
    }
}

internal class BookViewModel : BaseViewModel
{
    private BookChapter? selectedChapter;
    private BookImage? cover;

    public BookViewModel(FileInfo path,
        IEnumerable<PropertyViewModel> properties,
        IEnumerable<BookChapter> chapters,
        IEnumerable<BookImage> images,
        BookImage? cover)
    {
        Path = path;
        Chapters = [.. chapters];
        Properties = [.. properties];
        Images = [.. images];
        selectedChapter = Chapters.FirstOrDefault();
    }

    public FileInfo Path { get; }

    public ObservableCollection<BookChapter> Chapters { get; }

    public ObservableCollection<PropertyViewModel> Properties { get; }

    public ObservableCollection<BookImage> Images { get; }

    public BookChapter? SelectedChapter { get => selectedChapter; set => SetAndRaise(ref selectedChapter, value); }

    public BookImage? Cover { get => cover; set => SetAndRaise(ref cover, value); }
}

internal class ChapterViewModel(string title, string content)
{
    public string Title { get; set; } = title;

    public string Content { get; set; } = content;
}

internal class PropertyViewModel(string name, string value)
{
    public string Name { get; } = name;

    public string Value { get; set; } = value;
}

internal record LogMessage(string Text, Color Color);

internal class DelegateCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            await execute(parameter);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.None);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
