using Microsoft.Extensions.Logging.Abstractions;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private const string supportedAudiobookFormatFilter = "M4B audio book format (.m4b)|*.m4b";
    private const string supportedImageFormatsFilter = "PNG|*.png|JPeg Image|*.jpg|GIF Image|*.gif|Scalable Vector Graphics|*.svg";
    private const int coverComparePresession = 10000;
    private readonly IAudioSynthesizer audioSynthesizer;
    private readonly IEpubBookParser bookParser;

    private bool isGenerating;
    private bool isPlaying;
    private BookViewModel? book;
    private Book? latestBookState;
    private VoiceInfo? selectedVoice;
    private int progressPercentage;
    private string progressMessage = "";

    public BookViewModel? Book
    {
        get => book;
        private set => SetAndRaise(
            ref book,
            value,
            [nameof(IsBookSelected), nameof(ShowBookSelection), nameof(TextContentSectionHeader), nameof(ImagesSectionHeader)],
            [SaveImageAsCommand, AddImageCommand, PlayOrStopCommand, GenerateCommand]);
    }

    public bool IsGenerating { get => isGenerating; private set => SetAndRaise(ref isGenerating, value); }

    public int ProgressPercentage { get => progressPercentage; private set => SetAndRaise(ref progressPercentage, value); }

    public string ProgressMessage { get => progressMessage; private set => SetAndRaise(ref progressMessage, value); }

    public bool IsPlaying { get => isPlaying; private set => SetAndRaise(ref isPlaying, value, [nameof(PlayStopIcon), nameof(PlayStopToolTip)], []); }

    public string PlayStopIcon { get => isPlaying ? "\xE769" /* pause icon */ : "\xE768"; /* play icon */ }

    public string PlayStopToolTip { get => isPlaying ? Resources.StopToolTip : Resources.PlayTooltip; }

    public VoiceInfo? SelectedVoice { get => selectedVoice; set => SetAndRaise(ref selectedVoice, value, [nameof(IsVoiceSelected)], [PlayOrStopCommand, GenerateCommand]); }

    public ObservableCollection<VoiceInfo> Voices { get; }

    public ObservableCollection<LogMessage> Logs { get; } = new();

    public string TextContentSectionHeader => Resources.TextContentSectionHeader + (Book == null ? "" : string.Format(Resources.ChaptersLable, Book.Chapters.Count));

    public string ImagesSectionHeader => Resources.ImagesSectionHeader + (Book == null ? "" : string.Format(Resources.ImagesLable, Book.Images.Count));

    public bool IsBookSelected => Book != null;

    public bool ShowBookSelection => !IsBookSelected;

    public bool IsVoiceSelected => SelectedVoice != null;

    public DelegateCommand SelectBookCommand { get; }

    public DelegateCommand PlayOrStopCommand { get; }

    public DelegateCommand SaveImageAsCommand { get; }

    public DelegateCommand AddImageCommand { get; }

    public DelegateCommand GenerateCommand { get; }

    public DelegateCommand ShowHowToAddVoiceCommand { get; }

    public AudiobookGeneratorViewModel(IEpubBookParser parser, IAudioSynthesizer synthesizer)
    {
        bookParser = parser;
        audioSynthesizer = synthesizer;

        ShowHowToAddVoiceCommand = new DelegateCommand(ShowHowToAddVoiceAsync);
        SelectBookCommand = new DelegateCommand(SelectBookAsync);
        PlayOrStopCommand = new DelegateCommand(PlayOrStopAsync, parameter => this.IsVoiceSelected && this.Book != null && this.Book.SelectedChapter != null);

        bool canExecuteWhenBookSelected(object? parameter) => this.IsBookSelected;
        SaveImageAsCommand = new DelegateCommand(SaveImageAsAsync, canExecuteWhenBookSelected);
        AddImageCommand = new DelegateCommand(AddImageAsync, canExecuteWhenBookSelected);

        GenerateCommand = new DelegateCommand(GenerateAsync, parameter => this.IsVoiceSelected && this.IsBookSelected);

        Voices = [.. audioSynthesizer.GetVoices()];
    }

    private Task ShowHowToAddVoiceAsync(object? parameter)
    {
        _ = Process.Start(new ProcessStartInfo(Resources.AddVoiceLink) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task SelectBookAsync(object? parameter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = supportedBookFormatFilter
        };

        bool? result = dialog.ShowDialog();

        if (result != true)
        {
            return;
        }

        var file = new FileInfo(dialog.FileName);

        Mouse.OverrideCursor = Cursors.Wait;
        var book = await bookParser.ParseAsync(file, CancellationToken.None);
        Mouse.OverrideCursor = null;

        Book = new BookViewModel(
            file,
            [
                new PropertyViewModel(BookPropertyType.Title, book.Title),
                new PropertyViewModel(BookPropertyType.Description, book.Description),
                new PropertyViewModel(BookPropertyType.Authors, string.Join(BookViewModel.authorsSeparator, book.AuthorList)),
            ],
            book.Chapters,
            book.Images,
            book.CoverImage != null
                ? book.Images.FirstOrDefault(i => Enumerable.SequenceEqual(i.Content.Take(coverComparePresession), book.CoverImage.Take(coverComparePresession)))
                : null
                    ?? book.Images.FirstOrDefault());
    }

    private Task PlayOrStopAsync(object? parameter)
    {
        if (this.SelectedVoice == null || this.Book == null || this.Book.SelectedChapter == null)
        {
            return Task.CompletedTask;
        }

        IsPlaying = !IsPlaying;

        if (IsPlaying)
        {
            audioSynthesizer.Speak(Book.SelectedChapter.Content, SelectedVoice);
        }
        else
        {
            audioSynthesizer.StopSpeaking();
        }

        return Task.CompletedTask;
    }

    private async Task SaveImageAsAsync(object? parameter)
    {
        if (Book == null || Book.Cover == null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Book.Cover.FileName,
            Filter = supportedImageFormatsFilter,
            DefaultExt = Path.GetExtension(Book.Cover.FileName)
        };

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

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = supportedImageFormatsFilter
        };

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
        if (SelectedVoice == null || Book == null)
        {
            return;
        }

        var audiobookExtension = ".m4b";

        var converter = new BookConverter(bookParser, audioSynthesizer, new FfmpegAudioConverter(), new NullLogger<BookConverter>());

        var updatedBook = Book.CreateUpdatedModel();
        latestBookState = updatedBook;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.ChangeExtension(updatedBook.FileName, audiobookExtension),
            DefaultExt = audiobookExtension,
            Filter = supportedAudiobookFormatFilter
        };

        var result = dialog.ShowDialog();

        if (result != true)
        {
            return;
        }

        IsGenerating = true;

        var output = new FileInfo(dialog.FileName);

        if (output.Extension != audiobookExtension)
        {
            throw new InvalidOperationException($"We can only produce .{audiobookExtension} files, not {output.Extension}.");
        }

        var tmpFiles = output.Directory ?? new DirectoryInfo(Path.GetTempPath());

        await converter.ConvertAsync(
            SelectedVoice,
            updatedBook,
            output,
            tmpFiles,
            new ActionProgress<ProgressUpdate>(ProgressUpdate),
            CancellationToken.None);

        IsGenerating = false;
    }

    private void ProgressUpdate(ProgressUpdate progress)
    {
        if (Book == null || latestBookState == null)
        {
            return;
        }

        ProgressPercentage = progress.GetPercentage(latestBookState);

        var stageMessage = progress.CurrentStage switch
        {
            StageType.ConvertTextToWav => Resources.ConvertTextToWavMessage,
            StageType.ConvertWavToAac => Resources.ConvertWavToAacMessage,
            StageType.SavingImage => Resources.SavingImageMessage,
            StageType.MergingIntoM4b => Resources.MergingIntoM4bMessage,
            StageType.UpdatingM4bMetadata => Resources.UpdatingM4bMetadataMessage,
            StageType.Installing => Resources.InstallingMessage,
            _ => throw new ArgumentOutOfRangeException($"Unexpected enum value {nameof(progress.CurrentStage)}")
        };

        var scopeMessage = string.IsNullOrEmpty(progress.Scope) ? " " : $" \"{progress.Scope}\" ";

        var stateMessage = progress.State switch
        {
            Progress.Started => Resources.StartedMessage,
            Progress.Failed => Resources.FailedMessage,
            Progress.Done => Resources.DoneMessage,
            _ => throw new ArgumentOutOfRangeException($"Unexpected enum value {nameof(progress.State)}")
        };

        ProgressMessage = $"{stageMessage}{scopeMessage}{stateMessage}";
    }
}

