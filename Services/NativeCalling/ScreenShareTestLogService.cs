using System;
using System.IO;
using System.Text;

namespace Zink.Services.NativeCalling
{
    public static class ScreenShareTestLogService
    {
        private static readonly object SyncRoot = new();
        private static StreamWriter? _writer;

        public static bool IsRunning
        {
            get
            {
                lock (SyncRoot)
                    return _writer != null;
            }
        }

        public static string? CurrentPath { get; private set; }

        public static string Start()
        {
            lock (SyncRoot)
            {
                if (_writer != null && CurrentPath != null)
                    return CurrentPath;

                Directory.CreateDirectory(DiagnosticLogService.LogDirectoryPath);
                CurrentPath = Path.Combine(
                    DiagnosticLogService.LogDirectoryPath,
                    $"zink-screen-share-test-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                _writer = new StreamWriter(CurrentPath, false, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                WriteLineLocked("=== Zink screen-share test started ===");
                WriteLineLocked($"Device={Environment.MachineName}; time={DateTimeOffset.Now:O}");
                return CurrentPath;
            }
        }

        public static void WriteLine(string? message)
        {
            lock (SyncRoot)
            {
                if (_writer == null)
                    return;

                WriteLineLocked(message);
            }
        }

        public static void Stop()
        {
            lock (SyncRoot)
            {
                if (_writer == null)
                    return;

                WriteLineLocked("=== Zink screen-share test stopped ===");
                _writer.Dispose();
                _writer = null;
            }
        }

        private static void WriteLineLocked(string? message)
        {
            try
            {
                _writer?.WriteLine($"{DateTimeOffset.Now:O} {message}");
            }
            catch
            {
            }
        }
    }
}
