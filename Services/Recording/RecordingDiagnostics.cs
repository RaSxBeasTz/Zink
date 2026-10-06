using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Services.Recording
{
    internal static class RecordingDiagnostics
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false
        };

        public static Task WriteHdrAsync(string eventName, object details)
            => WriteAsync("HDR", eventName, details);

        public static Task WriteRecordingAsync(string eventName, object details)
            => WriteAsync("Recording", eventName, details);

        private static async Task WriteAsync(string category, string eventName, object details)
        {
            try
            {
                await Gate.WaitAsync();
                try
                {
                    string root = Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        "RecordingDiagnostics",
                        category);
                    Directory.CreateDirectory(root);

                    string path = Path.Combine(root, $"{category}-{DateTime.Now:yyyy-MM-dd}.jsonl");
                    var entry = new Dictionary<string, object?>
                    {
                        ["timestampUtc"] = DateTimeOffset.UtcNow,
                        ["event"] = eventName,
                        ["details"] = details
                    };
                    string json = JsonSerializer.Serialize(entry, JsonOptions);
                    await File.AppendAllTextAsync(path, json + Environment.NewLine);
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch
            {
                // Diagnostics must never stop replay capture or saving.
            }
        }
    }
}
