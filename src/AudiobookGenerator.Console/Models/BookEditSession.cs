using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Console.Models;

/// <summary>
/// Mutable session wrapper for editing a <see cref="Book"/> before conversion.
/// Allows editing chapters, metadata, and images without rebuilding immutable records.
/// </summary>
internal sealed class BookEditSession
{
    private readonly Book _originalBook;
    private readonly Dictionary<string, string> _editedChapterContent = [];
    private readonly List<BookImage> _images;

    public BookEditSession(Book book, FileInfo? sourceFile = null)
    {
        _originalBook = book;
        _images = [.. book.Images];
        SourceFile = sourceFile;

        Title = book.Title;
        Description = book.Description;
        Authors = [.. book.AuthorList];
        CoverImage = book.CoverImage;
        FileName = book.FileName;
    }

    /// <summary>
    /// The original file name (without extension).
    /// </summary>
    public string FileName { get; }

    public FileInfo? SourceFile { get; }

    public string? Language => _originalBook.Language;

    /// <summary>
    /// Editable book title.
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// Editable book description.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Editable list of authors.
    /// </summary>
    public List<string> Authors { get; set; }

    /// <summary>
    /// Current cover image bytes (can be changed via <see cref="SetCoverImage"/>).
    /// </summary>
    public byte[]? CoverImage { get; private set; }

    /// <summary>
    /// Currently selected TTS voice for conversion.
    /// </summary>
    public SpeechVoice? SelectedVoice { get; set; }

    public string? SelectedVisionProfileId { get; set; }

    /// <summary>
    /// Gets all chapters with their current content (edited or original).
    /// </summary>
    public IReadOnlyList<BookChapter> Chapters => _originalBook.Chapters
        .Select(c => _editedChapterContent.TryGetValue(c.FileName, out var edited)
            ? c with { Content = edited }
            : c)
        .ToList();

    /// <summary>
    /// Gets all images in the book.
    /// </summary>
    public IReadOnlyList<BookImage> Images => _images;

    /// <summary>
    /// Gets a chapter by index.
    /// </summary>
    public BookChapter GetChapter(int index) => Chapters[index];

    /// <summary>
    /// Updates the content of a chapter.
    /// </summary>
    public void UpdateChapterContent(string fileName, string newContent)
    {
        var currentContent = Chapters.First(chapter => chapter.FileName == fileName).Content;
        if (!ImageNarrationMarker.HasSameOccurrences(currentContent, newContent))
        {
            throw new InvalidOperationException("Chapter edits must preserve every [[image-ref:...]] marker.");
        }

        _editedChapterContent[fileName] = newContent;
    }

    /// <summary>
    /// Sets an existing image as the cover image.
    /// </summary>
    public void SetCoverImage(BookImage image)
    {
        CoverImage = image.Content;
    }

    /// <summary>
    /// Clears the cover image.
    /// </summary>
    public void ClearCoverImage()
    {
        CoverImage = null;
    }

    /// <summary>
    /// Adds a new image to the book.
    /// </summary>
    public void AddImage(BookImage image)
    {
        if (_images.Any(existing => string.Equals(existing.Id, image.Id, StringComparison.Ordinal)))
        {
            image = image with { Id = $"added-{Guid.NewGuid():N}" };
        }

        _images.Add(image);
    }

    public void UpdateImage(BookImage image)
    {
        var index = _images.FindIndex(existing => string.Equals(existing.Id, image.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException($"Image '{image.Id}' is not part of this book.");
        }

        _images[index] = image;
    }

    public int GetReferenceCount(BookImage image) =>
        Chapters.Sum(chapter =>
            (chapter.ImageOccurrences ?? []).Count(occurrence =>
                string.Equals(occurrence.ImageId, image.Id, StringComparison.Ordinal)));

    /// <summary>
    /// Checks if an image is the current cover.
    /// </summary>
    public bool IsCoverImage(BookImage image) =>
        CoverImage != null && image.Content.Take(200).SequenceEqual(CoverImage.Take(200));

    /// <summary>
    /// Checks if any edits have been made to the book.
    /// </summary>
    public bool HasEdits =>
        _editedChapterContent.Count > 0
        || Title != _originalBook.Title
        || Description != _originalBook.Description
        || !Authors.SequenceEqual(_originalBook.AuthorList)
        || !ReferenceEquals(CoverImage, _originalBook.CoverImage)
        || !_images.SequenceEqual(_originalBook.Images);

    /// <summary>
    /// Builds the final <see cref="Book"/> record with all edits applied.
    /// </summary>
    public Book BuildBook() => new(
        FileName,
        Title,
        Description,
        Authors,
        CoverImage,
        [.. Chapters],
        [.. _images],
        _originalBook.Language);
}
