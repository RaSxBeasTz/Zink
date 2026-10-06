using System;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Microsoft.UI.Xaml;
using Zink.Models;

namespace Zink.Services.Recording
{
    public sealed class RecordingManager
    {
        public static RecordingManager Instance { get; } = new RecordingManager();

        private readonly ManualRecordingService _manualRecordingService = ManualRecordingService.Instance;
        private readonly BackgroundClipService _backgroundClipService = BackgroundClipService.Instance;

        private GraphicsCaptureItem? _captureItem;
        private RecordingOptions _options = new();

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? CaptureTargetChanged;
        public event EventHandler<bool>? ManualRecordingStateChanged;
        public event EventHandler<bool>? BackgroundRecordingStateChanged;

        public bool IsManualRecording => _manualRecordingService.IsManualRecording;
        public bool IsBackgroundClipping => _backgroundClipService.IsRunning || _manualRecordingService.IsReplayBufferRunning;

        public string LastStatus { get; private set; } = "Idle";
        public string LastCaptureTargetText { get; private set; } = "No capture target selected.";

        private RecordingManager()
        {
            _manualRecordingService.StatusChanged += (_, message) => PublishStatus(message);
            _backgroundClipService.StatusChanged += (_, message) => PublishStatus(message);
        }

        public async Task PickCaptureTargetAsync(IntPtr hwnd, bool includeSystemAudio, bool includeMicrophone, string? renderDeviceId, string? micDeviceId)
        {
            var item = await CapturePickerService.PickCaptureTargetAsync(hwnd);

            if (item is null)
            {
                PublishStatus("Capture selection cancelled.");
                return;
            }

            _captureItem = item;
            _options.IncludeSystemAudio = includeSystemAudio;
            _options.IncludeMicrophone = includeMicrophone;
            _options.SelectedRenderDeviceId = renderDeviceId;
            _options.SelectedMicDeviceId = micDeviceId;

            _manualRecordingService.SetCaptureItem(item, _options);
            _backgroundClipService.SetCaptureItem(item, _options);

            LastCaptureTargetText = $"Selected target: {item.DisplayName}";
            CaptureTargetChanged?.Invoke(this, LastCaptureTargetText);
            PublishStatus("Capture target selected.");
        }

        public async Task StartManualRecordingAsync(bool includeSystemAudio, bool includeMicrophone, string? renderDeviceId, string? micDeviceId)
        {
            if (_captureItem is null)
            {
                PublishStatus("Pick a display or window first.");
                return;
            }

            _options.IncludeSystemAudio = includeSystemAudio;
            _options.IncludeMicrophone = includeMicrophone;
            _options.SelectedRenderDeviceId = renderDeviceId;
            _options.SelectedMicDeviceId = micDeviceId;

            if (_manualRecordingService.IsManualRecording)
            {
                await StopManualRecordingAsync();
                return;
            }

            if (_backgroundClipService.IsRunning)
            {
                await _backgroundClipService.StopAsync();
                BackgroundRecordingStateChanged?.Invoke(this, false);
            }

            if (_manualRecordingService.IsReplayBufferRunning)
            {
                await _manualRecordingService.StopAsync();

                if (Microsoft.UI.Xaml.Application.Current is App app)
                {
                    app.NotifyReplayBufferStopped();
                }
            }

            _manualRecordingService.SetCaptureItem(_captureItem, _options);
            await _manualRecordingService.StartAsync();

            ManualRecordingStateChanged?.Invoke(this, _manualRecordingService.IsManualRecording);
            PublishStatus("Manual recording started.");
        }

        public async Task StopManualRecordingAsync()
        {
            await _manualRecordingService.StopAsync();
            ManualRecordingStateChanged?.Invoke(this, _manualRecordingService.IsManualRecording);

            if (_captureItem is not null && RecordingPreferences.IsGamingBackgroundReplayEnabled)
            {
                _manualRecordingService.SetCaptureItem(_captureItem, _options);
                await _manualRecordingService.StartReplayAsync(_captureItem, _options);

                if (Microsoft.UI.Xaml.Application.Current is App app)
                {
                    app.NotifyReplayBufferStarted();
                }

                BackgroundRecordingStateChanged?.Invoke(this, _manualRecordingService.IsReplayBufferRunning);
                PublishStatus("Manual recording stopped and replay buffer restarted.");
            }
            else
            {
                if (Microsoft.UI.Xaml.Application.Current is App app)
                {
                    app.NotifyReplayBufferStopped();
                }

                BackgroundRecordingStateChanged?.Invoke(this, false);
                PublishStatus("Manual recording stopped.");
            }
        }

        public async Task StartBackgroundClippingAsync(bool includeSystemAudio, bool includeMicrophone, string? renderDeviceId, string? micDeviceId)
        {
            if (!RecordingPreferences.IsGamingBackgroundReplayEnabled)
            {
                BackgroundRecordingStateChanged?.Invoke(this, false);
                PublishStatus("Background replay for gaming is turned off in Settings.");
                return;
            }

            if (_captureItem is null)
            {
                PublishStatus("Pick a display or window first.");
                return;
            }

            _options.IncludeSystemAudio = includeSystemAudio;
            _options.IncludeMicrophone = includeMicrophone;
            _options.SelectedRenderDeviceId = renderDeviceId;
            _options.SelectedMicDeviceId = micDeviceId;

            _backgroundClipService.SetCaptureItem(_captureItem, _options);
            await _backgroundClipService.StartAsync();
            BackgroundRecordingStateChanged?.Invoke(this, _backgroundClipService.IsRunning);
            PublishStatus("Background clipping started.");
        }

        public async Task StopBackgroundClippingAsync()
        {
            await _backgroundClipService.StopAsync();
            BackgroundRecordingStateChanged?.Invoke(this, _backgroundClipService.IsRunning);
            PublishStatus("Background clipping stopped.");
        }

        public async Task<string?> SaveLast45SecondsAsync()
            => await SaveLastReplayAsync(TimeSpan.FromSeconds(RecordingPreferences.BackgroundReplayDurationSeconds));

        public async Task<string?> SaveLastReplayAsync(TimeSpan requestedDuration)
        {
            var supportedDuration = TimeSpan.FromSeconds(RecordingPreferences.BackgroundReplayDurationSeconds);
            var label = FormatReplayDuration(requestedDuration);

            if (requestedDuration > supportedDuration)
            {
                PublishStatus("Replay currently keeps up to 45 seconds. Start a manual recording for longer clips.");
                return null;
            }

            PublishStatus($"Saving the last {label}.");

            if (_manualRecordingService.IsReplayBufferRunning)
            {
                string path = await _manualRecordingService.SaveReplayAsync(requestedDuration);
                PublishStatus($"Saved the last {label}.");
                return path;
            }

            if (_backgroundClipService.IsRunning)
            {
                string path = await _backgroundClipService.SaveLast45SecondsAsync();
                PublishStatus($"Saved the last {label}.");
                return path;
            }

            PublishStatus("Replay buffer is not running.");
            return null;
        }

        private static string FormatReplayDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes >= 1)
                return $"{(int)duration.TotalMinutes} minute{(duration.TotalMinutes >= 2 ? "s" : "")}";

            return $"{Math.Max(1, (int)duration.TotalSeconds)} seconds";
        }

        public void PublishStatus(string message)
        {
            LastStatus = message;
            StatusChanged?.Invoke(this, message);
        }
    }
}
