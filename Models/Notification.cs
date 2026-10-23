using System;

namespace Zink.Pages
{
    public enum NotificationPriority { Normal, Call, Critical }

    public class Notification
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public NotificationPriority Priority { get; set; }
        public string ActionLabel { get; set; } = "";
        public string ActionUri { get; set; } = "";
        public DateTime Timestamp { get; set; }

        // Pre‑formatted so we don’t need StringFormat in XAML
        public string FormattedTimestamp => Timestamp.ToString("g");
        public bool HasAction => !string.IsNullOrWhiteSpace(ActionLabel) && !string.IsNullOrWhiteSpace(ActionUri);
    }
}
