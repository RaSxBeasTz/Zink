using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Input;
using System;
using System.IO;
using System.Linq;
using Windows.ApplicationModel;
using Windows.Services.Store;
using Windows.Storage;
using Windows.System;
using WinRT.Interop; // <-- needed for WindowNative & InitializeWithWindow
using Zink.Services;
using Zink.Services.Recording;

namespace Zink.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private readonly StoreContext _store;
        private bool _isLoadingStartupState;
        private bool _isLoadingReplayState;
        private bool _isLoadingHotkeyClipRecordingState;
        private bool _isLoadingDiagnosticLogState;
        private bool _isLoadingDiscordRichPresenceState;
        private bool _isLoadingLanguage;
        private string? _latestHealthReportPath;
        private string? _latestSupportBundlePath;

        private const string BackgroundRunSettingKey = "ZinkBackgroundRunEnabled";
        private const string BackgroundNotificationsSettingKey = "ZinkBackgroundNotificationsEnabled";
        private const string LowResourceBackgroundSettingKey = "ZinkLowResourceBackgroundEnabled";

        public SettingsPage()
        {
            this.InitializeComponent();

            _isLoadingLanguage = true;
            LanguageComboBox.ItemsSource = LocalizationService.Languages;
            LanguageComboBox.SelectedItem = LocalizationService.Languages.FirstOrDefault(x => x.Tag == LocalizationService.Preference)
                ?? LocalizationService.Languages[0];
            UpdateLanguageStatus();
            _isLoadingLanguage = false;

            // Create the StoreContext
            _store = StoreContext.GetDefault();

            // IMPORTANT: attach the StoreContext to your main WinUI 3 window
            try
            {
                // App.MainWindow is your main WinUI 3 window (you already use it elsewhere)
                IntPtr hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                InitializeWithWindow.Initialize(_store, hwnd);
            }
            catch (Exception ex)
            {
                // Optional: show a one-time init error (won’t stop the app)
                if (StatusText != null)
                {
                    StatusText.Text = $"Error initialising update system: {ex.Message}";
                }
            }

        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadBackgroundSettingState();
            LoadReplaySettingState();
            LoadHotkeyClipRecordingSettingState();
            LoadDiagnosticLogSettingState();
            LoadDiscordRichPresenceSettingState();
            _ = LoadStartupTaskStateAsync();
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingLanguage || LanguageComboBox.SelectedItem is not AppLanguage language) return;
            LocalizationService.SetLanguage(language.Tag);
            UpdateLanguageStatus();
        }

        private void LanguageSearchButton_Click(object sender, RoutedEventArgs e) => FilterLanguages();

        private void LanguageSearchBox_TextChanged(object sender, TextChangedEventArgs e) => FilterLanguages(false);

        private void LanguageSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            FilterLanguages();
            e.Handled = true;
        }

        private void FilterLanguages(bool openResults = true)
        {
            var query = LanguageSearchBox.Text?.Trim() ?? "";
            var filtered = string.IsNullOrEmpty(query)
                ? LocalizationService.Languages.ToList()
                : LocalizationService.Languages
                    .Where(language => LocalizationService.LanguageMatchesQuery(language, query))
                    .ToList();

            _isLoadingLanguage = true;
            LanguageComboBox.ItemsSource = filtered;
            LanguageComboBox.SelectedItem = filtered.FirstOrDefault(language => language.Tag == LocalizationService.Preference);
            _isLoadingLanguage = false;

            LanguageStatusText.Text = filtered.Count == 0
                ? "No languages match your search."
                : $"{filtered.Count} language(s) found.";
            if (openResults) LanguageComboBox.IsDropDownOpen = filtered.Count > 0;
        }

        private void UpdateLanguageStatus()
        {
            var active = LocalizationService.Languages.FirstOrDefault(x => x.Tag == LocalizationService.CurrentLanguageTag)?.DisplayName ?? "English";
            LanguageStatusText.Text = LocalizationService.Preference == LocalizationService.SystemLanguageTag
                ? $"Using Windows language ({active})."
                : $"Using {active}.";
        }

        private async System.Threading.Tasks.Task LoadStartupTaskStateAsync()
        {
            _isLoadingStartupState = true;

            try
            {
                bool backgroundRunEnabled = GetBackgroundRunEnabledSetting();

                var startupTask = await StartupTask.GetAsync("ZinkStartupTask");

                switch (startupTask.State)
                {
                    case StartupTaskState.Enabled:
                    case StartupTaskState.EnabledByPolicy:
                        StartupToggle.IsChecked = backgroundRunEnabled;
                        UpdateStartupToggleVisual(backgroundRunEnabled, false);
                        StartupStatusText.Text = backgroundRunEnabled
                            ? "Zink background startup is enabled."
                            : "Windows startup is enabled, but background startup is turned off in app settings.";
                        break;

                    case StartupTaskState.Disabled:
                        StartupToggle.IsChecked = false;
                        UpdateStartupToggleVisual(false, false);
                        StartupStatusText.Text = "Zink background startup is disabled.";
                        break;

                    case StartupTaskState.DisabledByUser:
                        StartupToggle.IsChecked = false;
                        UpdateStartupToggleVisual(false, false);
                        StartupStatusText.Text = "Startup is disabled by the user in Windows.";
                        break;

                    case StartupTaskState.DisabledByPolicy:
                        StartupToggle.IsChecked = false;
                        UpdateStartupToggleVisual(false, false);
                        StartupStatusText.Text = "Startup is disabled by system policy.";
                        break;

                    default:
                        StartupToggle.IsChecked = false;
                        UpdateStartupToggleVisual(false, false);
                        StartupStatusText.Text = $"Startup status: {startupTask.State}";
                        break;
                }
            }
            catch (Exception ex)
            {
                StartupToggle.IsChecked = false;
                UpdateStartupToggleVisual(false, false);
                StartupStatusText.Text = $"Error loading startup setting: {ex.Message}";
            }
            finally
            {
                _isLoadingStartupState = false;
            }
        }

        private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingStartupState)
                return;

            bool enabled = StartupToggle.IsChecked == true;
            UpdateStartupToggleVisual(enabled, true);
            StartupStatusText.Text = enabled
                ? "Turning background startup on..."
                : "Turning background startup off...";
            _ = ApplyStartupToggleAsync(enabled);
        }

        private async System.Threading.Tasks.Task ApplyStartupToggleAsync(bool enabled)
        {
            await System.Threading.Tasks.Task.Yield();

            try
            {
                var startupTask = await StartupTask.GetAsync("ZinkStartupTask");

                if (enabled)
                {
                    SetBackgroundRunEnabledSetting(true);

                    var newState = await startupTask.RequestEnableAsync();

                    switch (newState)
                    {
                        case StartupTaskState.Enabled:
                        case StartupTaskState.EnabledByPolicy:
                            StartupToggle.IsChecked = true;
                            UpdateStartupToggleVisual(true, false);
                            StartupStatusText.Text = "Zink background startup is enabled.";
                            break;

                        case StartupTaskState.DisabledByUser:
                            SetBackgroundRunEnabledSetting(false);
                            StartupToggle.IsChecked = false;
                            UpdateStartupToggleVisual(false, true);
                            StartupStatusText.Text = "Startup is disabled by the user in Windows. Re-enable it in Task Manager > Startup apps.";
                            break;

                        case StartupTaskState.DisabledByPolicy:
                            SetBackgroundRunEnabledSetting(false);
                            StartupToggle.IsChecked = false;
                            UpdateStartupToggleVisual(false, true);
                            StartupStatusText.Text = "Startup is disabled by system policy.";
                            break;

                        default:
                            SetBackgroundRunEnabledSetting(false);
                            StartupToggle.IsChecked = false;
                            UpdateStartupToggleVisual(false, true);
                            StartupStatusText.Text = $"Unable to enable startup. Current state: {newState}";
                            break;
                    }
                }
                else
                {
                    SetBackgroundRunEnabledSetting(false);
                    startupTask.Disable();
                    StartupToggle.IsChecked = false;
                    UpdateStartupToggleVisual(false, false);
                    StartupStatusText.Text = "Zink background startup is disabled.";
                }

                await LoadStartupTaskStateAsync();
            }
            catch (Exception ex)
            {
                StartupStatusText.Text = $"Error changing startup setting: {ex.Message}";
            }
        }

        private void LoadReplaySettingState()
        {
            _isLoadingReplayState = true;

            try
            {
                bool enabled = RecordingPreferences.IsGamingBackgroundReplayEnabled;
                BackgroundReplayOffToggle.IsChecked = enabled;
                UpdateBackgroundReplayToggleVisual(enabled, false);
                BackgroundReplayStatusText.Text = enabled
                    ? "Background replay buffer is allowed for gaming clips."
                    : "Background replay buffer is off and will not start automatically.";
            }
            finally
            {
                _isLoadingReplayState = false;
            }
        }

        private void LoadBackgroundSettingState()
        {
            bool notificationsEnabled = GetBoolSetting(BackgroundNotificationsSettingKey, true);
            BackgroundNotificationsToggle.IsChecked = notificationsEnabled;
            UpdateBackgroundNotificationsToggleVisual(notificationsEnabled, false);
            BackgroundNotificationsStatusText.Text = notificationsEnabled
                ? "Background notifications are enabled."
                : "Background notifications are off.";

            bool lowResourceEnabled = GetBoolSetting(LowResourceBackgroundSettingKey, false);
            LowResourceBackgroundToggle.IsChecked = lowResourceEnabled;
            UpdateLowResourceBackgroundToggleVisual(lowResourceEnabled, false);
            LowResourceBackgroundStatusText.Text = lowResourceEnabled
                ? "Zink will prefer lower memory and CPU use in the background."
                : "Zink will use normal background resource behavior.";
        }

        private void LoadHotkeyClipRecordingSettingState()
        {
            _isLoadingHotkeyClipRecordingState = true;

            try
            {
                bool enabled = RecordingPreferences.IsHotkeyGameClipRecordingEnabled;
                HotkeyClipRecordingToggle.IsChecked = enabled;
                UpdateHotkeyClipRecordingToggleVisual(enabled, false);
                HotkeyClipRecordingStatusText.Text = enabled
                    ? $"The clip hotkey can save the last {RecordingPreferences.BackgroundReplayDurationSeconds} seconds."
                    : "The clip hotkey is off and will not save game clips.";
            }
            finally
            {
                _isLoadingHotkeyClipRecordingState = false;
            }
        }


        private void BackgroundNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = BackgroundNotificationsToggle.IsChecked == true;
            UpdateBackgroundNotificationsToggleVisual(enabled, true);
            BackgroundNotificationsStatusText.Text = enabled
                ? "Background notifications are enabled."
                : "Background notifications are off.";

            DispatcherQueue.TryEnqueue(() =>
            {
                ApplicationData.Current.LocalSettings.Values[BackgroundNotificationsSettingKey] = enabled;
                StatusText.Text = "Background notification setting saved.";
            });
        }

        private void LowResourceBackgroundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = LowResourceBackgroundToggle.IsChecked == true;
            UpdateLowResourceBackgroundToggleVisual(enabled, true);
            LowResourceBackgroundStatusText.Text = enabled
                ? "Zink will prefer lower memory and CPU use in the background."
                : "Zink will use normal background resource behavior.";

            DispatcherQueue.TryEnqueue(() =>
            {
                ApplicationData.Current.LocalSettings.Values[LowResourceBackgroundSettingKey] = enabled;
                StatusText.Text = "Low resource background setting saved.";
            });
        }

        private void BackgroundReplayOffToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingReplayState)
                return;

            bool enabled = BackgroundReplayOffToggle.IsChecked == true;
            UpdateBackgroundReplayToggleVisual(enabled, true);
            BackgroundReplayStatusText.Text = enabled
                ? "Background replay buffer is allowed for gaming clips."
                : "Background replay buffer is off and will not start automatically.";
            StatusText.Text = "Background replay setting saved.";
            _ = ApplyBackgroundReplayToggleAsync(enabled);
        }

        private void BackgroundRecordingSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(BackgroundRecordingSettingsPage));
        }

        private void HotkeyClipRecordingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingHotkeyClipRecordingState)
                return;

            bool enabled = HotkeyClipRecordingToggle.IsChecked == true;
            UpdateHotkeyClipRecordingToggleVisual(enabled, true);
            HotkeyClipRecordingStatusText.Text = enabled
                ? $"The clip hotkey can save the last {RecordingPreferences.BackgroundReplayDurationSeconds} seconds."
                : "The clip hotkey is off and will not save game clips.";
            RecordingPreferences.SetHotkeyGameClipRecordingEnabled(enabled);
            StatusText.Text = "Game clip hotkey setting saved.";
        }

        private async System.Threading.Tasks.Task ApplyBackgroundReplayToggleAsync(bool enabled)
        {
            await System.Threading.Tasks.Task.Yield();

            RecordingPreferences.SetGamingBackgroundReplayEnabled(enabled);

            if (enabled)
            {
                BackgroundReplayStatusText.Text = "Background replay buffer is allowed for gaming clips.";
                StatusText.Text = "Background replay setting saved.";
                return;
            }

            BackgroundReplayStatusText.Text = "Background replay buffer is off and will not start automatically.";

            try
            {
                var service = ManualRecordingService.Instance;
                if (service.IsReplayBufferRunning)
                {
                    await service.StopAsync();

                    if (Application.Current is App app)
                    {
                        app.NotifyReplayBufferStopped();
                    }

                    StatusText.Text = "Background replay buffer stopped and setting saved.";
                }
                else
                {
                    StatusText.Text = "Background replay setting saved.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Background replay setting saved, but stopping the active buffer failed: {ex.Message}";
            }
        }

        private async void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            ResetDefaultsButton.IsEnabled = false;

            try
            {
                StatusText.Text = "Resetting settings to defaults...";

                SetBackgroundRunEnabledSetting(true);
                RecordingPreferences.SetGamingBackgroundReplayEnabled(false);
                RecordingPreferences.SetHotkeyGameClipRecordingEnabled(true);
                DiagnosticLogService.SetEnabled(true);
                DiscordPresenceService.Instance.SetEnabled(true);
                LoadReplaySettingState();
                LoadHotkeyClipRecordingSettingState();
                LoadDiagnosticLogSettingState();
                LoadDiscordRichPresenceSettingState();

                var startupTask = await StartupTask.GetAsync("ZinkStartupTask");
                var newState = await startupTask.RequestEnableAsync();

                switch (newState)
                {
                    case StartupTaskState.Enabled:
                    case StartupTaskState.EnabledByPolicy:
                        StartupToggle.IsChecked = true;
                        UpdateStartupToggleVisual(true, false);
                        StartupStatusText.Text = "Zink background startup is enabled.";
                        StatusText.Text = "Settings reset to defaults.";
                        break;

                    case StartupTaskState.DisabledByUser:
                        StartupToggle.IsChecked = false;
                        UpdateStartupToggleVisual(false, false);
                        StartupStatusText.Text = "Startup is disabled by the user in Windows. Re-enable it in Task Manager > Startup apps.";
                        StatusText.Text = "Startup was reset, but Windows is still blocking startup.";
                        break;

                    case StartupTaskState.DisabledByPolicy:
                        StartupToggle.IsChecked = false;
                        UpdateStartupToggleVisual(false, false);
                        StartupStatusText.Text = "Startup is disabled by system policy.";
                        StatusText.Text = "Startup was reset, but startup is blocked by system policy.";
                        break;

                    default:
                        StartupToggle.IsChecked = false;
                        UpdateStartupToggleVisual(false, false);
                        StartupStatusText.Text = $"Unable to enable startup. Current state: {newState}";
                        StatusText.Text = "Defaults were partially reset, but Windows startup could not be enabled.";
                        break;
                }

                await LoadStartupTaskStateAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error resetting settings: {ex.Message}";
            }
            finally
            {
                ResetDefaultsButton.IsEnabled = true;
            }
        }

        private static bool GetBackgroundRunEnabledSetting()
        {
            return GetBoolSetting(BackgroundRunSettingKey, true);
        }

        private static bool GetBoolSetting(string key, bool fallback)
        {
            try
            {
                object value = ApplicationData.Current.LocalSettings.Values[key];
                if (value is bool boolValue)
                    return boolValue;
            }
            catch
            {
            }

            return fallback;
        }

        private static void SetBackgroundRunEnabledSetting(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[BackgroundRunSettingKey] = enabled;
        }

        private void LoadDiagnosticLogSettingState()
        {
            _isLoadingDiagnosticLogState = true;

            try
            {
                DiagnosticLogToggle.IsChecked = DiagnosticLogService.GetEnabledSetting();
                UpdateDiagnosticLogToggleVisual(DiagnosticLogToggle.IsChecked == true, false);
                DiagnosticLogStatusText.Text = $"Logging to {DiagnosticLogService.CurrentLogPath}";
                _latestHealthReportPath = Path.Combine(
                    DiagnosticLogService.LogDirectoryPath,
                    $"zink-health-{DiagnosticLogService.DeviceName}-latest.txt");
                _latestSupportBundlePath = FindLatestSupportBundlePath();

                if (File.Exists(_latestHealthReportPath))
                    HealthCheckStatusText.Text = $"Latest report: {_latestHealthReportPath}";
            }
            finally
            {
                _isLoadingDiagnosticLogState = false;
            }
        }

        private void DiagnosticLogToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingDiagnosticLogState)
                return;

            if (DiagnosticLogToggle.IsChecked != true)
                DiagnosticLogToggle.IsChecked = true;

            UpdateDiagnosticLogToggleVisual(true, true);
            DiagnosticLogStatusText.Text = $"Logging to {DiagnosticLogService.CurrentLogPath}";
            StatusText.Text = "Diagnostic file logging stays enabled while stream diagnostics are active.";

            DispatcherQueue.TryEnqueue(() =>
            {
                DiagnosticLogService.SetEnabled(true);
                LoadDiagnosticLogSettingState();
                StatusText.Text = "Diagnostic file logging stays enabled while stream diagnostics are active.";
            });
        }

        private void UpdateStartupToggleVisual(bool enabled, bool animate) =>
            UpdateSlidingToggleVisual(
                StartupToggleTrack,
                StartupToggleKnobTransform,
                StartupToggleOnLabel,
                StartupToggleOffLabel,
                enabled,
                animate);

        private void UpdateBackgroundNotificationsToggleVisual(bool enabled, bool animate) =>
            UpdateSlidingToggleVisual(
                BackgroundNotificationsToggleTrack,
                BackgroundNotificationsToggleKnobTransform,
                BackgroundNotificationsToggleOnLabel,
                BackgroundNotificationsToggleOffLabel,
                enabled,
                animate);

        private void UpdateLowResourceBackgroundToggleVisual(bool enabled, bool animate) =>
            UpdateSlidingToggleVisual(
                LowResourceBackgroundToggleTrack,
                LowResourceBackgroundToggleKnobTransform,
                LowResourceBackgroundToggleOnLabel,
                LowResourceBackgroundToggleOffLabel,
                enabled,
                animate);

        private void UpdateBackgroundReplayToggleVisual(bool enabled, bool animate) =>
            UpdateSlidingToggleVisual(
                BackgroundReplayToggleTrack,
                BackgroundReplayToggleKnobTransform,
                BackgroundReplayToggleOnLabel,
                BackgroundReplayToggleOffLabel,
                enabled,
                animate);

        private void UpdateHotkeyClipRecordingToggleVisual(bool enabled, bool animate) =>
            UpdateSlidingToggleVisual(
                HotkeyClipRecordingToggleTrack,
                HotkeyClipRecordingToggleKnobTransform,
                HotkeyClipRecordingToggleOnLabel,
                HotkeyClipRecordingToggleOffLabel,
                enabled,
                animate);

        private void UpdateDiagnosticLogToggleVisual(bool enabled, bool animate) =>
            UpdateSlidingToggleVisual(
                DiagnosticLogToggleTrack,
                DiagnosticLogToggleKnobTransform,
                DiagnosticLogToggleOnLabel,
                DiagnosticLogToggleOffLabel,
                enabled,
                animate);

        private void LoadDiscordRichPresenceSettingState()
        {
            _isLoadingDiscordRichPresenceState = true;

            try
            {
                bool enabled = DiscordPresenceService.GetEnabledSetting();
                DiscordRichPresenceToggle.IsChecked = enabled;
                UpdateDiscordRichPresenceToggleVisual(enabled, false);
                DiscordRichPresenceStatusText.Text = enabled
                    ? "Zink activity is visible on your Discord profile."
                    : "Zink activity is hidden from your Discord profile.";
            }
            finally
            {
                _isLoadingDiscordRichPresenceState = false;
            }
        }

        private void DiscordRichPresenceToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingDiscordRichPresenceState)
                return;

            bool enabled = DiscordRichPresenceToggle.IsChecked == true;
            UpdateDiscordRichPresenceToggleVisual(enabled, true);
            DiscordRichPresenceStatusText.Text = enabled
                ? "Zink activity is visible on your Discord profile."
                : "Zink activity is hidden from your Discord profile.";
            DiscordPresenceService.Instance.SetEnabled(enabled);
            StatusText.Text = "Discord Rich Presence setting saved.";
        }

        private void UpdateDiscordRichPresenceToggleVisual(bool enabled, bool animate) =>
            UpdateSlidingToggleVisual(
                DiscordRichPresenceToggleTrack,
                DiscordRichPresenceToggleKnobTransform,
                DiscordRichPresenceToggleOnLabel,
                DiscordRichPresenceToggleOffLabel,
                enabled,
                animate);

        private static void UpdateSlidingToggleVisual(
            Border track,
            TranslateTransform knobTransform,
            TextBlock onLabel,
            TextBlock offLabel,
            bool enabled,
            bool animate)
        {
            const double offX = 0;
            const double onX = 86;

            var targetX = enabled ? onX : offX;
            track.Background = new SolidColorBrush(enabled
                ? global::Windows.UI.Color.FromArgb(255, 25, 91, 71)
                : global::Windows.UI.Color.FromArgb(255, 116, 43, 55));
            track.BorderBrush = new SolidColorBrush(enabled
                ? global::Windows.UI.Color.FromArgb(255, 112, 242, 191)
                : global::Windows.UI.Color.FromArgb(255, 255, 138, 160));

            onLabel.Opacity = enabled ? 1 : 0;
            offLabel.Opacity = enabled ? 0 : 1;

            if (!animate)
            {
                knobTransform.X = targetX;
                return;
            }

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                From = knobTransform.X,
                To = targetX,
                Duration = new Duration(TimeSpan.FromMilliseconds(170)),
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(animation, knobTransform);
            Storyboard.SetTargetProperty(animation, "X");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private async void OpenDiagnosticLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            OpenDiagnosticLogFolderButton.IsEnabled = false;

            try
            {
                var folder = await DiagnosticLogService.GetLogFolderAsync();
                await Launcher.LaunchFolderAsync(folder);
                StatusText.Text = "Diagnostic log folder opened.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error opening log folder: {ex.Message}";
            }
            finally
            {
                OpenDiagnosticLogFolderButton.IsEnabled = true;
            }
        }

        private void ClearDiagnosticLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DiagnosticLogService.ClearCurrentLog();
                LoadDiagnosticLogSettingState();
                StatusText.Text = "Diagnostic log cleared.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error clearing diagnostic log: {ex.Message}";
            }
        }

        private async void RunHealthCheckButton_Click(object sender, RoutedEventArgs e)
        {
            RunHealthCheckButton.IsEnabled = false;
            OpenHealthReportButton.IsEnabled = false;
            HealthCheckStatusText.Text = "Running Zink health check...";
            StatusText.Text = "Running Zink health check...";

            try
            {
                var report = await ZinkHealthCheckService.RunAsync();
                _latestHealthReportPath = report.ReportPath;
                _latestSupportBundlePath = report.BundlePath;
                HealthCheckStatusText.Text = $"Health check complete: {report.Summary}. Report: {report.ReportPath}. Bundle: {report.BundlePath}";
                StatusText.Text = report.Failed == 0
                    ? "Health check complete."
                    : $"Health check found {report.Failed} failed check(s).";
            }
            catch (Exception ex)
            {
                HealthCheckStatusText.Text = $"Health check failed: {ex.Message}";
                StatusText.Text = $"Health check failed: {ex.Message}";
            }
            finally
            {
                RunHealthCheckButton.IsEnabled = true;
                OpenHealthReportButton.IsEnabled = true;
            }
        }

        private async void OpenHealthReportButton_Click(object sender, RoutedEventArgs e)
        {
            OpenHealthReportButton.IsEnabled = false;

            try
            {
                if (string.IsNullOrWhiteSpace(_latestHealthReportPath) || !File.Exists(_latestHealthReportPath))
                {
                    _latestHealthReportPath = Path.Combine(
                        DiagnosticLogService.LogDirectoryPath,
                        $"zink-health-{DiagnosticLogService.DeviceName}-latest.txt");
                }

                if (!File.Exists(_latestHealthReportPath))
                {
                    HealthCheckStatusText.Text = "No health report exists yet. Run a health check first.";
                    StatusText.Text = "No health report exists yet.";
                    return;
                }

                var file = await StorageFile.GetFileFromPathAsync(_latestHealthReportPath);
                await Launcher.LaunchFileAsync(file);
                StatusText.Text = "Health report opened.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error opening health report: {ex.Message}";
            }
            finally
            {
                OpenHealthReportButton.IsEnabled = true;
            }
        }

        private async void UploadHealthReportButton_Click(object sender, RoutedEventArgs e)
        {
            UploadHealthReportButton.IsEnabled = false;
            HealthCheckStatusText.Text = "Uploading diagnostics support bundle...";
            StatusText.Text = "Uploading diagnostics support bundle...";

            try
            {
                if (string.IsNullOrWhiteSpace(_latestSupportBundlePath) || !File.Exists(_latestSupportBundlePath))
                    _latestSupportBundlePath = FindLatestSupportBundlePath();

                if (string.IsNullOrWhiteSpace(_latestSupportBundlePath) || !File.Exists(_latestSupportBundlePath))
                {
                    var report = await ZinkHealthCheckService.RunAsync();
                    _latestHealthReportPath = report.ReportPath;
                    _latestSupportBundlePath = report.BundlePath;
                }

                var result = await DiagnosticsUploadService.UploadSupportBundleAsync(_latestSupportBundlePath);
                HealthCheckStatusText.Text = $"Diagnostics uploaded. Report id: {result.ReportId}. Download: {result.DownloadUrl}";
                StatusText.Text = "Diagnostics uploaded.";
            }
            catch (Exception ex)
            {
                HealthCheckStatusText.Text = $"Diagnostics upload failed: {ex.Message}";
                StatusText.Text = $"Diagnostics upload failed: {ex.Message}";
            }
            finally
            {
                UploadHealthReportButton.IsEnabled = true;
            }
        }

        private static string? FindLatestSupportBundlePath()
        {
            try
            {
                var directory = DiagnosticLogService.LogDirectoryPath;
                if (!Directory.Exists(directory))
                    return null;

                var pattern = $"zink-support-{DiagnosticLogService.DeviceName}-*.zip";
                string? latestPath = null;
                DateTime latestWrite = DateTime.MinValue;

                foreach (var path in Directory.EnumerateFiles(directory, pattern))
                {
                    var write = File.GetLastWriteTimeUtc(path);
                    if (write <= latestWrite)
                        continue;

                    latestWrite = write;
                    latestPath = path;
                }

                return latestPath;
            }
            catch
            {
                return null;
            }
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            CheckForUpdatesButton.IsEnabled = false;
            StatusText.Text = "Checking for updates…";

            try
            {
                // 1) get list of available updates
                var updates = await _store.GetAppAndOptionalStorePackageUpdatesAsync();

                if (updates.Count == 0)
                {
                    StatusText.Text = "Your app is up to date.";
                }
                else
                {
                    NotificationService.Instance.ShowAppUpdateReady();
                    StatusText.Text = $"{updates.Count} update(s) available. Downloading…";

                    // 2) download & install them
                    var result = await _store.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);

                    // 3) examine the result
                    if (result.OverallState == StorePackageUpdateState.Completed)
                    {
                        StatusText.Text = "Update installed. Restart your app to apply changes.";
                    }
                    else
                    {
                        StatusText.Text = $"Update failed: {result.OverallState}";
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error checking for updates: {ex.Message}";
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
            }
        }
    }
}
