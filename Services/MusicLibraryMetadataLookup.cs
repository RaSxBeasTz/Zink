using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace Zink.Services
{
    // Library imports share one queue and cache so a folder scan cannot flood
    // the search service with separate metadata and artwork requests.
    internal sealed class MusicLibraryMetadataLookup
    {
        public static MusicLibraryMetadataLookup Instance { get; } = new(
            new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));

        private readonly HttpClient _http;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _retryDelay;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, CachedResponse> _responses = new(StringComparer.OrdinalIgnoreCase);
        private string _cachePath;
        private DateTimeOffset _nextRequest;
        private DateTimeOffset _blockedUntil;

        private sealed class CachedResponse
        {
            public string Json { get; set; }
            public DateTimeOffset StoredAt { get; set; }
        }

        public void EnableDiskCache(string path)
        {
            if (_cachePath != null)
                return;
            _cachePath = path;
            try
            {
                if (!File.Exists(path)) return;
                var stored = JsonSerializer.Deserialize<Dictionary<string, CachedResponse>>(File.ReadAllText(path));
                if (stored != null)
                    foreach (var item in stored)
                        if (item.Value?.Json != null && DateTimeOffset.UtcNow - item.Value.StoredAt < TimeSpan.FromDays(7))
                            _responses[item.Key] = item.Value;
            }
            catch { }
        }

        internal MusicLibraryMetadataLookup(HttpClient http, TimeSpan interval, TimeSpan retryDelay)
        {
            _http = http;
            _interval = interval;
            _retryDelay = retryDelay;
        }

        public async Task<string> SearchAsync(string term, Action<string> log)
        {
            term = term.Trim();
            await _gate.WaitAsync();
            try
            {
                if (_responses.TryGetValue(term, out var cached) &&
                    DateTimeOffset.UtcNow - cached.StoredAt < TimeSpan.FromDays(7))
                {
                    log($"Lookup cache hit: '{term}'.");
                    return cached.Json;
                }
                if (_blockedUntil > DateTimeOffset.UtcNow)
                    throw new InvalidOperationException(
                        $"Music lookup service is cooling down until {_blockedUntil.ToLocalTime():HH:mm:ss zzz}; retry the scan afterwards.");

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var wait = _nextRequest - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait);

                    var url = "https://itunes.apple.com/search?media=music&entity=song&limit=10&term=" +
                        Uri.EscapeDataString(term);
                    _nextRequest = DateTimeOffset.UtcNow + _interval;
                    using var response = await _http.GetAsync(url);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                        response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        var delay = response.Headers.RetryAfter?.Delta ??
                            (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ??
                            TimeSpan.FromTicks(_retryDelay.Ticks * (attempt + 1));
                        if (delay < _interval)
                            delay = _interval;
                        _nextRequest = DateTimeOffset.UtcNow + delay;
                        log($"Lookup blocked ({(int)response.StatusCode}): '{term}'; " +
                            $"cooldown {delay.TotalSeconds:0}s; attempt {attempt + 1}/3.");
                        if (attempt < 2)
                            continue;
                        // Stop the rest of this scan repeatedly hitting a blocked
                        // service. Cached results can still be used during cooldown.
                        _blockedUntil = _nextRequest;
                    }

                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync();
                    _responses[term] = new CachedResponse { Json = json, StoredAt = DateTimeOffset.UtcNow };
                    if (_cachePath != null)
                    {
                        try
                        {
                            var temporaryPath = _cachePath + ".tmp";
                            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_responses));
                            File.Move(temporaryPath, _cachePath, overwrite: true);
                        }
                        catch (Exception ex) { log($"Lookup disk cache could not be saved: {ex.Message}"); }
                    }
                    return json;
                }

                throw new InvalidOperationException("Music lookup retry limit reached.");
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
