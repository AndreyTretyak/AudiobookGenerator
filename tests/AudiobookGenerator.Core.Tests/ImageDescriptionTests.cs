using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SkiaSharp;

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

using Xunit;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Core.Tests;

public sealed class ImageDescriptionTests
{
    [Fact]
    public void EpubPathsResolveRelativeImagesWithoutCollidingBasenames()
    {
        Assert.Equal(
            "OEBPS/Images/figure.png",
            EpubImagePath.Resolve("OEBPS/Text/chapter.xhtml", "../Images/figure.png?size=large#view"));
        Assert.Equal(
            "Other/figure.png",
            EpubImagePath.Resolve("Other/chapter.xhtml", "figure.png"));
        Assert.Null(EpubImagePath.Resolve("OEBPS/Text/chapter.xhtml", "https://example.test/figure.png"));
    }

    [Fact]
    public async Task HtmlConversionReturnsImageReferencesInsteadOfNarrationProse()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        var converter = services.GetRequiredService<IHtmlConverter>();

        var result = await converter.HtmlToPlaineTextAsync(
            """
            <html><head><title>Chapter</title></head><body>
            Before <img src="../Images/map.png" alt="A &amp; B" /> after.
            </body></html>
            """,
            CancellationToken.None);

        var reference = Assert.Single(result.Images);
        Assert.Equal("../Images/map.png", reference.Source);
        Assert.Equal("A & B", reference.AltText);
        Assert.Contains(reference.Placeholder, result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("book image", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EpubParserLinksRelativeImageAndPreservesAltText()
    {
        using var temporary = new TemporaryDirectory();
        var epub = new FileInfo(System.IO.Path.Combine(temporary.Path, "book.epub"));
        Directory.CreateDirectory(temporary.Path);
        CreateEpub(epub);
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        var parser = services.GetRequiredService<IEpubBookParser>();

        var book = await parser.ParseAsync(epub, CancellationToken.None);

        Assert.Equal("en", book.Language);
        var image = Assert.Single(book.Images);
        Assert.Equal("OEBPS/Images/figure.png", image.SourcePath);
        Assert.Equal("A red square", image.ApprovedDescription);
        Assert.Equal(["A red square", "A crimson block"], image.SourceAltTexts);
        Assert.Equal(ImageDescriptionOrigin.EpubAltText, image.DescriptionOrigin);
        var chapter = Assert.Single(book.Chapters);
        Assert.Equal(2, chapter.ImageOccurrences!.Length);
        Assert.All(chapter.ImageOccurrences, occurrence => Assert.Equal(image.Id, occurrence.ImageId));
        Assert.All(chapter.ImageOccurrences, occurrence =>
            Assert.Contains(ImageNarrationMarker.Create(occurrence.Id), chapter.Content, StringComparison.Ordinal));
    }

    [Fact]
    public void NarrationUsesApprovedTextAndIgnoresPendingCandidates()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        var renderer = services.GetRequiredService<IImageNarrationRenderer>();
        var occurrence = new BookImageOccurrence("occurrence-1", "image-1", "figure.png", null);
        var chapter = new BookChapter(
            "chapter",
            "Chapter",
            $"Before {ImageNarrationMarker.Create(occurrence.Id)} after",
            [occurrence]);
        var image = new BookImage("figure.png", CreatePng(20, 20))
        {
            Id = "image-1",
            ApprovedDescription = "A quiet forest",
            CandidateDescription = "A noisy city",
            DescriptionOrigin = ImageDescriptionOrigin.EpubAltText
        };

        Assert.Equal("Before A quiet forest. after", renderer.Render(chapter, [image]));
        Assert.Equal("Before  after", renderer.Render(chapter, [image with { IsDecorative = true }]));
        Assert.Equal(
            "Before Image. after",
            renderer.Render(chapter, [image with { ApprovedDescription = null, CandidateDescription = "Pending" }]));
        Assert.Equal("Изображение.", ImageNarrationFallback.ForLanguage("ru-RU"));
    }

    [Fact]
    public void ChapterMarkerValidationDetectsRemovedReferences()
    {
        var first = $"Text {ImageNarrationMarker.Create("occurrence-a")} and {ImageNarrationMarker.Create("occurrence-b")}.";
        var reordered = $"{ImageNarrationMarker.Create("occurrence-b")} Text {ImageNarrationMarker.Create("occurrence-a")}.";

        Assert.True(ImageNarrationMarker.HasSameOccurrences(first, reordered));
        Assert.False(ImageNarrationMarker.HasSameOccurrences(first, first.Replace(
            ImageNarrationMarker.Create("occurrence-a"),
            string.Empty,
            StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NormalizerResizesRasterAndRasterizesSvg()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        var normalizer = services.GetRequiredService<IImageNormalizer>();

        var raster = await normalizer.NormalizeAsync(
            new BookImage("large.png", CreatePng(1200, 600)),
            maximumDimension: 256,
            maximumBytes: 256 * 1024,
            CancellationToken.None);
        var svg = await normalizer.NormalizeAsync(
            new BookImage(
                "shape.svg",
                Encoding.UTF8.GetBytes(
                    """<svg xmlns="http://www.w3.org/2000/svg" width="400" height="200"><rect width="400" height="200" fill="red"/></svg>""")),
            maximumDimension: 256,
            maximumBytes: 256 * 1024,
            CancellationToken.None);

        Assert.Equal(256, raster.Width);
        Assert.Equal(128, raster.Height);
        Assert.True(raster.Content.Length <= 256 * 1024);
        Assert.Equal("image/png", svg.MimeType);
        Assert.Equal(256, svg.Width);
        Assert.Equal(128, svg.Height);
    }

    [Fact]
    public async Task NormalizerRejectsExternalSvgResources()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        var normalizer = services.GetRequiredService<IImageNormalizer>();
        var image = new BookImage(
            "external.svg",
            Encoding.UTF8.GetBytes(
                """<svg xmlns="http://www.w3.org/2000/svg"><image href="file:///C:/secret.png"/></svg>"""));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            normalizer.NormalizeAsync(image, 512, 512 * 1024, CancellationToken.None));

        Assert.Contains("external resources", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VisionProviderSendsBoundedMultimodalRequestAndParsesDescription()
    {
        using var temporary = new TemporaryDirectory();
        string? requestJson = null;
        HttpRequestMessage? observedRequest = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            observedRequest = request;
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("A red square on a white background.");
        });
        using var services = CreateServices(temporary.SettingsPath, handler);
        var secretName = $"AUDIOBOOKGENERATOR_VISION_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(secretName, "vision-secret");
        try
        {
            await SaveVisionSettingsAsync(services, secretName);
            var service = services.GetRequiredService<IImageDescriptionService>();
            var session = await service.CreateSessionAsync("vision", CancellationToken.None);
            var (book, image) = CreateBookForVision(new string('x', 5000));

            var result = await session.DescribeAsync(book, image, false, CancellationToken.None);

            Assert.Equal("A red square on a white background.", result.Description);
            Assert.Equal("Bearer", observedRequest!.Headers.Authorization!.Scheme);
            Assert.Equal("vision-secret", observedRequest.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(requestJson!);
            Assert.Equal("vision-model", json.RootElement.GetProperty("model").GetString());
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Equal(OpenAiCompatibleVisionProfile.DefaultDescriptionPrompt, messages[0].GetProperty("content").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
            Assert.Equal(200, json.RootElement.GetProperty("max_tokens").GetInt32());
            var content = messages[1].GetProperty("content");
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            var dataUrl = content[1].GetProperty("image_url").GetProperty("url").GetString();
            Assert.StartsWith("data:image/", dataUrl, StringComparison.Ordinal);
            Assert.True(content[0].GetProperty("text").GetString()!.Length < 2500);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
        }
    }

    [Fact]
    public async Task BulkWorkflowTargetsOnlyReferencedMissingNonDecorativeImages()
    {
        using var temporary = new TemporaryDirectory();
        var requests = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(JsonResponse("Generated description."));
        });
        using var services = CreateServices(temporary.SettingsPath, handler);
        await SaveVisionSettingsAsync(services);
        var missing = new BookImage("missing.png", CreatePng(20, 20)) { Id = "missing" };
        var approved = new BookImage("approved.png", CreatePng(20, 20))
        {
            Id = "approved",
            ApprovedDescription = "Existing."
        };
        var decorative = new BookImage("decorative.png", CreatePng(20, 20))
        {
            Id = "decorative",
            IsDecorative = true
        };
        var unreferenced = new BookImage("unreferenced.png", CreatePng(20, 20)) { Id = "unreferenced" };
        var occurrences = new[]
        {
            new BookImageOccurrence("occ-1", missing.Id, "missing.png", null),
            new BookImageOccurrence("occ-2", approved.Id, "approved.png", null),
            new BookImageOccurrence("occ-3", decorative.Id, "decorative.png", null)
        };
        var book = new Book(
            "book",
            "Book",
            string.Empty,
            [],
            null,
            [new BookChapter(
                "chapter",
                "Chapter",
                string.Join(" ", occurrences.Select(occurrence => ImageNarrationMarker.Create(occurrence.Id))),
                occurrences)],
            [missing, approved, decorative, unreferenced]);
        var workflow = services.GetRequiredService<IImageDescriptionWorkflow>();

        var result = await workflow.GenerateMissingReferencedAsync(
            book,
            profileId: null,
            progress: null,
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(missing.Id, candidate.ImageId);
        Assert.Empty(result.Failures);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task VisionSettingsPersistCamelCaseIndentedJson()
    {
        using var temporary = new TemporaryDirectory();
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        await SaveVisionSettingsAsync(services);

        var json = await File.ReadAllTextAsync(System.IO.Path.Combine(temporary.Path, "vision-settings.json"));
        Assert.Contains($"{Environment.NewLine}  \"defaultProfileId\": \"vision\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maximumOutputTokens\": 200", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DefaultProfileId\"", json, StringComparison.Ordinal);

        var settings = await services.GetRequiredService<IVisionSettingsStore>().LoadAsync(CancellationToken.None);
        Assert.Equal("vision", settings.DefaultProfileId);
        Assert.Equal("vision-model", Assert.Single(settings.Profiles).Model);
    }

    [Fact]
    public async Task VisionProviderParsesStructuredContentAndReportsRefusal()
    {
        using var temporary = new TemporaryDirectory();
        var responseIndex = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            responseIndex++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseIndex == 1
                        ? """{"choices":[{"message":{"content":[{"type":"text","text":"Structured description."}]},"finish_reason":"stop"}]}"""
                        : """{"choices":[{"message":{"content":"","refusal":"unsupported image"},"finish_reason":"stop"}]}""",
                    Encoding.UTF8,
                    "application/json")
            });
        });
        using var services = CreateServices(temporary.SettingsPath, handler);
        await SaveVisionSettingsAsync(services);
        var service = services.GetRequiredService<IImageDescriptionService>();
        var session = await service.CreateSessionAsync("vision", CancellationToken.None);
        var (book, image) = CreateBookForVision("Context");

