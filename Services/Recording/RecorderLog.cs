using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Services.Recording
{
    public static class RecorderLog
    {
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static string? _logPath;

        public static async Task InitializeAsync()
        {
            if (!string.IsNullOrWhiteSpace(_logPath))
                return;

            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            StorageFolder logsFolder = await localFolder.CreateFolderAsync(
                "RecorderLogs",
                CreationCollisionOption.OpenIfExists);

            StorageFile file = await logsFolder.CreateFileAsync(
                $"RecorderLog-{DateTime.Now:yyyy-MM-dd}.txt",
                CreationCollisionOption.OpenIfExists);

            _logPath = file.Path;
        }

        public static async Task WriteAsync(string source, string message)
        {
            try
            {
                await InitializeAsync();

                if (string.IsNullOrWhiteSpace(_logPath))
                    return;

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message}{Environment.NewLine}";

                await _gate.WaitAsync();
                try
                {
                    await File.AppendAllTextAsync(_logPath, line, Encoding.UTF8);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch
            {
            }
        }

        public static Task InfoAsync(string source, string message)
        {
            return WriteAsync(source, "INFO: " + message);
        }

        public static Task ErrorAsync(string source, Exception ex, string? context = null)
        {
            string text = context is null
                ? $"ERROR: {ex}"
                : $"ERROR: {context} | {ex}";
            return WriteAsync(source, text);
        }

        public static string? GetCurrentLogPath()
        {
            return _logPath;
        }
    }

    internal static class GameReplayLog
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static string? _logPath;

        public static async Task WriteAsync(string source, string message)
        {
            try
            {
                await Gate.WaitAsync();
                try
                {
                    if (string.IsNullOrWhiteSpace(_logPath))
                    {
                        StorageFolder recorderLogs = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                            "RecorderLogs",
                            CreationCollisionOption.OpenIfExists);
                        StorageFolder replayLogs = await recorderLogs.CreateFolderAsync(
                            "Zink game replay logs",
                            CreationCollisionOption.OpenIfExists);
                        StorageFile file = await replayLogs.CreateFileAsync(
                            $"ZinkGameReplay-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt",
                            CreationCollisionOption.OpenIfExists);
                        _logPath = file.Path;
                    }

                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message}{Environment.NewLine}";
                    await File.AppendAllTextAsync(_logPath, line, Encoding.UTF8);
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch
            {
                // Diagnostics must never affect replay capture or saving.
            }
        }
    }
}
