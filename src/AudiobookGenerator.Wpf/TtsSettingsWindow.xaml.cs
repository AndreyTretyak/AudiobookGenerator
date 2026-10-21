using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

using YewCone.AudiobookGenerator.Core;

namespace YewCone.AudiobookGenerator.Wpf;

public partial class TtsSettingsWindow : Window
{
    private readonly ITtsSettingsStore settingsStore;
    private readonly IAudioSynthesizer synthesizer;
    private readonly IAudioPreviewService preview;
    private TtsSettings settings = new();
    private OpenAiCompatibleTtsProfile? selectedProfile;
    private CancellationTokenSource? testCancellation;
    private bool isLoading;

    public TtsSettingsWindow(
        ITtsSettingsStore settingsStoreInstance,
        IAudioSynthesizer audioSynthesizer,
        IAudioPreviewService previewService)
    {
        settingsStore = settingsStoreInstance;
        synthesizer = audioSynthesizer;
        preview = previewService;
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

    private void HandleClosed(object? sender, EventArgs e)
    {
        testCancellation?.Cancel();
        testCancellation?.Dispose();
        preview.Stop();
    }

    private void HandleAddProfile(object sender, RoutedEventArgs e)
    {
        var suffix = 1;
        var id = "local-tts";
        while (settings.OpenAiCompatibleProfiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            id = $"local-tts-{++suffix}";
        }

        var profile = new OpenAiCompatibleTtsProfile
        {
            Id = id,
            DisplayName = "Local TTS",
            Model = "kokoro",
            Voices = [new ConfiguredSpeechVoice { Id = "af_heart", DisplayName = "Heart", Culture = "en-US" }]
        };
        settings.OpenAiCompatibleProfiles.Add(profile);
        RefreshLists(profile);
    }

    private async void HandleRemoveProfile(object sender, RoutedEventArgs e)
    {
        if (selectedProfile == null)
        {
            return;
        }

        if (MessageBox.Show(
                $"Remove TTS profile '{selectedProfile.DisplayName}'?",
                "Remove TTS profile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var removedId = selectedProfile.Id;
        _ = settings.OpenAiCompatibleProfiles.Remove(selectedProfile);
        selectedProfile = null;
        if (string.Equals(settings.DefaultProviderId, removedId, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultProviderId = TtsSettings.WindowsProviderId;
        }

        try
        {
            await settingsStore.SaveAsync(settings, CancellationToken.None);
            RefreshLists();
            ClearEditor();
            StatusTextBlock.Text = "Profile removed.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void HandleProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedProfile = ProfileList.SelectedItem as OpenAiCompatibleTtsProfile;
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
        VoicesTextBox.Text = TtsVoiceListCodec.Format(selectedProfile.Voices);
        SpeedTextBox.Text = selectedProfile.Speed.ToString(CultureInfo.InvariantCulture);
        MaximumInputTextBox.Text = selectedProfile.MaximumInputCharacters.ToString(CultureInfo.InvariantCulture);
        TimeoutTextBox.Text = selectedProfile.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ApiKeyEnvironmentVariableTextBox.Text = selectedProfile.ApiKeyEnvironmentVariable ?? string.Empty;
    }

    private async void HandleSaveProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            await ApplyAndSaveAsync();
            StatusTextBlock.Text = "TTS profile saved.";
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

            var voices = await synthesizer.GetVoicesAsync(selectedProfile.Id, CancellationToken.None);
            var voice = voices.FirstOrDefault()
                ?? throw new InvalidOperationException("Configure at least one voice before testing this provider.");

            testCancellation?.Cancel();
            testCancellation?.Dispose();
            testCancellation = new CancellationTokenSource();
            TestButton.IsEnabled = false;
            StatusTextBlock.Text = "Requesting test speech...";
            await preview.PlayAsync(
                "This is a test of the configured audiobook voice.",
                voice,
                testCancellation.Token);
            StatusTextBlock.Text = $"Playing {voice.Name}.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Test cancelled.";
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

    private void HandleStopPreview(object sender, RoutedEventArgs e)
    {
        testCancellation?.Cancel();
        preview.Stop();
        StatusTextBlock.Text = "Preview stopped.";
    }

    private async void HandleDefaultProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isLoading || DefaultProviderComboBox.SelectedValue is not string providerId)
        {
            return;
        }

        settings.DefaultProviderId = providerId;
        try
        {
            await settingsStore.SaveAsync(settings, CancellationToken.None);
            StatusTextBlock.Text = "Default provider saved.";
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
        if (settings.OpenAiCompatibleProfiles.Any(profile =>
                !ReferenceEquals(profile, selectedProfile)
                && string.Equals(profile.Id, newId, StringComparison.OrdinalIgnoreCase))
            || string.Equals(newId, TtsSettings.WindowsProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"TTS provider ID '{newId}' already exists.");
        }

        selectedProfile.Id = newId;
        selectedProfile.DisplayName = DisplayNameTextBox.Text.Trim();
        selectedProfile.BaseUrl = BaseUrlTextBox.Text.Trim();
        selectedProfile.Model = ModelTextBox.Text.Trim();
        selectedProfile.Voices = TtsVoiceListCodec.Parse(VoicesTextBox.Text);
        selectedProfile.Speed = ParseDouble(SpeedTextBox.Text, "Speed");
        selectedProfile.MaximumInputCharacters = ParseInteger(MaximumInputTextBox.Text, "Maximum input characters");
        selectedProfile.TimeoutSeconds = ParseInteger(TimeoutTextBox.Text, "Timeout");
        selectedProfile.ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariableTextBox.Text)
            ? null
            : ApiKeyEnvironmentVariableTextBox.Text.Trim();

        if (string.Equals(settings.DefaultProviderId, oldId, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultProviderId = selectedProfile.Id;
        }

        await settingsStore.SaveAsync(settings, CancellationToken.None);
        RefreshLists(selectedProfile);
    }

    private void RefreshLists(OpenAiCompatibleTtsProfile? profileToSelect = null)
    {
        isLoading = true;
        try
        {
            ProfileList.ItemsSource = null;
            ProfileList.ItemsSource = settings.OpenAiCompatibleProfiles;
            ProfileList.SelectedItem = profileToSelect;

            var providers = new List<SpeechProviderInfo>
            {
                new(TtsSettings.WindowsProviderId, "Windows voices", SpeechProviderKind.Windows)
            };
            providers.AddRange(settings.OpenAiCompatibleProfiles.Select(profile =>
                new SpeechProviderInfo(profile.Id, profile.DisplayName, SpeechProviderKind.OpenAiCompatible)));
            DefaultProviderComboBox.ItemsSource = providers;
            DefaultProviderComboBox.SelectedValue = settings.DefaultProviderId;
        }
        finally
        {
            isLoading = false;
        }
    }

    private void ClearEditor()
    {
        ProfileEditor.IsEnabled = false;
        ProfileIdTextBox.Clear();
        DisplayNameTextBox.Clear();
        BaseUrlTextBox.Clear();
        ModelTextBox.Clear();
        VoicesTextBox.Clear();
        SpeedTextBox.Clear();
        MaximumInputTextBox.Clear();
        TimeoutTextBox.Clear();
        ApiKeyEnvironmentVariableTextBox.Clear();
    }

    private void ShowError(Exception exception)
    {
        StatusTextBlock.Text = exception.Message;
        _ = MessageBox.Show(exception.Message, "TTS settings error", MessageBoxButton.OK, MessageBoxImage.Error);
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
