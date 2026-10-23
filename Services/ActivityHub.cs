using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace Zink.Services
{
    public static class ActivityHub
    {
        public enum ActivityKind
        {
            Music,
            Video,
            Radio,

            // ✅ NEW
            PageVisit,
            WebView
        }

        public sealed class ActivityItem
        {
            public string Title { get; set; } = "";
            public string Subtitle { get; set; } = "";
            public string Type { get; set; } = "";     // "Music" / "Video" / "Radio" / "PageVisit" / "WebView"
            public string Payload { get; set; } = "";  // file path OR station title OR url OR page type name

            // ✅ optional image to show in dashboard/history (album art / station logo / thumbnail uri)
            public string ImageUri { get; set; } = "";

            public DateTimeOffset When { get; set; } = DateTimeOffset.UtcNow;

            // for Insights (optional)
            public double WatchedSeconds { get; set; } = 0;
            public double ListenedSeconds { get; set; } = 0;
        }

        private const string KEY_RECENT = "ActivityHub_RecentJson";
        private const string KEY_LAST = "ActivityHub_LastJson";
        private const int MAX_RECENT = 24;

        public static ObservableCollection<ActivityItem> Recent { get; } = new();

        public static event EventHandler? Changed;

        static ActivityHub()
        {
            Load();
        }

        public static void EnsureLoaded()
        {
            // no-op now (static ctor loads), but keeps your older calls safe
        }

        public static ActivityItem? GetLast()
        {
            try
            {
                var json = GetSettings().Values[KEY_LAST] as string;
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<ActivityItem>(json);
            }
            catch { return null; }
        }

        public static void Record(
            ActivityKind kind,
            string title,
            string subtitle,
            string payload,
            double watchedSeconds = 0,
            double listenedSeconds = 0,
            string imageUri = "")
        {
            try
            {
                var item = new ActivityItem
                {
                    Title = title ?? "",
                    Subtitle = subtitle ?? "",
                    Type = kind.ToString(),
                    Payload = payload ?? "",
                    ImageUri = imageUri ?? "",
                    When = DateTimeOffset.UtcNow,
                    WatchedSeconds = watchedSeconds,
                    ListenedSeconds = listenedSeconds
                };

                // de-dupe (same type + payload) by moving to top
                var existing = Recent.FirstOrDefault(x =>
                    string.Equals(x.Type, item.Type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Payload, item.Payload, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    // keep best image if new one is empty
                    if (string.IsNullOrWhiteSpace(item.ImageUri) && !string.IsNullOrWhiteSpace(existing.ImageUri))
                        item.ImageUri = existing.ImageUri;

                    Recent.Remove(existing);
                }

                Recent.Insert(0, item);

                while (Recent.Count > MAX_RECENT)
                    Recent.RemoveAt(Recent.Count - 1);

                SaveLast(item);
                SaveRecent();

                if (kind != ActivityKind.PageVisit && kind != ActivityKind.WebView)
                    NotificationService.Instance.RecordActivity(item.Title, item.Subtitle);
                Changed?.Invoke(null, EventArgs.Empty);
            }
            catch { }
        }

        public static void Clear()
        {
            try
            {
                Recent.Clear();
                GetSettings().Values.Remove(KEY_RECENT);
                GetSettings().Values.Remove(KEY_LAST);
                Changed?.Invoke(null, EventArgs.Empty);
            }
            catch { }
        }

        public static void RemovePayload(string? payload, string? type = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(payload))
                    return;

                var removedAny = false;

                for (var i = Recent.Count - 1; i >= 0; i--)
                {
                    var item = Recent[i];
                    var payloadMatches = string.Equals(item.Payload, payload, StringComparison.OrdinalIgnoreCase);
                    var typeMatches = string.IsNullOrWhiteSpace(type) ||
                                      string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase);

                    if (payloadMatches && typeMatches)
                    {
                        Recent.RemoveAt(i);
                        removedAny = true;
                    }
                }

                var last = GetLast();
                if (last != null &&
                    string.Equals(last.Payload, payload, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(type) || string.Equals(last.Type, type, StringComparison.OrdinalIgnoreCase)))
                {
                    if (Recent.Count > 0)
                        SaveLast(Recent[0]);
                    else
                        GetSettings().Values.Remove(KEY_LAST);

                    removedAny = true;
                }

                if (!removedAny)
                    return;

                SaveRecent();
                Changed?.Invoke(null, EventArgs.Empty);
            }
            catch { }
        }

        public static TimeSpan GetTotalWatchedTime()
        {
            try
            {
                var seconds = Recent.Sum(x => x.WatchedSeconds);
                return TimeSpan.FromSeconds(seconds);
            }
            catch { return TimeSpan.Zero; }
        }

        public static TimeSpan GetTotalListenedTime()
        {
            try
            {
                var seconds = Recent.Sum(x => x.ListenedSeconds);
                return TimeSpan.FromSeconds(seconds);
            }
            catch { return TimeSpan.Zero; }
        }

        public static string GetMostUsedType()
        {
            try
            {
                var top = Recent
                    .Where(x => !string.IsNullOrWhiteSpace(x.Type))
                    .GroupBy(x => x.Type)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault();

                return top?.Key ?? "-";
            }
            catch { return "-"; }
        }

        private static ApplicationDataContainer GetSettings() => ApplicationData.Current.LocalSettings;

        private static void Load()
        {
            try
            {
                Recent.Clear();

                var json = GetSettings().Values[KEY_RECENT] as string;
                if (string.IsNullOrWhiteSpace(json)) return;

                var list = JsonSerializer.Deserialize<ActivityItem[]>(json);
                if (list == null) return;

                foreach (var item in list.OrderByDescending(x => x.When))
                    Recent.Add(item);
            }
            catch { }
        }

        private static void SaveRecent()
        {
            try
            {
                var json = JsonSerializer.Serialize(Recent.ToArray());
                GetSettings().Values[KEY_RECENT] = json;
            }
            catch { }
        }

        private static void SaveLast(ActivityItem item)
        {
            try
            {
                var json = JsonSerializer.Serialize(item);
                GetSettings().Values[KEY_LAST] = json;
            }
            catch { }
        }
    }
}
