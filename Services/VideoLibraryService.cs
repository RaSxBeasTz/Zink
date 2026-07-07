using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Search;

namespace Zink.Services
{
    public sealed class VideoLibraryService
    {
        public static VideoLibraryService Current { get; } = new VideoLibraryService();

        private const string LibraryFileName = "video_library.json";
        private readonly string[] _exts = new[]
        {
            ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv",
            ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".3gp", ".3g2", ".ogv",
            ".vob", ".divx", ".asf"
        };

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly HashSet<string> _folderTokens = new(StringComparer.OrdinalIgnoreCase);

        private DispatcherTimer? _timer;
        private bool _started;
        private int _scanDepth;
        private int _busyDepth;

        public event EventHandler? LibraryChanged;
        public event EventHandler? BusyChanged;

        // ✅ NEW: status events for UI (spinner + message)
        public event EventHandler? ScanStarted;
        public event EventHandler<VideoLibraryScanResult>? ScanFinished;
        public bool IsScanning => _scanDepth > 0;
        public bool IsBusy => _busyDepth > 0;

        private VideoLibraryService() { }

        public void BeginLibraryBusy()
        {
            var wasBusy = IsBusy;
            _busyDepth++;

            if (!wasBusy)
            {
                try { BusyChanged?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        public void EndLibraryBusy()
        {
            var wasBusy = IsBusy;

            if (_busyDepth > 0)
                _busyDepth--;

            if (wasBusy && !IsBusy)
            {
                try { BusyChanged?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        public async Task StartAsync(TimeSpan? interval = null)
        {
            if (_started) return;
            _started = true;

            await LoadTokensAndEnsureSaveShapeAsync();

            // Initial scan on app start
            await RescanAsync(pruneMissing: true);

            _timer = new DispatcherTimer();
            _timer.Interval = interval ?? TimeSpan.FromSeconds(30);
            _timer.Tick += async (_, __) =>
            {
                try { await RescanAsync(pruneMissing: true); } catch { }
            };
            _timer.Start();
        }

        public void Stop()
        {
            try
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer = null;
                }
            }
            catch { }
        }

        public async Task AddFolderTokenAsync(string token)
        {
            await _gate.WaitAsync();
            try
            {
                if (_folderTokens.Add(token))
                {
                    await PersistTokensOnlyAsync();
                }
            }
            catch { }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<List<string>> GetFolderTokensAsync()
        {
            await _gate.WaitAsync();
            try { return _folderTokens.ToList(); }
            catch { return new List<string>(); }
            finally { _gate.Release(); }
        }

        public async Task RescanAsync(bool pruneMissing)
        {
            // Fire "started" before we take the gate (so UI can react quickly)
            _scanDepth++;
            BeginLibraryBusy();
            try { ScanStarted?.Invoke(this, EventArgs.Empty); } catch { }

            int added = 0;
            int removed = 0;

            await _gate.WaitAsync();
            try
            {
                var saved = await LoadSaveAsync() ?? new VideoLibrarySave();

                if (saved.FolderTokens != null)
                {
                    foreach (var t in saved.FolderTokens)
                        if (!string.IsNullOrWhiteSpace(t))
                            _folderTokens.Add(t);
                }

                foreach (var t in saved.Items.Select(i => i.FolderToken).Where(t => !string.IsNullOrWhiteSpace(t)))
                    _folderTokens.Add(t);

                saved.FolderTokens = _folderTokens.ToList();

                bool changed = false;
                HashSet<(string token, string rel)>? seen = pruneMissing ? new HashSet<(string, string)>() : null;
                var existingKeys = saved.Items
                    .Select(v => BuildVideoKey(v.FolderToken, v.RelativePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var token in _folderTokens.ToList())
                {
                    try
                    {
                        var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);

                        var q = new QueryOptions(CommonFileQuery.DefaultQuery, _exts)
                        {
                            FolderDepth = FolderDepth.Deep
                        };

                        var result = folder.CreateFileQueryWithOptions(q);
                        var files = await result.GetFilesAsync();

                        foreach (var file in files)
                        {
                            var rel = GetRelativePathSafe(folder.Path, file.Path);

                            if (pruneMissing)
                                seen!.Add((token, rel));

                            if (!existingKeys.Add(BuildVideoKey(token, rel)))
                                continue;

                            saved.Items.Add(new VideoLibraryEntry
                            {
                                Name = file.DisplayName,
                                FileName = file.Name,
                                FolderToken = token,
                                RelativePath = rel
                            });

                            added++;
                            changed = true;
                        }
                    }
                    catch { }
                }

                if (pruneMissing && seen != null)
                {
                    for (int i = saved.Items.Count - 1; i >= 0; i--)
                    {
                        var it = saved.Items[i];
                        if (!seen.Contains((it.FolderToken, it.RelativePath)))
                        {
                            saved.Items.RemoveAt(i);
                            removed++;
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    await SaveAsync(saved);
                    try { LibraryChanged?.Invoke(this, EventArgs.Empty); } catch { }
                }
            }
            catch { }
            finally
            {
                _gate.Release();
                if (_scanDepth > 0)
                    _scanDepth--;
                EndLibraryBusy();

                // ✅ Notify UI we’re done (even if nothing changed)
                try
                {
                    ScanFinished?.Invoke(this, new VideoLibraryScanResult(added, removed));
                }
                catch { }
            }
        }

        private static string GetRelativePathSafe(string baseDir, string fullPath)
        {
            try { return Path.GetRelativePath(baseDir, fullPath); }
            catch { return Path.GetFileName(fullPath); }
        }

        private static string BuildVideoKey(string folderToken, string relativePath)
        {
            return $"{folderToken}|{relativePath}";
        }

        private async Task LoadTokensAndEnsureSaveShapeAsync()
        {
            try
            {
                var saved = await LoadSaveAsync();
                if (saved == null) return;

                if (saved.FolderTokens != null)
                {
                    foreach (var t in saved.FolderTokens)
                        if (!string.IsNullOrWhiteSpace(t))
                            _folderTokens.Add(t);
                }

                foreach (var t in saved.Items.Select(i => i.FolderToken).Where(t => !string.IsNullOrWhiteSpace(t)))
                    _folderTokens.Add(t);

                saved.FolderTokens = _folderTokens.ToList();
                await SaveAsync(saved);
            }
            catch { }
        }

        private async Task PersistTokensOnlyAsync()
        {
            try
            {
                var saved = await LoadSaveAsync() ?? new VideoLibrarySave();
                saved.FolderTokens = _folderTokens.ToList();
                await SaveAsync(saved);
                try { LibraryChanged?.Invoke(this, EventArgs.Empty); } catch { }
            }
            catch { }
        }

        private async Task<VideoLibrarySave?> LoadSaveAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(LibraryFileName) as StorageFile;
                if (file == null) return null;

                using var s = await file.OpenReadAsync();
                using var stream = s.AsStreamForRead();
                return await JsonSerializer.DeserializeAsync<VideoLibrarySave>(stream);
            }
            catch
            {
                return null;
            }
        }

        private async Task SaveAsync(VideoLibrarySave save)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(LibraryFileName, CreationCollisionOption.ReplaceExisting);
                using var s = await file.OpenStreamForWriteAsync();
                await JsonSerializer.SerializeAsync(s, save, new JsonSerializerOptions { WriteIndented = true });
                await s.FlushAsync();
            }
            catch { }
        }

        public sealed class VideoLibrarySave
        {
            public List<string> FolderTokens { get; set; } = new();
            public List<VideoLibraryEntry> Items { get; set; } = new();
        }

        public sealed class VideoLibraryEntry
        {
            public string Name { get; set; } = "";
            public string FileName { get; set; } = "";
            public string FolderToken { get; set; } = "";
            public string RelativePath { get; set; } = "";
        }
    }

    // ✅ NEW: strongly typed result for UI messaging
    public sealed class VideoLibraryScanResult
    {
        public int Added { get; }
        public int Removed { get; }

        public VideoLibraryScanResult(int added, int removed)
        {
            Added = added;
            Removed = removed;
        }
    }
}
