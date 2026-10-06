using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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
                if (result != null)
                    Cache[searchTitle] = result;
                return result;
            }
            catch
            {
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
            if (wiki == null && !string.Equals(result.Title, searchTitle, StringComparison.OrdinalIgnoreCase))
                wiki = await TryFindWikipediaSummaryAsync(searchTitle, imdb?.Year);

            if (wiki == null || string.IsNullOrWhiteSpace(wiki.Extract))
                wiki = await TryFindWikidataLinkedWikipediaSummaryAsync(result.Title, searchTitle, imdb?.Year);

            if (wiki == null || string.IsNullOrWhiteSpace(wiki.Extract))
                wiki = await TryFindWikipediaSearchSnippetSummaryAsync(result.Title, searchTitle, imdb?.Year);

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
            var page = await TryFindWikipediaPageAsync(title, year);
            if (page == null || string.IsNullOrWhiteSpace(page.Title))
                return null;

            string sourceUrl = "";
            string titleFromSummary = page.Title;
            string wikibaseItemId = "";
            string extract = "";

            var url = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(page.Title)}";
            using (var response = await Http.GetAsync(url))
            {
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);

                    titleFromSummary = TryGetString(doc.RootElement, "title");
                    if (string.IsNullOrWhiteSpace(titleFromSummary))
                        titleFromSummary = page.Title;

                    extract = TryGetString(doc.RootElement, "extract");

                    if (doc.RootElement.TryGetProperty("content_urls", out var urls) &&
                        urls.TryGetProperty("desktop", out var desktop))
                    {
                        sourceUrl = TryGetString(desktop, "page");
                    }

                    wikibaseItemId = TryGetString(doc.RootElement, "wikibase_item");
                }
            }

            if (string.IsNullOrWhiteSpace(extract))
                extract = await TryFindWikipediaExtractByPageIdAsync(page.PageId);

            if (string.IsNullOrWhiteSpace(wikibaseItemId))
                wikibaseItemId = await TryFindWikipediaWikibaseItemIdAsync(page.Title);

            if (string.IsNullOrWhiteSpace(sourceUrl))
                sourceUrl = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(page.Title.Replace(' ', '_'));

            return new WikipediaSummary(
                titleFromSummary,
                extract.Trim(),
                sourceUrl,
                wikibaseItemId);
        }

        private static async Task<WikipediaSummary?> TryFindWikidataLinkedWikipediaSummaryAsync(string title, string searchTitle, int? year)
        {
            foreach (var query in BuildFilmTitleVariants(title, searchTitle, year))
            {
                var entity = await TryFindWikidataFilmEntityAsync(query, year);
                if (entity == null || string.IsNullOrWhiteSpace(entity.EnwikiTitle))
                    continue;

                var summary = await TryFindWikipediaSummaryByPageTitleAsync(entity.EnwikiTitle);
                if (summary == null)
                    continue;

                if (string.IsNullOrWhiteSpace(summary.WikibaseItemId))
                {
                    summary = summary with { WikibaseItemId = entity.Id };
                }

                return summary;
            }

            return null;
        }

        private static async Task<WikidataFilmEntity?> TryFindWikidataFilmEntityAsync(string query, int? year)
        {
            if (string.IsNullOrWhiteSpace(query))
                return null;

            var url = "https://www.wikidata.org/w/api.php?action=wbsearchentities&language=en&format=json&limit=8&search=" +
                      Uri.EscapeDataString(query);
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("search", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var result in results.EnumerateArray())
            {
                var id = TryGetString(result, "id");
                var label = TryGetString(result, "label");
                var description = TryGetString(result, "description");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(label))
                    continue;

                var isLikelyFilm =
                    description.Contains("film", StringComparison.OrdinalIgnoreCase) ||
                    description.Contains("movie", StringComparison.OrdinalIgnoreCase) ||
                    (year > 0 && description.Contains(year.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));

                if (!isLikelyFilm)
                    continue;

                var enwikiTitle = await TryFindWikidataEnglishWikipediaTitleAsync(id);
                if (!string.IsNullOrWhiteSpace(enwikiTitle))
                    return new WikidataFilmEntity(id, label, enwikiTitle);
            }

            return null;
        }

        private static async Task<string> TryFindWikidataEnglishWikipediaTitleAsync(string wikibaseItemId)
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
                !entity.TryGetProperty("sitelinks", out var sitelinks) ||
                !sitelinks.TryGetProperty("enwiki", out var enwiki))
            {
                return "";
            }

            return TryGetString(enwiki, "title");
        }

        private static async Task<WikipediaSummary?> TryFindWikipediaSummaryByPageTitleAsync(string pageTitle)
        {
            if (string.IsNullOrWhiteSpace(pageTitle))
                return null;

            var pageId = await TryFindWikipediaPageIdByTitleAsync(pageTitle);
            string sourceUrl = "";
            string titleFromSummary = pageTitle;
            string wikibaseItemId = "";
            string extract = "";

            var url = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(pageTitle)}";
            using (var response = await Http.GetAsync(url))
            {
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);

                    titleFromSummary = TryGetString(doc.RootElement, "title");
                    if (string.IsNullOrWhiteSpace(titleFromSummary))
                        titleFromSummary = pageTitle;

                    extract = TryGetString(doc.RootElement, "extract");
                    wikibaseItemId = TryGetString(doc.RootElement, "wikibase_item");

                    if (doc.RootElement.TryGetProperty("content_urls", out var urls) &&
                        urls.TryGetProperty("desktop", out var desktop))
                    {
                        sourceUrl = TryGetString(desktop, "page");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(extract))
                extract = await TryFindWikipediaExtractByPageIdAsync(pageId);

            if (string.IsNullOrWhiteSpace(wikibaseItemId))
                wikibaseItemId = await TryFindWikipediaWikibaseItemIdAsync(pageTitle);

            if (string.IsNullOrWhiteSpace(sourceUrl))
                sourceUrl = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(pageTitle.Replace(' ', '_'));

            return string.IsNullOrWhiteSpace(extract) && string.IsNullOrWhiteSpace(wikibaseItemId)
                ? null
                : new WikipediaSummary(titleFromSummary, extract.Trim(), sourceUrl, wikibaseItemId);
        }

        private static async Task<WikipediaSummary?> TryFindWikipediaSearchSnippetSummaryAsync(string title, string searchTitle, int? year)
        {
            foreach (var query in BuildFilmTitleVariants(title, searchTitle, year))
            {
                var url = "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=8&srsearch=" +
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
                    var snippet = CleanWikipediaSnippet(TryGetString(result, "snippet"));
                    if (string.IsNullOrWhiteSpace(pageTitle) || string.IsNullOrWhiteSpace(snippet))
                        continue;

                    if (!LooksLikeFilmSearchResult(pageTitle, snippet, title, searchTitle, year))
                        continue;

                    var sourceUrl = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(pageTitle.Replace(' ', '_'));
                    var wikibaseItemId = await TryFindWikipediaWikibaseItemIdAsync(pageTitle);

                    return new WikipediaSummary(pageTitle, snippet, sourceUrl, wikibaseItemId);
                }
            }

            return null;
        }

        private static async Task<int> TryFindWikipediaPageIdByTitleAsync(string pageTitle)
        {
            var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&titles=" +
                      Uri.EscapeDataString(pageTitle);
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return 0;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages) ||
                pages.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            foreach (var page in pages.EnumerateObject())
            {
                var id = TryGetInt(page.Value, "pageid");
                if (id > 0)
                    return id;
            }

            return 0;
        }

        private static async Task<string> TryFindWikipediaExtractByPageIdAsync(int pageId)
        {
            if (pageId <= 0)
                return "";

            var url = "https://en.wikipedia.org/w/api.php?action=query&prop=extracts&exintro=1&explaintext=1&format=json&pageids=" +
                      pageId.ToString(CultureInfo.InvariantCulture);
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return "";

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages) ||
                !pages.TryGetProperty(pageId.ToString(CultureInfo.InvariantCulture), out var page))
            {
                return "";
            }

            return TryGetString(page, "extract").Trim();
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

        private static async Task<WikipediaPageMatch?> TryFindWikipediaPageAsync(string title, int? year)
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

                    var normalizedPageTitle = NormalizeMovieTitleForMatch(pageTitle);
                    var normalizedTitle = NormalizeMovieTitleForMatch(title);
                    if (string.Equals(normalizedPageTitle, normalizedTitle, StringComparison.OrdinalIgnoreCase))
                        return new WikipediaPageMatch(pageTitle, TryGetInt(result, "pageid"));
                }

                foreach (var result in results.EnumerateArray())
                {
                    var pageTitle = TryGetString(result, "title");
                    if (string.IsNullOrWhiteSpace(pageTitle))
                        continue;

                    var normalizedPageTitle = NormalizeMovieTitleForMatch(pageTitle);
                    var normalizedTitle = NormalizeMovieTitleForMatch(title);
                    var looksRelevant =
                        normalizedPageTitle.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase) ||
                        pageTitle.Contains("film", StringComparison.OrdinalIgnoreCase);

                    if (looksRelevant)
                        return new WikipediaPageMatch(pageTitle, TryGetInt(result, "pageid"));
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

        private static IEnumerable<string> BuildFilmTitleVariants(string title, string searchTitle, int? year)
        {
            var values = new List<string> { title, searchTitle };

            foreach (var value in values.ToArray())
            {
                var trimmed = value?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (!trimmed.Contains(':') && trimmed.Count(char.IsWhiteSpace) >= 1)
                {
                    var lastSpace = trimmed.LastIndexOf(' ');
                    if (lastSpace > 0 && lastSpace < trimmed.Length - 1)
                        values.Add(trimmed[..lastSpace] + ": " + trimmed[(lastSpace + 1)..]);
                }

                if (year > 0)
                    values.Add($"{trimmed} {year.Value.ToString(CultureInfo.InvariantCulture)} film");

                values.Add($"{trimmed} film");
            }

            return values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool LooksLikeFilmSearchResult(string pageTitle, string snippet, string title, string searchTitle, int? year)
        {
            var combined = $"{pageTitle} {snippet}";
            var normalizedCombined = NormalizeMovieTitleForMatch(combined);
            var normalizedTitle = NormalizeMovieTitleForMatch(title);
            var normalizedSearchTitle = NormalizeMovieTitleForMatch(searchTitle);

            var titleMatches =
                (!string.IsNullOrWhiteSpace(normalizedTitle) && normalizedCombined.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(normalizedSearchTitle) && normalizedCombined.Contains(normalizedSearchTitle, StringComparison.OrdinalIgnoreCase));

            if (!titleMatches)
                return false;

            var filmish =
                combined.Contains("film", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("movie", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("director", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("starring", StringComparison.OrdinalIgnoreCase);

            if (!filmish)
                return false;

            if (year == null || year <= 0)
                return true;

            return combined.Contains(year.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase) ||
                   pageTitle.Contains("film", StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanWikipediaSnippet(string snippet)
        {
            if (string.IsNullOrWhiteSpace(snippet))
                return "";

            var text = Regex.Replace(snippet, "<.*?>", "");
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(text))
                return "";

            if (!text.EndsWith(".", StringComparison.Ordinal) &&
                !text.EndsWith("!", StringComparison.Ordinal) &&
                !text.EndsWith("?", StringComparison.Ordinal))
            {
                text += ".";
            }

            return text;
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
        private sealed record WikidataFilmEntity(string Id, string Label, string EnwikiTitle);
        private sealed record WikipediaPageMatch(string Title, int PageId);
        private sealed record WikipediaSummary(string Title, string Extract, string Url, string WikibaseItemId);
    }
}
