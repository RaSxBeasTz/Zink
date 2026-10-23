using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZinkLyrics;

/// <summary>Uses local lyrics first, then a persistent cache, then LRCLIB metadata lookup.</summary>
public sealed class LyricsReader
{
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _client;
    private readonly string _cacheDirectory;

    public LyricsReader(string cacheDirectory, HttpClient? client = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = cacheDirectory;
        _client = client ?? DefaultClient;
    }

    public async Task<LyricsResult> ReadAsync(string songPath, string? title, string? artist,
        CancellationToken cancellationToken = default)
    {
        var local = await LocalLyricsReader.ReadAsync(songPath, cancellationToken);
        if (local.HasLyrics) return local;

        string? album = null;
        double duration = 0;
        await Task.Run(() =>
        {
            try
            {
                using var tags = TagLib.File.Create(songPath);
                if (!string.IsNullOrWhiteSpace(tags.Tag.Title)) title = tags.Tag.Title;
                if (tags.Tag.Performers.Length > 0) artist = string.Join(", ", tags.Tag.Performers);
                album = tags.Tag.Album;
                duration = tags.Properties.Duration.TotalSeconds;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or TagLib.CorruptFileException or TagLib.UnsupportedFormatException) { }
        }, cancellationToken);

        string originalFilenameTitle = Path.GetFileNameWithoutExtension(songPath).Replace('_', ' ');
        string filenameTitle = Regex.Replace(originalFilenameTitle,
            @"^\s*\d{1,3}\s*[-.]\s+", "").Trim();
        if (!IsUnknown(title) && title!.EndsWith(Path.GetExtension(songPath), StringComparison.OrdinalIgnoreCase)
            && Normalize(title) == Normalize(Path.GetFileName(songPath).Replace('_', ' ')))
            title = Path.GetFileNameWithoutExtension(title);
        var filenameParts = Regex.Split(filenameTitle, @"\s+[-\u2013\u2014]\s+", RegexOptions.None, TimeSpan.FromSeconds(1));
        var filenameArtists = filenameParts.Length >= 2 ? SplitArtists(filenameParts[0]) : [];
        bool inferredArtist = false;
        if (IsUnknown(artist))
        {
            artist = "";
            var parts = Regex.Split(filenameTitle, @"\s+[-\u2013\u2014]\s+", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                artist = parts[0].Trim();
                if (IsUnknown(title) || Normalize(title!) == Normalize(filenameTitle)
                    || Normalize(title!) == Normalize(originalFilenameTitle))
                    title = string.Join(" - ", parts.Skip(1)).Trim();
                inferredArtist = true;
            }
        }
        else if (IsUnknown(title) || Normalize(title!) == Normalize(originalFilenameTitle))
        {
            var parts = Regex.Split(filenameTitle, @"\s+[-\u2013\u2014]\s+", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (parts.Length >= 2 && Normalize(parts[0]) == Normalize(artist!))
                title = string.Join(" - ", parts.Skip(1)).Trim();
        }
        if (IsUnknown(title)) title = filenameTitle;
        if (string.IsNullOrWhiteSpace(title))
            return new LyricsResult("", "The filename has no song title to search for.");

        var artistHints = SplitArtists(artist ?? "");
        if (filenameArtists.Length > 0 && (artistHints.Length == 0
            || artistHints.Any(hint => filenameArtists.Any(name => Normalize(name) == Normalize(hint)))))
            artistHints = filenameArtists.Concat(artistHints).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Keep the previous cache key so existing downloads remain usable offline.
        if (artistHints.Length > 1)
        {
            artist = artistHints[^1];
            inferredArtist = true;
        }

        string signature = JsonSerializer.Serialize(new[] { title.Trim(), artist!.Trim(), album ?? "",
            duration.ToString("F2", CultureInfo.InvariantCulture) });
        string cachePath = Path.Combine(_cacheDirectory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))) + ".json");
        LyricsResult? cachedFallback = null;
        try
        {
            var cached = JsonSerializer.Deserialize<LyricsResult>(
                await File.ReadAllTextAsync(cachePath, cancellationToken));
            if (cached is not null && (cached.HasLyrics || cached.Source == "Instrumental track (LRCLIB)"))
            {
                cachedFallback = cached with { Source = cached.Source + " (offline cache)" };
                if (cached.CacheVersion >= 3) return cachedFallback;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        var callerCancellation = cancellationToken;
        using var lookupBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookupBudget.CancelAfter(TimeSpan.FromSeconds(8));
        cancellationToken = lookupBudget.Token;
        artistHints = await ArtistCreditResolver.ResolveAsync(_client, title, artistHints, duration, cancellationToken);
        if (artistHints.Length > 0) artist = artistHints[0];

        bool searchByTitle = string.IsNullOrWhiteSpace(artist);
        string url = searchByTitle
            ? "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title.Trim())
            : "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title.Trim())
                + "&artist_name=" + Uri.EscapeDataString(artist.Trim());
        if (!string.IsNullOrWhiteSpace(album)) url += "&album_name=" + Uri.EscapeDataString(album);
        if (duration >= 1 && duration <= 3600)
            url += "&duration=" + duration.ToString("F2", CultureInfo.InvariantCulture);
        try
        {
            var payload = await FetchAsync(url, cancellationToken);
            if (payload is null && artistHints.Length > 0)
            {
                // Artist-filtered search handles records that credit collaborators differently.
                foreach (string hint in artistHints)
                {
                    await Task.Delay(250, cancellationToken);
                    var search = await FetchAsync("https://lrclib.net/api/search?track_name="
                        + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(hint), cancellationToken);
                    if (search.HasValue && search.Value.ValueKind == JsonValueKind.Array
                        && MatchingCandidates(search.Value, title, duration, artistHints).Length > 0)
                    {
                        payload = search;
                        searchByTitle = true;
                        break;
                    }
                }
            }
            // A dash may belong to a song title, rather than separate its artist.
            if (payload is null && inferredArtist)
            {
                await Task.Delay(250, cancellationToken);
                title = filenameTitle;
                searchByTitle = true;
                artistHints = [];
                payload = await FetchAsync("https://lrclib.net/api/search?track_name="
                    + Uri.EscapeDataString(title), cancellationToken);
            }
            if (payload is null)
                return cachedFallback ?? new LyricsResult("", "No matching lyrics found for this song. Add a local .lrc or .txt file.");
            JsonElement root = payload.Value;
            if (searchByTitle)
            {
                var candidates = MatchingCandidates(root, title, duration, artistHints);
                if (candidates.Length == 0)
                    return cachedFallback ?? new LyricsResult("", "No matching lyrics found from the filename. Try naming it Artist - Song, or add local lyrics.");
                if (artistHints.Length == 0
                    && candidates.Select(item => Normalize(GetString(item, "artistName"))).Distinct().Count() > 1)
                    return cachedFallback ?? new LyricsResult("", "Several artists match this filename. Name it Artist - Song to identify the right lyrics.");
                root = candidates.OrderByDescending(item => ArtistScore(GetString(item, "artistName"), artistHints))
                    .ThenByDescending(item => item.TryGetProperty("hasWordSync", out var value)
                        && value.ValueKind == JsonValueKind.True)
                    .ThenByDescending(item => !string.IsNullOrWhiteSpace(GetString(item, "syncedLyrics")))
                    .First();
            }
            bool instrumental = root.TryGetProperty("instrumental", out var flag) && flag.ValueKind == JsonValueKind.True;
            string plain = root.TryGetProperty("plainLyrics", out var plainValue) ? plainValue.GetString() ?? "" : "";
            string synced = root.TryGetProperty("syncedLyrics", out var syncedValue) ? syncedValue.GetString() ?? "" : "";
            string? lyricsFile = root.TryGetProperty("lyricsfile", out var fileValue) ? fileValue.GetString() : null;
            var fileLines = TimedLyrics.ParseLyricsFile(lyricsFile);
            string displayText = !string.IsNullOrWhiteSpace(plain) ? plain
                : !string.IsNullOrWhiteSpace(synced) ? LocalLyricsReader.FormatLyrics(synced)
                : string.Join(Environment.NewLine, fileLines.Select(line => line.Text));
            var result = new LyricsResult(displayText, instrumental
                ? "Instrumental track (LRCLIB)" : searchByTitle || inferredArtist
                    ? "Lyrics from LRCLIB: " + GetString(root, "artistName") + " - " + GetString(root, "trackName")
                    : "Lyrics from LRCLIB")
                { SyncedLyrics = synced, LyricsFile = lyricsFile, CacheVersion = 4 };
            if (!result.HasLyrics && !instrumental)
                return cachedFallback ?? new LyricsResult("", "No lyrics available for this track on LRCLIB.");
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                string temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result), cancellationToken);
                    File.Move(temporary, cachePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return result;
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            return cachedFallback ?? new LyricsResult("", "Lyrics lookup timed out. Local and cached lyrics remain available offline.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return cachedFallback ?? new LyricsResult("", "Online lyrics unavailable. You can add a local .lrc or .txt file.");
        }
    }

    private async Task<JsonElement?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ZinkLyrics/1.0 (+https://zink.app)");
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.Clone();
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static bool IsUnknown(string? value) => string.IsNullOrWhiteSpace(value)
        || value.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("Unknown Title", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static string[] SplitArtists(string value) => Regex.Split(value,
        @"\s*[,;&/]\s*|\s+(?:feat\.?|ft\.?|featuring|with|x)\s+", RegexOptions.IgnoreCase)
        .Select(name => name.Trim()).Where(name => !IsUnknown(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static int ArtistScore(string candidate, string[] hints)
    {
        var names = SplitArtists(candidate).Select(Normalize).ToArray();
        for (int i = 0; i < hints.Length; i++)
            if (names.Contains(Normalize(hints[i])))
                return (hints.Length - i) * 10 + (names.Distinct().Count() == 1 ? 1 : 0);
        return 0;
    }

    private static string NormalizeTitle(string value) => Normalize(Regex.Replace(value,
        @"\s*[\[(](?:with|feat\.?|ft\.?|featuring)\s+[^\])]+[\])]\s*$", "", RegexOptions.IgnoreCase));

    private static JsonElement[] MatchingCandidates(JsonElement payload, string title, double duration, string[] hints) =>
        payload.EnumerateArray()
            .Where(item => NormalizeTitle(GetString(item, "trackName")) == NormalizeTitle(title))
            .Where(item => hints.Length == 0 || ArtistScore(GetString(item, "artistName"), hints) > 0)
            .Where(item => duration <= 0 || (item.TryGetProperty("duration", out var value)
                && value.TryGetDouble(out double seconds) && Math.Abs(seconds - duration) <= 2))
            .ToArray();
}
