using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZinkLyrics;

/// <summary>Resolves recording credits rather than guessing artist prominence from filename order.</summary>
internal static class ArtistCreditResolver
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    internal static async Task<string[]> ResolveAsync(HttpClient client, string title, string[] artists,
        double duration, CancellationToken cancellationToken)
    {
        if (artists.Length < 2) return artists;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        var requestCancellation = budget.Token;
        try
        {
            await RequestGate.WaitAsync(requestCancellation);
            JsonElement data;
            try
            {
                var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastRequestUtc);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, requestCancellation);
                _lastRequestUtc = DateTime.UtcNow;
                string query = "recording:\"" + Escape(title) + "\" AND "
                    + string.Join(" AND ", artists.Take(6).Select(name => "artist:\"" + Escape(name) + "\""));
                string url = "https://musicbrainz.org/ws/2/recording/?fmt=json&limit=25&query=" + Uri.EscapeDataString(query);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("ZinkLyrics/1.0 (+https://zink.app)");
                using var response = await client.SendAsync(request, requestCancellation);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(requestCancellation));
                data = json.RootElement.Clone();
            }
            finally { RequestGate.Release(); }
            if (!data.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
                return artists;
            var matches = new List<(string[] Credits, double Difference, int Score)>();
            foreach (var recording in recordings.EnumerateArray())
            {
                if (!recording.TryGetProperty("title", out var name) || NormalizeTitle(name.GetString() ?? "") != NormalizeTitle(title)) continue;
                if (!recording.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) continue;
                var names = credits.EnumerateArray().Select(credit => credit.TryGetProperty("name", out var value)
                    ? value.GetString() ?? "" : "").Where(value => value.Length > 0).ToArray();
                if (!artists.All(artist => names.Any(value => Normalize(value) == Normalize(artist)))) continue;
                double difference = 0;
                if (duration > 0)
                {
                    if (!recording.TryGetProperty("length", out var length) || !length.TryGetDouble(out double milliseconds)) continue;
                    difference = Math.Abs(milliseconds / 1000 - duration);
                    if (difference > 3) continue;
                }
                int score = recording.TryGetProperty("score", out var rank) && rank.TryGetInt32(out int valueScore) ? valueScore : 0;
                if (score < 90) continue;
                matches.Add((names, difference, score));
            }
            var ranked = matches.OrderBy(match => match.Difference).ThenByDescending(match => match.Score).ToArray();
            if (ranked.Length == 0) return artists;
            var best = ranked[0];
            // Conflicting equally good recordings do not justify inventing a lead artist.
            if (ranked.Any(match => Math.Abs(match.Difference - best.Difference) < 0.1
                && match.Score == best.Score && Normalize(match.Credits[0]) != Normalize(best.Credits[0]))) return artists;
            return best.Credits.Concat(artists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return artists; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { return artists; }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private static string NormalizeTitle(string value) => Normalize(Regex.Replace(value,
        @"\s*[\[(](?:with|feat\.?|ft\.?|featuring)\s+[^\])]+[\])]\s*$", "", RegexOptions.IgnoreCase));
}
