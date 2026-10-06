using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Services
{
    public sealed class DiscordPresenceService
    {
        private const string EnabledSettingKey = "ZinkDiscordRichPresenceEnabled";
        private const string ApplicationIdSettingKey = "ZinkDiscordApplicationId";

        private const string DefaultApplicationId = "1487472795767279857";

        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(850);
        private static DiscordPresenceService? _instance;

        private readonly SemaphoreSlim _pipeLock = new(1, 1);
        private NamedPipeClientStream? _pipe;
        private string? _lastPayload;
        private DateTimeOffset _activityStartedAtUtc = DateTimeOffset.UtcNow;
        private bool _initialized;
        private string? _liveStreamingDestination;

        public static DiscordPresenceService Instance => _instance ??= new DiscordPresenceService();
        public bool IsEnabled => GetEnabledSetting();

        private DiscordPresenceService()
        {
        }

        public static bool GetEnabledSetting()
        {
            try
            {
                object value = ApplicationData.Current.LocalSettings.Values[EnabledSettingKey];
                return value is not bool boolValue || boolValue;
            }
            catch
            {
                return true;
            }
        }

        public void SetEnabled(bool enabled)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[EnabledSettingKey] = enabled;
            }
            catch
            {
            }

            if (enabled)
                SetAppPresence();
            else
                QueuePresence(BuildCommand(null), allowWhenDisabled: true);
        }

        public void Initialize()
        {
            _initialized = true;
            SetAppPresence();
        }

        public void Shutdown()
        {
            try
            {
                _pipeLock.Wait(250);
                DisposePipe();
                _lastPayload = null;
            }
            catch
            {
            }
            finally
            {
                try { _pipeLock.Release(); } catch { }
            }
        }

        public void Clear()
        {
            if (HasActiveLiveStreamingPresence())
                return;

            QueuePresence(BuildCommand(null));
        }

        public void SetAppPresence(string? state = null)
        {
            if (HasActiveLiveStreamingPresence())
                return;

            _activityStartedAtUtc = DateTimeOffset.UtcNow;
            var activity = CreateBaseActivity(
                details: "Using Zink",
                state: string.IsNullOrWhiteSpace(state) ? "Home dashboard" : state,
                activityType: 3);

            QueuePresence(BuildCommand(activity));
        }

        public void SetPagePresence(string pageName, string? category = null, string? action = null)
        {
            if (HasActiveLiveStreamingPresence())
                return;

            pageName = Clean(pageName, "Zink");

            var details = Clean(action, "Using");
            var state = string.IsNullOrWhiteSpace(category)
                ? pageName
                : $"{pageName} - {category}";

            _activityStartedAtUtc = DateTimeOffset.UtcNow;
            var activity = CreateBaseActivity(details, state, 3);
            QueuePresence(BuildCommand(activity));
        }

        public void SetWebPresence(string siteName, string? category = null, string? pageTitle = null)
        {
            if (HasActiveLiveStreamingPresence())
                return;

            siteName = Clean(siteName, "Zink");

            _activityStartedAtUtc = DateTimeOffset.UtcNow;
            var activity = CreateBaseActivity(
                details: Clean(pageTitle, $"Browsing {siteName}"),
                state: string.IsNullOrWhiteSpace(category) ? siteName : $"{category} on Zink",
                activityType: 3);

            QueuePresence(BuildCommand(activity));
        }

        public void SetMusicPresence(
            string songTitle,
            string? artistName,
            string? sourceName = null,
            string? largeImageKey = null,
            string? largeImageText = null,
            string? buttonUrl = null,
            bool isPlaying = true)
        {
            if (HasActiveLiveStreamingPresence())
                return;

            var title = Clean(songTitle, "Music");
            var artist = Clean(artistName, "Unknown artist");
            var source = Clean(sourceName, "Zink Music");

            _activityStartedAtUtc = DateTimeOffset.UtcNow;
            var activity = CreateBaseActivity(
                details: isPlaying ? $"Listening to {title}" : $"Paused {title}",
                state: $"{artist} on {source}",
                activityType: 2,
                largeImageKey: CleanAssetKey(largeImageKey, "zink_1024"),
                largeImageText: Clean(largeImageText, "Zink Music"));

            AddButton(activity, "Open Zink", buttonUrl);
            QueuePresence(BuildCommand(activity));
        }

        public void SetCallPresence(
            string status,
            int participantCount,
            bool isScreenSharing,
            bool isMuted,
            bool isDeafened,
            TimeSpan? connectedFor = null)
        {
            if (HasActiveLiveStreamingPresence())
                return;

            var details = Clean(status, "In a Zink call");
            var state = participantCount <= 1
                ? "Private call"
                : $"{participantCount} people";

            if (isScreenSharing)
                state += " - screen sharing";
            if (isMuted)
                state += " - muted";
            if (isDeafened)
                state += " - deafened";

            _activityStartedAtUtc = DateTimeOffset.UtcNow;
            var activity = CreateBaseActivity(details, state, 3);
            if (connectedFor.HasValue)
                activity["timestamps"] = new { start = DateTimeOffset.UtcNow.Subtract(connectedFor.Value).ToUnixTimeSeconds() };

            QueuePresence(BuildCommand(activity));
        }

        public void SetRadioPresence(
            string stationName,
            string? songTitle,
            string? artistName,
            string? stationAssetKey = null,
            string? buttonUrl = null)
        {
            if (HasActiveLiveStreamingPresence())
                return;

            var station = Clean(stationName, "Radio");
            var artist = string.IsNullOrWhiteSpace(artistName) ? "Zink Radio" : artistName.Trim();

            _activityStartedAtUtc = DateTimeOffset.UtcNow;
            var activity = CreateBaseActivity(
                details: "Listening to a live radio station",
                state: $"{artist} on {station}",
                activityType: 2,
                largeImageKey: CleanAssetKey(stationAssetKey, GetStationAssetKey(station)),
                largeImageText: station);

            AddButton(activity, "Listen on Zink", buttonUrl);
            QueuePresence(BuildCommand(activity));
        }

        public void UpdateRadioTrack(
            string stationName,
            string? songTitle,
            string? artistName,
            string? stationAssetKey = null,
            string? buttonUrl = null)
        {
            SetRadioPresence(stationName, songTitle, artistName, stationAssetKey, buttonUrl);
        }

        public void SetStreamingPresence(string destination = "Twitch", bool isLive = true)
        {
            var target = Clean(destination, "Twitch");
            if (isLive)
            {
                _liveStreamingDestination = target;
                _activityStartedAtUtc = DateTimeOffset.UtcNow;
            }
            else if (string.Equals(_liveStreamingDestination, target, StringComparison.OrdinalIgnoreCase))
            {
                _liveStreamingDestination = null;
                _activityStartedAtUtc = DateTimeOffset.UtcNow;
            }
            else if (HasActiveLiveStreamingPresence())
            {
                return;
            }

            var activity = CreateBaseActivity(
                details: isLive ? $"Live streaming on {target}" : "Preparing a stream",
                state: isLive ? "Broadcasting from Zink" : $"Ready to stream on {target}",
                activityType: 3,
                largeImageKey: "zink_1024",
                largeImageText: "Zink Streaming");

            QueuePresence(BuildCommand(activity));
        }

        public void SetVideoPresence(
            string videoTitle,
            TimeSpan position,
            TimeSpan duration,
            string? largeImageKey = null,
            string? largeImageText = null,
            string? buttonUrl = null)
        {
            if (HasActiveLiveStreamingPresence())
                return;

            var activity = CreateVideoActivity(videoTitle, position, duration, false, largeImageKey, largeImageText);
            AddButton(activity, "Watch on Zink", buttonUrl);
            QueuePresence(BuildCommand(activity));
        }

        public void SetVideoPausedPresence(
            string videoTitle,
            TimeSpan position,
            TimeSpan duration,
            string? largeImageKey = null,
            string? largeImageText = null,
            string? buttonUrl = null)
        {
            if (HasActiveLiveStreamingPresence())
                return;

            var activity = CreateVideoActivity(videoTitle, position, duration, true, largeImageKey, largeImageText);
            AddButton(activity, "Watch on Zink", buttonUrl);
            QueuePresence(BuildCommand(activity));
        }

        private bool HasActiveLiveStreamingPresence()
        {
            return !string.IsNullOrWhiteSpace(_liveStreamingDestination);
        }

        private static System.Collections.Generic.Dictionary<string, object> CreateVideoActivity(
            string videoTitle,
            TimeSpan position,
            TimeSpan duration,
            bool paused,
            string? largeImageKey,
            string? largeImageText)
        {
            var title = Clean(videoTitle, "Video");
            var activity = CreateBaseActivity(
                details: paused ? $"Paused {title}" : $"Watching {title}",
                state: "Zink Video Player",
                activityType: 3,
                largeImageKey: CleanAssetKey(largeImageKey, "zink_1024"),
                largeImageText: Clean(largeImageText, "Zink Video"));

            if (!paused && duration > TimeSpan.Zero && position >= TimeSpan.Zero && position < duration)
            {
                var now = DateTimeOffset.UtcNow;
                activity["timestamps"] = new
                {
                    start = now.Subtract(position).ToUnixTimeSeconds(),
                    end = now.Add(duration - position).ToUnixTimeSeconds()
                };
            }

            return activity;
        }

        private static System.Collections.Generic.Dictionary<string, object> CreateBaseActivity(
            string details,
            string state,
            int activityType,
            string? largeImageKey = "zink_1024",
            string? largeImageText = "Zink")
        {
            var activity = new System.Collections.Generic.Dictionary<string, object>
            {
                ["type"] = activityType,
                ["details"] = Truncate(Clean(details, "Using Zink"), 128),
                ["state"] = Truncate(Clean(state, "Zink"), 128),
                ["timestamps"] = new { start = Instance._activityStartedAtUtc.ToUnixTimeSeconds() },
                ["assets"] = new
                {
                    large_image = CleanAssetKey(largeImageKey, "zink_1024"),
                    large_text = Truncate(Clean(largeImageText, "Zink"), 128)
                }
            };

            return activity;
        }

        private static void AddButton(System.Collections.Generic.Dictionary<string, object> activity, string label, string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
                return;

            activity["buttons"] = new[]
            {
                new
                {
                    label,
                    url
                }
            };
        }

        private object BuildCommand(object? activity)
        {
            return new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid = Environment.ProcessId,
                    activity
                },
                nonce = Guid.NewGuid().ToString("N")
            };
        }

        private void QueuePresence(object command, bool allowWhenDisabled = false)
        {
            if (!allowWhenDisabled && !IsEnabled)
                return;

            var applicationId = GetApplicationId();
            if (string.IsNullOrWhiteSpace(applicationId))
                return;

            var payload = JsonSerializer.Serialize(command);
            if (string.Equals(payload, _lastPayload, StringComparison.Ordinal))
                return;

            _lastPayload = payload;

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(applicationId, payload).ConfigureAwait(false);
                }
                catch
                {
                    DisposePipe();
                }
            });
        }

        private async Task SendAsync(string applicationId, string payload)
        {
            if (!_initialized)
                return;

            await _pipeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_pipe == null || !_pipe.IsConnected)
                {
                    DisposePipe();
                    _pipe = await ConnectAsync().ConfigureAwait(false);
                    if (_pipe == null)
                        return;

                    await WriteFrameAsync(_pipe, 0, JsonSerializer.Serialize(new
                    {
                        v = 1,
                        client_id = applicationId
                    })).ConfigureAwait(false);
                }

                await WriteFrameAsync(_pipe, 1, payload).ConfigureAwait(false);
            }
            finally
            {
                _pipeLock.Release();
            }
        }

        private static async Task<NamedPipeClientStream?> ConnectAsync()
        {
            for (var i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    using var cts = new CancellationTokenSource(ConnectTimeout);
                    await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
                    return pipe;
                }
                catch
                {
                    pipe.Dispose();
                }
            }

            return null;
        }

        private static async Task WriteFrameAsync(Stream stream, int opCode, string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var header = new byte[8];
            BitConverter.GetBytes(opCode).CopyTo(header, 0);
            BitConverter.GetBytes(payload.Length).CopyTo(header, 4);

            await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private void DisposePipe()
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }

        private static string GetApplicationId()
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(ApplicationIdSettingKey, out var value) &&
                    value is string settingValue &&
                    !string.IsNullOrWhiteSpace(settingValue))
                {
                    return settingValue.Trim();
                }
            }
            catch
            {
            }

            return DefaultApplicationId;
        }

        private static string GetStationAssetKey(string stationName)
        {
            var key = (stationName ?? "")
                .ToLowerInvariant()
                .Replace(" ", "")
                .Replace("!", "")
                .Replace("+", "plus")
                .Replace("&", "and")
                .Replace(".", "");

            return key switch
            {
                "heart" or "heartmiltonkeynes" or "heart70s" or "heart80s" or "heart90s" or "heartdance" or "heartlove" => "heartfm_1024",
                "capitalfm" or "capitalanthems" or "capitalchill" or "capitalscotland" => "capitalfm_1024",
                "capitalxtra" or "capitalextra" => "capitalxtra_1024",
                "capitaldance" => "capitaldance_1024",
                "kissfm" or "kisstory" or "kissfresh" => "kissfm_1024",
                "smoothradio" or "smoothchill" or "smoothcountry" or "smooth70s" or "smooth80s" or "smoothrelax" => "smoothfm_1024",
                "magicradio" => "magicradio_1024",
                "bbcradio1" => "bbcradio1_1024",
                "bbcradio2" => "bbcradio2_1024",
                "bbcradio5live" or "bbcradio5sportsextra" => "bbcradio5live_1024",
                "bbcradio1xtra" => "bbc1xtra_1024",
                "bbcworldservice" or "bbcradio4" or "bbcradio4extra" or "bbcradiolondon" or "bbcradiomanchester" or "bbcradioscotland" or "bbcradiowales" or "bbcradiocymru" or "bbcradioulster" => "bbcworld_1024",
                "hitsradio" => "hitsradio_1024",
                "greatesthitsradio" => "greatesthitsradio_1024",
                "talksport" or "talkradio" => "talksport_1024",
                "absoluteradio" => "absolute_1024",
                "classicfm" or "goldradio" or "scalaradio" => "classicfm_1024",
                "radiox" or "radioxclassicrock" or "radioxchilled" or "unionjackradio" => "radiox_1024",
                "gem106" => "gem106_1024",
                "premierchristianradio" => "premier_1024",
                "bbcradioderby" => "radioderby_1024",
                "jazzfm" => "jazzfm_1024",
                "mkfm" => "mkfm_1024",
                "lbc" or "lbcnews" => "lbc_1024",
                "timesradio" or "virginradiouk" or "virginradioanthems" or "virginradiochilled" => "timesradio_1024",
                "radioessex" => "radioessex_1024",
                _ => "zink_1024"
            };
        }

        private static string Clean(string? value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string CleanAssetKey(string? value, string fallback)
        {
            var clean = Clean(value, fallback);
            return clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? clean[..^4]
                : clean;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}
