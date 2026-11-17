#nullable enable

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Preview
{
    internal sealed class MajViewPreviewSession : IMajViewPreviewSession
    {
        private static readonly byte[] FallbackCover = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWPQ0ND4DwADTAGEEUpS3gAAAABJRU5ErkJggg==");
        private static readonly Lazy<byte[]> Page = new(() => ReadEmbedded("index.html"));
        private static readonly Lazy<byte[]> Script = new(() => ReadEmbedded("preview.js"));
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromMinutes(3));
        private readonly Dictionary<string, Resource> _resources;
        private readonly Task _server;
        private int _stopped;

        public MajViewPreviewSession(MajViewRuntime runtime, string title, ChartPreviewAssets assets,
            Func<string, CancellationToken, Task<Stream>> openMedia)
        {
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                title,
                hasVideo = assets.VideoPath is not null,
                embeddedViewport = MajViewRuntime.EmbeddedViewport,
                difficulties = assets.Difficulties.Select(item => new { number = item.Number, level = item.Level })
            });
            _resources = new(StringComparer.Ordinal)
            {
                [""] = Bytes(Page.Value, "text/html; charset=utf-8"),
                ["preview.js"] = Bytes(Script.Value, "application/javascript; charset=utf-8"),
                ["song.json"] = Bytes(manifest, "application/json; charset=utf-8"),
                ["chart"] = Bytes(Encoding.UTF8.GetBytes(assets.ChartText), "text/plain; charset=utf-8"),
                ["track"] = new("audio/mpeg", token => openMedia(assets.AudioPath, token)),
                ["cover"] = assets.CoverPath is { } cover
                    ? new Resource("image/png", token => openMedia(cover, token))
                    : Bytes(FallbackCover, "image/png")
            };
            foreach (var file in MajViewRuntime.Files)
            {
                _resources.Add("runtime/" + file.Name, new(file.ContentType, _ => Task.FromResult(runtime.Open(file.Name)), file.Length));
            }
            if (assets.VideoPath is { } video)
            {
                _resources["video.mp4"] = new Resource("video/mp4", token => openMedia(video, token));
            }

            var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            Address = new Uri($"http://127.0.0.1:{port}/{prefix}/");
            _listener = new HttpListener { IgnoreWriteExceptions = true };
            _listener.Prefixes.Add(Address.AbsoluteUri);
            try
            {
                _listener.Start();
            }
            catch
            {
                _listener.Close();
                _lifetime.Dispose();
                throw;
            }
            _server = ServeAsync(_lifetime.Token);
        }

        public Uri Address { get; }

        public async ValueTask DisposeAsync()
        {
            Stop();
            await _server.ConfigureAwait(false);
        }

        private async Task ServeAsync(CancellationToken token)
        {
            var requests = new List<Task>();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    requests.RemoveAll(request =>
                    {
                        _ = request.Exception;
                        return request.IsCompleted;
                    });
                    if (requests.Count >= 16)
                    {
                        await Task.WhenAny(requests).WaitAsync(token).ConfigureAwait(false);
                        continue;
                    }
                    var context = await _listener.GetContextAsync().WaitAsync(token).ConfigureAwait(false);
                    requests.Add(ServeRequestAsync(context, token));
                }
            }
            catch (Exception exception) when (token.IsCancellationRequested
                && exception is OperationCanceledException or ObjectDisposedException or HttpListenerException)
            {
            }
            finally
            {
                Stop();
                await Task.WhenAll(requests).ConfigureAwait(false);
                _lifetime.Dispose();
            }
        }

        private async Task ServeRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            var response = context.Response;
            var close = false;
            try
            {
                var request = context.Request;
                response.KeepAlive = false;
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["Referrer-Policy"] = "no-referrer";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'none'; connect-src 'self' blob:";
                if (request.Url is not { } url || url.Authority != Address.Authority
                    || !url.AbsolutePath.StartsWith(Address.AbsolutePath, StringComparison.Ordinal))
                {
                    response.StatusCode = 404;
                    return;
                }

                var route = url.AbsolutePath[Address.AbsolutePath.Length..];
                if (route == "closed" && request.HttpMethod == "POST")
                {
                    response.StatusCode = 204;
                    close = true;
                    return;
                }
                if (request.HttpMethod is not ("GET" or "HEAD"))
                {
                    response.StatusCode = 405;
                    return;
                }
                if (route == "alive")
                {
                    _lifetime.CancelAfter(TimeSpan.FromMinutes(3));
                    response.StatusCode = 204;
                    return;
                }
                if (!_resources.TryGetValue(route, out var resource))
                {
                    response.StatusCode = 404;
                    return;
                }

                if (route.StartsWith("runtime/", StringComparison.Ordinal))
                {
                    response.Headers["Cache-Control"] = "private, max-age=7200, immutable";
                }
                await using var source = await resource.Open(token).ConfigureAwait(false);
                response.ContentType = resource.Type;
                var seekable = source.CanSeek;
                var availableLength = seekable ? source.Length : resource.Length;
                if (availableLength is not { } length)
                {
                    response.SendChunked = true;
                    if (request.HttpMethod == "GET")
                    {
                        await source.CopyToAsync(response.OutputStream, token).ConfigureAwait(false);
                    }
                    return;
                }

                var count = length;
                if (seekable)
                {
                    response.Headers["Accept-Ranges"] = "bytes";
                }
                if (seekable && request.Headers["Range"] is { } range)
                {
                    if (!TryReadRange(range, length, out var start, out var end))
                    {
                        response.StatusCode = 416;
                        response.Headers["Content-Range"] = $"bytes */{length}";
                        return;
                    }
                    source.Position = start;
                    count = end - start + 1;
                    response.StatusCode = 206;
                    response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
                }
                response.ContentLength64 = count;
                if (request.HttpMethod == "GET")
                {
                    await CopyBytesAsync(source, response.OutputStream, count, token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or HttpListenerException or OperationCanceledException
                or ObjectDisposedException or UnauthorizedAccessException)
            {
                response.Abort();
            }
            finally
            {
                response.Close();
                if (close)
                {
                    Stop();
                }
            }
        }

        private void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _lifetime.Cancel();
                _listener.Close();
            }
        }

        private static bool TryReadRange(string range, long length, out long start, out long end)
        {
            start = end = 0;
            if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || length <= 0)
            {
                return false;
            }
            var bounds = range.AsSpan(6);
            var dash = bounds.IndexOf('-');
            if (dash < 0)
            {
                return false;
            }
            var last = bounds[(dash + 1)..];
            end = length - 1;
            if (dash == 0)
            {
                if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                {
                    return false;
                }
                start = Math.Max(0, length - suffix);
                return true;
            }
            if (!long.TryParse(bounds[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length
                || !last.IsEmpty && !long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out end))
            {
                return false;
            }
            end = Math.Min(end, length - 1);
            return end >= start;
        }

        private static async Task CopyBytesAsync(Stream source, Stream output, long bytes, CancellationToken token)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (bytes > 0)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes)), token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        throw new EndOfStreamException();
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    bytes -= count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static byte[] ReadEmbedded(string name)
        {
            using var stream = typeof(MajViewPreviewSession).Assembly.GetManifestResourceStream("MajMusic.MajView." + name)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }

        private static Resource Bytes(byte[] bytes, string type) => new(type, _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)));
        internal static FileStream OpenFile(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous);

        private sealed record Resource(string Type, Func<CancellationToken, Task<Stream>> Open, long? Length = null);
    }
}
