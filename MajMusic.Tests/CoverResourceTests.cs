#nullable enable

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Tests
{
    /// <summary>Checks that cover resources resolve independently of song details and audio availability.</summary>
    internal static class CoverResourceTests
    {
        /// <summary>Checks direct image routes, offline reuse, revision aliases and known integrity metadata.</summary>
        /// <returns>A task representing all isolated cover-resource regressions.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-cover-resources-" + Guid.NewGuid().ToString("N"));
            try
            {
                await ValidateCoverOnlyAsync(Path.Combine(root, "cover-only"));
                await ValidateOfflineAliasesAsync(Path.Combine(root, "aliases"));
                await ValidateIntegrityAsync(Path.Combine(root, "integrity"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-cover-resources-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the cover-resource test directory.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Loads a lazy image without consulting an info response that has no audio descriptor.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing direct image and offline-local checks.</returns>
        private static async Task ValidateCoverOnlyAsync(string root)
        {
            using var transport = new CoverTransport();
            using var client = new HttpClient(transport, false);
            MusicTrack track;
            string? path;
            using (var workspace = CreateWorkspace(root, client))
            {
                track = await SynchronizeAsync(workspace);
                transport.Requests.Clear();
                path = await workspace.ResolveCoverAsync(track);
                TestAssert.True(path is not null && File.Exists(path), "A list entry's image route must work without audio details.");
                var downloaded = await File.ReadAllBytesAsync(path!);
                TestAssert.True(transport.CoverBytes.SequenceEqual(downloaded), "The image route must return its own bytes.");
                AssertOnlyImages(transport, 1);
                var stored = (await workspace.SearchAsync(null)).Single();
                TestAssert.Equal(track.Audio, stored.Audio, "Reading a cover cannot resolve or replace the audio resource.");
                TestAssert.True(stored.Cover?.Sha256 is not null && stored.Cover.LocalPath == path, "Completed images retain their digest and local path.");
                TestAssert.Equal(path, await workspace.ResolveCoverAsync(track), "A stale view object reuses the persisted image.");
                AssertOnlyImages(transport, 1);
            }

            transport.Requests.Clear();
            using var reopened = CreateWorkspace(root, client);
            await reopened.LoadSettingsAsync();
            TestAssert.True(!reopened.IsOnline, "A reopened workspace begins offline.");
            TestAssert.Equal(path, await reopened.ResolveCoverAsync(track), "A downloaded image remains available offline after restart.");
            TestAssert.True(await reopened.ResolveCoverAsync(track with
            {
                Id = "uncached-cover",
                Cover = new MediaResource { RemoteUri = new Uri("https://covers.example.test/api/maichart/missing/image"), CacheVersion = "missing" }
            }) is null, "An offline image miss leaves an empty cover.");
            TestAssert.True(await reopened.ResolveCoverAsync(track with { Id = "no-cover", Cover = null }) is null,
                "A missing cover descriptor leaves an empty cover.");
            var local = Path.Combine(root, "local-cover.png");
            await File.WriteAllBytesAsync(local, transport.CoverBytes);
            TestAssert.Equal(local, await reopened.ResolveCoverAsync(track with
            {
                Id = "local-cover",
                RemoteId = null,
                Cover = new MediaResource { LocalPath = local }
            }), "An offline local cover is referenced directly.");
            TestAssert.Equal(0, transport.Requests.Count, "Offline covers, misses and local files must never issue a request.");
        }

        /// <summary>Reuses an image-cache alias offline while keeping source revisions separate.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing address-alias and revision checks.</returns>
        private static async Task ValidateOfflineAliasesAsync(string root)
        {
            using var transport = new CoverTransport();
            using var client = new HttpClient(transport, false);
            MusicTrack lazy;
            string? originalPath;
            using (var workspace = CreateWorkspace(root, client))
            {
                lazy = await SynchronizeAsync(workspace);
                originalPath = await workspace.ResolveCoverAsync(lazy);
            }

            var library = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
            await library.UpsertTracksAsync([lazy]);
            transport.Requests.Clear();
            using (var offline = CreateWorkspace(root, client))
            {
                await offline.LoadSettingsAsync();
                TestAssert.Equal(originalPath, await offline.ResolveCoverAsync(lazy), "An existing revision-scoped URI alias works offline without a stored digest.");
                TestAssert.Equal(0, transport.Requests.Count, "An offline alias lookup never contacts the source.");
            }

            var newer = lazy with { Revision = "revision-2", Cover = lazy.Cover! with { CacheVersion = "revision-2" } };
            await library.UpsertTracksAsync([newer]);
            using var current = CreateWorkspace(root, client);
            await current.LoadSettingsAsync();
            TestAssert.True(await current.ResolveCoverAsync(newer) is null, "A new source revision cannot reuse an older image alias.");
            TestAssert.Equal(0, transport.Requests.Count, "An uncached offline revision remains a local miss.");
            transport.Revision = "revision-2";
            transport.CoverBytes = "second image fixture"u8.ToArray();
            await SynchronizeAsync(current);
            transport.Requests.Clear();
            var newerPath = await current.ResolveCoverAsync(newer);
            TestAssert.True(newerPath is not null && newerPath != originalPath, "Changed image bytes are published as a distinct object.");
            var newerBytes = await File.ReadAllBytesAsync(newerPath!);
            TestAssert.True(transport.CoverBytes.SequenceEqual(newerBytes), "The new revision reads the current image route.");
            AssertOnlyImages(transport, 1);
        }

        /// <summary>Retains hash and length validation when a cover descriptor is already known.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing integrity rejection and retry checks.</returns>
        private static async Task ValidateIntegrityAsync(string root)
        {
            using var transport = new CoverTransport();
            using var client = new HttpClient(transport, false);
            using var workspace = CreateWorkspace(root, client);
            var lazy = await SynchronizeAsync(workspace);
            var expected = transport.CoverBytes;
            var known = lazy with
            {
                Cover = lazy.Cover! with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(expected)), Length = expected.Length }
            };
            var library = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
            await library.UpsertTracksAsync([known]);
            transport.Requests.Clear();
            transport.CoverBytes = expected.Select(value => (byte)(value ^ 1)).ToArray();
            await TestAssert.ThrowsAsync<InvalidDataException>(() => workspace.ResolveCoverAsync(lazy));
            TestAssert.True((await workspace.SearchAsync(null)).Single().Cover!.LocalPath is null, "A failed digest cannot publish an image path.");
            transport.CoverBytes = expected;
            var path = await workspace.ResolveCoverAsync(lazy);
            TestAssert.True(path is not null && File.Exists(path), "A rejected image remains retryable with valid bytes.");
            TestAssert.Equal(known.Cover!.Sha256, (await workspace.SearchAsync(null)).Single().Cover!.Sha256, "The source's known cover digest remains authoritative.");
            await library.UpsertTracksAsync([known with { Cover = known.Cover with { Length = expected.Length + 1 } }]);
            await TestAssert.ThrowsAsync<InvalidDataException>(() => workspace.ResolveCoverAsync(lazy));
            AssertOnlyImages(transport, 3);
        }

        /// <summary>Creates a workspace that cannot access system credentials.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <param name="client">The in-memory HTTP transport.</param>
        /// <returns>An idle workspace.</returns>
        private static MusicWorkspace CreateWorkspace(string root, HttpClient client)
        {
            return new MusicWorkspace(root, client, client, credentialStore: new UnusedCredentialStore());
        }

        /// <summary>Primes source identity and lazy list metadata before observing image requests.</summary>
        /// <param name="workspace">The isolated workspace.</param>
        /// <returns>The lazy list entry.</returns>
        private static async Task<MusicTrack> SynchronizeAsync(MusicWorkspace workspace)
        {
            await workspace.LoadSettingsAsync();
            var endpoint = new EndpointConfiguration
            {
                Id = "cover-source",
                Name = "Cover fixture",
                BaseUri = new Uri("https://covers.example.test/api/")
            };
            await workspace.SaveSettingsAsync(new AppSettings { OnlineEnabled = true, ActiveEndpointId = endpoint.Id, Endpoints = [endpoint] });
            await workspace.SynchronizeAsync(endpoint);
            return (await workspace.SearchAsync(null)).Single();
        }

        /// <summary>Rejects any image-resolution request for song info, audio or unrelated source resources.</summary>
        /// <param name="transport">The observed in-memory transport.</param>
        /// <param name="expectedCount">The expected number of image transfers.</param>
        private static void AssertOnlyImages(CoverTransport transport, int expectedCount)
        {
            TestAssert.Equal(expectedCount, transport.Requests.Count, "Cover reads issue only the expected image transfers.");
            TestAssert.True(transport.Requests.All(path => path == "/api/maichart/cover-only/image"),
                "Cover reads cannot request /info, /track or /audio resources.");
        }

        /// <summary>Serves a chart whose optional info response contains a cover but no audio resource.</summary>
        private sealed class CoverTransport : HttpMessageHandler
        {
            /// <summary>Gets paths observed by the in-memory transport.</summary>
            public List<string> Requests { get; } = new();
            /// <summary>Gets or sets the current list revision.</summary>
            public string Revision { get; set; } = "revision-1";
            /// <summary>Gets or sets the image returned by the media route.</summary>
            public byte[] CoverBytes { get; set; } = "first image fixture"u8.ToArray();

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = request.RequestUri!.AbsolutePath;
                Requests.Add(path);
                var metadata = new { id = "cover-only", title = "Cover fixture", hash = Revision };
                var response = path switch
                {
                    "/api/info" => Json(new { id = "cover-server", name = "Cover fixture" }),
                    "/api/maichart/list" => Json(new[] { metadata }),
                    "/api/maichart/cover-only/info" => Json(new { metadata, resources = new { thumbnail = new { url = "maichart/cover-only/image" } } }),
                    "/api/maichart/cover-only/image" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CoverBytes) },
                    _ => throw new InvalidOperationException("Unexpected cover-resource fixture request: " + path)
                };
                return Task.FromResult(response);
            }

            /// <summary>Builds an in-memory JSON response.</summary>
            /// <param name="value">The response payload.</param>
            /// <returns>The serialized response.</returns>
            private static HttpResponseMessage Json(object value)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
                };
            }
        }

        /// <summary>Fails if cover resolution attempts to read or modify any credential.</summary>
        private sealed class UnusedCredentialStore : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Cover tests cannot read credentials.");
            }

            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Cover tests cannot write credentials.");
            }

            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Cover tests cannot delete credentials.");
            }
        }
    }
}
