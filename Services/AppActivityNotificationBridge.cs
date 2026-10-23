using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Zink.Pages;
using Zink.Services.Recording;
using Zink.Services.NativeCalling;
using Zink.Services.Streaming;

namespace Zink.Services
{
    /// <summary>Connects meaningful app events to notifications and monitors video suppression.</summary>
    internal static class AppActivityNotificationBridge
    {
        public static void Attach(FrameworkElement root)
        {
            void Scan()
            {
                try
                {
                    NotificationVideoGuard.TrackTree(root);
                    if (root.XamlRoot != null)
                        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot))
                            NotificationVideoGuard.TrackTree(popup.Child);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Activity observer: " + ex.Message); }
            }

            root.Loaded += (_, _) => Scan();
            // Tracks embedded videos created after navigation, including flyouts.
            var timer = root.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (_, _) => Scan();
            timer.Start();
            root.Unloaded += (_, _) => timer.Stop();
            root.Loaded += (_, _) => timer.Start();

            var playback = AppPlaybackService.Instance;
            playback.NowPlayingChanged += (_, _) => NotificationService.Instance.RecordActivity(
                $"{playback.CurrentKind} playback updated", playback.PrimaryText);
            playback.IsPlayingChanged += (_, _) => NotificationService.Instance.RecordActivity(
                playback.IsPlaying ? "Playback started" : "Playback paused or stopped", playback.PrimaryText);
            ManualRecordingService.Instance.StatusChanged += (_, status) =>
                NotificationService.Instance.RecordActivity("Recording", status,
                    status.Contains("failed", StringComparison.OrdinalIgnoreCase) ? NotificationPriority.Critical : NotificationPriority.Normal);
            Zink.Services.Capture.ZinkAreaCaptureService.Instance.StatusChanged += (_, status) =>
                NotificationService.Instance.RecordActivity("Screen capture", status,
                    status.Contains("failed", StringComparison.OrdinalIgnoreCase) || status.Contains("Could not finish", StringComparison.OrdinalIgnoreCase)
                        ? NotificationPriority.Critical : NotificationPriority.Normal);
            string lastSpotifyState = "";
            SpotifyControllerService.Instance.TrackChanged += (_, track) =>
            {
                var state = $"{track.Artist}\n{track.Title}\n{track.IsPlaying}";
                if (state == lastSpotifyState || string.IsNullOrWhiteSpace(track.Title)) return;
                lastSpotifyState = state;
                NotificationService.Instance.RecordActivity(
                    track.IsPlaying ? "Spotify playing" : "Spotify paused", $"{track.Artist} - {track.Title}");
            };
            VideoLibraryService.Current.ScanFinished += (_, result) =>
            {
                if (result.Added != 0 || result.Removed != 0)
                    NotificationService.Instance.RecordActivity("Video library updated",
                        $"Added {result.Added} videos; removed {result.Removed} videos.");
            };
            LocalizationService.LanguageChanged += (_, _) =>
                NotificationService.Instance.RecordActivity("Language changed", "App language updated");
            string lastCallState = "";
            NativeCallCoordinator.Instance.SessionChanged += (_, session) =>
            {
                var state = $"{session.CallId}:{session.State}:{session.IsScreenShare}";
                if (state == lastCallState) return;
                lastCallState = state;
                NotificationService.Instance.RecordActivity(
                    session.IsScreenShare ? $"Screen share: {session.State}" : $"Call: {session.State}", session.StatusText,
                    NotificationPriority.Call);
            };
            ObserveStreaming(NativeTwitchStreamingService.Instance, "Twitch");
            ObserveStreaming(NativeTwitchStreamingService.YouTubeInstance, "YouTube");
            ObserveStreaming(NativeTwitchStreamingService.KickInstance, "Kick");
            ObserveStreaming(NativeTwitchStreamingService.InstagramInstance, "Instagram");
            ObserveStreaming(NativeTwitchStreamingService.TikTokInstance, "TikTok");
            ObserveStreaming(NativeTwitchStreamingService.FacebookInstance, "Facebook");
            ObserveStreaming(NativeTwitchStreamingService.XInstance, "X");
        }

        private static void ObserveStreaming(NativeTwitchStreamingService service, string platform)
        {
            service.StreamingStateChanged += (_, streaming) => NotificationService.Instance.RecordActivity(
                streaming ? $"{platform} stream started" : $"{platform} stream stopped", "Streaming state changed");
        }

    }
}