        var result = await session.DescribeAsync(book, image, false, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.DescribeAsync(book, image, false, CancellationToken.None));

        Assert.Equal("Structured description.", result.Description);
        Assert.Contains("unsupported image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkWorkflowKeepsSuccessesAndReportsIndividualFailures()
    {
        using var temporary = new TemporaryDirectory();
        var requestIndex = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            requestIndex++;
            return Task.FromResult(requestIndex == 1
                ? JsonResponse("First description.")
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("request rejected")
                });
        });
        using var services = CreateServices(temporary.SettingsPath, handler);
        await SaveVisionSettingsAsync(services);
        var first = new BookImage("first.png", CreatePng(20, 20)) { Id = "first" };
        var second = new BookImage("second.png", CreatePng(20, 20)) { Id = "second" };
        var occurrences = new[]
        {
            new BookImageOccurrence("occ-first", first.Id, "first.png", null),
            new BookImageOccurrence("occ-second", second.Id, "second.png", null)
        };
        var book = new Book(
            "book",
            "Book",
            string.Empty,
            [],
            null,
            [new BookChapter(
                "chapter",
                "Chapter",
                string.Join(" ", occurrences.Select(occurrence => ImageNarrationMarker.Create(occurrence.Id))),
                occurrences)],
            [first, second]);
        var workflow = services.GetRequiredService<IImageDescriptionWorkflow>();

        var result = await workflow.GenerateMissingReferencedAsync(
            book,
            "vision",
            null,
            CancellationToken.None);

        Assert.Single(result.Candidates);
        Assert.Single(result.Failures);
        Assert.Equal(second.Id, result.Failures[0].ImageId);
    }

    [Fact]
    public async Task BulkWorkflowDoesNotOverwritePendingCandidates()
    {
        using var temporary = new TemporaryDirectory();
        var requests = 0;
        using var services = CreateServices(
            temporary.SettingsPath,
            new RecordingHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(JsonResponse("Replacement."));
            }));
        await SaveVisionSettingsAsync(services);
        var image = new BookImage("pending.png", CreatePng(20, 20))
        {
            Id = "pending",
            CandidateDescription = "Awaiting review."
        };
        var occurrence = new BookImageOccurrence("occ-pending", image.Id, "pending.png", null);
        var book = new Book(
            "book",
            "Book",
            string.Empty,
            [],
            null,
            [new BookChapter(
                "chapter",
                "Chapter",
                ImageNarrationMarker.Create(occurrence.Id),
                [occurrence])],
            [image]);
        var workflow = services.GetRequiredService<IImageDescriptionWorkflow>();

        var result = await workflow.GenerateMissingReferencedAsync(
            book,
            "vision",
            null,
            CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Equal(0, requests);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.GenerateSelectedAsync(
                book,
                image.Id,
                "vision",
                regenerate: true,
                progress: null,
                CancellationToken.None));
        Assert.Contains("Approve or reject", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptionTransitionsDoNotReplaceApprovedTextBeforeAcceptance()
    {
        var image = new BookImage("figure.png", CreatePng(10, 10))
        {
            ApprovedDescription = "Original alt.",
            DescriptionOrigin = ImageDescriptionOrigin.EpubAltText
        };

        var pending = ImageDescriptionTransitions.WithCandidate(image, "Generated candidate.");
        var rejected = ImageDescriptionTransitions.RejectCandidate(pending);
        var approved = ImageDescriptionTransitions.ApproveCandidate(pending);

        Assert.Equal("Original alt.", pending.ApprovedDescription);
        Assert.Equal("Generated candidate.", pending.CandidateDescription);
        Assert.Equal("Original alt.", rejected.ApprovedDescription);
        Assert.Equal("Generated candidate.", approved.ApprovedDescription);
        Assert.Equal(ImageDescriptionOrigin.Generated, approved.DescriptionOrigin);
    }

    [Fact]
    public async Task SidecarRoundTripMatchesByHashAndExcludesSecrets()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Path);
        var source = new FileInfo(System.IO.Path.Combine(temporary.Path, "source.epub"));
        await File.WriteAllTextAsync(source.FullName, "source-one");
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        var secretName = $"AUDIOBOOKGENERATOR_VISION_TEST_{Guid.NewGuid():N}";
        var secretValue = $"secret-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(secretName, secretValue);
        try
        {
            await SaveVisionSettingsAsync(services, secretName);
            var content = CreatePng(20, 20);
            var annotated = new BookImage("figure.png", content)
            {
                Id = "old-id",
                SourcePath = "Images/figure.png",
                ApprovedDescription = "Approved.",
                CandidateDescription = "Pending.",
                DescriptionOrigin = ImageDescriptionOrigin.UserEdited,
                IsDecorative = false
            };
            var imported = new BookImage("imported.png", CreatePng(10, 10));
            var book = new Book("book", "Book", string.Empty, [], content, [], [annotated, imported]);
            var store = services.GetRequiredService<IImageDescriptionProjectStore>();
            var sidecar = new FileInfo(System.IO.Path.Combine(temporary.Path, "book.audiobook.json"));

            var saveResult = await store.SaveAsync(sidecar, source, book, "vision", CancellationToken.None);
            var serialized = await File.ReadAllTextAsync(sidecar.FullName);
            using var serializedJson = JsonDocument.Parse(serialized);
            var project = await store.LoadAsync(sidecar, CancellationToken.None);
            Assert.False(Path.IsPathRooted(project.SourceEpubPath));
            Assert.Equal(source.FullName, store.ResolveSourceEpub(sidecar, project).FullName);
            var reparsed = book with
            {
                Images = [new BookImage("renamed.png", content) { Id = "new-id" }]
            };
            var applied = await store.ApplyAsync(project, source, reparsed, CancellationToken.None);

            Assert.DoesNotContain(secretValue, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(content), serialized, StringComparison.Ordinal);
            Assert.Equal("SourceImage", serializedJson.RootElement.GetProperty("coverState").GetString());
            Assert.Equal(
                "UserEdited",
                serializedJson.RootElement.GetProperty("images")[0].GetProperty("descriptionOrigin").GetString());
            Assert.Equal(1, saveResult.SkippedImportedImageCount);
            Assert.False(saveResult.CoverSelectionNotPersisted);
            Assert.Equal(1, project.SkippedImportedImageCount);
            Assert.True(applied.SourceFingerprintMatches);
            Assert.Empty(applied.UnmatchedImageIds);
            Assert.Equal("Approved.", Assert.Single(applied.Book.Images).ApprovedDescription);
            Assert.Equal(content, applied.Book.CoverImage);

            var clearedSidecar = new FileInfo(System.IO.Path.Combine(temporary.Path, "cleared.audiobook.json"));
            await store.SaveAsync(
                clearedSidecar,
                source,
                book with { CoverImage = null },
                "vision",
                CancellationToken.None);
            var clearedProject = await store.LoadAsync(clearedSidecar, CancellationToken.None);
            var clearedResult = await store.ApplyAsync(clearedProject, source, reparsed, CancellationToken.None);
            Assert.Equal(ImageDescriptionCoverState.Cleared, clearedProject.CoverState);
            Assert.Null(clearedResult.Book.CoverImage);

            var importedCoverResult = await store.SaveAsync(
                new FileInfo(System.IO.Path.Combine(temporary.Path, "imported-cover.audiobook.json")),
                source,
                book with { CoverImage = imported.Content },
                "vision",
                CancellationToken.None);
            Assert.True(importedCoverResult.CoverSelectionNotPersisted);

            var ambiguous = reparsed with
            {
                Images =
                [
                    new BookImage("one.png", content) { Id = "one" },
                    new BookImage("two.png", content) { Id = "two" }
                ]
            };
            var ambiguousResult = await store.ApplyAsync(project, source, ambiguous, CancellationToken.None);
            Assert.Equal(2, ambiguousResult.UnmatchedImageIds.Count);
            Assert.Contains("old-id", ambiguousResult.UnmatchedImageIds);
            Assert.Contains(
                ambiguousResult.UnmatchedImageIds,
                static value => value.StartsWith("cover:", StringComparison.Ordinal));
            Assert.All(ambiguousResult.Book.Images, static image => Assert.Null(image.ApprovedDescription));

            await File.WriteAllTextAsync(source.FullName, "source-two");
            var mismatched = await store.ApplyAsync(project, source, reparsed, CancellationToken.None);
            Assert.False(mismatched.SourceFingerprintMatches);
            Assert.Equal("Approved.", Assert.Single(mismatched.Book.Images).ApprovedDescription);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
        }
    }

    [Fact]
    public void SidecarPreservesAbsoluteSourceWhenRelativePathIsUnavailable()
    {
        var storeType = typeof(IImageDescriptionProjectStore).Assembly.GetType(
            "YewCone.AudiobookGenerator.Core.ImageDescriptionProjectStore",
            throwOnError: true)!;
        var method = storeType.GetMethod(
            "CreatePortableSourcePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var source = new FileInfo(@"C:\books\title.epub");
        var sidecar = new FileInfo(@"D:\exports\title.audiobook.json");

        var savedPath = Assert.IsType<string>(method.Invoke(null, [sidecar, source]));

        Assert.Equal(source.FullName, savedPath);
    }

    [Fact]
    public void SidecarResolvesWindowsStyleRelativeSeparatorsOnEveryPlatform()
    {
        using var temporary = new TemporaryDirectory();
        var sidecarDirectory = System.IO.Path.Combine(temporary.Path, "output");
        Directory.CreateDirectory(sidecarDirectory);
        var sidecar = new FileInfo(System.IO.Path.Combine(sidecarDirectory, "book.audiobook.json"));
        using var services = CreateServices(temporary.SettingsPath, new RecordingHandler(static (_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        var store = services.GetRequiredService<IImageDescriptionProjectStore>();
        var project = new ImageDescriptionProject
        {
            SourceEpubPath = @"..\books\book.epub",
            SourceEpubSha256 = new string('0', 64)
        };

        var resolved = store.ResolveSourceEpub(sidecar, project);

        Assert.Equal(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(temporary.Path, "books", "book.epub")),
            resolved.FullName);
    }

    private static ServiceProvider CreateServices(string settingsPath, HttpMessageHandler handler)
    {
        var services = new ServiceCollection()
            .AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug))
            .AddBookConverter(settingsPath);
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static async Task SaveVisionSettingsAsync(
        IServiceProvider services,
        string? apiKeyEnvironmentVariable = null)
    {
        var store = services.GetRequiredService<IVisionSettingsStore>();
        await store.SaveAsync(
            new VisionSettings
            {
                DefaultProfileId = "vision",
                Profiles =
                [
                    new OpenAiCompatibleVisionProfile
                    {
                        Id = "vision",
                        DisplayName = "Vision",
                        BaseUrl = "http://127.0.0.1:11434/v1/",
                        Model = "vision-model",
                        MaximumImageDimension = 512,
                        MaximumImageBytes = 512 * 1024,
                        MaximumContextCharacters = 1200,
                        ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable
                    }
                ]
            },
            CancellationToken.None);
    }

    private static (Book Book, BookImage Image) CreateBookForVision(string context)
    {
        var image = new BookImage("figure.png", CreatePng(32, 32)) { Id = "image-1" };
        var occurrence = new BookImageOccurrence("occurrence-1", image.Id, "figure.png", null);
        var split = context.Length / 2;
        var chapter = new BookChapter(
            "chapter",
            "Chapter One",
            $"{context[..split]} {ImageNarrationMarker.Create(occurrence.Id)} {context[split..]}",
            [occurrence]);
        return (
            new Book("book", "Book", string.Empty, [], null, [chapter], [image], "en-US"),
            image);
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static HttpResponseMessage JsonResponse(string description) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"choices":[{"message":{"role":"assistant","content":{{JsonSerializer.Serialize(description)}}},"finish_reason":"stop"}]}""",
            Encoding.UTF8,
            "application/json")
    };

    private static void CreateEpub(FileInfo file)
    {
        using var archive = ZipFile.Open(file.FullName, ZipArchiveMode.Create);
        WriteEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        WriteEntry(
            archive,
            "META-INF/container.xml",
            """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);
        WriteEntry(
            archive,
            "OEBPS/content.opf",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="id">test-book</dc:identifier>
                <dc:title>Image Book</dc:title>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="chapter" href="Text/chapter.xhtml" media-type="application/xhtml+xml"/>
                <item id="figure" href="Images/figure.png" media-type="image/png"/>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
              </manifest>
              <spine toc="ncx"><itemref idref="chapter"/></spine>
            </package>
            """);
        WriteEntry(
            archive,
            "OEBPS/toc.ncx",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
              <head><meta name="dtb:uid" content="test-book"/></head>
              <docTitle><text>Image Book</text></docTitle>
              <navMap><navPoint id="one" playOrder="1"><navLabel><text>One</text></navLabel><content src="Text/chapter.xhtml"/></navPoint></navMap>
            </ncx>
            """);
        WriteEntry(
            archive,
            "OEBPS/Text/chapter.xhtml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title></head>
            <body><p>Before.</p><img src="../Images/figure.png" alt="A red square"/><p>Between.</p><img src="../Images/figure.png" alt="A crimson block"/><p>After.</p></body></html>
            """);
        var imageEntry = archive.CreateEntry("OEBPS/Images/figure.png");
        using var imageStream = imageEntry.Open();
        imageStream.Write(CreatePng(20, 20));
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        string content,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(path, compressionLevel);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AudiobookImageTests-{Guid.NewGuid():N}");
            SettingsPath = System.IO.Path.Combine(Path, "tts-settings.json");
        }

        public string Path { get; }

        public string SettingsPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
