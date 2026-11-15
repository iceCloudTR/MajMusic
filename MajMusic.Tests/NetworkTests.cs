#nullable enable

using MajMusic.Core.Models;
using MajMusic.Infrastructure.Downloads;
using MajMusic.Infrastructure.Network;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MajMusic.Tests
{
    /// <summary>Exercises offline gates, endpoint contracts, credential isolation and completed-file cache semantics.</summary>
    internal static class NetworkTests
    {
        /// <summary>Runs deterministic network and cache regressions using only in-memory HTTP transports.</summary>
        /// <returns>A task representing the complete regression suite.</returns>
        public static async Task RunAsync()
        {
            await DefaultOfflineAndSourceMappingAsync();
            await LegacyIdentityAndGuestAsync();
            await ResourceIdsRemainPathSegmentsAsync();
            await OfflineCancelsRequestAsync();
            await SessionDoesNotCrossOriginsAsync();
            await CacheIntegrityAndRetentionAsync();
            await CacheAliasesAndRevisionsAsync();
            await CacheVersionScopesLazyAliasesAsync();
            await IndexFailurePreservesPinsAsync();
            await CacheCancellationAsync();
            await CancellationBeforePublicationAsync();
            Console.WriteLine("PASS network: offline gates, cancellation, source mapping, cookies, cache integrity and retention");
        }

        /// <summary>Checks that no source request occurs until explicitly enabled and metadata does not download media.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task DefaultOfflineAndSourceMappingAsync()
        {
            using var policy = new NetworkAccessPolicy();
            var paths = new List<string>();
            using var handler = new StubHandler((request, _) =>
            {
                paths.Add(request.RequestUri!.PathAndQuery);
                var body = request.RequestUri.AbsolutePath switch
                {
                    "/api/v1/info" => """{"id":"shared-server","name":"Test source"}""",
                    "/api/v1/maichart/list" => """[{"id":"track-1","title":"Music","artist":"Artist","hash":"source-revision","tags":["tag"]}]""",
                    "/api/v1/maichart/track-1/info" => """{"metadata":{"id":"track-1","title":"Music","artist":"Artist"},"resources":{"track":{"url":"https://cdn.example.test/music.mp3","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","length":123},"thumbnail":{"url":"maichart/track-1/image","length":12},"video":{"url":"https://cdn.example.test/ignored.mp4"}}}""",
                    _ => throw new InvalidOperationException("Unexpected test request.")
                };
                return Task.FromResult(JsonResponse(body));
            });
            using var transport = new HttpClient(handler);
            using var client = new MajNetClient(Endpoint(), policy, transport);
            TestAssert.True(!new AppSettings().OnlineEnabled, "New preferences must be offline.");
            TestAssert.Equal(0, new AppSettings().Endpoints.Count, "No source may be preconfigured.");
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.GetLibraryAsync());
            TestAssert.Equal(0, handler.RequestCount, "An offline action must not issue a request.");

            policy.SetEnabled(true);
            var tracks = await client.GetLibraryAsync();
            TestAssert.Equal(2, handler.RequestCount, "Library sync must use identity plus metadata only.");
            TestAssert.Equal("shared-server", tracks[0].SourceId, "Entries must use server identity.");
            TestAssert.Equal("/api/v1/maichart/track-1/track", tracks[0].Audio.RemoteUri!.AbsolutePath, "Audio route must preserve API prefix.");
            TestAssert.Equal("/api/v1/maichart/track-1/image", tracks[0].Cover!.RemoteUri!.AbsolutePath, "Cover route must preserve API prefix.");
            TestAssert.Equal("source-revision", tracks[0].Audio.CacheVersion, "Lazy audio aliases must retain the source revision.");
            TestAssert.Equal("source-revision", tracks[0].Cover!.CacheVersion, "Lazy cover aliases must retain the source revision.");
            var detailed = await client.GetTrackAsync("track-1");
            TestAssert.Equal(123L, detailed!.Audio.Length, "File metadata must retain byte length.");
            TestAssert.Equal("cdn.example.test", detailed.Audio.RemoteUri!.Host, "Public audio descriptors must remain separate resources.");
            TestAssert.True(paths.All(path => !path.Contains("video", StringComparison.Ordinal)), "Music sync must not request video.");

            policy.SetEnabled(false);
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.GetLibraryAsync());
            TestAssert.Equal(3, handler.RequestCount, "Cached server identity must not bypass the library's offline gate.");
        }

        /// <summary>Checks legacy server identity fallback, literal resource IDs and Guest-only restrictions.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task LegacyIdentityAndGuestAsync()
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var handler = new StubHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("/info", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : JsonResponse("[]")));
            using var transport = new HttpClient(handler);
            using var client = new MajNetClient(Endpoint(), policy, transport);
            var server = await client.GetServerAsync();
            TestAssert.Equal("source-config", server.Id, "Older endpoints must retain configured identity.");
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.GetRecommendationsAsync(0));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => client.GetCloudPlaylistsAsync());
            TestAssert.Equal(1, handler.RequestCount, "Guest restrictions must run before sending requests.");
            TestAssert.True(!client.IsLoggedIn, "A new source must start in Guest mode.");

            var rejected = false;
            try
            {
                using var invalid = new MajNetClient(Endpoint() with { BaseUri = new Uri("http://remote.example.test/api") }, policy);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            TestAssert.True(rejected, "Remote cleartext endpoints must be rejected.");
        }

        /// <summary>Checks that restoring offline mode cancels a request already in flight.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task OfflineCancelsRequestAsync()
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new StubHandler(async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return JsonResponse("{}");
            });
            using var transport = new HttpClient(handler);
            using var client = new MajNetClient(Endpoint(), policy, transport);
            var pending = client.GetLibraryAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            policy.SetEnabled(false);
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => pending);
            TestAssert.Equal(1, handler.RequestCount, "Cancellation must prevent follow-up library requests.");
        }

        /// <summary>Checks that source identifiers cannot replace the API path prefix or introduce a query.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task ResourceIdsRemainPathSegmentsAsync()
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            var paths = new List<string>();
            using var handler = new StubHandler((request, _) =>
            {
                paths.Add(request.RequestUri!.AbsoluteUri);
                return Task.FromResult(paths.Count == 1
                    ? JsonResponse("""{"id":"server"}""")
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var transport = new HttpClient(handler);
            using var client = new MajNetClient(Endpoint(), policy, transport);
            await client.GetTrackAsync("id/part?x=1");
            TestAssert.True(paths[1].Contains("/api/v1/maichart/id%2Fpart%3Fx%3D1/info", StringComparison.Ordinal), "Source identifiers must be escaped as one path segment.");
        }

        /// <summary>Checks password wire compatibility and removes session headers before a cross-origin media redirect.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task SessionDoesNotCrossOriginsAsync()
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            string? sentForm = null;
            using var apiHandler = new StubHandler(async (request, token) =>
            {
                sentForm = await request.Content!.ReadAsStringAsync(token);
                var response = JsonResponse("{}");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "session=private-session; Path=/; Secure; HttpOnly");
                return response;
            });
            using var transport = new HttpClient(apiHandler);
            using var client = new MajNetClient(Endpoint(), policy, transport);
            await client.LoginAsync("listener", "test-password");
            TestAssert.True(client.IsLoggedIn, "A successful explicit login must establish session state.");
            var expectedDigest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("test-password"))).ToLowerInvariant();
            TestAssert.True(sentForm!.Contains(expectedDigest, StringComparison.Ordinal) && !sentForm.Contains("test-password", StringComparison.Ordinal), "Login must use the established digest protocol.");

            var cookies = new List<string?>();
            using var mediaHandler = new StubHandler((request, _) =>
            {
                cookies.Add(request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : null);
                if (request.RequestUri!.Host == "source.example.test")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://cdn.example.test/audio.mp3");
                    return Task.FromResult(redirect);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            });
            using var mediaClient = client.CreateMediaClient(mediaHandler);
            using var mediaResponse = await mediaClient.GetAsync("https://source.example.test/api/v1/maichart/a/track");
            TestAssert.True(cookies[0]?.Contains("private-session", StringComparison.Ordinal) == true, "Protected same-origin audio must receive the endpoint session.");
            TestAssert.True(cookies[1] is null, "Cross-origin media must not receive the endpoint's session.");
        }

        /// <summary>Checks corruption rejection, offline reuse, content deduplication and persisted pinned retention.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task CacheIntegrityAndRetentionAsync()
        {
            var root = NewTemporaryRoot();
            try
            {
                using var policy = new NetworkAccessPolicy();
                var bytes = Encoding.UTF8.GetBytes("completed-audio-object");
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
                using var transport = new HttpClient(handler);
                var resource = new MediaResource { RemoteUri = new Uri("https://cdn.example.test/audio.mp3"), Sha256 = hash, Length = bytes.Length };
                using (var cache = new MediaCache(root, transport, policy))
                {
                    await TestAssert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(resource));
                    TestAssert.Equal(0, handler.RequestCount, "An offline cache miss must not issue HTTP.");
                    policy.SetEnabled(true);
                    await TestAssert.ThrowsAsync<InvalidDataException>(() => cache.GetAsync(resource with { Sha256 = new string('a', 64) }));
                    TestAssert.True(!Directory.Exists(Path.Combine(root, "objects")), "A bad digest must never publish an object.");
                    var paths = await Task.WhenAll(cache.GetAsync(resource), cache.GetAsync(resource, true));
                    TestAssert.Equal(paths[0], paths[1], "Concurrent requests must share one completed object.");
                    TestAssert.Equal(2, handler.RequestCount, "Only one valid download may be needed after the corrupt attempt.");
                    policy.SetEnabled(false);
                    var offlinePath = await cache.GetAsync(resource);
                    TestAssert.True(File.Exists(offlinePath), "A cache hit must remain playable offline.");
                    TestAssert.Equal(0, await cache.ClearCacheAsync(), "Pinned downloads must survive cache cleanup.");
                }

                using var reopened = new MediaCache(root, transport, policy);
                var persisted = await reopened.GetAsync(resource);
                TestAssert.True(File.Exists(persisted), "The cache index must survive process restart.");
                TestAssert.Equal(0, await reopened.ClearCacheAsync(), "Pin state must survive process restart.");
                TestAssert.Equal(2, handler.RequestCount, "Offline reuse and cleanup must not contact a source.");
                TestAssert.True(!File.ReadAllText(Path.Combine(root, "index.json")).Contains("https://", StringComparison.Ordinal), "The cache index must not persist signed URLs.");

                var unpinnedRoot = Path.Combine(root, "unpinned");
                policy.SetEnabled(true);
                using var unpinned = new MediaCache(unpinnedRoot, transport, policy);
                var unpinnedPath = await unpinned.GetAsync(resource);
                policy.SetEnabled(false);
                TestAssert.Equal(1, await unpinned.ClearCacheAsync(), "Ordinary cached objects must be evictable.");
                TestAssert.True(!File.Exists(unpinnedPath), "Cleanup must remove the unpinned object.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>Checks that an offline transition cannot publish an unfinished media transfer.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task CacheCancellationAsync()
        {
            var root = NewTemporaryRoot();
            try
            {
                using var policy = new NetworkAccessPolicy();
                policy.SetEnabled(true);
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var handler = new StubHandler(async (_, token) =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });
                using var transport = new HttpClient(handler);
                using var cache = new MediaCache(root, transport, policy);
                var pending = cache.GetAsync(new MediaResource { RemoteUri = new Uri("https://cdn.example.test/audio.mp3") });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                policy.SetEnabled(false);
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => pending);
                TestAssert.True(!Directory.Exists(Path.Combine(root, "objects")), "Cancellation must not publish a media object.");
                TestAssert.True(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(), "Cancellation must clean temporary files.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>Checks lazy-address aliases across hash enrichment, source revisions and cache restarts.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task CacheAliasesAndRevisionsAsync()
        {
            var root = NewTemporaryRoot();
            try
            {
                using var policy = new NetworkAccessPolicy();
                policy.SetEnabled(true);
                var firstBytes = Encoding.UTF8.GetBytes("version-one");
                var secondBytes = Encoding.UTF8.GetBytes("version-two");
                var currentBytes = firstBytes;
                var firstHash = Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant();
                var secondHash = Convert.ToHexString(SHA256.HashData(secondBytes)).ToLowerInvariant();
                using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(currentBytes) }));
                using var transport = new HttpClient(handler);
                var lazy = new MediaResource { RemoteUri = new Uri("https://cdn.example.test/stable-audio.mp3") };
                var first = lazy with { Sha256 = firstHash, Length = firstBytes.Length };
                var second = lazy with { Sha256 = secondHash, Length = secondBytes.Length };
                string firstPath;
                string secondPath;
                using (var cache = new MediaCache(root, transport, policy))
                {
                    firstPath = await cache.GetAsync(lazy);
                    policy.SetEnabled(false);
                    TestAssert.Equal(firstPath, await cache.GetAsync(first, true), "Hash enrichment must find a previously downloaded lazy object offline.");
                    await TestAssert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(second));
                    TestAssert.Equal(1, handler.RequestCount, "A different expected revision must not reuse a stale URI alias.");
                    policy.SetEnabled(true);
                    currentBytes = secondBytes;
                    secondPath = await cache.GetAsync(second);
                    TestAssert.True(firstPath != secondPath, "Different verified content must have separate object paths.");
                    policy.SetEnabled(false);
                }

                using var reopened = new MediaCache(root, transport, policy);
                TestAssert.Equal(secondPath, await reopened.GetAsync(lazy), "A lazy reference must resolve to the latest verified URI alias after restart.");
                TestAssert.Equal(firstPath, await reopened.GetAsync(first), "An explicit old digest must retain its original object after the URI changes.");
                TestAssert.Equal(1, await reopened.ClearCacheAsync(), "Only the new unpinned revision should be removed.");
                TestAssert.True(File.Exists(firstPath), "A newer URI alias must not overwrite the old revision's pin.");
                TestAssert.True(!File.Exists(secondPath), "Unpinned newer content remains evictable.");
                TestAssert.Equal(firstPath, await reopened.GetAsync(first), "The pinned revision must remain readable offline after cleanup.");
                TestAssert.Equal(2, handler.RequestCount, "Aliases and explicit digest lookups must not introduce duplicate transfers.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>Checks cancellation after the transfer completes but before digest validation and publication.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task CancellationBeforePublicationAsync()
        {
            var root = NewTemporaryRoot();
            try
            {
                using var policy = new NetworkAccessPolicy();
                policy.SetEnabled(true);
                using var cancellation = new CancellationTokenSource();
                using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new CancelAfterTransferStream(Encoding.UTF8.GetBytes("completed-transfer"), cancellation))
                }));
                using var transport = new HttpClient(handler);
                using var cache = new MediaCache(root, transport, policy);
                var resource = new MediaResource { RemoteUri = new Uri("https://cdn.example.test/cancel.mp3") };
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => cache.GetAsync(resource, true, cancellation.Token));
                TestAssert.True(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(), "Cancellation before publication must leave no completed file or index.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>Checks that a failed index commit cannot remove an existing pin or delete cleanup candidates.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task IndexFailurePreservesPinsAsync()
        {
            var root = NewTemporaryRoot();
            try
            {
                using var policy = new NetworkAccessPolicy();
                policy.SetEnabled(true);
                var pinnedBytes = Encoding.UTF8.GetBytes("pinned-object");
                var cachedBytes = Encoding.UTF8.GetBytes("cached-object");
                var currentBytes = pinnedBytes;
                using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(currentBytes) }));
                using var transport = new HttpClient(handler);
                var pinned = new MediaResource
                {
                    RemoteUri = new Uri("https://cdn.example.test/pinned.mp3"),
                    Sha256 = Convert.ToHexString(SHA256.HashData(pinnedBytes)).ToLowerInvariant()
                };
                var cached = new MediaResource
                {
                    RemoteUri = new Uri("https://cdn.example.test/cached.mp3"),
                    Sha256 = Convert.ToHexString(SHA256.HashData(cachedBytes)).ToLowerInvariant()
                };
                string pinnedPath;
                string cachedPath;
                using (var cache = new MediaCache(root, transport, policy))
                {
                    pinnedPath = await cache.GetAsync(pinned, true);
                    currentBytes = cachedBytes;
                    cachedPath = await cache.GetAsync(cached);
                    policy.SetEnabled(false);
                    var indexPath = Path.Combine(root, "index.json");
                    var backupPath = Path.Combine(root, "saved-index.json");
                    File.Move(indexPath, backupPath);
                    Directory.CreateDirectory(indexPath);
                    try
                    {
                        await ExpectIndexWriteFailureAsync(() => cache.GetAsync(pinned with { RemoteUri = new Uri("https://cdn.example.test/new-alias.mp3") }));
                        currentBytes = Encoding.UTF8.GetBytes("failed-new-object");
                        var failedResource = pinned with { Sha256 = Convert.ToHexString(SHA256.HashData(currentBytes)).ToLowerInvariant() };
                        policy.SetEnabled(true);
                        await ExpectIndexWriteFailureAsync(() => cache.GetAsync(failedResource, true));
                        policy.SetEnabled(false);
                        TestAssert.Equal(2, Directory.EnumerateFiles(Path.Combine(root, "objects"), "*", SearchOption.AllDirectories).Count(), "An object with a failed index commit must be rolled back without deleting existing files.");
                        await ExpectIndexWriteFailureAsync(() => cache.ClearCacheAsync());
                        TestAssert.True(File.Exists(pinnedPath), "An alias persistence failure must preserve already pinned content.");
                        TestAssert.True(File.Exists(cachedPath), "Cleanup must persist its decision before deleting files.");
                    }
                    finally
                    {
                        Directory.Delete(indexPath);
                        File.Move(backupPath, indexPath);
                    }

                    TestAssert.Equal(1, await cache.ClearCacheAsync(), "A failed cleanup must not mutate the active index.");
                }

                using var reopened = new MediaCache(root, transport, policy);
                TestAssert.Equal(pinnedPath, await reopened.GetAsync(pinned), "An existing pin must survive failed commits and restart.");
                TestAssert.Equal(0, await reopened.ClearCacheAsync(), "Persisted pinned content remains protected.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>Checks unknown-digest revisions cannot reuse an earlier revision's stable-address alias.</summary>
        /// <returns>A task representing the regression.</returns>
        private static async Task CacheVersionScopesLazyAliasesAsync()
        {
            var root = NewTemporaryRoot();
            try
            {
                using var policy = new NetworkAccessPolicy();
                policy.SetEnabled(true);
                var bytes = Encoding.UTF8.GetBytes("verified-one-revision");
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
                using var transport = new HttpClient(handler);
                var first = new MediaResource { RemoteUri = new Uri("https://cdn.example.test/stable.mp3"), CacheVersion = "revision-one" };
                var next = first with { CacheVersion = "revision-two" };
                string completedPath;
                using (var cache = new MediaCache(root, transport, policy))
                {
                    completedPath = await cache.GetAsync(first, true);
                    policy.SetEnabled(false);
                    TestAssert.Equal(completedPath, await cache.GetAsync(first with { Sha256 = hash }), "Digest enrichment of the same revision must work offline.");
                    await TestAssert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(next));
                }

                using var reopened = new MediaCache(root, transport, policy);
                TestAssert.Equal(completedPath, await reopened.GetAsync(first), "A revision-scoped lazy alias must survive restart.");
                await TestAssert.ThrowsAsync<InvalidOperationException>(() => reopened.GetAsync(next));
                TestAssert.Equal(completedPath, await reopened.GetAsync(next with { Sha256 = hash }), "An explicitly verified content identity may deduplicate across source revisions.");
                TestAssert.Equal(completedPath, await reopened.GetAsync(next), "A new revision alias becomes valid only after its digest has been verified.");
                TestAssert.Equal(0, await reopened.ClearCacheAsync(), "Cross-revision aliases must preserve the original object's pin.");
                TestAssert.Equal(1, handler.RequestCount, "Version-scoped offline lookups must not request media.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>Provides a fixed synthetic endpoint whose address is never accessed over the network.</summary>
        /// <returns>The test-only endpoint configuration.</returns>
        private static EndpointConfiguration Endpoint()
        {
            return new EndpointConfiguration { Id = "source-config", Name = "Source", BaseUri = new Uri("https://source.example.test/api/v1") };
        }

        /// <summary>Checks a deterministic index-path conflict across platform-specific filesystem error mappings.</summary>
        /// <param name="action">The index write expected to fail.</param>
        /// <returns>A task representing the assertion.</returns>
        private static async Task ExpectIndexWriteFailureAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            throw new InvalidOperationException("The conflicting index path should prevent publication.");
        }

        /// <summary>Creates a successful in-memory JSON response.</summary>
        /// <param name="body">The response JSON.</param>
        /// <returns>The response owned by the caller.</returns>
        private static HttpResponseMessage JsonResponse(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        /// <summary>Allocates an isolated temporary directory for one cache regression.</summary>
        /// <returns>The unique temporary root.</returns>
        private static string NewTemporaryRoot()
        {
            var path = Path.Combine(Path.GetTempPath(), $"MajMusic-NetworkTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Replaces sockets with deterministic request behavior.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            /// <summary>Processes each synthetic request.</summary>
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

            /// <summary>Gets the observed number of outgoing attempts.</summary>
            public int RequestCount { get; private set; }

            /// <summary>Creates a deterministic test transport.</summary>
            /// <param name="send">The request callback.</param>
            public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
            {
                _send = send;
            }

            /// <summary>Records and executes an in-memory HTTP request.</summary>
            /// <param name="request">The request to inspect.</param>
            /// <param name="cancellationToken">Cancels the synthetic request.</param>
            /// <returns>The callback's response.</returns>
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return _send(request, cancellationToken);
            }
        }

        /// <summary>Ends a synthetic transfer by cancelling its caller before cache publication begins.</summary>
        private sealed class CancelAfterTransferStream : MemoryStream
        {
            /// <summary>Cancels the caller once all bytes have been copied.</summary>
            private readonly CancellationTokenSource _cancellation;

            /// <summary>Creates an in-memory body with a deterministic cancellation boundary.</summary>
            /// <param name="bytes">The complete synthetic body.</param>
            /// <param name="cancellation">The caller's cancellation source.</param>
            public CancelAfterTransferStream(byte[] bytes, CancellationTokenSource cancellation)
                : base(bytes, false)
            {
                _cancellation = cancellation;
            }

            /// <summary>Copies the complete body and then cancels the pending cache publication.</summary>
            /// <param name="destination">The temporary cache file.</param>
            /// <param name="bufferSize">The requested copy buffer size.</param>
            /// <param name="cancellationToken">Cancels the transfer.</param>
            /// <returns>A task representing the completed body copy.</returns>
            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                await base.CopyToAsync(destination, bufferSize, cancellationToken);
                _cancellation.Cancel();
            }

            /// <summary>Cancels at the same end-of-body boundary when the consumer reads measured chunks.</summary>
            /// <param name="buffer">The next response buffer.</param>
            /// <param name="cancellationToken">Cancels the response read.</param>
            /// <returns>The number of bytes read before the end-of-body cancellation.</returns>
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var count = await base.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    _cancellation.Cancel();
                }

                return count;
            }
        }
    }
}
