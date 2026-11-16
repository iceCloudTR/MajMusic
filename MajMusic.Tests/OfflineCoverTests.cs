#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Downloads;
using MajMusic.Infrastructure.Network;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Tests
{
    /// <summary>Checks that explicit offline audio retains artwork and cached resources never depend on account discovery.</summary>
    internal static class OfflineCoverTests
    {
        /// <summary>Runs isolated download, repair, cancellation and cache-identity regressions.</summary>
        /// <returns>A task representing the complete offline-artwork suite.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-offline-covers-" + Guid.NewGuid().ToString("N"));
            try
            {
                await ValidateCompleteDownloadAsync(Path.Combine(root, "complete"));
                await ValidateExistingAudioRepairAsync(Path.Combine(root, "repair"));
                await ValidateCoverCancellationAsync(Path.Combine(root, "cancel"));
                await ValidateLocalOnlyIdentityAsync(Path.Combine(root, "identity"));
                await ValidateSameOriginSlotsAsync(Path.Combine(root, "slots"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-offline-covers-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the offline-cover test directory.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Downloads both resources, survives cleanup and reopens without consulting a rejecting source.</summary>
        /// <param name="root">The isolated application directory.</param>
        /// <returns>A task representing complete offline retention and local-only reuse.</returns>
        private static async Task ValidateCompleteDownloadAsync(string root)
        {
            using var transport = new CoverTransport();
            using var client = new HttpClient(transport, false);
            MusicTrack lazy;
            string audioPath;
            string coverPath;
            using (var workspace = CreateWorkspace(root, client))
            {
                lazy = await SynchronizeAsync(workspace);
                transport.Requests.Clear();
                audioPath = await workspace.ResolveAudioAsync(lazy, true);
                var stored = (await workspace.SearchAsync(null)).Single();
                coverPath = stored.Cover?.LocalPath ?? throw new InvalidOperationException("An explicit download must also remember its cover.");
                TestAssert.True(stored.Cover!.Sha256 is not null && File.Exists(coverPath), "The independent cover is persisted alongside the audio.");
                TestAssert.True(transport.Requests.Count(path => path.EndsWith("/image", StringComparison.Ordinal)) == 1
                    && transport.Requests.Count(path => path.EndsWith("/track", StringComparison.Ordinal)) == 1,
                    "A fresh explicit offline download transfers each resource exactly once.");
                await AssertPinnedAsync(root, client, stored.Audio, stored.Cover);
                await workspace.ClearCacheAsync();
                TestAssert.True(File.Exists(audioPath) && File.Exists(coverPath), "Cache cleanup must retain both explicitly downloaded objects.");
            }

            // Preserve only the original lazy descriptors to also exercise revision-scoped URI aliases after restart.
            await new SqliteLibraryRepository(Path.Combine(root, "library.db")).UpsertTracksAsync([lazy]);
            transport.Requests.Clear();
            transport.RejectAll = true;
            using (var reopened = CreateWorkspace(root, client))
            {
                await reopened.LoadSettingsAsync();
                TestAssert.True(reopened.IsOnline, "The rejection boundary must exercise an online session with no discovered server.");
                TestAssert.Equal(audioPath, await reopened.ResolveAudioAsync(lazy, false), "Cached audio must precede source info and session discovery.");
                TestAssert.Equal(coverPath, await reopened.ResolveCoverAsync(lazy), "Cached artwork must precede source identity and account access.");
                TestAssert.Equal(0, transport.Requests.Count, "Completed local resources must not contact a rejecting API or media endpoint.");
                await reopened.UpdateOnlineEnabledAsync(false);
            }

            using var offline = CreateWorkspace(root, client);
            await offline.LoadSettingsAsync();
            TestAssert.Equal(audioPath, await offline.ResolveAudioAsync(lazy, false), "A retained track remains playable offline after another restart.");
            TestAssert.Equal(coverPath, await offline.ResolveCoverAsync(lazy), "A retained cover remains visible offline after another restart.");
            TestAssert.Equal(0, transport.Requests.Count, "Offline reuse never issues HTTP.");
        }

        /// <summary>Repairs artwork when an existing managed playback cache object is explicitly promoted to an offline download.</summary>
        /// <param name="root">The isolated application directory.</param>
        /// <returns>A task representing pin publication, failed artwork recovery and retry.</returns>
        private static async Task ValidateExistingAudioRepairAsync(string root)
        {
            using var transport = new CoverTransport();
            using var client = new HttpClient(transport, false);
            using var workspace = CreateWorkspace(root, client);
            var lazy = await SynchronizeAsync(workspace);
            var audioPath = await workspace.ResolveAudioAsync(lazy, false);
            TestAssert.True(transport.Requests.All(path => !path.EndsWith("/image", StringComparison.Ordinal)),
                "Ordinary playback resolution must not wait for or transfer optional cover art.");
            var cached = (await workspace.SearchAsync(null)).Single();
            using (var policy = new NetworkAccessPolicy())
            using (var cache = new MediaCache(Path.Combine(root, "Cache"), client, policy))
            {
                TestAssert.True(!await cache.IsKeptOfflineAsync(cached.Audio), "A playback cache object must not become pinned implicitly.");
            }

            transport.Requests.Clear();
            transport.RejectCover = true;
            TestAssert.Equal(audioPath, await workspace.ResolveAudioAsync(cached, true), "A failed independent cover cannot invalidate already available audio.");
            TestAssert.True(transport.Requests.SequenceEqual(["/api/maichart/song/image"]),
                "Promoting an existing managed local path must pin it and retry artwork without reopening info or audio.");
            await AssertPinnedAsync(root, client, cached.Audio);
            transport.RejectCover = false;
            transport.Requests.Clear();
            var coverPath = await workspace.ResolveCoverAsync(cached);
            TestAssert.True(coverPath is not null && transport.Requests.SequenceEqual(["/api/maichart/song/image"]),
                "The failed image remains independently recoverable on the next cover read.");
            var repaired = (await workspace.SearchAsync(null)).Single();
            await AssertPinnedAsync(root, client, repaired.Audio, repaired.Cover!);
            await workspace.ClearCacheAsync();
            TestAssert.True(File.Exists(audioPath) && File.Exists(coverPath), "A recovered cover inherits the explicit audio retention decision.");
        }

        /// <summary>Preserves caller cancellation while artwork is being fetched after the audio has completed.</summary>
        /// <param name="root">The isolated application directory.</param>
        /// <returns>A task representing canceled optional artwork and retained usable audio.</returns>
        private static async Task ValidateCoverCancellationAsync(string root)
        {
            using var transport = new CoverTransport();
            using var client = new HttpClient(transport, false);
            using var workspace = CreateWorkspace(root, client);
            var track = await SynchronizeAsync(workspace);
            var audioPath = await workspace.ResolveAudioAsync(track, false);
            transport.BlockCover = true;
            using var cancellation = new CancellationTokenSource();
            var downloading = workspace.ResolveAudioAsync(track, true, cancellation.Token);
            await transport.CoverEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => downloading);
            TestAssert.True(File.Exists(audioPath), "Canceling optional artwork cannot remove already completed audio.");
            transport.RejectAll = true;
            TestAssert.Equal(audioPath, await workspace.ResolveAudioAsync(track, false), "Canceled artwork leaves local audio immediately usable.");
        }

        /// <summary>Checks that cache-only queries retain address and revision boundaries without silently performing HTTP.</summary>
        /// <param name="root">The isolated cache directory.</param>
        /// <returns>A task representing local-only alias, pin and missing-resource behavior.</returns>
        private static async Task ValidateLocalOnlyIdentityAsync(string root)
        {
            using var transport = new CoverTransport();
            using var client = new HttpClient(transport, false);
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var cache = new MediaCache(root, client, policy);
            var resource = new MediaResource { RemoteUri = new Uri("https://offline-covers.example.test/api/maichart/song/image"), CacheVersion = "revision-1" };
            var path = await cache.GetAsync(resource);
            transport.Requests.Clear();
            transport.RejectAll = true;
            TestAssert.Equal(path, await cache.TryGetCachedAsync(resource), "A local-only query preserves the complete revision-scoped URI alias.");
            TestAssert.True(await cache.TryGetCachedAsync(resource with { CacheVersion = "revision-2" }) is null,
                "Another revision cannot reuse an older cover alias.");
            TestAssert.True(await cache.TryGetCachedAsync(resource with { RemoteUri = new Uri("https://other-source.example.test/api/maichart/song/image") }) is null,
                "Another source origin cannot reuse an unrelated cover alias.");
            TestAssert.True(await cache.TryGetCachedAsync(new MediaResource()) is null, "An empty local-only descriptor is a miss rather than a network request.");
            TestAssert.True(!await cache.IsKeptOfflineAsync(resource), "A plain cache hit does not claim explicit retention.");
            TestAssert.Equal(path, await cache.TryGetCachedAsync(resource with { LocalPath = path }, true),
                "A managed LocalPath hit must publish the explicit offline pin instead of returning before retention.");
            TestAssert.True(await cache.IsKeptOfflineAsync(resource), "The managed local-path promotion persists its retention decision.");
            await cache.ClearCacheAsync();
            TestAssert.True(File.Exists(path) && transport.Requests.Count == 0, "Pinned local-only hits survive cleanup without contacting the source.");
        }

        /// <summary>Prevents equal unknown-content addresses from crossing configured session slots at the same server origin.</summary>
        /// <param name="root">The isolated workspace directory.</param>
        /// <returns>A task representing slot isolation and session-free alias recovery.</returns>
        private static async Task ValidateSameOriginSlotsAsync(string root)
        {
            using var transport = new CoverTransport();
            using var client = new HttpClient(transport, false);
            var first = new EndpointConfiguration { Id = "first-slot", Name = "First", BaseUri = new Uri("https://offline-covers.example.test/api/") };
            var second = first with { Id = "second-slot", Name = "Second" };
            MusicTrack firstTrack;
            MusicTrack secondTrack;
            string? firstPath;
            string? secondPath;
            using (var workspace = CreateWorkspace(root, client))
            {
                await workspace.LoadSettingsAsync();
                await workspace.SaveSettingsAsync(new AppSettings { OnlineEnabled = true, Endpoints = [first, second] });
                await workspace.SynchronizeAsync(first);
                await workspace.SynchronizeAsync(second);
                var tracks = await workspace.SearchAsync(null);
                firstTrack = tracks.Single(track => track.EndpointId == first.Id);
                secondTrack = tracks.Single(track => track.EndpointId == second.Id);
                TestAssert.True(firstTrack.Cover!.RemoteUri == secondTrack.Cover!.RemoteUri
                    && firstTrack.Cover.CacheVersion == secondTrack.Cover.CacheVersion
                    && firstTrack.Cover.Sha256 is null && secondTrack.Cover.Sha256 is null,
                    "The isolation fixture must begin with identical unknown-content URI and revision aliases in different configured slots.");
                transport.Requests.Clear();
                transport.ImageOverride = "first independently owned image"u8.ToArray();
                firstPath = await workspace.ResolveCoverAsync(firstTrack);
                transport.ImageOverride = "second independently owned image"u8.ToArray();
                secondPath = await workspace.ResolveCoverAsync(secondTrack);
                TestAssert.True(firstPath is not null && secondPath is not null && firstPath != secondPath
                    && transport.Requests.Count(path => path.EndsWith("/image", StringComparison.Ordinal)) == 2,
                    "A cached first-slot URI cannot bypass the second slot's own media transfer.");
                TestAssert.Equal("second independently owned image", await File.ReadAllTextAsync(secondPath!),
                    "The second configured slot must receive its own bytes rather than another slot's cached object.");
                var persisted = await workspace.SearchAsync(null);
                TestAssert.True(persisted.All(track => track.Cover!.CacheVersion == "revision-1"),
                    "Runtime alias namespaces cannot overwrite or repeatedly wrap the persisted source revision.");
                await TestAssert.ThrowsAsync<FileNotFoundException>(() => workspace.ResolveCoverAsync(secondTrack with
                {
                    Id = "changed-source",
                    SourceId = "another-server"
                }));
            }

            // Retain only lazy source descriptors so both hits must use their exact scoped aliases without discovery.
            await new SqliteLibraryRepository(Path.Combine(root, "library.db")).UpsertTracksAsync([firstTrack, secondTrack]);
            transport.Requests.Clear();
            transport.RejectAll = true;
            using var reopened = CreateWorkspace(root, client);
            await reopened.LoadSettingsAsync();
            TestAssert.Equal(firstPath, await reopened.ResolveCoverAsync(firstTrack), "The first slot can recover its own alias with a new undiscovered session.");
            TestAssert.Equal(secondPath, await reopened.ResolveCoverAsync(secondTrack), "The second slot can recover its different alias with a new undiscovered session.");
            TestAssert.Equal(0, transport.Requests.Count, "Scoped local aliases must remain usable without API or account access after restart.");
        }

        /// <summary>Asserts persisted retention through a fresh cache index reader.</summary>
        /// <param name="root">The isolated workspace directory.</param>
        /// <param name="client">The borrowed fake media client.</param>
        /// <param name="resources">The objects expected to survive cleanup.</param>
        /// <returns>A task representing all persisted-index assertions.</returns>
        private static async Task AssertPinnedAsync(string root, HttpClient client, params MediaResource[] resources)
        {
            using var policy = new NetworkAccessPolicy();
            using var cache = new MediaCache(Path.Combine(root, "Cache"), client, policy);
            foreach (var resource in resources)
            {
                TestAssert.True(await cache.IsKeptOfflineAsync(resource), "Explicit audio and artwork retention must be present in the on-disk index.");
            }
        }

        /// <summary>Creates an idle workspace whose credential store rejects all access.</summary>
        /// <param name="root">The isolated application directory.</param>
        /// <param name="client">The fake HTTP transport.</param>
        /// <returns>The independent workspace.</returns>
        private static MusicWorkspace CreateWorkspace(string root, HttpClient client)
        {
            return new MusicWorkspace(root, client, client, credentialStore: new UnusedCredentialStore());
        }

        /// <summary>Creates only a lazy catalog entry without downloading either media object.</summary>
        /// <param name="workspace">The isolated application workspace.</param>
        /// <returns>The synchronized lazy entry.</returns>
        private static async Task<MusicTrack> SynchronizeAsync(MusicWorkspace workspace)
        {
            await workspace.LoadSettingsAsync();
            var endpoint = new EndpointConfiguration { Id = "offline-cover-source", Name = "Generated cover source", BaseUri = new Uri("https://offline-covers.example.test/api/") };
            await workspace.SaveSettingsAsync(new AppSettings { OnlineEnabled = true, ActiveEndpointId = endpoint.Id, Endpoints = [endpoint] });
            await workspace.SynchronizeAsync(endpoint);
            return (await workspace.SearchAsync(null)).Single();
        }

        /// <summary>Serves synthetic source responses without cookies, accounts, real audio playback or external HTTP.</summary>
        private sealed class CoverTransport : HttpMessageHandler
        {
            /// <summary>Retains only nonsecret request paths.</summary>
            public ConcurrentQueue<string> Requests { get; } = new();
            /// <summary>Gets or sets whether every attempted source access is rejected.</summary>
            public bool RejectAll { get; set; }
            /// <summary>Gets or sets whether artwork is temporarily inaccessible.</summary>
            public bool RejectCover { get; set; }
            /// <summary>Gets or sets whether the next image request waits for caller cancellation.</summary>
            public bool BlockCover { get; set; }
            /// <summary>Gets or sets independently controlled image bytes for configured-slot isolation.</summary>
            public byte[]? ImageOverride { get; set; }
            /// <summary>Signals the actual blocked media request.</summary>
            public TaskCompletionSource CoverEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Contains fixed audio bytes; no native player is invoked.</summary>
            private static readonly byte[] AudioBytes = "isolated audio object"u8.ToArray();
            /// <summary>Contains a complete small PNG independent of local user artwork.</summary>
            private static readonly byte[] CoverBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jEuoAAAAASUVORK5CYII=");

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = request.RequestUri!.AbsolutePath;
                Requests.Enqueue(path);
                if (RejectAll || RejectCover && path.EndsWith("/image", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                if (BlockCover && path.EndsWith("/image", StringComparison.Ordinal))
                {
                    CoverEntered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                var metadata = new { id = "song", title = "Offline fixture", hash = "revision-1" };
                return path switch
                {
                    "/api/info" => Json(new { id = "offline-cover-server", name = "Offline fixture" }),
                    "/api/maichart/list" => Json(new[] { metadata }),
                    "/api/maichart/song/info" => Json(new
                    {
                        metadata,
                        resources = new
                        {
                            track = new { sha256 = Convert.ToHexStringLower(SHA256.HashData(AudioBytes)), length = AudioBytes.Length },
                            thumbnail = new { url = "maichart/song/image" }
                        }
                    }),
                    "/api/maichart/song/track" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AudioBytes) },
                    "/api/maichart/song/image" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageOverride ?? CoverBytes) },
                    _ => throw new InvalidOperationException("Unexpected isolated cover request: " + path)
                };
            }

            /// <summary>Serializes one source fixture response.</summary>
            /// <param name="value">The generated metadata.</param>
            /// <returns>The owned JSON response.</returns>
            private static HttpResponseMessage Json(object value)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
            }
        }

        /// <summary>Prevents tests from reading or modifying the operating-system credential store.</summary>
        private sealed class UnusedCredentialStore : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Offline-cover tests cannot access credentials.");
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Offline-cover tests cannot access credentials.");
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Offline-cover tests cannot access credentials.");
            }
        }
    }
}
