#nullable enable

using System.Collections.Concurrent;
using System.Net;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Downloads;
using MajMusic.Infrastructure.Network;

namespace MajMusic.Tests
{
    /// <summary>Checks independent media transfers, shared progress and cancellation without real HTTP or user files.</summary>
    internal static class MediaCacheConcurrencyTests
    {
        /// <summary>Runs deterministic cache races using response streams controlled by the test.</summary>
        /// <returns>A task representing all concurrent media-cache regressions.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-cache-concurrency-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                await IndependentResourcesAsync(Path.Combine(root, "independent"));
                await SharedTransferProgressAsync(Path.Combine(root, "shared"));
                await IdenticalContentPinAsync(Path.Combine(root, "identical"));
                await CancellationAndRetryAsync(Path.Combine(root, "cancel"));
                await BodyIdleTimeoutAsync(Path.Combine(root, "idle"));
                await ContinuousBodyAsync(Path.Combine(root, "continuous"));
            }
            finally
            {
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var resolved = Path.GetFullPath(root);
                TestAssert.True(resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(resolved).StartsWith("MajMusic-cache-concurrency-", StringComparison.Ordinal),
                    "Concurrency fixtures must remain beneath the temporary directory.");
                Directory.Delete(resolved, true);
            }
        }

        /// <summary>Checks a paused cover cannot block another download, a completed hit or cache cleanup.</summary>
        /// <param name="root">The isolated cache directory.</param>
        /// <returns>A task representing the independent-resource checks.</returns>
        private static async Task IndependentResourcesAsync(string root)
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var slow = new GatedBodyStream("slow-cover"u8.ToArray());
            using var transport = new RoutingTransport(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath == "/cover" ? new StreamContent(slow) : new ByteArrayContent("audio"u8.ToArray())
            });
            using var client = new HttpClient(transport);
            using var cache = new MediaCache(root, client, policy);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cached = Resource("cached");
            var cachedPath = await cache.GetAsync(cached, true, lifetime.Token);
            var cover = cache.GetAsync(Resource("cover"), cancellationToken: lifetime.Token);
            Task<string>? audio = null;
            Task<string>? hit = null;
            try
            {
                await slow.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
                audio = cache.GetAsync(Resource("next-audio"), cancellationToken: lifetime.Token);
                var progress = new ProgressSink();
                hit = cache.GetAsync(cached, cancellationToken: lifetime.Token, progress: progress);
                var nextPath = await audio.WaitAsync(TimeSpan.FromSeconds(2));
                TestAssert.Equal(cachedPath, await hit.WaitAsync(TimeSpan.FromSeconds(2)), "A slow response cannot delay a completed audio hit.");
                TestAssert.True(File.Exists(nextPath) && !cover.IsCompleted, "A different audio must complete while the cover body remains paused.");
                TestAssert.Equal(0, progress.Snapshots.Count, "A completed cache hit must not invent a download progress bar.");
                await cache.ClearCacheAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(2));
                TestAssert.Equal(1, Directory.EnumerateFiles(Path.Combine(root, "partial")).Count(),
                    "Cache cleanup must leave an active private transfer file intact.");
                slow.Release.TrySetResult();
                TestAssert.True(File.Exists(await cover), "A transfer must remain publishable after concurrent cleanup.");
                TestAssert.True(!Directory.EnumerateFiles(Path.Combine(root, "partial")).Any(), "Completed transfers leave no partial file.");
            }
            finally
            {
                lifetime.Cancel();
                slow.Release.TrySetResult();
                await ObserveAsync(cover, audio ?? Task.CompletedTask, hit ?? Task.CompletedTask);
            }
        }

        /// <summary>Checks one resource uses one request and later callers receive actual shared progress and pin retention.</summary>
        /// <param name="root">The isolated cache directory.</param>
        /// <returns>A task representing coalescing and progress checks.</returns>
        private static async Task SharedTransferProgressAsync(string root)
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            var bytes = "shared-audio-response"u8.ToArray();
            using var body = new GatedBodyStream(bytes, 4);
            using var transport = new RoutingTransport(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
            using var client = new HttpClient(transport);
            using var cache = new MediaCache(root, client, policy);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reentrantCleanupFinished = false;
            var ownerProgress = new ProgressSink(snapshot =>
            {
                if (snapshot.ReceivedBytes == 0)
                {
                    reentrantCleanupFinished = cache.ClearCacheAsync().Wait(TimeSpan.FromSeconds(2));
                }
            });
            var joiningProgress = new ProgressSink();
            var resource = Resource("shared");
            var first = cache.GetAsync(resource, cancellationToken: lifetime.Token, progress: ownerProgress);
            Task<string>? second = null;
            try
            {
                await body.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
                second = cache.GetAsync(resource, true, lifetime.Token, progress: joiningProgress);
                TestAssert.True(joiningProgress.Snapshots.Any(snapshot => snapshot.ReceivedBytes == 4),
                    "A joining caller must immediately receive the already transferred byte count.");
                using var canceledWait = new CancellationTokenSource();
                var canceled = cache.GetAsync(resource, cancellationToken: canceledWait.Token);
                canceledWait.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => canceled);
                TestAssert.True(!first.IsCompleted, "Canceling a waiter cannot cancel the current resource owner.");
                body.Release.TrySetResult();
                TestAssert.Equal(await first, await second, "Waiting callers must share the completed content object.");
                TestAssert.Equal(1, transport.Count("/shared"), "Concurrent identical resources must issue one HTTP request.");
                TestAssert.True(reentrantCleanupFinished, "Progress callbacks must run outside the index gate.");
                foreach (var observer in new[] { ownerProgress, joiningProgress })
                {
                    var snapshots = observer.Snapshots.ToArray();
                    TestAssert.True(snapshots[^1] is { IsCompleted: true } completed && completed.ReceivedBytes == bytes.Length,
                        "Every active subscriber must receive the complete final byte count.");
                    TestAssert.True(snapshots[^1].TotalBytes is null, "Unknown-length bodies must retain an indeterminate total.");
                    TestAssert.True(snapshots.Select(snapshot => snapshot.ReceivedBytes).SequenceEqual(
                        snapshots.Select(snapshot => snapshot.ReceivedBytes).Order()), "Subscriber progress must not replay older byte counts after newer ones.");
                    TestAssert.True(snapshots.Length <= 4, "Byte progress must be throttled rather than reported for every small read.");
                }

                TestAssert.Equal(0, await cache.ClearCacheAsync(), "A coalesced caller's pin must survive the owner's unpinned publication.");
                using var response = await client.GetAsync("https://cache.example.test/borrowed-client");
                TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, "The cache must not dispose its caller-owned HTTP client.");
            }
            finally
            {
                lifetime.Cancel();
                body.Release.TrySetResult();
                await ObserveAsync(first, second ?? Task.CompletedTask);
            }
        }

        /// <summary>Checks separate URI transfers publishing the same digest cannot lose either alias or a concurrent pin.</summary>
        /// <param name="root">The isolated cache directory.</param>
        /// <returns>A task representing content-addressed publication checks.</returns>
        private static async Task IdenticalContentPinAsync(string root)
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            var bytes = "identical-content-at-two-addresses"u8.ToArray();
            using var firstBody = new GatedBodyStream(bytes);
            using var secondBody = new GatedBodyStream(bytes);
            using var transport = new RoutingTransport(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(request.RequestUri!.AbsolutePath == "/one" ? firstBody : secondBody)
            });
            using var client = new HttpClient(transport);
            using var cache = new MediaCache(root, client, policy);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = cache.GetAsync(Resource("one"), cancellationToken: lifetime.Token);
            var second = cache.GetAsync(Resource("two"), true, lifetime.Token);
            try
            {
                await Task.WhenAll(firstBody.Blocked.Task, secondBody.Blocked.Task).WaitAsync(TimeSpan.FromSeconds(2));
                secondBody.Release.TrySetResult();
                var pinnedPath = await second;
                firstBody.Release.TrySetResult();
                TestAssert.Equal(pinnedPath, await first, "Separate aliases must converge on the same validated hash path.");
                TestAssert.Equal(1, Directory.EnumerateFiles(Path.Combine(root, "objects"), "*", SearchOption.AllDirectories).Count(),
                    "Concurrent identical content must publish one physical object.");
                TestAssert.Equal(0, await cache.ClearCacheAsync(), "A later unpinned publish cannot erase an already committed pin.");
                policy.SetEnabled(false);
                using var reopened = new MediaCache(root, client, policy);
                TestAssert.Equal(pinnedPath, await reopened.GetAsync(Resource("one")), "The first alias must survive atomic publication and restart.");
                TestAssert.Equal(pinnedPath, await reopened.GetAsync(Resource("two")), "The second alias must survive atomic publication and restart.");
            }
            finally
            {
                lifetime.Cancel();
                firstBody.Release.TrySetResult();
                secondBody.Release.TrySetResult();
                await ObserveAsync(first, second);
            }
        }

        /// <summary>Checks canceling an owner allows a waiting caller to retry with its own transport and cleans interrupted files.</summary>
        /// <param name="root">The isolated cache directory.</param>
        /// <returns>A task representing owner cancellation and offline cancellation checks.</returns>
        private static async Task CancellationAndRetryAsync(string root)
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var firstBody = new GatedBodyStream("canceled-response"u8.ToArray());
            using var firstTransport = new RoutingTransport(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(firstBody) });
            using var secondTransport = new RoutingTransport(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("retry-response"u8.ToArray()) });
            using var firstClient = new HttpClient(firstTransport);
            using var secondClient = new HttpClient(secondTransport);
            using var cache = new MediaCache(root, secondClient, policy);
            using var owner = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resource = Resource("retry");
            var first = cache.GetAsync(resource, cancellationToken: owner.Token, downloadClient: firstClient);
            await firstBody.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var retry = cache.GetAsync(resource, downloadClient: secondClient);
            owner.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => first);
            firstClient.Dispose();
            TestAssert.True(File.Exists(await retry.WaitAsync(TimeSpan.FromSeconds(2))), "A canceled owner cannot poison the per-resource gate.");
            TestAssert.Equal(1, firstTransport.Count("/retry"), "The canceled owner's client must serve only its own request.");
            TestAssert.Equal(1, secondTransport.Count("/retry"), "A waiting caller must retry using its own still-owned endpoint client.");
            TestAssert.True(!Directory.EnumerateFiles(Path.Combine(root, "partial")).Any(), "Canceled and completed attempts must remove their private files.");

            using var offlineBody = new GatedBodyStream("offline-canceled"u8.ToArray());
            using var offlineTransport = new RoutingTransport(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(offlineBody) });
            using var offlineClient = new HttpClient(offlineTransport);
            var offline = cache.GetAsync(Resource("offline"), downloadClient: offlineClient);
            await offlineBody.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            policy.SetEnabled(false);
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => offline);
            TestAssert.True(!Directory.EnumerateFiles(Path.Combine(root, "partial")).Any(), "Going offline must remove the interrupted body file.");
        }

        /// <summary>Checks a response with immediate headers and a stalled body gets its own read deadline.</summary>
        /// <param name="root">The isolated cache directory.</param>
        /// <returns>A task representing the response-body timeout checks.</returns>
        private static async Task BodyIdleTimeoutAsync(string root)
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var body = new GatedBodyStream("stalled-body"u8.ToArray());
            using var transport = new RoutingTransport(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
            using var client = new HttpClient(transport) { Timeout = TimeSpan.FromMilliseconds(10) };
            using var cache = new MediaCache(root, client, policy, TimeSpan.FromMilliseconds(80));
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = cache.GetAsync(Resource("idle"), cancellationToken: lifetime.Token);
            try
            {
                await body.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await TestAssert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
                TestAssert.True(pending.IsCompleted, "The response-body deadline, rather than the test's outer deadline, must stop the request.");
                TestAssert.True(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(),
                    "A stalled response must leave neither a partial body nor a completed object.");
            }
            finally
            {
                lifetime.Cancel();
                body.Release.TrySetResult();
                await ObserveAsync(pending);
            }
        }

        /// <summary>Checks the idle deadline resets after every received chunk instead of limiting the whole response duration.</summary>
        /// <param name="root">The isolated cache directory.</param>
        /// <returns>A task representing continuously advancing slow-response checks.</returns>
        private static async Task ContinuousBodyAsync(string root)
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            var bytes = "abcdefgh"u8.ToArray();
            using var transport = new RoutingTransport(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PacedBodyStream(bytes)) });
            using var client = new HttpClient(transport);
            using var cache = new MediaCache(root, client, policy, TimeSpan.FromMilliseconds(300));
            var progress = new ProgressSink();
            var path = await cache.GetAsync(Resource("continuous"), progress: progress).WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "A continuously advancing body must complete after multiple idle intervals.");
            TestAssert.True(progress.Snapshots.Last() is { IsCompleted: true, TotalBytes: 8, ReceivedBytes: 8 },
                "A known-length response must report its final byte count and completion.");
            TestAssert.True(progress.Snapshots.Count >= 3 && progress.Snapshots.Count <= 7,
                "Longer transfers must expose intermediate progress with approximately 100ms throttling.");
        }

        /// <summary>Creates a fake HTTPS resource without using any real service.</summary>
        /// <param name="name">The resource's distinct path.</param>
        /// <returns>The remote descriptor handled entirely in memory.</returns>
        private static MediaResource Resource(string name)
        {
            return new MediaResource { RemoteUri = new Uri("https://cache.example.test/" + name) };
        }

        /// <summary>Observes canceled test operations before disposing their cache or temporary files.</summary>
        /// <param name="tasks">The operations whose cleanup must finish.</param>
        /// <returns>A task representing observed completion regardless of the original test outcome.</returns>
        private static async Task ObserveAsync(params Task[] tasks)
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                // The original assertion or cancellation remains the test outcome.
            }
        }

        /// <summary>Counts in-memory requests and immediately supplies their response headers.</summary>
        private sealed class RoutingTransport : HttpMessageHandler
        {
            /// <summary>Constructs responses without performing network I/O.</summary>
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
            /// <summary>Counts requests independently for every fake path.</summary>
            private readonly ConcurrentDictionary<string, int> _counts = new();

            /// <summary>Creates an immediate-header test transport.</summary>
            /// <param name="response">The fake response factory.</param>
            public RoutingTransport(Func<HttpRequestMessage, HttpResponseMessage> response)
            {
                _response = response;
            }

            /// <summary>Gets the request count for a fake path.</summary>
            /// <param name="path">The absolute request path.</param>
            /// <returns>The number of requests received for that path.</returns>
            public int Count(string path)
            {
                return _counts.GetValueOrDefault(path);
            }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _counts.AddOrUpdate(request.RequestUri!.AbsolutePath, 1, (_, count) => count + 1);
                return Task.FromResult(_response(request));
            }
        }

        /// <summary>Records synchronous progress safely across concurrent callers.</summary>
        private sealed class ProgressSink : IProgress<MediaTransferProgress>
        {
            /// <summary>Optionally checks reentrant non-owning notification behavior.</summary>
            private readonly Action<MediaTransferProgress>? _onReport;

            /// <summary>Creates an observed progress receiver.</summary>
            /// <param name="onReport">An optional callback invoked synchronously after recording.</param>
            public ProgressSink(Action<MediaTransferProgress>? onReport = null)
            {
                _onReport = onReport;
            }

            /// <summary>Gets the snapshots actually delivered to this subscriber.</summary>
            public ConcurrentQueue<MediaTransferProgress> Snapshots { get; } = new();

            /// <inheritdoc />
            public void Report(MediaTransferProgress value)
            {
                Snapshots.Enqueue(value);
                _onReport?.Invoke(value);
            }
        }

        /// <summary>Optionally emits a measured prefix, then pauses body reads until explicitly released or canceled.</summary>
        private sealed class GatedBodyStream : Stream
        {
            /// <summary>Stores the complete fake response body.</summary>
            private readonly byte[] _bytes;
            /// <summary>Stores the optional prefix emitted before the pause.</summary>
            private readonly int _prefix;
            /// <summary>Stores the next response byte.</summary>
            private int _position;

            /// <summary>Creates a response with a controllable body-read boundary.</summary>
            /// <param name="bytes">The response payload.</param>
            /// <param name="prefix">Bytes to emit after a measurable interval before pausing.</param>
            public GatedBodyStream(byte[] bytes, int prefix = 0)
            {
                _bytes = bytes;
                _prefix = prefix;
            }

            /// <summary>Signals that the response body is waiting for more bytes.</summary>
            public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Allows the response body to continue.</summary>
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <inheritdoc />
            public override bool CanRead => true;
            /// <inheritdoc />
            public override bool CanSeek => false;
            /// <inheritdoc />
            public override bool CanWrite => false;
            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();
            /// <inheritdoc />
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            /// <inheritdoc />
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position >= _bytes.Length)
                {
                    return 0;
                }

                if (_position == 0 && _prefix > 0)
                {
                    await Task.Delay(125, cancellationToken);
                    var prefix = Math.Min(_prefix, buffer.Length);
                    _bytes.AsMemory(0, prefix).CopyTo(buffer);
                    _position = prefix;
                    return prefix;
                }

                Blocked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                var count = Math.Min(buffer.Length, _bytes.Length - _position);
                _bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>Advances slowly for longer than an idle interval while supplying bytes regularly.</summary>
        private sealed class PacedBodyStream : MemoryStream
        {
            /// <summary>Creates a read-only paced response.</summary>
            /// <param name="bytes">The complete response payload.</param>
            public PacedBodyStream(byte[] bytes) : base(bytes, false)
            {
            }

            /// <inheritdoc />
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(60, cancellationToken);
                return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
            }
        }
    }
}
