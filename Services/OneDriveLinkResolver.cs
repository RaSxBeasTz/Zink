using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services
{
    internal sealed record OneDriveResolvedVideo(Uri OriginalUri, string Name, string ContentType = "application/octet-stream");
    internal sealed class OneDriveResolutionException : HttpRequestException
    {
        internal string Stage { get; }
        internal string GraphCode { get; }
        internal OneDriveResolutionException(HttpStatusCode status, string stage, string graphCode)
            : base($"OneDrive {stage} failed: HTTP {(int)status} ({graphCode}).", null, status)
        { Stage = stage; GraphCode = graphCode; }
    }

    internal static class OneDriveLinkResolver
    {
        // Resolve metadata, never navigate to a download command or click a website control.
        internal static async Task<OneDriveResolvedVideo?> ResolveAsync(Uri sharingLink, string accessToken, CancellationToken token)
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            return await ResolveWithClientAsync(sharingLink, accessToken, client, token);
        }

        internal static async Task<OneDriveResolvedVideo?> ResolveWithClientAsync(Uri sharingLink, string accessToken, HttpClient client, CancellationToken token)
        {
            var shareId = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(sharingLink.AbsoluteUri))
                .TrimEnd('=').Replace('/', '_').Replace('+', '-');
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://graph.microsoft.com/v1.0/shares/" + shareId + "/driveItem"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("Prefer", "redeemSharingLinkIfNecessary");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            await EnsureGraphSuccessAsync(response, "metadata", token);
            await using var body = await response.Content.ReadAsStreamAsync(token);
            using var json = await ReadDocumentAsync(body, token);
            if (json == null) return null;
            var candidates = new List<OneDriveResolvedVideo>();
            Find(json.RootElement, null, candidates);
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count > 1) return null;

            // The download annotation is not always present on shared-item metadata.
            // Ask Graph for the original stream redirect instead of depending on that annotation.
            var item = json.RootElement;
            if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("folder", out _)) return null;
            if (item.TryGetProperty("remoteItem", out var remote)) item = remote;
            if (!IsVideo(item) || !item.TryGetProperty("name", out var itemName) || itemName.ValueKind != JsonValueKind.String) return null;
            // /shares/.../driveItem/content is not available consistently on Graph v1.0.
            // Resolve the canonical drive/item address from the returned file metadata.
            if (!item.TryGetProperty("id", out var itemId) || itemId.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("parentReference", out var parent) || parent.ValueKind != JsonValueKind.Object ||
                !parent.TryGetProperty("driveId", out var driveId) || driveId.ValueKind != JsonValueKind.String) return null;
            var contentUri = new Uri("https://graph.microsoft.com/v1.0/drives/" + Uri.EscapeDataString(driveId.GetString()!) +
                "/items/" + Uri.EscapeDataString(itemId.GetString()!) + "/content");
            using var contentRequest = new HttpRequestMessage(HttpMethod.Get, contentUri);
            contentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var contentResponse = await client.SendAsync(contentRequest, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)contentResponse.StatusCode is >= 300 and < 400)
            {
                if (contentResponse.Headers.Location is not Uri location)
                    throw new OneDriveResolutionException(contentResponse.StatusCode, "content-address", "missingRedirectAddress");
                var originalUri = location.IsAbsoluteUri ? location : new Uri(contentUri, location);
                if (!OneDriveOriginalStream.IsFileHost(originalUri))
                    throw new OneDriveResolutionException(contentResponse.StatusCode, "content-address", "unsupportedOriginalHost");
                return new OneDriveResolvedVideo(originalUri, itemName.GetString()!, GetMimeType(item));
            }
            await EnsureGraphSuccessAsync(contentResponse, "content-address", token);
            return null;
        }

        private static async Task EnsureGraphSuccessAsync(HttpResponseMessage response, string stage, CancellationToken token)
        {
            if (response.IsSuccessStatusCode) return;
            var code = "unknown";
            // Only retain Microsoft's short error identifier, never response messages, URLs or tokens.
            await using var body = await response.Content.ReadAsStreamAsync(token);
            using var json = await ReadDocumentAsync(body, token);
            if (json?.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var candidate = value.GetString();
                if (candidate is { Length: > 0 and <= 64 } && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) code = candidate;
            }
            throw new OneDriveResolutionException(response.StatusCode, stage, code);
        }

        internal static string? GetItemId(Uri uri)
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && (pair[0].Equals("id", StringComparison.OrdinalIgnoreCase) || pair[0].Equals("resid", StringComparison.OrdinalIgnoreCase)))
                    return Uri.UnescapeDataString(pair[1]);
            }
            return null;
        }

        internal static async Task<OneDriveResolvedVideo?> ReadMetadataAsync(Stream body, string? expectedId, CancellationToken token)
        {
            using var json = await ReadDocumentAsync(body, token);
            if (json == null) return null;
            var candidates = new List<OneDriveResolvedVideo>();
            Find(json.RootElement, expectedId, candidates);
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static async Task<JsonDocument?> ReadDocumentAsync(Stream body, CancellationToken token)
        {
            // Do not read arbitrarily large response bodies while resolving a sharing link.
            using var bytes = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await body.ReadAsync(buffer, token)) != 0)
            {
                if (bytes.Length + read > 2 * 1024 * 1024) return null;
                bytes.Write(buffer, 0, read);
            }
            try
            {
                return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (JsonException) { return null; }
        }

        private static void Find(JsonElement node, string? expectedId, List<OneDriveResolvedVideo> candidates)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (expectedId == null && node.TryGetProperty("folder", out _)) return;
                var idMatches = expectedId == null || (node.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String && string.Equals(id.GetString(), expectedId, StringComparison.OrdinalIgnoreCase));
                if (idMatches && node.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && IsVideo(node))
                {
                    foreach (var key in new[] { "@content.downloadUrl", "@microsoft.graph.downloadUrl", "content.downloadUrl" })
                    {
                        if (node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                            Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) && OneDriveOriginalStream.IsFileHost(uri))
                        {
                            var candidate = new OneDriveResolvedVideo(uri, name.GetString()!, GetMimeType(node));
                            if (!candidates.Contains(candidate)) candidates.Add(candidate);
                        }
                    }
                }
                foreach (var property in node.EnumerateObject()) Find(property.Value, expectedId, candidates);
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Find(item, expectedId, candidates);
        }

        private static string GetMimeType(JsonElement item) =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object &&
            file.TryGetProperty("mimeType", out var mime) && mime.ValueKind == JsonValueKind.String
                ? mime.GetString()! : "application/octet-stream";

        private static bool IsVideo(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("folder", out _)) return false;
            if (GetMimeType(item).StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                (item.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)) return true;
            return item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                Path.GetExtension(name.GetString()!).ToLowerInvariant() is
                    ".mp4" or ".m4v" or ".mkv" or ".webm" or ".mov" or ".avi" or ".wmv" or ".m2ts" or ".ts";
        }
    }
}
