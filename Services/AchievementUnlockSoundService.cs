using NAudio.Wave;
using System;
using System.Diagnostics;
using System.IO;

namespace Zink.Services
{
    /// <summary>Plays the original ZINK achievement cue once per unlock event.</summary>
    public static class AchievementUnlockSoundService
    {
        private static readonly object SyncRoot = new();
        private static WaveOut? _output;
        private static WaveFileReader? _reader;
        private static DateTimeOffset _lastPlayTime;

        public static void Play()
        {
            lock (SyncRoot)
            {
                // The web and Windows listeners may see the same unlock milliseconds apart.
                if (DateTimeOffset.UtcNow - _lastPlayTime < TimeSpan.FromMilliseconds(900))
                    return;

                _lastPlayTime = DateTimeOffset.UtcNow;

                try
                {
                    StopCore();
                    string path = Path.Combine(
                        AppContext.BaseDirectory,
                        "Assets",
                        "Sounds",
                        "zink-achievement-unlock.wav");

                    if (!File.Exists(path))
                    {
                        Debug.WriteLine($"[AchievementSound] Missing sound asset: {path}");
                        return;
                    }

                    _reader = new WaveFileReader(path);
                    _output = new WaveOut
                    {
                        DeviceNumber = -1,
                        BufferMilliseconds = 70
                    };
                    _output.PlaybackStopped += Output_PlaybackStopped;
                    _output.Init(_reader);
                    _output.Play();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AchievementSound] Playback failed: {ex.Message}");
                    StopCore();
                }
            }
        }

        private static void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            lock (SyncRoot)
                StopCore();
        }

        private static void StopCore()
        {
            if (_output != null)
                _output.PlaybackStopped -= Output_PlaybackStopped;

            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            _output = null;
            _reader = null;
        }
    }
}
