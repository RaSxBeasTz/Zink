using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Storage;
using Zink.Pages;

namespace Zink.Services
{
    public sealed class NotificationService
    {
        private const string StoredNotificationsKey = "Zink.Notifications.Stored";
        private static string HistoryPath => Path.Combine(ApplicationData.Current.LocalFolder.Path, "notifications.json");
        public const string AppUpdateNotificationKind = "AppUpdateAvailable";
        public const string AppUpdateNotificationTitle = "Zink app update ready";
        public const string AppUpdateNotificationMessage = "The Zink app has a new update ready to be installed. Installing the update is recommended because it can contain bug fixes, performance improvements and new features.";
        public const string AppUpdateNotificationActionLabel = "Install the update";
        public const string AppUpdateNotificationActionUri = "ms-windows-store://downloadsandupdates";
        private static readonly NotificationService _instance = new NotificationService();

        public static NotificationService Instance => _instance;
        public ObservableCollection<Notification> Notifications { get; } = new();
        public event Action<Notification>? BannerRequested;

        private NotificationService()
        {
            LoadStoredNotifications();
        }

        // Activity is stored and presented in-app, without creating a desktop toast.
        public void RecordActivity(string title, string message, NotificationPriority priority = NotificationPriority.Normal,
            bool showBanner = true)
        {
            Publish(title, message, priority, store: true, allowDesktopToast: false, showBanner: showBanner);
        }

        private static bool DispatchToUI(Action action)
        {
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                dispatcher.TryEnqueue(() => action());
                return true;
            }
            return false;
        }

        public void Show(string title, string message)
        {
            Show(title, message, store: true);
        }

        public void Show(string title, string message, bool store)
        {
            Show(title, message, NotificationPriority.Normal, store);
        }

        public void Show(string title, string message, NotificationPriority priority, bool store = true)
        {
            Publish(title, message, priority, store, allowDesktopToast: true);
        }

        private void Publish(string title, string message, NotificationPriority priority, bool store,
            bool allowDesktopToast, bool showBanner = true)
        {
            if (DispatchToUI(() => Publish(title, message, priority, store, allowDesktopToast, showBanner))) return;
            if (store)
                AddStoredNotification(title, message, priority);

            if (!showBanner) return;
            if (priority == NotificationPriority.Normal && App.MainWindow?.AreRoutineNotificationsSuppressed == true)
                return;

            if (App.MainWindow?.IsNotificationWindowActive == true && BannerRequested != null)
            {
                BannerRequested.Invoke(new Notification
                {
                    Title = title ?? "", Message = message ?? "", Priority = priority, Timestamp = DateTime.Now
                });
                return;
            }

            if (!allowDesktopToast) return;
            try
            {
                var notification = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message)
                    .BuildNotification();

                AppNotificationManager.Default.Show(notification);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Notification FAILED: " + ex);
            }

        }

        public void ShowAppUpdateReady()
        {
            Show(
                AppUpdateNotificationTitle,
                AppUpdateNotificationMessage,
                store: false);

            AddOrUpdateStoredNotification(
                AppUpdateNotificationKind,
                AppUpdateNotificationTitle,
                AppUpdateNotificationMessage,
                AppUpdateNotificationActionLabel,
                AppUpdateNotificationActionUri);
        }

        public void Delete(Notification notification)
        {
            if (DispatchToUI(() => Delete(notification))) return;
            if (notification == null)
                return;

            try
            {
                var existing = Notifications.FirstOrDefault(item =>
                    string.Equals(item.Id, notification.Id, StringComparison.Ordinal));

                if (existing != null)
                    Notifications.Remove(existing);

                SaveStoredNotifications();
            }
            catch
            {
            }
        }

        public void DeleteAll()
        {
            if (DispatchToUI(DeleteAll)) return;
            try
            {
                Notifications.Clear();
                SaveStoredNotifications();
            }
            catch
            {
            }
        }

        private void AddStoredNotification(string title, string message, NotificationPriority priority = NotificationPriority.Normal)
        {
            if (DispatchToUI(() => AddStoredNotification(title, message, priority))) return;
            try
            {
                Notifications.Insert(0, new Notification
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = title ?? "",
                    Message = message ?? "",
                    Priority = priority,
                    Timestamp = DateTime.Now
                });

                SaveStoredNotifications();
            }
            catch
            {
            }
        }

        private void AddOrUpdateStoredNotification(
            string kind,
            string title,
            string message,
            string actionLabel,
            string actionUri)
        {
            if (DispatchToUI(() => AddOrUpdateStoredNotification(kind, title, message, actionLabel, actionUri))) return;
            try
            {
                var existing = Notifications.FirstOrDefault(item =>
                    string.Equals(item.Kind, kind, StringComparison.Ordinal));

                if (existing != null)
                {
                    existing.Title = title ?? "";
                    existing.Message = message ?? "";
                    existing.ActionLabel = actionLabel ?? "";
                    existing.ActionUri = actionUri ?? "";
                    existing.Timestamp = DateTime.Now;

                    Notifications.Remove(existing);
                    Notifications.Insert(0, existing);
                }
                else
                {
                    Notifications.Insert(0, new Notification
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Kind = kind ?? "",
                        Title = title ?? "",
                        Message = message ?? "",
                        ActionLabel = actionLabel ?? "",
                        ActionUri = actionUri ?? "",
                        Timestamp = DateTime.Now
                    });
                }

                SaveStoredNotifications();
            }
            catch
            {
            }
        }

        private void LoadStoredNotifications()
        {
            try
            {
                var json = File.Exists(HistoryPath) ? File.ReadAllText(HistoryPath) :
                    ApplicationData.Current.LocalSettings.Values[StoredNotificationsKey] as string;
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var stored = JsonSerializer.Deserialize<Notification[]>(json);
                if (stored == null)
                    return;

                Notifications.Clear();
                foreach (var notification in stored.OrderByDescending(item => item.Timestamp))
                {
                    if (string.IsNullOrWhiteSpace(notification.Id))
                        notification.Id = Guid.NewGuid().ToString("N");

                    Notifications.Add(notification);
                }
            }
            catch
            {
            }
        }

        private void SaveStoredNotifications()
        {
            try
            {
                var json = JsonSerializer.Serialize(Notifications.ToArray());
                var temporaryPath = HistoryPath + ".tmp";
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, HistoryPath, overwrite: true);
            }
            catch
            {
            }
        }
    }
}
