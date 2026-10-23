using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Zink.Services
{
    /// <summary>
    /// Converts Xbox/Gaming Services achievement toasts into ZINK's on-screen animation.
    /// Windows owns the permission prompt and the original notification remains untouched.
    /// </summary>
    public sealed class XboxAchievementNotificationService : IDisposable
    {
        private static readonly Regex GamerscorePattern =
            new(@"(?<!\w)(\d{1,5})\s*(?:G|Gamerscore)(?!\w)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly UserNotificationListener _listener = UserNotificationListener.Current;
        private readonly HashSet<uint> _handledNotificationIds = new();
        private readonly SemaphoreSlim _syncGate = new(1, 1);
        private bool _started;

        public XboxAchievementNotificationService(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue;
        }

        public async Task<bool> StartAsync()
        {
            if (_started)
                return true;

            UserNotificationListenerAccessStatus status = _listener.GetAccessStatus();
            if (status != UserNotificationListenerAccessStatus.Allowed)
                status = await _listener.RequestAccessAsync();

            if (status != UserNotificationListenerAccessStatus.Allowed)
                return false;

            _listener.NotificationChanged += Listener_NotificationChanged;
            _started = true;

            // Record existing notifications without replaying old achievements.
            IReadOnlyList<UserNotification> existing =
                await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (UserNotification notification in existing)
                _handledNotificationIds.Add(notification.Id);

            return true;
        }

        private async void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            if (args.ChangeKind != UserNotificationChangedKind.Added)
                return;

            await ProcessNotificationAsync(args.UserNotificationId);
        }

        private async Task ProcessNotificationAsync(uint notificationId)
        {
            await _syncGate.WaitAsync();
            try
            {
                if (!_handledNotificationIds.Add(notificationId))
                    return;

                UserNotification? notification = _listener.GetNotification(notificationId);
                if (notification == null || !IsXboxNotification(notification))
                    return;

                IReadOnlyList<string> lines = ReadToastLines(notification);
                string combined = string.Join(" ", lines);
                Match scoreMatch = GamerscorePattern.Match(combined);
                bool saysAchievement =
                    combined.Contains("achievement", StringComparison.OrdinalIgnoreCase) &&
                    (combined.Contains("unlock", StringComparison.OrdinalIgnoreCase) ||
                     combined.Contains("earn", StringComparison.OrdinalIgnoreCase));

                if (!saysAchievement && !scoreMatch.Success)
                    return;

                string title = FindAchievementTitle(lines) ?? "Xbox achievement";
                string score = scoreMatch.Success ? $"{scoreMatch.Groups[1].Value} G" : "G";
                string description = lines.FirstOrDefault(line =>
                    !ReferenceEquals(line, title) &&
                    !line.Contains("achievement", StringComparison.OrdinalIgnoreCase) &&
                    !GamerscorePattern.IsMatch(line)) ?? "Added to your Xbox profile";

                NotificationService.Instance.RecordActivity($"Xbox achievement: {title}", $"{score} — {description}", showBanner: false);
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (App.MainWindow?.AreRoutineNotificationsSuppressed != true)
                        XboxAchievementOverlayWindow.Show(title, score, description);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[XboxAchievementListener] {ex.Message}");
            }
            finally
            {
                _syncGate.Release();
            }
        }

        private static bool IsXboxNotification(UserNotification notification)
        {
            string displayName = notification.AppInfo?.DisplayInfo?.DisplayName ?? string.Empty;
            string packageFamily = notification.AppInfo?.PackageFamilyName ?? string.Empty;
            string identity = $"{displayName} {packageFamily}";

            return identity.Contains("xbox", StringComparison.OrdinalIgnoreCase) ||
                   identity.Contains("gamingservices", StringComparison.OrdinalIgnoreCase) ||
                   identity.Contains("gaming services", StringComparison.OrdinalIgnoreCase) ||
                   identity.Contains("gamebar", StringComparison.OrdinalIgnoreCase) ||
                   identity.Contains("game bar", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> ReadToastLines(UserNotification notification)
        {
            NotificationBinding? binding =
                notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding == null)
                return Array.Empty<string>();

            return binding.GetTextElements()
                .Select(element => element.Text?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string? FindAchievementTitle(IReadOnlyList<string> lines)
        {
            return lines.FirstOrDefault(line =>
                line.Length is > 2 and < 120 &&
                !line.Contains("achievement unlocked", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("achievement earned", StringComparison.OrdinalIgnoreCase) &&
                !GamerscorePattern.IsMatch(line));
        }

        public void Dispose()
        {
            if (_started)
                _listener.NotificationChanged -= Listener_NotificationChanged;
            _syncGate.Dispose();
            _started = false;
        }
    }
}
