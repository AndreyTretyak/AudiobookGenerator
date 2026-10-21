using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Wpf;

public partial class VisionSettingsWindow : Window
{
    private readonly IVisionSettingsStore settingsStore;
    private readonly IImageDescriptionService descriptions;
    private readonly BookImage? testImage;
    private VisionSettings settings = new();
    private OpenAiCompatibleVisionProfile? selectedProfile;
    private bool isLoading;

    public VisionSettingsWindow(
        IVisionSettingsStore settingsStoreInstance,
        IImageDescriptionService imageDescriptions,
        BookImage? selectedImage)
    {
        settingsStore = settingsStoreInstance;
        descriptions = imageDescriptions;
        testImage = selectedImage;
        InitializeComponent();
    }

    private async void HandleLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            settings = await settingsStore.LoadAsync(CancellationToken.None);
            RefreshLists();
            StatusTextBlock.Text = $"Settings: {settingsStore.SettingsPath}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void HandleAddProfile(object sender, RoutedEventArgs e)
    {
        var suffix = 1;
        var id = "local-vision";
        while (settings.Profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            id = $"local-vision-{++suffix}";
        }

        var profile = new OpenAiCompatibleVisionProfile
        {
            Id = id,
            DisplayName = "Local Vision",
            Model = "qwen3-vl:8b"
        };
        settings.Profiles.Add(profile);
        settings.DefaultProfileId ??= profile.Id;
        RefreshLists(profile);
    }

