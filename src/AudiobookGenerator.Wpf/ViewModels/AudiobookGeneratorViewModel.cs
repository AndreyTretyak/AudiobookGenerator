using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

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
    private bool isGenerating;
    private bool isPlaying;
    private BookViewModel? book;
    private string? selectedVoice;
    private ChapterViewModel? selectedChapter;

    public BookViewModel? Book { 
        get => book; 
        private set => SetAndRaise(
            ref book,
            value,
            [nameof(IsBookSelected), nameof(TextContentSectionHeader), nameof(ImagesSectionHeader)],
            [SaveImageAsCommand, AddImageCommand, GenerateCommand]); }

    public bool IsGenerating { get => isGenerating; private set => SetAndRaise(ref isGenerating, value); }

    public bool IsPlaying { get => isPlaying; private set=> SetAndRaise(ref isPlaying, value); }

    public string? SelectedVoice { get => selectedVoice; set => SetAndRaise(ref selectedVoice, value, [], [PlayOrStopCommand, GenerateCommand]); }

    public ChapterViewModel? SelectedChapter { get => selectedChapter; set => SetAndRaise(ref selectedChapter, value); }

    public ObservableCollection<string> Voices { get; } = new();

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

    public AudiobookGeneratorViewModel()
    {
        SelectBookCommand = new DelegateCommand(SelectBook);
        PlayOrStopCommand = new DelegateCommand(PlayOrStop, parameter => this.IsVoiceSelected);

        Func<object?, bool> canExecuteWhenBookSelected = parameter => this.IsBookSelected;
        SaveImageAsCommand = new DelegateCommand(SaveImageAs, canExecuteWhenBookSelected);
        AddImageCommand = new DelegateCommand(AddImage, canExecuteWhenBookSelected);

        GenerateCommand = new DelegateCommand(Generate, parameter => this.IsVoiceSelected && this.IsBookSelected);

        Voices.Add("Test Voice 1");
        Voices.Add(SelectedVoice = "Test Voice 2");
        Voices.Add("Test Voice 3");
    }

    private void SelectBook(object? parameter)
    {
        var images = Enumerable.Range(1, 5).Select(i => new ImageViewModel($"Image name {i}", []));
        Book = new BookViewModel(
            new FileInfo("StubBookPath.epub"),
            Enumerable.Range(1,5).Select(i => new PropertyViewModel($"Property {i}", $"Value {i}")),
            Enumerable.Range(1, 13).Select(i => new ChapterViewModel($"Chapter {i}", $"Content {i}")),
            images,
            images.Skip(1).First());
    }

    private void PlayOrStop(object? parameter)
    {
        if (SelectedVoice == null) 
        {
            return;
        }

        IsPlaying = !IsPlaying;
    }

    private void SaveImageAs(object? parameter)
    {
        if (Book == null)
        {
            return;
        }
    }

    private void AddImage(object? parameter)
    {
        if (Book == null)
        {
            return;
        }

        this.Book.Images.Add(new ImageViewModel("AddedImage", []));
    }

    private void Generate(object? parameter)
    {
        IsGenerating = true;
        Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith((t,p) => this.IsGenerating = false, null);
    }
}

internal class BookViewModel(FileInfo path,
    IEnumerable<PropertyViewModel> properties,
    IEnumerable<ChapterViewModel> chapters,
    IEnumerable<ImageViewModel> images,
    ImageViewModel cover)
{
    public FileInfo Path { get; } = path;

    public ObservableCollection<ChapterViewModel> Chapters { get; } = new(chapters);

    public ObservableCollection<ImageViewModel> Images { get; } = new(images);

    public ImageViewModel Cover { get; set; } = cover;

    public ObservableCollection<PropertyViewModel> Properties { get; } = new(properties);
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

internal record ImageViewModel(string Name, byte[] Content);

internal record LogMessage(string Text, Color Color);

internal class DelegateCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
