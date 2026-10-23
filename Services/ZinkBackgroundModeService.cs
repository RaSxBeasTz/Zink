using Microsoft.Windows.AppNotifications;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;
using Zink.Services.Calling;
using Zink.Services.Social;

namespace Zink.Services
{
    public sealed class ZinkBackgroundModeService : IDisposable
    {
        public static ZinkBackgroundModeService Instance { get; } = new ZinkBackgroundModeService();

        private CancellationTokenSource? _cts;
        private Task? _pollTask;
        private int _lastFriendRequestCount = -1;
        private int _lastUpdateCount = -1;
        private bool _disposed;
        private bool _eventsAttached;

        private ZinkBackgroundModeService()
        {
        }

        public bool IsRunning => _pollTask != null && !_pollTask.IsCompleted;

        public async Task ApplyAsync()
        {
            if (_disposed)
                return;

            ApplyLowResourcePreference();

            if (!BackgroundModePreferences.AreBackgroundNotificationsEnabled)
            {
                await StopAsync();
                return;
            }

            if (IsRunning)
                return;

            await StartAsync();
        }

        public async Task StartAsync()
        {
            if (_disposed || IsRunning)
                return;

            _cts = new CancellationTokenSource();
            AttachRealtimeEvents();
            _pollTask = RunAsync(_cts.Token);

            await EnsureRealtimeConnectedAsync();
        }

        public async Task StopAsync()
        {
            var cts = _cts;
            _cts = null;

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                }
            }

            var task = _pollTask;
            _pollTask = null;

            if (task != null)
            {
                try
                {
                    await task;
                }
                catch
                {
                }
            }

            DetachRealtimeEvents();
            cts?.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await EnsureRealtimeConnectedAsync();
                    await CheckFriendRequestsAsync();
                    await CheckStoreUpdatesAsync();
                }
                catch
                {
                }

                var delay = BackgroundModePreferences.IsLowResourceBackgroundModeEnabled
                    ? TimeSpan.FromMinutes(8)
                    : TimeSpan.FromMinutes(2);

                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static void ApplyLowResourcePreference()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = BackgroundModePreferences.IsLowResourceBackgroundModeEnabled
                    ? ProcessPriorityClass.BelowNormal
                    : ProcessPriorityClass.Normal;
            }
            catch
            {
            }
        }

        private async Task EnsureRealtimeConnectedAsync()
        {
            try
            {
                var token = await TokenStore.Instance.GetTokenAsync();
                if (string.IsNullOrWhiteSpace(token))
                    return;

                if (!SocialManager.Instance.Realtime.IsConnected)
                    await SocialManager.Instance.Realtime.ConnectAsync();
            }
            catch
            {
            }
        }

        private async Task CheckFriendRequestsAsync()
        {
            try
            {
                var token = await TokenStore.Instance.GetTokenAsync();
                if (string.IsNullOrWhiteSpace(token))
                    return;

                var requests = await SocialManager.Instance.Api.GetRequestsAsync();
                var count = requests.Count;

                if (_lastFriendRequestCount >= 0 && count > _lastFriendRequestCount)
                {
                    ShowNotification(
                        "Zink friend request",
                        count == 1
                            ? "You have a new friend request."
                            : $"You have {count} friend requests.");
                }

                _lastFriendRequestCount = count;
            }
            catch
            {
            }
        }

        private async Task CheckStoreUpdatesAsync()
        {
            if (!BackgroundModePreferences.AreAppUpdateChecksEnabled)
                return;

            try
            {
                var updates = await StoreContext.GetDefault().GetAppAndOptionalStorePackageUpdatesAsync();
                var count = updates.Count;

                if (count > 0 && (_lastUpdateCount < 0 || count > _lastUpdateCount))
                {
                    NotificationService.Instance.ShowAppUpdateReady();
                }

                _lastUpdateCount = count;
            }
            catch
            {
            }
        }

        private void AttachRealtimeEvents()
        {
            if (_eventsAttached)
                return;

            SocialManager.Instance.Realtime.IncomingCall += Realtime_IncomingCall;
            SocialManager.Instance.Realtime.IncomingMessage += Realtime_IncomingMessage;
            _eventsAttached = true;
        }

        private void DetachRealtimeEvents()
        {
            if (!_eventsAttached)
                return;

            SocialManager.Instance.Realtime.IncomingCall -= Realtime_IncomingCall;
            SocialManager.Instance.Realtime.IncomingMessage -= Realtime_IncomingMessage;
            _eventsAttached = false;
        }

        private void Realtime_IncomingCall(object? sender, IncomingCallEventArgs e)
        {
            var caller = !string.IsNullOrWhiteSpace(e.FromDisplayName)
                ? e.FromDisplayName
                : (!string.IsNullOrWhiteSpace(e.FromUsername) ? e.FromUsername : $"User {e.FromUserId}");

            ShowNotification("Incoming Zink call", $"{caller} is calling you.", Zink.Pages.NotificationPriority.Call);
        }

        private void Realtime_IncomingMessage(object? sender, IncomingMessageEventArgs e)
        {
            var senderName = !string.IsNullOrWhiteSpace(e.FromDisplayName)
                ? e.FromDisplayName
                : (!string.IsNullOrWhiteSpace(e.FromUsername) ? e.FromUsername : $"User {e.FromUserId}");

            var preview = string.IsNullOrWhiteSpace(e.Message)
                ? "New message received."
                : e.Message;

            ShowNotification(senderName, preview);
        }

        private static void ShowNotification(string title, string message,
            Zink.Pages.NotificationPriority priority = Zink.Pages.NotificationPriority.Normal)
        {
            if (!BackgroundModePreferences.AreBackgroundNotificationsEnabled)
                return;

            try
            {
                NotificationService.Instance.Show(title, message, priority);
            }
            catch
            {
                try
                {
                    AppNotificationManager.Default.Show(
                        new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                            .AddText(title)
                            .AddText(message)
                            .BuildNotification());
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _ = StopAsync();
        }
    }
}
