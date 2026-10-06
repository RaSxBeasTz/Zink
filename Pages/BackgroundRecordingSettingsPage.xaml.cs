using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using Zink.Models;
using Zink.Services.Recording;

namespace Zink.Pages
{
    public sealed partial class BackgroundRecordingSettingsPage : Page
    {
        private bool _loading;

        public BackgroundRecordingSettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _loading = true;
            int selectedSeconds = RecordingPreferences.BackgroundReplayDurationSeconds;
            foreach (var item in DurationComboBox.Items)
            {
                if (item is ComboBoxItem option &&
                    int.TryParse(option.Tag?.ToString(), out int seconds) &&
                    seconds == selectedSeconds)
                {
                    DurationComboBox.SelectedItem = option;
                    break;
                }
            }
            SystemAudioToggle.IsOn = RecordingPreferences.IsBackgroundReplaySystemAudioEnabled;
            MicrophoneToggle.IsOn = RecordingPreferences.IsBackgroundReplayMicrophoneEnabled;
            SeparateTracksToggle.IsOn = RecordingPreferences.IsBackgroundReplaySeparateTracksEnabled;
            DesktopTrackCheckBox.IsChecked = RecordingPreferences.IsBackgroundReplaySeparateDesktopTrackEnabled;
            MicrophoneTrackCheckBox.IsChecked = RecordingPreferences.IsBackgroundReplaySeparateMicrophoneTrackEnabled;
            SelectAudioBitrate(RecordingPreferences.BackgroundReplayAudioBitrateKbps);
            UpdateSeparateTrackOptions();
            _loading = false;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(SettingsPage));
        }

        private async void DurationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || DurationComboBox.SelectedItem is not ComboBoxItem item ||
                !int.TryParse(item.Tag?.ToString(), out int seconds))
                return;

            RecordingPreferences.SetBackgroundReplayDurationSeconds(seconds);
            await RestartReplayIfRunningAsync();
            StatusText.Text = $"Background clip length saved: {item.Content}.";
        }

        private async void MicrophoneToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            RecordingPreferences.SetBackgroundReplayMicrophoneEnabled(MicrophoneToggle.IsOn);
            await RestartReplayIfRunningAsync();
            UpdateSeparateTrackOptions();
            StatusText.Text = MicrophoneToggle.IsOn
                ? "Microphone recording is on."
                : "Microphone recording is off.";
        }

        private async void SystemAudioToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            RecordingPreferences.SetBackgroundReplaySystemAudioEnabled(SystemAudioToggle.IsOn);
            await RestartReplayIfRunningAsync();
            UpdateSeparateTrackOptions();
            StatusText.Text = SystemAudioToggle.IsOn
                ? "Desktop and game audio recording is on."
                : "Desktop and game audio recording is off.";
        }

        private void SeparateTracksToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            RecordingPreferences.SetBackgroundReplaySeparateTracksEnabled(SeparateTracksToggle.IsOn);
            UpdateSeparateTrackOptions();
            StatusText.Text = SeparateTracksToggle.IsOn
                ? "Separate audio tracks will be added to new clips."
                : "New clips will use one combined compatibility track.";
        }

        private void DesktopTrackCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            RecordingPreferences.SetBackgroundReplaySeparateDesktopTrackEnabled(
                DesktopTrackCheckBox.IsChecked == true);
            StatusText.Text = "Separate desktop/game track preference saved.";
        }

        private void MicrophoneTrackCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            RecordingPreferences.SetBackgroundReplaySeparateMicrophoneTrackEnabled(
                MicrophoneTrackCheckBox.IsChecked == true);
            StatusText.Text = "Separate microphone track preference saved.";
        }

        private void AudioBitrateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || AudioBitrateComboBox.SelectedItem is not ComboBoxItem item ||
                !int.TryParse(item.Tag?.ToString(), out int bitrate))
                return;

            RecordingPreferences.SetBackgroundReplayAudioBitrateKbps(bitrate);
            StatusText.Text = $"Audio quality saved: {bitrate} kbps AAC.";
        }

        private void SelectAudioBitrate(int selectedBitrate)
        {
            foreach (var item in AudioBitrateComboBox.Items)
            {
                if (item is ComboBoxItem option &&
                    int.TryParse(option.Tag?.ToString(), out int bitrate) &&
                    bitrate == selectedBitrate)
                {
                    AudioBitrateComboBox.SelectedItem = option;
                    break;
                }
            }
        }

        private void UpdateSeparateTrackOptions()
        {
            DesktopTrackCheckBox.IsEnabled = SeparateTracksToggle.IsOn && SystemAudioToggle.IsOn;
            MicrophoneTrackCheckBox.IsEnabled = SeparateTracksToggle.IsOn && MicrophoneToggle.IsOn;
        }

        private static async Task RestartReplayIfRunningAsync()
        {
            var service = ManualRecordingService.Instance;
            if (!service.IsReplayBufferRunning)
                return;

            await service.StopAsync();
            await service.StartReplayAsync(new RecordingOptions
            {
                IncludeSystemAudio = RecordingPreferences.IsBackgroundReplaySystemAudioEnabled,
                IncludeMicrophone = RecordingPreferences.IsBackgroundReplayMicrophoneEnabled,
                RetrospectiveSeconds = RecordingPreferences.BackgroundReplayDurationSeconds
            });
        }
    }
}
