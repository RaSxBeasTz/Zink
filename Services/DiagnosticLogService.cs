using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Services
{
    public static class DiagnosticLogService
    {
        public const string EnabledSettingKey = "Zink.Diagnostics.FileLoggingEnabled";

        private const long MaxLogBytes = 12L * 1024L * 1024L;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SnapshotPublishInterval = TimeSpan.FromSeconds(30);
        private static readonly object SyncRoot = new();
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private static FileStream? _deviceFileStream;
        private static StreamWriter? _deviceWriter;
        private static ZinkTraceListener? _traceListener;
        private static TextWriter? _originalConsoleOut;
        private static TextWriter? _originalConsoleError;
        private static Timer? _flushTimer;
        private static bool _initialized;
        private static string? _activeLogDirectoryPath;
        private static DateTimeOffset _lastSnapshotPublishedUtc = DateTimeOffset.MinValue;

        public static bool IsEnabled { get; private set; }

        public static string LogDirectoryPath
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_activeLogDirectoryPath))
                    return _activeLogDirectoryPath;

                foreach (var candidate in GetLogDirectoryCandidates())
                    return candidate;

                return Path.Combine(AppContext.BaseDirectory, "Logs");
            }
        }

        public static string DeviceName => SanitizeFileName(Environment.MachineName, "unknown-device");

        public static string CurrentLogPath => CurrentDeviceLogPath;

        public static string CurrentDeviceLogPath => Path.Combine(LogDirectoryPath, $"zink-diagnostics-{DeviceName}-latest.txt");

        public static event EventHandler? StateChanged;

        public static void InitializeFromSettings()
        {
            lock (SyncRoot)
            {
                if (_initialized)
                    return;

                _initialized = true;
            }

            try
            {
                ApplicationData.Current.LocalSettings.Values[EnabledSettingKey] = true;
            }
            catch
            {
            }

            EnableFileLogging();
        }

        public static bool GetEnabledSetting()
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values[EnabledSettingKey] is bool enabled)
                    return enabled;
            }
            catch
            {
            }

            return true;
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[EnabledSettingKey] = true;
            }
            catch
            {
            }

            if (!enabled)
                WriteLine("Diagnostic file logging disable request ignored while stream diagnostics are required.");

            EnableFileLogging();

            StateChanged?.Invoke(null, EventArgs.Empty);
        }

        public static async Task<StorageFolder> GetLogFolderAsync()
        {
            Directory.CreateDirectory(LogDirectoryPath);
            return await StorageFolder.GetFolderFromPathAsync(LogDirectoryPath);
        }

        public static void ClearCurrentLog()
        {
            lock (SyncRoot)
            {
                CloseWriterLocked();

                try
                {
                    if (File.Exists(CurrentLogPath))
                        File.Delete(CurrentLogPath);

                }
                catch
                {
                }

                if (IsEnabled)
                {
                    OpenWriterLocked();
                    WriteLineLocked("Diagnostic log cleared.");
                    PublishLogSnapshotsLocked(force: true);
                }
            }
        }

        public static void WriteLine(string? message)
        {
            lock (SyncRoot)
            {
                WriteLineLocked(message);
            }
        }

        public static void Flush()
        {
            lock (SyncRoot)
            {
                try
                {
                    _deviceWriter?.Flush();
                    _deviceFileStream?.Flush(true);
                    PublishLogSnapshotsLocked(force: true);
                }
                catch
                {
                }
            }
        }

        public static void EnsureLogFile(string? reason = null)
        {
            InitializeFromSettings();

            lock (SyncRoot)
            {
                if (!IsEnabled)
                    EnableFileLogging();

                if (_deviceWriter == null)
                    OpenWriterLocked();

                if (!string.IsNullOrWhiteSpace(reason))
                    WriteLineLocked("Diagnostic log file ensured: " + reason);
            }
        }

        public static IReadOnlyList<string> GetKnownLogDirectoryPaths()
        {
            return new List<string>(GetLogDirectoryCandidates());
        }

        private static void EnableFileLogging()
        {
            lock (SyncRoot)
            {
                if (IsEnabled && _deviceWriter != null)
                    return;

                IsEnabled = true;
                OpenWriterLocked();
                InstallListenersLocked();
                WriteLineLocked("=== Zink diagnostic logging enabled ===");
                WriteLineLocked("Device: " + Environment.MachineName);
                WriteLineLocked("Log file: " + CurrentLogPath);
                WriteLineLocked("App base: " + AppContext.BaseDirectory);
                WriteLineLocked("Log directory: " + LogDirectoryPath);
                WriteLineLocked("Log mirrors: " + string.Join(" | ", GetLogDirectoryCandidates()));
                WriteLineLocked("User: " + Environment.UserName);
                WriteLineLocked("OS: " + Environment.OSVersion);
                PublishLogSnapshotsLocked(force: true);
                StartFlushTimerLocked();
            }
        }

        private static void DisableFileLogging()
        {
            lock (SyncRoot)
            {
                WriteLineLocked("=== Zink diagnostic logging disabled ===");
                StopFlushTimerLocked();
                UninstallListenersLocked();
                CloseWriterLocked();
                IsEnabled = false;
            }
        }

        private static void StartFlushTimerLocked()
        {
            if (_flushTimer != null)
                return;

            _flushTimer = new Timer(
                _ => FlushFromTimer(),
                null,
                FlushInterval,
                FlushInterval);
        }

        private static void StopFlushTimerLocked()
        {
            try
            {
                _flushTimer?.Dispose();
            }
            catch
            {
            }

            _flushTimer = null;
        }

        private static void FlushFromTimer()
        {
            lock (SyncRoot)
            {
                if (!IsEnabled || _deviceWriter == null)
                    return;

                try
                {
                    _deviceWriter.Flush();
                    _deviceFileStream?.Flush(false);
                    PublishLogSnapshotsLocked(force: false);
                }
                catch
                {
                }
            }
        }

        private static void OpenWriterLocked()
        {
            CloseWriterLocked();

            var failures = new List<string>();
            foreach (var logDirectory in GetActiveLogDirectoryCandidates())
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                    var deviceLogPath = Path.Combine(logDirectory, $"zink-diagnostics-{DeviceName}-latest.txt");
                    ArchiveExistingLatestLogLocked(deviceLogPath, "zink-diagnostics-" + DeviceName);

                    _deviceFileStream = new FileStream(deviceLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    _deviceWriter = new StreamWriter(_deviceFileStream, Utf8NoBom)
                    {
                        AutoFlush = false
                    };
                    _activeLogDirectoryPath = logDirectory;

                    if (failures.Count > 0)
                    {
                        WriteLineLocked("Diagnostic logging recovered using fallback directory: " + logDirectory);
                        foreach (var failure in failures)
                            WriteLineLocked("Log directory candidate failed: " + failure);
                    }

                    return;
                }
                catch (Exception ex)
                {
                    failures.Add($"{logDirectory}: {ex.GetType().Name}: {ex.Message}");
                    CloseWriterLocked();
                }
            }

            IsEnabled = false;
        }

        private static IEnumerable<string> GetActiveLogDirectoryCandidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (seen.Add(fullPath))
                        candidates.Add(fullPath);
                }
                catch
                {
                }
            }

            try
            {
                Add(Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs"));
            }
            catch
            {
            }

            Add(Path.Combine(AppContext.BaseDirectory, "Logs"));

            foreach (var mirror in GetLogDirectoryCandidates())
                Add(mirror);

            return candidates;
        }

        private static void CloseWriterLocked()
        {
            try
            {
                _deviceWriter?.Flush();
                _deviceFileStream?.Flush(true);
            }
            catch
            {
            }

            try
            {
                _deviceWriter?.Dispose();
            }
            catch
            {
            }

            try
            {
                _deviceFileStream?.Dispose();
            }
            catch
            {
            }

            _deviceWriter = null;
            _deviceFileStream = null;
        }

        private static void InstallListenersLocked()
        {
            if (_traceListener == null)
            {
                _traceListener = new ZinkTraceListener();
                Trace.Listeners.Add(_traceListener);
                Trace.AutoFlush = true;
            }

            if (_originalConsoleOut == null)
            {
                _originalConsoleOut = Console.Out;
                Console.SetOut(new ZinkConsoleWriter(_originalConsoleOut, isError: false));
            }

            if (_originalConsoleError == null)
            {
                _originalConsoleError = Console.Error;
                Console.SetError(new ZinkConsoleWriter(_originalConsoleError, isError: true));
            }
        }

        private static void UninstallListenersLocked()
        {
            if (_traceListener != null)
            {
                try
                {
                    Trace.Listeners.Remove(_traceListener);
                    _traceListener.Dispose();
                }
                catch
                {
                }

                _traceListener = null;
            }

            if (_originalConsoleOut != null)
            {
                try
                {
                    Console.SetOut(_originalConsoleOut);
                }
                catch
                {
                }

                _originalConsoleOut = null;
            }

            if (_originalConsoleError != null)
            {
                try
                {
                    Console.SetError(_originalConsoleError);
                }
                catch
                {
                }

                _originalConsoleError = null;
            }
        }

        private static void ArchiveExistingLatestLogLocked(string path, string archivePrefix)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
                return;

            var archiveName = archivePrefix + "-" +
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                ".txt";
            var archiveDirectory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(archiveDirectory))
                archiveDirectory = LogDirectoryPath;

            var archivePath = Path.Combine(archiveDirectory, archiveName);

            if (File.Exists(archivePath))
            {
                archivePath = Path.Combine(
                    archiveDirectory,
                    archivePrefix + "-" +
                    DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                    ".txt");
            }

            File.Move(path, archivePath);
        }

        private static void WriteLineLocked(string? message)
        {
            if (!IsEnabled)
                return;

            try
            {
                if (_deviceWriter == null)
                    OpenWriterLocked();

                if (_deviceWriter == null)
                    return;

                var normalized = (message ?? string.Empty)
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n');

                var lines = normalized.Split('\n');
                foreach (var line in lines)
                {
                    var entry = $"{Timestamp()} [{DeviceName}] [T{Environment.CurrentManagedThreadId}] {line}";
                    _deviceWriter?.WriteLine(entry);
                }

                PublishLogSnapshotsLocked(force: false);
            }
            catch
            {
            }
        }

        private static void PublishLogSnapshotsLocked(bool force)
        {
            var now = DateTimeOffset.UtcNow;
            if (!force && now - _lastSnapshotPublishedUtc < SnapshotPublishInterval)
                return;

            _lastSnapshotPublishedUtc = now;

            var activePath = CurrentDeviceLogPath;
            if (!File.Exists(activePath))
                return;

            try
            {
                _deviceWriter?.Flush();
                _deviceFileStream?.Flush(true);
            }
            catch
            {
            }

            foreach (var logDirectory in GetLogDirectoryCandidates())
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                    var targetPath = Path.Combine(logDirectory, $"zink-diagnostics-{DeviceName}-latest.txt");
                    if (string.Equals(Path.GetFullPath(activePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                        continue;

                    var tempPath = targetPath + ".tmp";
                    File.Copy(activePath, tempPath, overwrite: true);
                    if (File.Exists(targetPath))
                        File.Replace(tempPath, targetPath, null);
                    else
                        File.Move(tempPath, targetPath);
                }
                catch
                {
                }
            }
        }

        private static IEnumerable<string> GetLogDirectoryCandidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? path, List<string> paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (seen.Add(fullPath))
                        paths.Add(fullPath);
                }
                catch
                {
                }
            }

            var candidates = new List<string>();
            Add(TryFindProjectLogDirectory(), candidates);
            Add(GetDocumentsScreenShareLogDirectory(), candidates);

            foreach (var oneDriveRoot in GetOneDriveRoots())
            {
                Add(Path.Combine(oneDriveRoot, "App Projects", "ZINK", "Main file", "Logs"), candidates);
            }

            Add(Path.Combine(AppContext.BaseDirectory, "Logs"), candidates);

            try
            {
                Add(Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs"), candidates);
            }
            catch
            {
            }

            return candidates;
        }

        private static string? GetDocumentsScreenShareLogDirectory()
        {
            try
            {
                var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrWhiteSpace(documentsPath))
                    return null;

                return Path.Combine(documentsPath, "Zink", "Screen Share");
            }
            catch
            {
                return null;
            }
        }

        private static string SanitizeFileName(string? value, string fallback)
        {
            var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(text.Length);

            foreach (var ch in text)
            {
                if (Array.IndexOf(invalidChars, ch) >= 0 || char.IsWhiteSpace(ch))
                    builder.Append('-');
                else
                    builder.Append(ch);
            }

            var sanitized = builder.ToString().Trim('-', '.');
            return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
        }

        private static string? TryFindProjectLogDirectory()
        {
            try
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory != null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "Zink.csproj")) ||
                        File.Exists(Path.Combine(directory.FullName, "Package.appxmanifest")) ||
                        File.Exists(Path.Combine(directory.FullName, "App.xaml.cs")))
                    {
                        return Path.Combine(directory.FullName, "Logs");
                    }

                    directory = directory.Parent;
                }
            }
            catch
            {
            }

            foreach (var oneDriveRoot in GetOneDriveRoots())
            {
                try
                {
                    var knownProjectPath = Path.Combine(oneDriveRoot, "App Projects", "ZINK", "Main file");
                    if (Directory.Exists(knownProjectPath))
                        return Path.Combine(knownProjectPath, "Logs");
                }
                catch
                {
                }
            }

            return null;
        }

        private static IEnumerable<string> GetOneDriveRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            {
                var path = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && seen.Add(path))
                    yield return path;
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile) || !Directory.Exists(userProfile))
                yield break;

            foreach (var candidate in Directory.EnumerateDirectories(userProfile, "OneDrive*"))
            {
                if (Directory.Exists(candidate) && seen.Add(candidate))
                    yield return candidate;
            }
        }

        private static string Timestamp()
        {
            return DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        }

        private sealed class ZinkTraceListener : TraceListener
        {
            public override void Write(string? message)
            {
                if (!string.IsNullOrEmpty(message))
                    DiagnosticLogService.WriteLine(message);
            }

            public override void WriteLine(string? message)
            {
                DiagnosticLogService.WriteLine(message);
            }
        }

        private sealed class ZinkConsoleWriter : TextWriter
        {
            private readonly TextWriter _inner;
            private readonly bool _isError;

            public ZinkConsoleWriter(TextWriter inner, bool isError)
            {
                _inner = inner;
                _isError = isError;
            }

            public override Encoding Encoding => _inner.Encoding;

            public override void WriteLine(string? value)
            {
                try
                {
                    _inner.WriteLine(value);
                }
                catch
                {
                }

                DiagnosticLogService.WriteLine(_isError ? "STDERR: " + value : value);
            }

            public override void Write(string? value)
            {
                try
                {
                    _inner.Write(value);
                }
                catch
                {
                }

                if (!string.IsNullOrWhiteSpace(value))
                    DiagnosticLogService.WriteLine(_isError ? "STDERR: " + value : value);
            }
        }
    }
}
