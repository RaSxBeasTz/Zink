using Windows.Storage;

namespace Zink.Services.Recording
{
    public static class RecordingPreferences
    {
        private const string GamingBackgroundReplayEnabledKey = "ZinkGamingBackgroundReplayEnabled";
        private const string HotkeyGameClipRecordingEnabledKey = "ZinkHotkeyGameClipRecordingEnabled";
        private const string BackgroundReplayDurationSecondsKey = "ZinkBackgroundReplayDurationSeconds";
        private const string BackgroundReplayMicrophoneEnabledKey = "ZinkBackgroundReplayMicrophoneEnabled";
        private const string BackgroundReplaySystemAudioEnabledKey = "ZinkBackgroundReplaySystemAudioEnabled";
        private const string BackgroundReplaySeparateTracksEnabledKey = "ZinkBackgroundReplaySeparateTracksEnabled";
        private const string BackgroundReplaySeparateDesktopTrackEnabledKey = "ZinkBackgroundReplaySeparateDesktopTrackEnabled";
        private const string BackgroundReplaySeparateMicrophoneTrackEnabledKey = "ZinkBackgroundReplaySeparateMicrophoneTrackEnabled";
        private const string BackgroundReplayAudioBitrateKbpsKey = "ZinkBackgroundReplayAudioBitrateKbps";

        public static bool IsGamingBackgroundReplayEnabled
        {
            get
            {
                try
                {
                    object value = ApplicationData.Current.LocalSettings.Values[GamingBackgroundReplayEnabledKey];
                    if (value is bool enabled)
                        return enabled;
                }
                catch
                {
                }

                return true;
            }
        }

        public static void SetGamingBackgroundReplayEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[GamingBackgroundReplayEnabledKey] = enabled;
        }

        public static bool IsHotkeyGameClipRecordingEnabled
        {
            get
            {
                try
                {
                    object value = ApplicationData.Current.LocalSettings.Values[HotkeyGameClipRecordingEnabledKey];
                    if (value is bool enabled)
                        return enabled;
                }
                catch
                {
                }

                return true;
            }
        }

        public static void SetHotkeyGameClipRecordingEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[HotkeyGameClipRecordingEnabledKey] = enabled;
        }

        public static int BackgroundReplayDurationSeconds
        {
            get
            {
                try
                {
                    object value = ApplicationData.Current.LocalSettings.Values[BackgroundReplayDurationSecondsKey];
                    if (value is int seconds && IsSupportedReplayDuration(seconds))
                        return seconds;
                }
                catch
                {
                }

                return 45;
            }
        }

        public static void SetBackgroundReplayDurationSeconds(int seconds)
        {
            if (!IsSupportedReplayDuration(seconds))
                seconds = 45;
            ApplicationData.Current.LocalSettings.Values[BackgroundReplayDurationSecondsKey] = seconds;
        }

        public static bool IsBackgroundReplayMicrophoneEnabled
        {
            get
            {
                try
                {
                    object value = ApplicationData.Current.LocalSettings.Values[BackgroundReplayMicrophoneEnabledKey];
                    if (value is bool enabled)
                        return enabled;
                }
                catch
                {
                }

                return true;
            }
        }

        public static void SetBackgroundReplayMicrophoneEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[BackgroundReplayMicrophoneEnabledKey] = enabled;
        }

        public static bool IsBackgroundReplaySystemAudioEnabled =>
            ReadBoolean(BackgroundReplaySystemAudioEnabledKey, true);

        public static void SetBackgroundReplaySystemAudioEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[BackgroundReplaySystemAudioEnabledKey] = enabled;
        }

        public static bool IsBackgroundReplaySeparateTracksEnabled =>
            ReadBoolean(BackgroundReplaySeparateTracksEnabledKey, false);

        public static void SetBackgroundReplaySeparateTracksEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[BackgroundReplaySeparateTracksEnabledKey] = enabled;
        }

        public static bool IsBackgroundReplaySeparateDesktopTrackEnabled =>
            ReadBoolean(BackgroundReplaySeparateDesktopTrackEnabledKey, true);

        public static void SetBackgroundReplaySeparateDesktopTrackEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[BackgroundReplaySeparateDesktopTrackEnabledKey] = enabled;
        }

        public static bool IsBackgroundReplaySeparateMicrophoneTrackEnabled =>
            ReadBoolean(BackgroundReplaySeparateMicrophoneTrackEnabledKey, true);

        public static void SetBackgroundReplaySeparateMicrophoneTrackEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[BackgroundReplaySeparateMicrophoneTrackEnabledKey] = enabled;
        }

        public static int BackgroundReplayAudioBitrateKbps
        {
            get
            {
                try
                {
                    object value = ApplicationData.Current.LocalSettings.Values[BackgroundReplayAudioBitrateKbpsKey];
                    if (value is int bitrate && IsSupportedAudioBitrate(bitrate))
                        return bitrate;
                }
                catch
                {
                }

                return 320;
            }
        }

        public static void SetBackgroundReplayAudioBitrateKbps(int bitrate)
        {
            if (!IsSupportedAudioBitrate(bitrate))
                bitrate = 320;
            ApplicationData.Current.LocalSettings.Values[BackgroundReplayAudioBitrateKbpsKey] = bitrate;
        }

        private static bool ReadBoolean(string key, bool defaultValue)
        {
            try
            {
                object value = ApplicationData.Current.LocalSettings.Values[key];
                if (value is bool enabled)
                    return enabled;
            }
            catch
            {
            }

            return defaultValue;
        }

        private static bool IsSupportedAudioBitrate(int bitrate)
        {
            return bitrate is 256 or 320 or 384;
        }

        private static bool IsSupportedReplayDuration(int seconds)
        {
            return seconds is 30 or 45 or 60 or 120 or 240 or 300 or 420 or 480 or 600;
        }
    }
}
