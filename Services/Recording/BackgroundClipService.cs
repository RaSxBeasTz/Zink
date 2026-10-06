using System;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Zink.Models;

namespace Zink.Services.Recording
{
    public sealed class BackgroundClipService : IAsyncDisposable
    {
        public static BackgroundClipService Instance { get; } = new();

        private GraphicsCaptureItem? _captureItem;
        private RecordingOptions _options = new();

        public bool IsRunning => ManualRecordingService.Instance.IsReplayBufferRunning;

        public event EventHandler<string>? StatusChanged;

        private BackgroundClipService()
        {
            ManualRecordingService.Instance.StatusChanged += (_, message) =>
            {
                StatusChanged?.Invoke(this, message);
            };
        }

        public void SetCaptureItem(GraphicsCaptureItem item, RecordingOptions options)
        {
            _captureItem = item;
            _options = options.Clone();
            StatusChanged?.Invoke(this, "Background clip target updated.");
        }

        public async Task StartAsync()
        {
            if (!RecordingPreferences.IsGamingBackgroundReplayEnabled)
                throw new InvalidOperationException("Background replay for gaming is turned off in Settings.");

            if (_captureItem is null)
                throw new InvalidOperationException("Choose a capture source first.");

            await ManualRecordingService.Instance.StartReplayAsync(_captureItem, _options);
            StatusChanged?.Invoke(this, "Background clipping started.");
        }

        public async Task StopAsync()
        {
            if (ManualRecordingService.Instance.IsReplayBufferRunning)
            {
                await ManualRecordingService.Instance.StopAsync();
            }

            StatusChanged?.Invoke(this, "Background clipping stopped.");
        }

        public async Task<string> SaveLast45SecondsAsync()
        {
            string path = await ManualRecordingService.Instance.SaveReplayAsync(
                TimeSpan.FromSeconds(RecordingPreferences.BackgroundReplayDurationSeconds));
            StatusChanged?.Invoke(this, $"Saved last 45 seconds: {path}");
            return path;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}
