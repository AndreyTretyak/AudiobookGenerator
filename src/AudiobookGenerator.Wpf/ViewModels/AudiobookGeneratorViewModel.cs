using Microsoft.Extensions.Logging;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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
    private const string ebookSupportedExtension = ".epub";
    private const string projectSupportedExtension = ".audiobook.json";
    private const string audiobookSupportedExtension = ".m4b";
    private const string supportedBookFormatFilter =
        $"Audiobook Project|*{ebookSupportedExtension};*{projectSupportedExtension}|" +
        $"Electronic Publication Book (*{ebookSupportedExtension})|*{ebookSupportedExtension}|" +
        $"Image Description Project (*{projectSupportedExtension})|*{projectSupportedExtension}";
    private const string supportedAudiobookFormatFilter = $"M4B audio book format ({audiobookSupportedExtension})|*{audiobookSupportedExtension}";
    private const string supportedImageFormatsFilter =
        "Supported Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg|" +
        "PNG|*.png|JPEG Image|*.jpg;*.jpeg|GIF Image|*.gif|WebP Image|*.webp|Scalable Vector Graphics|*.svg";
    private const int coverComparePrecision = 10000;
    private readonly ILogger logger;
    private readonly BookConverter converter;

    private bool isGenerating;
    private bool isPlaying;
    private BookViewModel? book;
    private Book? latestBookState;
    private SpeechProviderInfo? selectedProvider;
    private SpeechVoice? selectedVoice;
    private int progressPercentage;
    private string progressMessage = "";
    private int voiceLoadVersion;
    private CancellationTokenSource? generationCancellation;
    private bool isGeneratingImageDescriptions;
    private CancellationTokenSource? imageDescriptionCancellation;
    private VisionProfileInfo? selectedVisionProfile;
    private BookImage? selectedImage;
    private string approvedImageDescriptionDraft = string.Empty;
    private string candidateImageDescriptionDraft = string.Empty;
    private string imageDescriptionProgress = string.Empty;

    public BookViewModel? Book
    {
        get => book;
        private set => SetAndRaise(
            ref book,
            value,
            [nameof(IsBookSelected), nameof(ShowBookSelection), nameof(TextContentSectionHeader), nameof(ImagesSectionHeader)],
            [
                SaveImageAsCommand,
                AddImageCommand,
                PlayOrStopCommand,
                GenerateCommand,
                GenerateMissingImageDescriptionsCommand,
                GenerateSelectedImageDescriptionCommand,
                SaveApprovedImageDescriptionCommand,
                ApproveImageDescriptionCommand,
                RejectImageDescriptionCommand,
                ToggleDecorativeImageCommand,
                SaveDescriptionProjectCommand
            ]);
    }

    public bool IsGenerating =>
        isGenerating;

    public bool CanConfigureSpeech => !IsGenerating && !IsGeneratingImageDescriptions;

    public bool CanConfigureVision => !IsGenerating && !IsGeneratingImageDescriptions;

    public bool CanChangeBook => !IsGenerating && !IsGeneratingImageDescriptions;

    public bool CanEditBook => !IsGenerating && !IsGeneratingImageDescriptions;

    private void SetIsGenerating(bool value) =>
        SetAndRaise(
            ref isGenerating,
            value,
            [nameof(CanConfigureSpeech), nameof(CanConfigureVision), nameof(CanChangeBook), nameof(CanEditBook)],
            [
                GenerateCommand,
                CancelGenerationCommand,
                GenerateMissingImageDescriptionsCommand,
                GenerateSelectedImageDescriptionCommand,
                SaveApprovedImageDescriptionCommand,
                ApproveImageDescriptionCommand,
                RejectImageDescriptionCommand,
                ToggleDecorativeImageCommand,
                RestoreImageAltTextCommand,
                SaveDescriptionProjectCommand,
                SelectBookCommand,
                SaveImageAsCommand,
                AddImageCommand
            ],
            nameof(IsGenerating));

    public bool IsGeneratingImageDescriptions => isGeneratingImageDescriptions;

    private void SetIsGeneratingImageDescriptions(bool value) =>
        SetAndRaise(
            ref isGeneratingImageDescriptions,
            value,
            [nameof(CanConfigureSpeech), nameof(CanConfigureVision), nameof(CanChangeBook), nameof(CanEditBook)],
            [
                GenerateMissingImageDescriptionsCommand,
                GenerateSelectedImageDescriptionCommand,
                CancelImageDescriptionGenerationCommand,
                SaveApprovedImageDescriptionCommand,
                ApproveImageDescriptionCommand,
                RejectImageDescriptionCommand,
                ToggleDecorativeImageCommand,
                RestoreImageAltTextCommand,
                SaveDescriptionProjectCommand,
                SelectBookCommand,
                SaveImageAsCommand,
                AddImageCommand
            ],
            nameof(IsGeneratingImageDescriptions));

    public string ImageDescriptionProgress
    {
        get => imageDescriptionProgress;
        private set => SetAndRaise(ref imageDescriptionProgress, value);
    }

    public int ProgressPercentage { get => progressPercentage; private set => SetAndRaise(ref progressPercentage, value); }

    public string ProgressMessage { get => progressMessage; private set => SetAndRaise(ref progressMessage, value); }

    public bool IsPlaying { get => isPlaying; private set => SetAndRaise(ref isPlaying, value, [nameof(PlayStopIcon), nameof(PlayStopToolTip)], []); }

    public string PlayStopIcon { get => isPlaying ? "\xE769" /* pause icon */ : "\xE768"; /* play icon */ }

    public string PlayStopToolTip { get => isPlaying ? Resources.StopToolTip : Resources.PlayTooltip; }

    public SpeechProviderInfo? SelectedProvider
    {
        get => selectedProvider;
        set
        {
            if (Equals(selectedProvider, value))
            {
                return;
            }

            SetAndRaise(ref selectedProvider, value);
            SelectedVoice = null;
            LoadVoicesForSelectedProvider();
        }
    }

    public SpeechVoice? SelectedVoice { get => selectedVoice; set => SetAndRaise(ref selectedVoice, value, [nameof(IsVoiceSelected)], [PlayOrStopCommand, GenerateCommand]); }

    public ObservableCollection<SpeechProviderInfo> Providers { get; } = [];

    public ObservableCollection<SpeechVoice> Voices { get; } = [];

    public ObservableCollection<VisionProfileInfo> VisionProfiles { get; } = [];

    public VisionProfileInfo? SelectedVisionProfile
    {
        get => selectedVisionProfile;
        set => SetAndRaise(ref selectedVisionProfile, value, [], [
            GenerateMissingImageDescriptionsCommand,
            GenerateSelectedImageDescriptionCommand
        ]);
    }

    public BookImage? SelectedImage
    {
        get => selectedImage;
        set
        {
            selectedImage = value;
            approvedImageDescriptionDraft = value?.ApprovedDescription ?? string.Empty;
            candidateImageDescriptionDraft = value?.CandidateDescription ?? string.Empty;
            Raise();
            Raise(nameof(ApprovedImageDescriptionDraft));
            Raise(nameof(CandidateImageDescription));
            Raise(nameof(CandidateImageDescriptionDraft));
            Raise(nameof(HasSelectedImage));
            Raise(nameof(HasCandidateImageDescription));
            Raise(nameof(IsSelectedImageDecorative));
            Raise(nameof(SelectedImageDescriptionStatus));
            Raise(nameof(SelectedImageReferenceCount));
            RaiseImageCommandStates();
        }
    }

    public string ApprovedImageDescriptionDraft
    {
        get => approvedImageDescriptionDraft;
        set
        {
            SetAndRaise(ref approvedImageDescriptionDraft, value);
            SaveApprovedImageDescriptionCommand.RaiseCanExecuteChanged();
        }
    }

    public string? CandidateImageDescription => SelectedImage?.CandidateDescription;

    public string CandidateImageDescriptionDraft
    {
        get => candidateImageDescriptionDraft;
        set
        {
            SetAndRaise(ref candidateImageDescriptionDraft, value);
            ApproveImageDescriptionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedImage => SelectedImage != null;

    public bool HasCandidateImageDescription => !string.IsNullOrWhiteSpace(CandidateImageDescription);

    public bool IsSelectedImageDecorative => SelectedImage?.IsDecorative == true;

    public string SelectedImageDescriptionStatus => SelectedImage switch
    {
        null => "No image selected",
        { IsDecorative: true } => "Decorative; not narrated",
        { CandidateDescription: not null } => "Generated candidate awaiting review",
        { ApprovedDescription: not null } image => $"Approved ({image.DescriptionOrigin})",
        _ => "Missing; narration uses the generic image marker"
    };

    public int SelectedImageReferenceCount => SelectedImage == null || Book == null
        ? 0
        : Book.Chapters.Sum(chapter =>
            (chapter.ImageOccurrences ?? []).Count(occurrence =>
                string.Equals(occurrence.ImageId, SelectedImage.Id, StringComparison.Ordinal)));

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

    public DelegateCommand CancelGenerationCommand { get; }

    public DelegateCommand GenerateMissingImageDescriptionsCommand { get; }

    public DelegateCommand GenerateSelectedImageDescriptionCommand { get; }

    public DelegateCommand CancelImageDescriptionGenerationCommand { get; }

    public DelegateCommand SaveApprovedImageDescriptionCommand { get; }

    public DelegateCommand ApproveImageDescriptionCommand { get; }

    public DelegateCommand RejectImageDescriptionCommand { get; }

    public DelegateCommand ToggleDecorativeImageCommand { get; }

    public DelegateCommand RestoreImageAltTextCommand { get; }

    public DelegateCommand SaveDescriptionProjectCommand { get; }

    public AudiobookGeneratorViewModel(
        BookConverter bookConverter,
        ILogger<AudiobookGeneratorViewModel> loggerInstance)
    {
        logger = loggerInstance;
        converter = bookConverter;

        SelectBookCommand = new DelegateCommand(
            SelectBookAsync,
            parameter => this.CanChangeBook);
        PlayOrStopCommand = new DelegateCommand(PlayOrStopAsync, parameter => this.IsVoiceSelected && this.Book != null && this.Book.SelectedChapter != null);

        bool canExecuteWhenBookSelected(object? parameter) =>
            this.IsBookSelected && this.CanEditBook;
        SaveImageAsCommand = new DelegateCommand(SaveImageAsAsync, canExecuteWhenBookSelected);
        AddImageCommand = new DelegateCommand(AddImageAsync, canExecuteWhenBookSelected);

        GenerateCommand = new DelegateCommand(
            GenerateAsync,
            parameter => this.IsVoiceSelected && this.IsBookSelected && !this.IsGenerating);
        CancelGenerationCommand = new DelegateCommand(
            CancelGenerationAsync,
            parameter => this.IsGenerating);
        GenerateMissingImageDescriptionsCommand = new DelegateCommand(
            GenerateMissingImageDescriptionsAsync,
            parameter => this.Book != null
                && this.SelectedVisionProfile != null
                && !this.IsGeneratingImageDescriptions
                && !this.IsGenerating);
        GenerateSelectedImageDescriptionCommand = new DelegateCommand(
            GenerateSelectedImageDescriptionAsync,
            parameter => this.Book != null
                && this.SelectedImage != null
                && this.SelectedVisionProfile != null
                && !this.SelectedImage.IsDecorative
                && string.IsNullOrWhiteSpace(this.SelectedImage.CandidateDescription)
                && !this.IsGeneratingImageDescriptions
                && !this.IsGenerating);
        CancelImageDescriptionGenerationCommand = new DelegateCommand(
            CancelImageDescriptionGenerationAsync,
            parameter => this.IsGeneratingImageDescriptions);
        SaveApprovedImageDescriptionCommand = new DelegateCommand(
            SaveApprovedImageDescriptionAsync,
            parameter => this.SelectedImage != null
                && !string.IsNullOrWhiteSpace(this.ApprovedImageDescriptionDraft)
                && !this.IsGenerating
                && !this.IsGeneratingImageDescriptions);
        ApproveImageDescriptionCommand = new DelegateCommand(
            ApproveImageDescriptionAsync,
            parameter => this.SelectedImage?.CandidateDescription != null
                && !string.IsNullOrWhiteSpace(this.CandidateImageDescriptionDraft)
                && !this.IsGenerating
                && !this.IsGeneratingImageDescriptions);
        RejectImageDescriptionCommand = new DelegateCommand(
            RejectImageDescriptionAsync,
            parameter => this.SelectedImage?.CandidateDescription != null
                && !this.IsGenerating
                && !this.IsGeneratingImageDescriptions);
        ToggleDecorativeImageCommand = new DelegateCommand(
            ToggleDecorativeImageAsync,
            parameter => this.SelectedImage != null
                && !this.IsGenerating
                && !this.IsGeneratingImageDescriptions);
        RestoreImageAltTextCommand = new DelegateCommand(
            RestoreImageAltTextAsync,
            parameter => this.SelectedImage?.SourceAltTexts.Length > 0
                && !this.IsGenerating
                && !this.IsGeneratingImageDescriptions);
        SaveDescriptionProjectCommand = new DelegateCommand(
            SaveDescriptionProjectAsync,
            parameter => this.Book != null
                && !this.IsGenerating
                && !this.IsGeneratingImageDescriptions);

    }

    public async Task InitializeAsync()
    {
        try
        {
            var providers = await converter.Synthesizer.GetProvidersAsync(CancellationToken.None);
            Providers.Clear();
            foreach (var provider in providers)
            {
                Providers.Add(provider);
            }

            var defaultProviderId = await converter.Synthesizer.GetDefaultProviderIdAsync(CancellationToken.None);
            SelectedProvider = null;
            SelectedProvider = Providers.FirstOrDefault(provider =>
                string.Equals(provider.Id, defaultProviderId, StringComparison.OrdinalIgnoreCase))
                ?? Providers.FirstOrDefault();
            await RefreshVisionProfilesAsync();
        }
        catch (Exception ex)
        {
            ShowError("Unable to load TTS providers", ex);
        }
    }

    public async Task RefreshVisionProfilesAsync()
    {
        var selectedId = SelectedVisionProfile?.Id;
        var profiles = await converter.ImageDescriptions.GetProfilesAsync(CancellationToken.None);
        VisionProfiles.Clear();
        foreach (var profile in profiles)
        {
            VisionProfiles.Add(profile);
        }

        var defaultId = selectedId
            ?? await converter.ImageDescriptions.GetDefaultProfileIdAsync(CancellationToken.None);
        SelectedVisionProfile = VisionProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, defaultId, StringComparison.OrdinalIgnoreCase))
            ?? VisionProfiles.FirstOrDefault();
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

        await OpenBookAsync(file);
    }

    public async Task OpenBookAsync(FileInfo bookFile)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            ImageDescriptionProject? project = null;
            var sourceFile = bookFile;
            if (bookFile.Name.EndsWith(projectSupportedExtension, StringComparison.OrdinalIgnoreCase))
            {
                project = await converter.ImageDescriptionProjects.LoadAsync(bookFile, CancellationToken.None);
                sourceFile = converter.ImageDescriptionProjects.ResolveSourceEpub(bookFile, project);
                if (!sourceFile.Exists)
                {
                    throw new FileNotFoundException(
                        $"The source EPUB recorded by the sidecar was not found: {sourceFile.FullName}",
                        sourceFile.FullName);
                }
            }

            var book = await converter.Parser.ParseAsync(sourceFile, CancellationToken.None);
            if (project != null)
            {
                var applied = await converter.ImageDescriptionProjects.ApplyAsync(
                    project,
                    sourceFile,
                    book,
                    CancellationToken.None);
                book = applied.Book;
                if (!applied.SourceFingerprintMatches
                    || applied.UnmatchedImageIds.Count > 0
                    || project.SkippedImportedImageCount > 0)
                {
                    _ = MessageBox.Show(
                        $"Source fingerprint match: {applied.SourceFingerprintMatches}. " +
                        $"{applied.UnmatchedImageIds.Count} image annotation(s) could not be matched. " +
                        $"{project.SkippedImportedImageCount} imported image(s) were not persisted by the annotation-only sidecar.",
                        "Sidecar project warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            Book = new BookViewModel(
                sourceFile,
                [
                    new PropertyViewModel(BookPropertyType.Title, book.Title),
                    new PropertyViewModel(BookPropertyType.Description, book.Description),
                    new PropertyViewModel(BookPropertyType.Authors, string.Join(BookViewModel.authorsSeparator, book.AuthorList)),
                ],
                book.Chapters,
                book.Images,
                book.CoverImage != null
                    ? book.Images.FirstOrDefault(i => Enumerable.SequenceEqual(i.Content.Take(coverComparePrecision), book.CoverImage.Take(coverComparePrecision)))
                    : null,
                book.Language);
            SelectedImage = Book.Images.FirstOrDefault();
            if (project?.Generation?.ProfileId is { } profileId)
            {
                SelectedVisionProfile = VisionProfiles.FirstOrDefault(profile =>
                    string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            ShowError(Resources.BookOpenError, ex);
            Book = null;
            SelectedImage = null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async Task PlayOrStopAsync(object? parameter)
    {
        if (this.SelectedVoice == null || this.Book == null || this.Book.SelectedChapter == null)
        {
            return;
        }

        IsPlaying = !IsPlaying;

        if (IsPlaying)
        {
            try
            {
                var narrationContent = converter.NarrationRenderer.Render(
                    Book.SelectedChapter,
                    Book.Images,
                    ImageNarrationFallback.ForLanguage(Book.Language));
                await converter.Preview.PlayAsync(narrationContent, SelectedVoice, CancellationToken.None);
            }
            catch
            {
                IsPlaying = false;
                throw;
            }
        }
        else
        {
            converter.Preview.Stop();
        }
    }

    private async void LoadVoicesForSelectedProvider()
    {
        var version = ++voiceLoadVersion;
        Voices.Clear();
        var providerId = SelectedProvider?.Id;
        if (providerId == null)
        {
            return;
        }

        try
        {
            var voices = await converter.Synthesizer.GetVoicesAsync(providerId, CancellationToken.None);
            if (version != voiceLoadVersion
                || !string.Equals(SelectedProvider?.Id, providerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var voice in voices)
            {
                Voices.Add(voice);
            }
        }
        catch (Exception ex)
        {
            ShowError("Unable to load TTS voices", ex);
        }
    }

    private async Task SaveImageAsAsync(object? parameter)
    {
        var image = SelectedImage ?? Book?.Cover;
        if (image == null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = image.FileName,
            Filter = supportedImageFormatsFilter,
            DefaultExt = Path.GetExtension(image.FileName)
        };

        var result = dialog.ShowDialog();

        if (result != true)
        {
            return;
        }

        await File.WriteAllBytesAsync(dialog.FileName, image.Content);
    }

    private async Task AddImageAsync(object? parameter)
    {
        var activeBook = Book;
        if (activeBook == null)
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
        if (activeBook.Images.Any(existing => string.Equals(existing.Id, image.Id, StringComparison.Ordinal)))
        {
            image = image with { Id = $"added-{Guid.NewGuid():N}" };
        }

        activeBook.Images.Add(image);
        activeBook.Cover = image;
        SelectedImage = image;
        Raise(nameof(ImagesSectionHeader));
    }

    private Task GenerateMissingImageDescriptionsAsync(object? parameter)
    {
        var profileId = SelectedVisionProfile?.Id;
        return RunImageDescriptionGenerationAsync(
            (bookModel, cancellationToken) => converter.ImageDescriptionWorkflow.GenerateMissingReferencedAsync(
                bookModel,
                profileId,
                new ActionProgress<ImageDescriptionProgress>(UpdateImageDescriptionProgress),
                cancellationToken));
    }

    private Task GenerateSelectedImageDescriptionAsync(object? parameter)
    {
        if (SelectedImage == null)
        {
            return Task.CompletedTask;
        }

        var imageId = SelectedImage.Id;
        var regenerate = !string.IsNullOrWhiteSpace(SelectedImage.ApprovedDescription);
        var profileId = SelectedVisionProfile?.Id;
        return RunImageDescriptionGenerationAsync(
            (bookModel, cancellationToken) => converter.ImageDescriptionWorkflow.GenerateSelectedAsync(
                bookModel,
                imageId,
                profileId,
                regenerate,
                new ActionProgress<ImageDescriptionProgress>(UpdateImageDescriptionProgress),
                cancellationToken));
    }

    private async Task RunImageDescriptionGenerationAsync(
        Func<Book, CancellationToken, Task<ImageDescriptionBatchResult>> generate)
    {
        var activeBook = Book;
        if (activeBook == null)
        {
            return;
        }
        var bookModel = activeBook.CreateUpdatedModel();

        imageDescriptionCancellation?.Dispose();
        imageDescriptionCancellation = new CancellationTokenSource();
        var cancellationToken = imageDescriptionCancellation.Token;
        SetIsGeneratingImageDescriptions(true);
        ImageDescriptionProgress = "Starting image description generation...";
        try
        {
            var result = await generate(bookModel, cancellationToken);
            foreach (var candidate in result.Candidates)
            {
                var image = activeBook.Images.First(item =>
                    string.Equals(item.Id, candidate.ImageId, StringComparison.Ordinal));
                var updated = ImageDescriptionTransitions.WithCandidate(image, candidate.Description);
                activeBook.ReplaceImage(updated);
                if (ReferenceEquals(Book, activeBook)
                    && string.Equals(SelectedImage?.Id, updated.Id, StringComparison.Ordinal))
                {
                    SelectedImage = updated;
                }
            }

            ImageDescriptionProgress =
                $"{result.Candidates.Count} candidate(s) generated; {result.Failures.Count} failed.";
            if (result.Failures.Count > 0)
            {
                _ = MessageBox.Show(
                    string.Join(Environment.NewLine, result.Failures.Select(failure =>
                        $"{failure.FileName}: {failure.Message}")),
                    "Some image descriptions failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ImageDescriptionProgress = "Image description generation cancelled.";
        }
        finally
        {
            SetIsGeneratingImageDescriptions(false);
            imageDescriptionCancellation.Dispose();
            imageDescriptionCancellation = null;
        }
    }

    private void UpdateImageDescriptionProgress(ImageDescriptionProgress progress)
    {
        ImageDescriptionProgress =
            $"{progress.Current}/{progress.Total}: {progress.FileName} - {progress.State}";
    }

    private Task CancelImageDescriptionGenerationAsync(object? parameter)
    {
        imageDescriptionCancellation?.Cancel();
        return Task.CompletedTask;
    }

    private Task SaveApprovedImageDescriptionAsync(object? parameter)
    {
        if (Book != null && SelectedImage != null)
        {
            ReplaceImage(ImageDescriptionTransitions.EditApproved(
                SelectedImage,
                ApprovedImageDescriptionDraft));
        }
        return Task.CompletedTask;
    }

    private Task ApproveImageDescriptionAsync(object? parameter)
    {
        if (Book != null && SelectedImage != null)
        {
            var edited = string.Equals(
                CandidateImageDescriptionDraft.Trim(),
                SelectedImage.CandidateDescription,
                StringComparison.Ordinal)
                    ? null
                    : CandidateImageDescriptionDraft;
            ReplaceImage(ImageDescriptionTransitions.ApproveCandidate(SelectedImage, edited));
        }
        return Task.CompletedTask;
    }

    private Task RejectImageDescriptionAsync(object? parameter)
    {
        if (Book != null && SelectedImage != null)
        {
            ReplaceImage(ImageDescriptionTransitions.RejectCandidate(SelectedImage));
        }
        return Task.CompletedTask;
    }

    private Task ToggleDecorativeImageAsync(object? parameter)
    {
        if (Book != null && SelectedImage != null)
        {
            ReplaceImage(ImageDescriptionTransitions.SetDecorative(
                SelectedImage,
                !SelectedImage.IsDecorative));
        }
        return Task.CompletedTask;
    }

    private Task RestoreImageAltTextAsync(object? parameter)
    {
        if (Book != null && SelectedImage != null)
        {
            ReplaceImage(ImageDescriptionTransitions.RestoreOriginalAltText(SelectedImage));
        }
        return Task.CompletedTask;
    }

    private async Task SaveDescriptionProjectAsync(object? parameter)
    {
        var activeBook = Book;
        if (activeBook == null)
        {
            return;
        }
        var visionProfileId = SelectedVisionProfile?.Id;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{Path.GetFileNameWithoutExtension(activeBook.Path.Name)}{projectSupportedExtension}",
            DefaultExt = projectSupportedExtension,
            Filter = $"Image Description Project (*{projectSupportedExtension})|*{projectSupportedExtension}"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var saveResult = await converter.ImageDescriptionProjects.SaveAsync(
            new FileInfo(dialog.FileName),
            activeBook.Path,
            activeBook.CreateUpdatedModel(),
            visionProfileId,
            CancellationToken.None);
        ImageDescriptionProgress = $"Saved {dialog.FileName}{FormatSidecarLimitations(saveResult)}";
    }

    private async Task GenerateAsync(object? parameter)
    {
        var voice = SelectedVoice;
        var activeBook = Book;
        if (voice == null || activeBook == null)
        {
            return;
        }

        var sourcePath = activeBook.Path;
        var visionProfileId = SelectedVisionProfile?.Id;
        var updatedBook = activeBook.CreateUpdatedModel();
        latestBookState = updatedBook;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.ChangeExtension(updatedBook.FileName, audiobookSupportedExtension),
            DefaultExt = audiobookSupportedExtension,
            Filter = supportedAudiobookFormatFilter
        };

        var result = dialog.ShowDialog();

        if (result != true)
        {
            return;
        }

        generationCancellation?.Dispose();
        generationCancellation = new CancellationTokenSource();
        var cancellationToken = generationCancellation.Token;
        SetIsGenerating(true);

        var output = new FileInfo(dialog.FileName);

        try
        {
            if (!string.Equals(output.Extension, audiobookSupportedExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"We can only produce {audiobookSupportedExtension} files, not {output.Extension}.");
            }

            var tmpFiles = output.Directory ?? new DirectoryInfo(Path.GetTempPath());

            await converter.ConvertAsync(
                voice,
                updatedBook,
                output,
                tmpFiles,
                new ActionProgress<ProgressUpdate>(ProgressUpdate),
                cancellationToken);

            var sidecar = new FileInfo(converter.ImageDescriptionProjects.GetSidecarPath(output));
            var sidecarResult = await converter.ImageDescriptionProjects.SaveAsync(
                sidecar,
                sourcePath,
                updatedBook,
                visionProfileId,
                cancellationToken);
            if (sidecarResult.SkippedImportedImageCount > 0
                || sidecarResult.CoverSelectionNotPersisted)
            {
                _ = MessageBox.Show(
                    FormatSidecarLimitations(sidecarResult).TrimStart(),
                    "Sidecar project limitations",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            if (output.Directory != null)
            {
                _ = Process.Start(new ProcessStartInfo(output.Directory.FullName) { UseShellExecute = true });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ProgressMessage = Resources.GenerationCancelledMessage;
        }
        finally
        {
            SetIsGenerating(false);
            generationCancellation.Dispose();
            generationCancellation = null;
        }
    }

    private Task CancelGenerationAsync(object? parameter)
    {
        generationCancellation?.Cancel();
        return Task.CompletedTask;
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

    private void ReplaceImage(BookImage image)
    {
        if (Book == null)
        {
            return;
        }

        Book.ReplaceImage(image);
        if (string.Equals(SelectedImage?.Id, image.Id, StringComparison.Ordinal))
        {
            SelectedImage = image;
        }
    }

    private static string FormatSidecarLimitations(ImageDescriptionProjectSaveResult result)
    {
        var messages = new List<string>();
        if (result.SkippedImportedImageCount > 0)
        {
            messages.Add(
                $"{result.SkippedImportedImageCount} imported image(s) are not embedded in the annotation-only sidecar.");
        }
        if (result.CoverSelectionNotPersisted)
        {
            messages.Add("The imported cover selection cannot be restored from the sidecar.");
        }

        return messages.Count == 0
            ? string.Empty
            : $" {string.Join(" ", messages)}";
    }

    private void RaiseImageCommandStates()
    {
        GenerateSelectedImageDescriptionCommand.RaiseCanExecuteChanged();
        SaveApprovedImageDescriptionCommand.RaiseCanExecuteChanged();
        ApproveImageDescriptionCommand.RaiseCanExecuteChanged();
        RejectImageDescriptionCommand.RaiseCanExecuteChanged();
        ToggleDecorativeImageCommand.RaiseCanExecuteChanged();
        RestoreImageAltTextCommand.RaiseCanExecuteChanged();
    }

    private void ShowError(string title, Exception exception)
    {
        logger.LogError(exception, title);
        _ = MessageBox.Show(exception.ToString(), title, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.None);
    }

    public static bool IsBookInputSupported(string filePath) =>
        filePath.EndsWith(ebookSupportedExtension, StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith(projectSupportedExtension, StringComparison.OrdinalIgnoreCase);
}

internal class BookViewModel : BaseViewModel
{
    internal const char authorsSeparator = ',';
    private BookChapter? selectedChapter;
    private BookImage? cover;

    public BookViewModel(
        FileInfo path,
        IEnumerable<PropertyViewModel> properties,
        IEnumerable<BookChapter> chapters,
        IEnumerable<BookImage> images,
        BookImage? bookCover,
        string? language)
    {
        Path = path;
        Chapters = [.. chapters];
        Properties = [.. properties];
        Images = [.. images];
        cover = bookCover;
        selectedChapter = Chapters.FirstOrDefault();
        Language = language;
    }

    public FileInfo Path { get; }

    public string? Language { get; }

    public ObservableCollection<BookChapter> Chapters { get; }

    public ObservableCollection<PropertyViewModel> Properties { get; }

    public ObservableCollection<BookImage> Images { get; }

    public BookChapter? SelectedChapter { get => selectedChapter; set => SetAndRaise(ref selectedChapter, value); }

    public BookImage? Cover { get => cover; set => SetAndRaise(ref cover, value); }

    public void ReplaceImage(BookImage image)
    {
        var index = Images
            .Select((candidate, itemIndex) => (candidate, itemIndex))
            .FirstOrDefault(item => string.Equals(item.candidate.Id, image.Id, StringComparison.Ordinal))
            .itemIndex;
        if (index < 0 || index >= Images.Count
            || !string.Equals(Images[index].Id, image.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Image '{image.Id}' is not part of this book.");
        }

        Images[index] = image;
        if (string.Equals(Cover?.Id, image.Id, StringComparison.Ordinal))
        {
            Cover = image;
        }
    }

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
            [.. Images],
            Language);
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
