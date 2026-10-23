using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services
{
    // A bounded read cache over the original file. Seeking requests another byte range;
    // it never buffers an entire film or substitutes a transcoded preview.
    internal sealed class OneDriveOriginalStream : Stream
    {
        private readonly HttpClient _client;
        private readonly SharedClient _sharedClient;
        private readonly Uri _uri;
        private readonly long _length;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private byte[] _cache = Array.Empty<byte>();
        private long _cacheOffset = -1;
        private long _position;
        private int _disposeState;
        private bool _disposed => Volatile.Read(ref _disposeState) != 0;
        public string ContentType { get; }

        private OneDriveOriginalStream(HttpClient client, Uri uri, long length, string contentType)
        {
            _client = client;
            _sharedClient = new SharedClient(client);
            _uri = uri;
            _length = length;
            ContentType = contentType;
        }

        private OneDriveOriginalStream(SharedClient shared, Uri uri, long length, string contentType)
        {
            shared.AddReference();
            _sharedClient = shared;
            _client = shared.Client;
            _uri = uri;
            _length = length;
            ContentType = contentType;
        }

        internal static bool IsFileHost(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            var host = uri.Host;
            return host == "onedrive.live.com" || host == "onedrive.com" || host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase) ||
                host == "1drv.ms" || host.EndsWith(".1drv.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".livefilestore.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".storage.live.com", StringComparison.OrdinalIgnoreCase) ||
                host == "microsoftpersonalcontent.com" || host.EndsWith(".microsoftpersonalcontent.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<OneDriveOriginalStream> OpenAsync(Uri uri, CookieContainer cookies, CancellationToken cancellationToken)
        {
            if (!IsFileHost(uri)) throw new InvalidOperationException("The original file must be hosted by OneDrive.");
            var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(45) };
            try
            {
                // Resolve redirects ourselves so the native stream cannot leave Microsoft's file hosts.
                for (var redirects = 0; redirects < 10; redirects++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Range = new RangeHeaderValue(0, 0);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri location)
                    {
                        uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                        if (!IsFileHost(uri)) throw new InvalidOperationException("OneDrive redirected to a sign-in page or an unsupported file host. Sign in and try again.");
                        continue;
                    }
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new InvalidOperationException("OneDrive denied file access. Sign in with an account that can download this video.");
                    response.EnsureSuccessStatusCode();
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range?.Length is not long length || length <= 0 || range.From != 0 || range.To != 0)
                        throw new InvalidOperationException("This link does not expose a seekable original video file. Check sharing access or try a different video link.");
                    var mime = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                    if (mime.Contains("html", StringComparison.OrdinalIgnoreCase) || mime.Contains("json", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("OneDrive returned a web page instead of the original video. Check sign-in and download permission.");
                    return new OneDriveOriginalStream(client, uri, length, mime);
                }
                throw new InvalidOperationException("Too many redirects while opening the original video.");
            }
            catch { client.Dispose(); throw; }
        }

        public override bool CanRead => !_disposed;
        internal OneDriveOriginalStream CloneAt(long position)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(_sharedClient, _uri, _length, ContentType) { _position = position };
        }
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _gate.WaitAsync(linked.Token);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (count == 0 || _position >= _length) return 0;
                if (_position < _cacheOffset || _position >= _cacheOffset + _cache.Length)
                {
                    var end = Math.Min(_length - 1, _position + 1024 * 1024 - 1);
                    using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
                    request.Headers.Range = new RangeHeaderValue(_position, end);
                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != _position || range.To != end || range.Length != _length)
                        throw new IOException("The original file stream changed or access expired. Reopen the OneDrive link.");
                    var block = new byte[checked((int)(end - _position + 1))];
                    await using var body = await response.Content.ReadAsStreamAsync(linked.Token);
                    await body.ReadExactlyAsync(block, linked.Token);
                    _cacheOffset = _position;
                    _cache = block;
                }
                var cacheIndex = checked((int)(_position - _cacheOffset));
                var copied = Math.Min(count, _cache.Length - cacheIndex);
                Buffer.BlockCopy(_cache, cacheIndex, buffer, offset, copied);
                _position += copied;
                return copied;
            }
            finally { _gate.Release(); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _gate.Wait();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => checked(_position + offset),
                    SeekOrigin.End => checked(_length + offset),
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                if (position < 0) throw new IOException("Cannot seek before the beginning of the video.");
                return _position = position;
            }
            finally { _gate.Release(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposeState, 1) == 0)
            {
                _lifetime.Cancel();
                _sharedClient.Release();
                _cache = Array.Empty<byte>();
            }
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private sealed class SharedClient
        {
            private readonly object _sync = new();
            private int _references = 1;
            internal HttpClient Client { get; }
            internal SharedClient(HttpClient client) => Client = client;
            internal void AddReference()
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_references == 0, this);
                    _references++;
                }
            }
            internal void Release()
            {
                lock (_sync)
                {
                    if (--_references == 0) Client.Dispose();
                }
            }
        }
    }
}