internal class BookViewModel : BaseViewModel
{
    internal const char authorsSeparator = ',';
    private BookChapter? selectedChapter;
    private BookImage? cover;

    public BookViewModel(FileInfo path,
        IEnumerable<PropertyViewModel> properties,
        IEnumerable<BookChapter> chapters,
        IEnumerable<BookImage> images,
        BookImage? bookCover)
    {
        Path = path;
        Chapters = [.. chapters];
        Properties = [.. properties];
        Images = [.. images];
        cover = bookCover;
        selectedChapter = Chapters.FirstOrDefault();
    }

    public FileInfo Path { get; }

    public ObservableCollection<BookChapter> Chapters { get; }

    public ObservableCollection<PropertyViewModel> Properties { get; }

    public ObservableCollection<BookImage> Images { get; }

    public BookChapter? SelectedChapter { get => selectedChapter; set => SetAndRaise(ref selectedChapter, value); }

    public BookImage? Cover { get => cover; set => SetAndRaise(ref cover, value); }

    public Book CreateUpdatedModel()
    {
        var props = Properties.ToDictionary(static p => p.Type, static p => p.Value);

        // TODO untangle mess of paramteres and collection types
        return new Book(
            Path.Name,
            props[BookPropertyType.Title],
            props[BookPropertyType.Description],
            [.. props[BookPropertyType.Authors].Split(authorsSeparator, StringSplitOptions.RemoveEmptyEntries)],
            cover?.Content,
            [.. Chapters],
            [.. Images]);
    }
}

internal class ChapterViewModel(string title, string content)
{
    public string Title { get; set; } = title;

    public string Content { get; set; } = content;
}

internal record PropertyViewModel(BookPropertyType Type, string Value)
{
    public string Name => Type switch
    {
        BookPropertyType.Title => Resources.TitleProperty,
        BookPropertyType.Description => Resources.DescriptionProperty,
        BookPropertyType.Authors => Resources.AuthorsProperties,
        _ => Type.ToString()
    };
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
            _ = MessageBox.Show(ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.None);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal enum BookPropertyType
{
    Title,
    Description,
    Authors
}