    private async void HandleRemoveProfile(object sender, RoutedEventArgs e)
    {
        if (selectedProfile == null
            || MessageBox.Show(
                $"Remove vision profile '{selectedProfile.DisplayName}'?",
                "Remove vision profile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var removedProfile = selectedProfile;
        var index = settings.Profiles.IndexOf(removedProfile);
        var oldDefaultId = settings.DefaultProfileId;
        var id = removedProfile.Id;
        _ = settings.Profiles.Remove(removedProfile);
        if (string.Equals(oldDefaultId, id, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultProfileId = settings.Profiles.FirstOrDefault()?.Id;
        }

        try
        {
            await settingsStore.SaveAsync(settings, CancellationToken.None);
            selectedProfile = null;
            RefreshLists();
            ClearEditor();
        }
        catch (Exception ex)
        {
            settings.Profiles.Insert(index, removedProfile);
            settings.DefaultProfileId = oldDefaultId;
            RefreshLists(removedProfile);
            ShowError(ex);
        }
    }

    private void HandleProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedProfile = ProfileList.SelectedItem as OpenAiCompatibleVisionProfile;
        if (selectedProfile == null)
        {
            ClearEditor();
            return;
        }

        ProfileEditor.IsEnabled = true;
        ProfileIdTextBox.Text = selectedProfile.Id;
        DisplayNameTextBox.Text = selectedProfile.DisplayName;
        BaseUrlTextBox.Text = selectedProfile.BaseUrl;
        ModelTextBox.Text = selectedProfile.Model;
        PromptTextBox.Text = selectedProfile.Prompt;
        TemperatureTextBox.Text = selectedProfile.Temperature.ToString(CultureInfo.InvariantCulture);
        MaximumOutputTokensTextBox.Text = selectedProfile.MaximumOutputTokens.ToString(CultureInfo.InvariantCulture);
        TimeoutTextBox.Text = selectedProfile.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        MaximumImageDimensionTextBox.Text = selectedProfile.MaximumImageDimension.ToString(CultureInfo.InvariantCulture);
        MaximumImageBytesTextBox.Text = selectedProfile.MaximumImageBytes.ToString(CultureInfo.InvariantCulture);
        MaximumContextCharactersTextBox.Text = selectedProfile.MaximumContextCharacters.ToString(CultureInfo.InvariantCulture);
        ApiKeyEnvironmentVariableTextBox.Text = selectedProfile.ApiKeyEnvironmentVariable ?? string.Empty;
    }

    private async void HandleSaveProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            await ApplyAndSaveAsync();
            StatusTextBlock.Text = "Vision profile saved.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void HandleTestProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            await ApplyAndSaveAsync();
            if (selectedProfile == null)
            {
                return;
            }

            var image = testImage ?? throw new InvalidOperationException(
                "Open a book and select an image before testing this profile.");
            TestButton.IsEnabled = false;
            StatusTextBlock.Text = "Generating test description...";
            var session = await descriptions.CreateSessionAsync(selectedProfile.Id, CancellationToken.None);
            var book = new Book("test", "Vision profile test", string.Empty, [], null, [], [image]);
            var result = await session.DescribeAsync(book, image, false, CancellationToken.None);
            StatusTextBlock.Text = result.Description;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void HandleDefaultProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isLoading || DefaultProfileComboBox.SelectedValue is not string profileId)
        {
            return;
        }

        settings.DefaultProfileId = profileId;
        try
        {
            await settingsStore.SaveAsync(settings, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void HandleClose(object sender, RoutedEventArgs e) => Close();

    private async Task ApplyAndSaveAsync()
    {
        if (selectedProfile == null)
        {
            throw new InvalidOperationException("Select or add a profile first.");
        }

        var oldId = selectedProfile.Id;
        var newId = ProfileIdTextBox.Text.Trim();
        if (settings.Profiles.Any(profile =>
            !ReferenceEquals(profile, selectedProfile)
            && string.Equals(profile.Id, newId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Vision profile ID '{newId}' already exists.");
        }

        selectedProfile.Id = newId;
        selectedProfile.DisplayName = DisplayNameTextBox.Text.Trim();
        selectedProfile.BaseUrl = BaseUrlTextBox.Text.Trim();
        selectedProfile.Model = ModelTextBox.Text.Trim();
        selectedProfile.Prompt = PromptTextBox.Text.Trim();
        selectedProfile.Temperature = ParseDouble(TemperatureTextBox.Text, "Temperature");
        selectedProfile.MaximumOutputTokens = ParseInteger(MaximumOutputTokensTextBox.Text, "Maximum output tokens");
        selectedProfile.TimeoutSeconds = ParseInteger(TimeoutTextBox.Text, "Timeout");
        selectedProfile.MaximumImageDimension = ParseInteger(MaximumImageDimensionTextBox.Text, "Maximum image dimension");
        selectedProfile.MaximumImageBytes = ParseInteger(MaximumImageBytesTextBox.Text, "Maximum image bytes");
        selectedProfile.MaximumContextCharacters = ParseInteger(MaximumContextCharactersTextBox.Text, "Maximum context characters");
        selectedProfile.ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariableTextBox.Text)
            ? null
            : ApiKeyEnvironmentVariableTextBox.Text.Trim();
        if (string.Equals(settings.DefaultProfileId, oldId, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultProfileId = selectedProfile.Id;
        }

        await settingsStore.SaveAsync(settings, CancellationToken.None);
        RefreshLists(selectedProfile);
    }

    private void RefreshLists(OpenAiCompatibleVisionProfile? profileToSelect = null)
    {
        isLoading = true;
        try
        {
            ProfileList.ItemsSource = null;
            ProfileList.ItemsSource = settings.Profiles;
            ProfileList.SelectedItem = profileToSelect;
            DefaultProfileComboBox.ItemsSource = settings.Profiles;
            DefaultProfileComboBox.SelectedValue = settings.DefaultProfileId;
        }
        finally
        {
            isLoading = false;
        }
    }

    private void ClearEditor()
    {
        ProfileEditor.IsEnabled = false;
        foreach (var textBox in new[]
        {
            ProfileIdTextBox,
            DisplayNameTextBox,
            BaseUrlTextBox,
            ModelTextBox,
            PromptTextBox,
            TemperatureTextBox,
            MaximumOutputTokensTextBox,
            TimeoutTextBox,
            MaximumImageDimensionTextBox,
            MaximumImageBytesTextBox,
            MaximumContextCharactersTextBox,
            ApiKeyEnvironmentVariableTextBox
        })
        {
            textBox.Clear();
        }
    }

    private void ShowError(Exception exception)
    {
        StatusTextBlock.Text = exception.Message;
        _ = MessageBox.Show(exception.Message, "Vision settings error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static int ParseInteger(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{name} must be an integer.");

    private static double ParseDouble(string value, string name) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{name} must be a number.");
}
