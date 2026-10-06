using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace Zink
{
    public static class SpotifyAuthHelper
    {
        private const string ClientId = "2e88dcd486ec48aaaf54ac86e8c266a2";
        private const string RedirectUri = "https://example.com/callback";
        private const string NativeScope = "streaming user-read-private user-read-email user-library-modify user-library-read user-read-playback-state user-read-currently-playing user-modify-playback-state playlist-read-private user-read-recently-played user-top-read";

        private static readonly string AppFolder = ApplicationData.Current.LocalFolder.Path;
        private static readonly string TokenFile = Path.Combine(AppFolder, "spotify_token.txt");
        private static readonly string RefreshTokenFile = Path.Combine(AppFolder, "spotify_refresh_token.txt");
        private static readonly string CodeVerifierFile = Path.Combine(AppFolder, "spotify_code_verifier.txt");
        private static readonly string CookieFile = Path.Combine(AppFolder, "spotify_cookies.txt");

        public static string AccessToken { get; private set; }

        public static Uri AuthorizationRedirectUri => new(RedirectUri);
        public static string DefaultRedirectUri => RedirectUri;

        public class SpotifyTrackResult
        {
            public string Id { get; set; } = "";
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public string Album { get; set; } = "";
            public string ImageUrl { get; set; } = "";
            public string SpotifyUrl { get; set; } = "";
            public string Subtitle { get; set; } = "";
            public string Kind { get; set; } = "Track";
        }

        public sealed class SpotifyPlaybackState
        {
            public string TrackId { get; set; } = "";
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public string Album { get; set; } = "";
            public string ImageUrl { get; set; } = "";
            public bool IsPlaying { get; set; }
            public int ProgressMs { get; set; }
            public int DurationMs { get; set; }
        }

        public sealed class SpotifySavedTrackResult : SpotifyTrackResult
        {
            public DateTime AddedAt { get; set; }
        }

        public sealed class SpotifyUserProfile
        {
            public string DisplayName { get; set; } = "";
            public string ImageUrl { get; set; } = "";
            public string Product { get; set; } = "";
        }

        public sealed class SpotifyDevice
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            public bool IsActive { get; set; }
            public int VolumePercent { get; set; }
        }

        public readonly struct SpotifyTrackMatch
        {
            public SpotifyTrackMatch(string trackId, string trackUrl)
            {
                TrackId = trackId ?? "";
                TrackUrl = trackUrl ?? "";
            }

            public string TrackId { get; }
            public string TrackUrl { get; }
        }

        public static async Task InitializeWebView2Async(WebView2 webView)
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await webView.EnsureCoreWebView2Async(env);
        }

        public static async Task ExchangeCodeForTokenAsync(string code)
        {
            await ExchangeCodeForTokenAsync(code, RedirectUri);
        }

        public static async Task ExchangeCodeForTokenAsync(string code, string redirectUri)
        {
            using var http = new HttpClient();

            var content = new StringContent(
                $"grant_type=authorization_code" +
                $"&code={Uri.EscapeDataString(code)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&client_id={Uri.EscapeDataString(ClientId)}" +
                $"&code_verifier={Uri.EscapeDataString(await GetStoredCodeVerifierAsync())}",
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var response = await http.PostAsync("https://accounts.spotify.com/api/token", content);
            response.EnsureSuccessStatusCode();

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            AccessToken = json.TryGetProperty("access_token", out var accessEl) ? accessEl.GetString() : null;
            string refreshToken = json.TryGetProperty("refresh_token", out var refreshEl) ? refreshEl.GetString() : null;

            Directory.CreateDirectory(AppFolder);

            if (!string.IsNullOrWhiteSpace(AccessToken))
                await File.WriteAllTextAsync(TokenFile, AccessToken);

            if (!string.IsNullOrWhiteSpace(refreshToken))
                await File.WriteAllTextAsync(RefreshTokenFile, refreshToken);
        }

        public static string GetNativeAuthorizationUrl(bool showDialog = false)
        {
            return GetNativeAuthorizationUrl(RedirectUri, showDialog);
        }

        public static string GetNativeAuthorizationUrl(string redirectUri, bool showDialog = false)
        {
            Directory.CreateDirectory(AppFolder);
            var verifier = CreateCodeVerifier();
            File.WriteAllText(CodeVerifierFile, verifier);
            var challenge = CreateCodeChallenge(verifier);

            return $"https://accounts.spotify.com/authorize?" +
                   $"client_id={ClientId}" +
                   $"&response_type=code" +
                   $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                   $"&scope={Uri.EscapeDataString(NativeScope)}" +
                   $"&code_challenge_method=S256" +
                   $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                   $"&show_dialog={showDialog.ToString().ToLowerInvariant()}";
        }

        public static string? TryGetCodeFromRedirect(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var trimmed = value.Trim();
            if (!trimmed.Contains("://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Contains("?", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Contains("&", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            try
            {
                var uri = new Uri(trimmed);
                return System.Web.HttpUtility.ParseQueryString(uri.Query).Get("code");
            }
            catch
            {
                return null;
            }
        }

        public static async Task LoadStoredTokenAsync()
        {
            try
            {
                Directory.CreateDirectory(AppFolder);

                if (File.Exists(TokenFile))
                    AccessToken = await File.ReadAllTextAsync(TokenFile);
            }
            catch { }
        }

        public static async Task RefreshAccessTokenAsync()
        {
            if (!File.Exists(RefreshTokenFile))
                return;

            string refreshToken = await File.ReadAllTextAsync(RefreshTokenFile);
            if (string.IsNullOrWhiteSpace(refreshToken))
                return;

            using var http = new HttpClient();

            var content = new StringContent(
                $"grant_type=refresh_token" +
                $"&refresh_token={Uri.EscapeDataString(refreshToken)}" +
                $"&client_id={Uri.EscapeDataString(ClientId)}",
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var response = await http.PostAsync("https://accounts.spotify.com/api/token", content);
            response.EnsureSuccessStatusCode();

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            AccessToken = json.TryGetProperty("access_token", out var accessEl) ? accessEl.GetString() : null;

            if (!string.IsNullOrWhiteSpace(AccessToken))
                await File.WriteAllTextAsync(TokenFile, AccessToken);
        }

        private static async Task<string> GetStoredCodeVerifierAsync()
        {
            Directory.CreateDirectory(AppFolder);

            if (File.Exists(CodeVerifierFile))
            {
                var stored = await File.ReadAllTextAsync(CodeVerifierFile);
                if (!string.IsNullOrWhiteSpace(stored))
                    return stored.Trim();
            }

            var verifier = CreateCodeVerifier();
            await File.WriteAllTextAsync(CodeVerifierFile, verifier);
            return verifier;
        }

        private static string CreateCodeVerifier()
        {
            Span<byte> bytes = stackalloc byte[64];
            RandomNumberGenerator.Fill(bytes);
            return Base64UrlEncode(bytes);
        }

        private static string CreateCodeChallenge(string verifier)
        {
            var bytes = Encoding.ASCII.GetBytes(verifier);
            var hash = SHA256.HashData(bytes);
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        public static async Task<bool> EnsureAccessTokenAsync()
        {
            if (!string.IsNullOrWhiteSpace(AccessToken))
                return true;

            await LoadStoredTokenAsync();

            if (!string.IsNullOrWhiteSpace(AccessToken))
                return true;

            try
            {
                await RefreshAccessTokenAsync();
            }
            catch { }

            return !string.IsNullOrWhiteSpace(AccessToken);
        }

        public static async Task<string> GetAccessTokenAsync()
        {
            return await EnsureAccessTokenAsync() ? AccessToken ?? "" : "";
        }

        public static async Task SaveCookiesAsync(WebView2 webView)
        {
            var cookieList = await webView.CoreWebView2.CookieManager.GetCookiesAsync("https://spotify.com");
            Directory.CreateDirectory(AppFolder);

            using var writer = new StreamWriter(CookieFile, false, Encoding.UTF8);

            foreach (var cookie in cookieList)
            {
                writer.WriteLine($"{cookie.Name}={cookie.Value}; Domain={cookie.Domain}; Path={cookie.Path}");
            }
        }

        public static async Task LoadCookiesAsync(WebView2 webView)
        {
            if (!File.Exists(CookieFile))
                return;

            using var reader = new StreamReader(CookieFile, Encoding.UTF8);
            while (!reader.EndOfStream)
            {
                var cookieData = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(cookieData))
                    continue;

                var cookieParts = cookieData.Split(';');
                if (cookieParts.Length < 3)
                    continue;

                var nameValue = cookieParts[0].Split('=', 2);
                if (nameValue.Length < 2)
                    continue;

                string name = nameValue[0].Trim();
                string value = nameValue[1].Trim();
                string domain = cookieParts[1].Replace("Domain=", "", StringComparison.OrdinalIgnoreCase).Trim();
                string path = cookieParts[2].Replace("Path=", "", StringComparison.OrdinalIgnoreCase).Trim();

                var cookie = webView.CoreWebView2.CookieManager.CreateCookie(name, value, domain, path);
                webView.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
            }
        }

        public static async Task<SpotifyTrackMatch?> SearchBestTrackAsync(string artist, string title, string album)
        {
            if (!await EnsureAccessTokenAsync())
                return null;

            using var http = CreateAuthorizedClient();

            string query = BuildSearchQuery(artist, title, album);
            string url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=1";

            var response = await http.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return null;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.GetAsync(url);
            }

            if (!response.IsSuccessStatusCode)
                return null;

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            if (!json.TryGetProperty("tracks", out var tracksObj))
                return null;

            if (!tracksObj.TryGetProperty("items", out var items))
                return null;

            if (items.GetArrayLength() == 0)
                return null;

            var first = items[0];

            string trackId = first.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
            string trackUrl = "";

            if (first.TryGetProperty("external_urls", out var extUrls) &&
                extUrls.TryGetProperty("spotify", out var spotifyUrlEl))
            {
                trackUrl = spotifyUrlEl.GetString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(trackId))
                return null;

            return new SpotifyTrackMatch(trackId, trackUrl);
        }

        public static async Task<string> GetArtistImageUrlAsync(string artist, string title, string album)
        {
            try
            {
                var match = await SearchBestTrackAsync(artist, title, album);
                if (match == null || string.IsNullOrWhiteSpace(match.Value.TrackId))
                    return "";

                return await GetArtistImageUrlForTrackAsync(match.Value.TrackId);
            }
            catch
            {
                return "";
            }
        }

        public static async Task<string> GetArtistImageUrlForTrackAsync(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return "";

            if (!await EnsureAccessTokenAsync())
                return "";

            string url = $"https://api.spotify.com/v1/tracks/{Uri.EscapeDataString(trackId)}";

            using var http = CreateAuthorizedClient();
            var response = await http.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return "";

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.GetAsync(url);
            }

            if (!response.IsSuccessStatusCode)
                return "";

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            if (!json.TryGetProperty("artists", out var artists))
                return "";

            if (artists.ValueKind != JsonValueKind.Array || artists.GetArrayLength() == 0)
                return "";

            var firstArtist = artists[0];
            string artistId = firstArtist.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(artistId))
                return "";

            return await GetArtistImageByArtistIdAsync(artistId);
        }

        public static async Task<string> GetArtistImageByArtistIdAsync(string artistId)
        {
            if (string.IsNullOrWhiteSpace(artistId))
                return "";

            if (!await EnsureAccessTokenAsync())
                return "";

            string url = $"https://api.spotify.com/v1/artists/{Uri.EscapeDataString(artistId)}";

            using var http = CreateAuthorizedClient();
            var response = await http.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return "";

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.GetAsync(url);
            }

            if (!response.IsSuccessStatusCode)
                return "";

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            if (!json.TryGetProperty("images", out var images))
                return "";

            if (images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
                return "";

            foreach (var image in images.EnumerateArray())
            {
                if (image.TryGetProperty("url", out var urlEl))
                {
                    var found = urlEl.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }

            return "";
        }

        public static async Task<bool> AddTrackToLikedSongsAsync(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return false;

            if (!await EnsureAccessTokenAsync())
                return false;

            string url = $"https://api.spotify.com/v1/me/tracks?ids={Uri.EscapeDataString(trackId)}";

            using var http = CreateAuthorizedClient();
            var response = await http.PutAsync(url, new StringContent(""));

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return false;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.PutAsync(url, new StringContent(""));
            }

            return response.IsSuccessStatusCode;
        }

        public static async Task<SpotifyPlaybackState?> GetCurrentPlaybackAsync()
        {
            if (!await EnsureAccessTokenAsync())
                return null;

            using var http = CreateAuthorizedClient();
            var response = await http.GetAsync("https://api.spotify.com/v1/me/player/currently-playing");

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return null;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.GetAsync("https://api.spotify.com/v1/me/player/currently-playing");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;

            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (!root.TryGetProperty("item", out var item) || item.ValueKind == JsonValueKind.Null)
                return null;

            return new SpotifyPlaybackState
            {
                TrackId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                Title = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                Artist = ReadArtists(item),
                Album = ReadAlbumName(item),
                ImageUrl = ReadAlbumImage(item),
                IsPlaying = root.TryGetProperty("is_playing", out var playingEl) && playingEl.GetBoolean(),
                ProgressMs = root.TryGetProperty("progress_ms", out var progressEl) ? progressEl.GetInt32() : 0,
                DurationMs = item.TryGetProperty("duration_ms", out var durationEl) ? durationEl.GetInt32() : 0
            };
        }

        public static async Task<SpotifyUserProfile?> GetCurrentUserProfileAsync()
        {
            using var doc = await GetJsonWithRefreshAsync("https://api.spotify.com/v1/me");
            if (doc == null)
                return null;

            var root = doc.RootElement;
            var profile = new SpotifyUserProfile
            {
                DisplayName = root.TryGetProperty("display_name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                Product = root.TryGetProperty("product", out var productEl) ? productEl.GetString() ?? "" : "",
                ImageUrl = ReadImages(root)
            };

            return profile;
        }

        public static async Task<SpotifyTrackResult[]> SearchTracksAsync(string query, int limit = 12)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Array.Empty<SpotifyTrackResult>();

            if (!await EnsureAccessTokenAsync())
                return Array.Empty<SpotifyTrackResult>();

            limit = Math.Clamp(limit, 1, 25);
            string url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query.Trim())}&type=track&limit={limit}";

            using var http = CreateAuthorizedClient();
            var response = await http.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return Array.Empty<SpotifyTrackResult>();

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.GetAsync(url);
            }

            if (!response.IsSuccessStatusCode)
                return Array.Empty<SpotifyTrackResult>();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                !tracks.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<SpotifyTrackResult>();
            }

            var results = new List<SpotifyTrackResult>();

            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                results.Add(new SpotifyTrackResult
                {
                    Id = id,
                    Title = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                    Artist = ReadArtists(item),
                    Album = ReadAlbumName(item),
                    ImageUrl = ReadAlbumImage(item),
                    SpotifyUrl = ReadSpotifyUrl(item),
                    Subtitle = "Track",
                    Kind = "Track"
                });
            }

            return results.ToArray();
        }

        public static async Task<SpotifySavedTrackResult[]> GetSavedTracksAsync(int limit = 20)
        {
            if (!await EnsureAccessTokenAsync())
                return Array.Empty<SpotifySavedTrackResult>();

            limit = Math.Clamp(limit, 1, 50);
            string url = $"https://api.spotify.com/v1/me/tracks?limit={limit}";

            using var http = CreateAuthorizedClient();
            var response = await http.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return Array.Empty<SpotifySavedTrackResult>();

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.GetAsync(url);
            }

            if (!response.IsSuccessStatusCode)
                return Array.Empty<SpotifySavedTrackResult>();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<SpotifySavedTrackResult>();
            }

            var results = new List<SpotifySavedTrackResult>();

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("track", out var track) || track.ValueKind == JsonValueKind.Null)
                    continue;

                var id = track.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                DateTime.TryParse(
                    item.TryGetProperty("added_at", out var addedEl) ? addedEl.GetString() : null,
                    out var addedAt);

                results.Add(new SpotifySavedTrackResult
                {
                    Id = id,
                    Title = track.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                    Artist = ReadArtists(track),
                    Album = ReadAlbumName(track),
                    ImageUrl = ReadAlbumImage(track),
                    SpotifyUrl = ReadSpotifyUrl(track),
                    Subtitle = "Liked song",
                    Kind = "Track",
                    AddedAt = addedAt
                });
            }

            return results.ToArray();
        }

        public static async Task<SpotifyTrackResult[]> GetSavedAlbumsAsync(int limit = 10)
        {
            using var doc = await GetJsonWithRefreshAsync($"https://api.spotify.com/v1/me/albums?limit={Math.Clamp(limit, 1, 50)}");
            if (doc == null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return Array.Empty<SpotifyTrackResult>();

            var results = new List<SpotifyTrackResult>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("album", out var album) || album.ValueKind == JsonValueKind.Null)
                    continue;

                var albumResult = new SpotifyTrackResult
                {
                    Id = album.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    Title = album.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                    Artist = ReadArtists(album),
                    Album = album.TryGetProperty("release_date", out var dateEl) ? dateEl.GetString() ?? "" : "",
                    ImageUrl = ReadImages(album),
                    SpotifyUrl = ReadSpotifyUrl(album),
                    Subtitle = "Album",
                    Kind = "Album"
                };

                if (!string.IsNullOrWhiteSpace(albumResult.Id))
                    results.Add(albumResult);
            }

            return results.ToArray();
        }

        public static async Task<SpotifyTrackResult[]> GetRecentlyPlayedAsync(int limit = 10)
        {
            using var doc = await GetJsonWithRefreshAsync($"https://api.spotify.com/v1/me/player/recently-played?limit={Math.Clamp(limit, 1, 50)}");
            if (doc == null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return Array.Empty<SpotifyTrackResult>();

            var results = new List<SpotifyTrackResult>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("track", out var track) && track.ValueKind != JsonValueKind.Null)
                    results.Add(ReadTrackResult(track, "Recently played"));
            }

            return results.Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToArray();
        }

        public static async Task<SpotifyTrackResult[]> GetTopTracksAsync(int limit = 10)
        {
            using var doc = await GetJsonWithRefreshAsync($"https://api.spotify.com/v1/me/top/tracks?time_range=short_term&limit={Math.Clamp(limit, 1, 50)}");
            if (doc == null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return Array.Empty<SpotifyTrackResult>();

            var results = new List<SpotifyTrackResult>();
            foreach (var track in items.EnumerateArray())
                results.Add(ReadTrackResult(track, "Top track"));

            return results.Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToArray();
        }

        public static async Task<SpotifyTrackResult[]> GetCurrentUserPlaylistsAsync(int limit = 10)
        {
            using var doc = await GetJsonWithRefreshAsync($"https://api.spotify.com/v1/me/playlists?limit={Math.Clamp(limit, 1, 50)}");
            if (doc == null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return Array.Empty<SpotifyTrackResult>();

            var results = new List<SpotifyTrackResult>();
            foreach (var playlist in items.EnumerateArray())
            {
                var total = "";
                if (playlist.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("total", out var totalEl))
                    total = $"{totalEl.GetInt32()} songs";

                var result = new SpotifyTrackResult
                {
                    Id = playlist.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    Title = playlist.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                    Artist = total,
                    Album = "Playlist",
                    ImageUrl = ReadImages(playlist),
                    SpotifyUrl = ReadSpotifyUrl(playlist),
                    Subtitle = "Playlist",
                    Kind = "Playlist"
                };

                if (!string.IsNullOrWhiteSpace(result.Id))
                    results.Add(result);
            }

            return results.ToArray();
        }

        public static async Task<SpotifyTrackResult[]> GetQueueAsync(int limit = 8)
        {
            using var doc = await GetJsonWithRefreshAsync("https://api.spotify.com/v1/me/player/queue");
            if (doc == null || !doc.RootElement.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Array)
                return Array.Empty<SpotifyTrackResult>();

            var results = new List<SpotifyTrackResult>();
            foreach (var item in queue.EnumerateArray())
            {
                if (results.Count >= limit)
                    break;

                if (item.TryGetProperty("type", out var typeEl) &&
                    string.Equals(typeEl.GetString(), "track", StringComparison.OrdinalIgnoreCase))
                    results.Add(ReadTrackResult(item, "Queue"));
            }

            return results.Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToArray();
        }

        public static async Task<SpotifyDevice[]> GetAvailableDevicesAsync()
        {
            using var doc = await GetJsonWithRefreshAsync("https://api.spotify.com/v1/me/player/devices");
            if (doc == null || !doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                return Array.Empty<SpotifyDevice>();

            var results = new List<SpotifyDevice>();
            foreach (var device in devices.EnumerateArray())
            {
                results.Add(new SpotifyDevice
                {
                    Id = device.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    Name = device.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                    Type = device.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "",
                    IsActive = device.TryGetProperty("is_active", out var activeEl) && activeEl.GetBoolean(),
                    VolumePercent = device.TryGetProperty("volume_percent", out var volumeEl) && volumeEl.ValueKind != JsonValueKind.Null ? volumeEl.GetInt32() : 0
                });
            }

            return results.Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToArray();
        }

        public static async Task<bool> StartPlaybackAsync(string trackId, string deviceId = "")
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return false;

            if (!await EnsureAccessTokenAsync())
                return false;

            using var http = CreateAuthorizedClient();
            var content = new StringContent(
                JsonSerializer.Serialize(new { uris = new[] { $"spotify:track:{trackId}" } }),
                Encoding.UTF8,
                "application/json");

            var endpoint = string.IsNullOrWhiteSpace(deviceId)
                ? "https://api.spotify.com/v1/me/player/play"
                : $"https://api.spotify.com/v1/me/player/play?device_id={Uri.EscapeDataString(deviceId)}";

            var response = await http.PutAsync(endpoint, content);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return false;

                using var retryHttp = CreateAuthorizedClient();
                using var retryContent = new StringContent(
                    JsonSerializer.Serialize(new { uris = new[] { $"spotify:track:{trackId}" } }),
                    Encoding.UTF8,
                    "application/json");
                response = await retryHttp.PutAsync(endpoint, retryContent);
            }

            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }

        public static async Task<bool> ResumePlaybackAsync()
        {
            if (!await EnsureAccessTokenAsync())
                return false;

            using var http = CreateAuthorizedClient();
            var response = await http.PutAsync("https://api.spotify.com/v1/me/player/play", new StringContent(""));

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return false;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.PutAsync("https://api.spotify.com/v1/me/player/play", new StringContent(""));
            }

            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }

        public static async Task<bool> PausePlaybackAsync()
        {
            if (!await EnsureAccessTokenAsync())
                return false;

            using var http = CreateAuthorizedClient();
            var response = await http.PutAsync("https://api.spotify.com/v1/me/player/pause", new StringContent(""));
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }

        public static async Task<bool> RemoveTrackFromLikedSongsAsync(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return false;

            if (!await EnsureAccessTokenAsync())
                return false;

            string url = $"https://api.spotify.com/v1/me/tracks?ids={Uri.EscapeDataString(trackId)}";
            using var http = CreateAuthorizedClient();
            var response = await http.DeleteAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return false;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.DeleteAsync(url);
            }

            return response.IsSuccessStatusCode;
        }

        public static Task<bool> NextTrackAsync()
        {
            return PostPlayerCommandAsync("https://api.spotify.com/v1/me/player/next");
        }

        public static Task<bool> PreviousTrackAsync()
        {
            return PostPlayerCommandAsync("https://api.spotify.com/v1/me/player/previous");
        }

        public static async Task<bool> SaveAlbumAsync(string albumId)
        {
            if (string.IsNullOrWhiteSpace(albumId))
                return false;

            if (!await EnsureAccessTokenAsync())
                return false;

            string url = $"https://api.spotify.com/v1/me/albums?ids={Uri.EscapeDataString(albumId)}";
            using var http = CreateAuthorizedClient();
            var response = await http.PutAsync(url, new StringContent(""));

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return false;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.PutAsync(url, new StringContent(""));
            }

            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }

        public static async Task<bool> TransferPlaybackAsync(string deviceId, bool play = true)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return false;

            if (!await EnsureAccessTokenAsync())
                return false;

            using var http = CreateAuthorizedClient();
            var content = new StringContent(
                JsonSerializer.Serialize(new { device_ids = new[] { deviceId }, play }),
                Encoding.UTF8,
                "application/json");

            var response = await http.PutAsync("https://api.spotify.com/v1/me/player", content);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return false;

                using var retryHttp = CreateAuthorizedClient();
                using var retryContent = new StringContent(
                    JsonSerializer.Serialize(new { device_ids = new[] { deviceId }, play }),
                    Encoding.UTF8,
                    "application/json");
                response = await retryHttp.PutAsync("https://api.spotify.com/v1/me/player", retryContent);
            }

            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }

        public static async Task<bool> OpenTrackInSpotifyAsync(string artist, string title, string album)
        {
            var match = await SearchBestTrackAsync(artist, title, album);
            if (match == null || string.IsNullOrWhiteSpace(match.Value.TrackId))
                return false;

            var spotifyUri = new Uri($"spotify:track:{match.Value.TrackId}");

            try
            {
                var launchedSpotifyApp = await Launcher.LaunchUriAsync(spotifyUri);
                if (launchedSpotifyApp)
                    return true;
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(match.Value.TrackUrl))
            {
                try
                {
                    return await Launcher.LaunchUriAsync(new Uri(match.Value.TrackUrl));
                }
                catch { }
            }

            return false;
        }

        private static string BuildSearchQuery(string artist, string title, string album)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(title))
                sb.Append(title.Trim());

            if (!string.IsNullOrWhiteSpace(artist))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(artist.Trim());
            }

            if (!string.IsNullOrWhiteSpace(album))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(album.Trim());
            }

            return sb.ToString().Trim();
        }

        private static HttpClient CreateAuthorizedClient()
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            return http;
        }

        private static async Task<bool> PostPlayerCommandAsync(string url)
        {
            if (!await EnsureAccessTokenAsync())
                return false;

            using var http = CreateAuthorizedClient();
            var response = await http.PostAsync(url, new StringContent(""));

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return false;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.PostAsync(url, new StringContent(""));
            }

            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }

        private static async Task<JsonDocument?> GetJsonWithRefreshAsync(string url)
        {
            if (!await EnsureAccessTokenAsync())
                return null;

            using var http = CreateAuthorizedClient();
            var response = await http.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return null;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.GetAsync(url);
            }

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonDocument.Parse(json);
        }

        private static SpotifyTrackResult ReadTrackResult(JsonElement track, string subtitle)
        {
            return new SpotifyTrackResult
            {
                Id = track.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                Title = track.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                Artist = ReadArtists(track),
                Album = ReadAlbumName(track),
                ImageUrl = ReadAlbumImage(track),
                SpotifyUrl = ReadSpotifyUrl(track),
                Subtitle = subtitle,
                Kind = "Track"
            };
        }

        private static string ReadArtists(JsonElement item)
        {
            if (!item.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
                return "";

            var names = new List<string>();

            foreach (var artist in artists.EnumerateArray())
            {
                if (artist.TryGetProperty("name", out var nameEl))
                {
                    var name = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            }

            return string.Join(", ", names);
        }

        private static string ReadAlbumName(JsonElement item)
        {
            if (item.TryGetProperty("album", out var album) &&
                album.TryGetProperty("name", out var nameEl))
            {
                return nameEl.GetString() ?? "";
            }

            return "";
        }

        private static string ReadAlbumImage(JsonElement item)
        {
            if (!item.TryGetProperty("album", out var album) ||
                !album.TryGetProperty("images", out var images) ||
                images.ValueKind != JsonValueKind.Array ||
                images.GetArrayLength() == 0)
            {
                return "";
            }

            foreach (var image in images.EnumerateArray())
            {
                if (image.TryGetProperty("url", out var urlEl))
                {
                    var url = urlEl.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(url))
                        return url;
                }
            }

            return "";
        }

        private static string ReadImages(JsonElement item)
        {
            if (!item.TryGetProperty("images", out var images) ||
                images.ValueKind != JsonValueKind.Array ||
                images.GetArrayLength() == 0)
            {
                return "";
            }

            foreach (var image in images.EnumerateArray())
            {
                if (image.TryGetProperty("url", out var urlEl))
                {
                    var url = urlEl.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(url))
                        return url;
                }
            }

            return "";
        }

        private static string ReadSpotifyUrl(JsonElement item)
        {
            if (item.TryGetProperty("external_urls", out var urls) &&
                urls.TryGetProperty("spotify", out var spotifyEl))
            {
                return spotifyEl.GetString() ?? "";
            }

            return "";
        }
    }
}
