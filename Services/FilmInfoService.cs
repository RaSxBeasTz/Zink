using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services
{
    public sealed class FilmInfoResult
    {
        public string SearchTitle { get; set; } = "";
        public string Title { get; set; } = "";
        public string ReleaseDateText { get; set; } = "";
        public string Overview { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string SourceUrl { get; set; } = "";
    }

    public static class FilmInfoService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly Dictionary<string, FilmInfoResult?> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim LookupGate = new(4, 4);

        static FilmInfoService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Zink/1.0 film-info lookup");
        }

        public static async Task<FilmInfoResult?> GetFilmInfoAsync(string title)
        {
            var searchTitle = CleanMovieSearchTitle(title);
            if (string.IsNullOrWhiteSpace(searchTitle))
                return null;

            if (Cache.TryGetValue(searchTitle, out var cached))
                return cached;

            await LookupGate.WaitAsync();
            try
            {
                if (Cache.TryGetValue(searchTitle, out cached))
                    return cached;

                var result = await TryFindFilmInfoAsync(searchTitle);
                Cache[searchTitle] = result;
                return result;
            }
            catch
            {
                Cache[searchTitle] = null;
                return null;
            }
            finally
            {
                LookupGate.Release();
            }
        }

        private static async Task<FilmInfoResult?> TryFindFilmInfoAsync(string searchTitle)
        {
            var imdb = await TryFindImdbSuggestionAsync(searchTitle);
            var result = new FilmInfoResult
            {
                SearchTitle = searchTitle,
                Title = imdb?.Title ?? searchTitle,
                ReleaseDateText = imdb?.Year > 0 ? $"Released {imdb.Year}" : ""
            };

            var wiki = await TryFindWikipediaSummaryAsync(result.Title, imdb?.Year);
            if (wiki != null)
            {
                result.Title = string.IsNullOrWhiteSpace(wiki.Title) ? result.Title : wiki.Title;
                result.Overview = wiki.Extract;
                result.SourceName = "Wikipedia";
                result.SourceUrl = wiki.Url;

                var exactReleaseDate = await TryFindWikidataReleaseDateAsync(wiki.WikibaseItemId);
                if (!string.IsNullOrWhiteSpace(exactReleaseDate))
                    result.ReleaseDateText = exactReleaseDate;
            }

            return string.IsNullOrWhiteSpace(result.ReleaseDateText) &&
                   string.IsNullOrWhiteSpace(result.Overview)
                ? null
                : result;
        }

        private static async Task<ImdbSuggestion?> TryFindImdbSuggestionAsync(string searchTitle)
        {
            var suggestionKey = BuildImdbSuggestionKey(searchTitle);
            if (string.IsNullOrWhiteSpace(suggestionKey))
                return null;

            var url = $"https://v3.sg.media-imdb.com/suggestion/{suggestionKey[0]}/{Uri.EscapeDataString(suggestionKey)}.json";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("d", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            ImdbSuggestion? fallback = null;
            var wanted = NormalizeMovieTitleForMatch(searchTitle);

            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("l", out var titleElement) ||
                    titleElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var title = titleElement.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var q = TryGetString(result, "q");
                var isFeature = string.IsNullOrWhiteSpace(q) ||
                    q.Contains("feature", StringComparison.OrdinalIgnoreCase) ||
                    q.Contains("movie", StringComparison.OrdinalIgnoreCase);

                if (!isFeature)
                    continue;

                var suggestion = new ImdbSuggestion(title, TryGetInt(result, "y"));
                fallback ??= suggestion;

                var candidate = NormalizeMovieTitleForMatch(title);
                if (string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase))
                    return suggestion;
            }

            return fallback;
        }

        private static async Task<WikipediaSummary?> TryFindWikipediaSummaryAsync(string title, int? year)
        {
            var pageTitle = await TryFindWikipediaPageTitleAsync(title, year);
            if (string.IsNullOrWhiteSpace(pageTitle))
                return null;

            var url = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(pageTitle)}";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var extract = TryGetString(doc.RootElement, "extract");
            if (string.IsNullOrWhiteSpace(extract))
                return null;

            string sourceUrl = "";
            if (doc.RootElement.TryGetProperty("content_urls", out var urls) &&
                urls.TryGetProperty("desktop", out var desktop))
            {
                sourceUrl = TryGetString(desktop, "page");
            }

            var wikibaseItemId = TryGetString(doc.RootElement, "wikibase_item");
            if (string.IsNullOrWhiteSpace(wikibaseItemId))
                wikibaseItemId = await TryFindWikipediaWikibaseItemIdAsync(pageTitle);

            return new WikipediaSummary(
                TryGetString(doc.RootElement, "title"),
                extract.Trim(),
                sourceUrl,
                wikibaseItemId);
        }

        private static async Task<string> TryFindWikipediaWikibaseItemIdAsync(string pageTitle)
        {
            var url = "https://en.wikipedia.org/w/api.php?action=query&prop=pageprops&format=json&titles=" +
                      Uri.EscapeDataString(pageTitle);
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return "";

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages) ||
                pages.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            foreach (var page in pages.EnumerateObject())
            {
                if (page.Value.TryGetProperty("pageprops", out var pageprops))
                    return TryGetString(pageprops, "wikibase_item");
            }

            return "";
        }

        private static async Task<string> TryFindWikidataReleaseDateAsync(string wikibaseItemId)
        {
            if (string.IsNullOrWhiteSpace(wikibaseItemId))
                return "";

            var url = $"https://www.wikidata.org/wiki/Special:EntityData/{Uri.EscapeDataString(wikibaseItemId)}.json";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return "";

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("entities", out var entities) ||
                !entities.TryGetProperty(wikibaseItemId, out var entity) ||
                !entity.TryGetProperty("claims", out var claims) ||
                !claims.TryGetProperty("P577", out var releaseDates) ||
                releaseDates.ValueKind != JsonValueKind.Array)
            {
                return "";
            }

            foreach (var claim in releaseDates.EnumerateArray())
            {
                if (!claim.TryGetProperty("mainsnak", out var mainsnak) ||
                    !mainsnak.TryGetProperty("datavalue", out var datavalue) ||
                    !datavalue.TryGetProperty("value", out var value))
                {
                    continue;
                }

                var time = TryGetString(value, "time");
                var precision = TryGetInt(value, "precision");
                var formatted = FormatWikidataDate(time, precision);
                if (!string.IsNullOrWhiteSpace(formatted))
                    return $"Released {formatted}";
            }

            return "";
        }

        private static async Task<string?> TryFindWikipediaPageTitleAsync(string title, int? year)
        {
            var queries = new[]
            {
                year > 0 ? $"{title} {year} film" : $"{title} film",
                $"{title} film",
                title
            }
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var query in queries)
            {
                var url = "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=5&srsearch=" +
                          Uri.EscapeDataString(query);
                using var response = await Http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    continue;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (!doc.RootElement.TryGetProperty("query", out var queryElement) ||
                    !queryElement.TryGetProperty("search", out var results) ||
                    results.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var result in results.EnumerateArray())
                {
                    var pageTitle = TryGetString(result, "title");
                    if (string.IsNullOrWhiteSpace(pageTitle))
                        continue;

                    if (pageTitle.Contains("film", StringComparison.OrdinalIgnoreCase) ||
                        NormalizeMovieTitleForMatch(pageTitle).Contains(NormalizeMovieTitleForMatch(title), StringComparison.OrdinalIgnoreCase))
                    {
                        return pageTitle;
                    }
                }
            }

            return null;
        }

        private static string CleanMovieSearchTitle(string title)
        {
            var cleaned = System.IO.Path.GetFileNameWithoutExtension(title ?? string.Empty);
            cleaned = cleaned.Replace('.', ' ').Replace('_', ' ');
            cleaned = Regex.Replace(cleaned, @"\b(480p|720p|1080p|2160p|4k|bluray|brrip|web[- ]?dl|webrip|hdrip|dvdrip|x264|x265|h264|h265|hevc|aac|dts|yts|proper|repack)\b", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\[[^\]]*\]|\{[^\}]*\}", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return cleaned;
        }

        private static string BuildImdbSuggestionKey(string title)
        {
            return Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        }

        private static string NormalizeMovieTitleForMatch(string title)
        {
            var normalized = title.Replace('&', ' ');
            normalized = Regex.Replace(normalized, @"[^a-zA-Z0-9]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static string TryGetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }

        private static int TryGetInt(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out var number)
                ? number
                : 0;
        }

        private static string FormatWikidataDate(string time, int precision)
        {
            if (string.IsNullOrWhiteSpace(time))
                return "";

            var match = Regex.Match(time, @"^[+]?(\d{1,4})-(\d{2})-(\d{2})");
            if (!match.Success)
                return "";

            var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

            if (precision >= 11 && month >= 1 && month <= 12 && day >= 1 && day <= 31)
                return new DateTime(year, month, day).ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

            if (precision == 10 && month >= 1 && month <= 12)
                return new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture);

            return year.ToString(CultureInfo.InvariantCulture);
        }

        private sealed record ImdbSuggestion(string Title, int Year);
        private sealed record WikipediaSummary(string Title, string Extract, string Url, string WikibaseItemId);
    }
}
