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
    /// <summary>Checks local-first composition and restart persistence.</summary>
    internal static class WorkspaceTests
    {
        /// <summary>Checks startup, explicit opt-in, playlist persistence and managed imports.</summary>
        /// <returns>A task representing all workspace checks.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-workspace-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var workspace = new MusicWorkspace(root))
                {
                    var settings = await workspace.LoadSettingsAsync();
                    TestAssert.True(!settings.OnlineEnabled && !workspace.IsOnline, "Startup must be offline.");
                    TestAssert.Equal(0, settings.Endpoints.Count, "No built-in sources may be supplied.");
                    TestAssert.Equal(0, (await workspace.SearchAsync(null)).Count, "New libraries must be empty.");
                    await workspace.SavePlaylistAsync(new MusicPlaylist { Id = "favorites", Name = "Favorites" });
                    await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.SynchronizeAsync(new EndpointConfiguration
                    {
                        Id = "unsaved",
                        Name = "Unsaved",
                        BaseUri = new Uri("https://example.invalid/api/")
                    }));
                }

                using (var reopened = new MusicWorkspace(root))
                {
                    await reopened.LoadSettingsAsync();
                    TestAssert.True(!reopened.IsOnline, "Reopening a local workspace must stay offline.");
                    TestAssert.Equal(1, (await reopened.GetPlaylistsAsync()).Count, "Playlists must survive workspace restart.");
                    await reopened.ClearCacheAsync();
                    TestAssert.Equal(1, (await reopened.GetPlaylistsAsync()).Count, "Cache cleanup must not remove playlists.");
                }

                await VerifyPermissionRacesAsync(Path.Combine(root, "permission-races"));
                await VerifyCachedVersionsAsync(Path.Combine(root, "cached-versions"));
                await VerifyNetworkCancellationAsync(Path.Combine(root, "cancellation"));
                await VerifyReadOnlyIndexAsync(Path.Combine(root, "read-only-index"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root))
                {
                    var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(root).StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(root).StartsWith("MajMusic-workspace-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Refusing to clean outside the workspace test directory.");
                    }

                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Checks read-only recursive indexing through persistence and restart.</summary>
        /// <param name="root">The isolated directory containing separate source and application data.</param>
        /// <returns>A task representing the read-only indexing regression.</returns>
        private static async Task VerifyReadOnlyIndexAsync(string root)
        {
            var source = Path.Combine(root, "source");
            var song = Path.Combine(source, "nested", "12345");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(song);
            var audio = Path.Combine(song, "track.mp3");
            var metadata = Path.Combine(song, "maidata.txt");
            await File.WriteAllBytesAsync(audio, "referenced audio fixture"u8.ToArray());
            await File.WriteAllTextAsync(metadata, "&title=Original text title\n&artist=Original artist\n");
            var originalAudio = await File.ReadAllBytesAsync(audio);
            var originalWriteTime = File.GetLastWriteTimeUtc(audio);
            using var handler = new WorkspaceTransport();
            using var client = new HttpClient(handler, false);
            string id;
            using (var workspace = new MusicWorkspace(data, client, client))
            {
                await workspace.LoadSettingsAsync();
                var result = await workspace.IndexDirectoryAsync(source);
                TestAssert.Equal(1, result.Tracks.Count, "The real workspace recursively indexes one nested song");
                id = result.Tracks[0].Id;
                TestAssert.Equal(audio, result.Tracks[0].Audio.LocalPath, "The workspace does not replace the original audio path");
                TestAssert.Equal(audio, await workspace.ResolveAudioAsync(result.Tracks[0], false), "Playback resolves the original reference directly");
                TestAssert.True(!Directory.Exists(Path.Combine(data, "Library")), "Read-only indexing creates no managed library copies");
                await File.WriteAllTextAsync(metadata, "&title=Updated text title\n&artist=Updated artist\n");
                var updated = await workspace.IndexDirectoryAsync(source);
                TestAssert.Equal(id, updated.Tracks[0].Id, "Metadata updates do not change the original-path identity");
                TestAssert.Equal(1, (await workspace.SearchAsync(null)).Count, "Repeated indexing updates instead of duplicating songs");
                TestAssert.Equal("Updated text title", (await workspace.SearchAsync(null))[0].Title, "Updated text titles replace the indexed metadata");
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => workspace.IndexDirectoryAsync(source, canceled.Token));
                TestAssert.Equal(1, (await workspace.SearchAsync(null)).Count, "Canceled scans do not change the persisted library");
                await workspace.ClearCacheAsync();
                var preservedAudio = await File.ReadAllBytesAsync(audio);
                TestAssert.True(File.Exists(audio) && originalAudio.SequenceEqual(preservedAudio), "Cache cleanup never changes referenced audio");
                TestAssert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(audio), "Indexing and resolving preserve the original audio timestamp");
                TestAssert.True(!workspace.IsOnline && handler.RequestCount == 0, "Read-only local indexing requires no source request");
            }

            using var reopened = new MusicWorkspace(data, client, client);
            var restored = (await reopened.SearchAsync(null)).Single();
            TestAssert.Equal(id, restored.Id, "Read-only references persist across restart");
            TestAssert.Equal(audio, restored.Audio.LocalPath, "Restart still references the user's original audio file");
        }

        /// <summary>Checks that delayed user saves and background identity persistence cannot replay old permission.</summary>
        /// <param name="root">The isolated workspace directory.</param>
        /// <returns>A task representing deterministic configuration race checks.</returns>
        private static async Task VerifyPermissionRacesAsync(string root)
        {
            using var handler = new WorkspaceTransport();
            using var apiClient = new HttpClient(handler, false);
            using var mediaClient = new HttpClient(handler, false);
            var disk = new JsonSettingsStore(Path.Combine(root, "settings.json"));
            var delayedStore = new PausableSettingsStore(disk);
            using var workspace = new MusicWorkspace(root, apiClient, mediaClient, delayedStore);
            await workspace.LoadSettingsAsync();
            TestAssert.Equal(0, handler.RequestCount, "Constructing and loading a workspace sends no requests");
            var settings = OnlineSettings();
            await workspace.SaveSettingsAsync(settings);
            TestAssert.True(workspace.IsOnline, "An explicit successful save may enable its configured source");
            TestAssert.Equal(0, handler.RequestCount, "Saving online preferences does not synchronize automatically");

            delayedStore.PauseNextSave();
            var pendingSave = workspace.SaveSettingsAsync(settings);
            await delayedStore.WaitUntilPausedAsync();
            workspace.SetOnlineEnabled(false);
            TestAssert.True(!workspace.IsOnline, "Offline permission applies without waiting for a disk write");
            delayedStore.ReleaseSave();
            await pendingSave;
            TestAssert.True(!workspace.IsOnline, "A delayed older save cannot re-enable the network");
            TestAssert.True(!(await disk.LoadAsync()).OnlineEnabled, "The delayed save corrects its stale permission on disk");
            await workspace.LoadSettingsAsync();
            TestAssert.True(!workspace.IsOnline, "Reloading settings cannot override an explicit runtime offline choice");

            workspace.SetOnlineEnabled(true);
            delayedStore.PauseNextSave();
            var synchronization = workspace.SynchronizeAsync(Endpoint());
            await delayedStore.WaitUntilPausedAsync();
            workspace.SetOnlineEnabled(false);
            delayedStore.ReleaseSave();
            TestAssert.Equal(1, await synchronization, "Synchronization may finish persisting already received metadata");
            var synchronizedSettings = await disk.LoadAsync();
            TestAssert.True(!workspace.IsOnline && !synchronizedSettings.OnlineEnabled,
                "Background server identity persistence cannot restore an older online choice");
            TestAssert.Equal("workspace-server", synchronizedSettings.Endpoints[0].ServerId,
                "Identity discovery remains persisted when offline wins the race");

            delayedStore.PauseNextSave();
            var olderSave = workspace.SaveSettingsAsync(settings with { OnlineEnabled = true });
            await delayedStore.WaitUntilPausedAsync();
            var newerSave = workspace.SaveSettingsAsync(settings with { OnlineEnabled = false });
            delayedStore.ReleaseSave();
            await Task.WhenAll(olderSave, newerSave);
            TestAssert.True(!workspace.IsOnline && !(await disk.LoadAsync()).OnlineEnabled,
                "A later queued explicit save retains the final permission decision");
        }

        /// <summary>Checks stale references, metadata-only resynchronization, explicit version changes and retained downloads.</summary>
        /// <param name="root">The isolated workspace directory.</param>
        /// <returns>A task representing versioned offline-resource checks.</returns>
        private static async Task VerifyCachedVersionsAsync(string root)
        {
            using var handler = new WorkspaceTransport { OmitDetailRevision = true };
            using var apiClient = new HttpClient(handler, false);
            using var mediaClient = new HttpClient(handler, false);
            MusicTrack stale;
            string downloaded;
            using (var workspace = new MusicWorkspace(root, apiClient, mediaClient))
            {
                await workspace.LoadSettingsAsync();
                await workspace.SaveSettingsAsync(OnlineSettings());
                await workspace.SynchronizeAsync(Endpoint());
                TestAssert.Equal(0, handler.MediaRequestCount, "A metadata synchronization downloads no media");
                stale = (await workspace.SearchAsync(null))[0];
                TestAssert.True(stale.Audio.Sha256 is null, "Initial list entries are lazy metadata descriptors");
                downloaded = await workspace.ResolveAudioAsync(stale, true);
                var cover = await workspace.ResolveCoverAsync(stale);
                TestAssert.True(File.Exists(downloaded) && File.Exists(cover), "Explicit resolution publishes audio and cover files");
                workspace.SetOnlineEnabled(false);
                var requests = handler.RequestCount;
                TestAssert.Equal(downloaded, await workspace.ResolveAudioAsync(stale, false),
                    "A pre-download view object resolves the updated persistent descriptor offline");
                TestAssert.Equal(cover, await workspace.ResolveCoverAsync(stale), "A stale cover reference remains usable offline");
                TestAssert.Equal(requests, handler.RequestCount, "Offline resolution makes no requests");

                workspace.SetOnlineEnabled(true);
                var mediaRequests = handler.MediaRequestCount;
                await workspace.SynchronizeAsync(Endpoint());
                TestAssert.Equal(mediaRequests, handler.MediaRequestCount, "Resynchronization remains metadata-only");
                var refreshed = (await workspace.SearchAsync(null))[0];
                TestAssert.Equal(stale.Revision, refreshed.Revision, "Unchanged revisions preserve source identity");
                TestAssert.True(refreshed.Audio.Sha256 is not null && refreshed.Audio.LocalPath == downloaded,
                    "Lazy synchronization preserves resolved audio hashes and local paths");
                TestAssert.True(refreshed.Cover?.Sha256 is not null && refreshed.Cover.LocalPath == cover,
                    "Lazy synchronization preserves resolved cover hashes and local paths");
                workspace.SetOnlineEnabled(false);
                TestAssert.Equal(downloaded, await workspace.ResolveAudioAsync(stale, false),
                    "A synchronized existing download remains reachable offline");

                workspace.SetOnlineEnabled(true);
                handler.Revision = "revision-two";
                await workspace.SynchronizeAsync(Endpoint());
                var versions = await workspace.SearchAsync(null);
                TestAssert.Equal(2, versions.Count, "A changed downloaded revision creates one explicit offline copy");
                var current = versions.Single(track => track.RemoteId is not null);
                var retained = versions.Single(track => track.SourceId == "local/archive");
                TestAssert.True(retained.Title.Contains("离线副本", StringComparison.Ordinal), "Retained versions are explicitly labeled");
                TestAssert.True(retained.Audio.RemoteUri is null, "An offline copy cannot silently fetch a newer remote file");
                TestAssert.True(current.Audio.Sha256 is null && current.Audio.LocalPath is null,
                    "A changed revision does not inherit the old audio identity");
                workspace.SetOnlineEnabled(false);
                TestAssert.Equal(downloaded, await workspace.ResolveAudioAsync(stale, false),
                    "A stale old-revision reference resolves its retained version offline");
                TestAssert.Equal(downloaded, await workspace.ResolveAudioAsync(retained, false), "The explicit offline copy is playable as a local resource");
                await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.ResolveAudioAsync(current, false));
                await workspace.ClearCacheAsync();
                TestAssert.True(File.Exists(downloaded), "Clearing cache preserves the pinned previous version");
                await workspace.SaveSettingsAsync((await workspace.LoadSettingsAsync()) with { OnlineEnabled = false });
            }

            var beforeRestartRequests = handler.RequestCount;
            using var reopened = new MusicWorkspace(root, apiClient, mediaClient);
            await reopened.LoadSettingsAsync();
            TestAssert.Equal(downloaded, await reopened.ResolveAudioAsync(stale, false), "Retained old-revision references survive restart");
            TestAssert.Equal(beforeRestartRequests, handler.RequestCount, "Restarting and reading a retained version stays offline");
        }

        /// <summary>Checks cancellation through the real workspace composition rather than only an isolated transport.</summary>
        /// <param name="root">The isolated workspace directory.</param>
        /// <returns>A task representing cancellation assertions.</returns>
        private static async Task VerifyNetworkCancellationAsync(string root)
        {
            using var handler = new WorkspaceTransport { BlockLibrary = true };
            using var apiClient = new HttpClient(handler, false);
            using var mediaClient = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, apiClient, mediaClient);
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(OnlineSettings());
            var synchronization = workspace.SynchronizeAsync(Endpoint());
            await handler.LibraryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            workspace.SetOnlineEnabled(false);
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => synchronization);
            TestAssert.True(!workspace.IsOnline, "Canceling a workspace request keeps the network disabled");
            TestAssert.Equal(0, handler.MediaRequestCount, "Canceled synchronization requests no media");
            await workspace.LoadSettingsAsync();
            TestAssert.True(!workspace.IsOnline, "A local settings reload does not undo cancellation");
        }

        /// <summary>Creates a deliberately configured source for in-memory transport tests.</summary>
        /// <returns>The test endpoint.</returns>
        private static EndpointConfiguration Endpoint()
        {
            return new EndpointConfiguration
            {
                Id = "workspace-source",
                Name = "Workspace source",
                BaseUri = new Uri("https://workspace.example.test/api/")
            };
        }

        /// <summary>Creates an explicit opt-in snapshot without embedding a real endpoint.</summary>
        /// <returns>The test preferences.</returns>
        private static AppSettings OnlineSettings()
        {
            return new AppSettings { OnlineEnabled = true, ActiveEndpointId = "workspace-source", Endpoints = [Endpoint()] };
        }

        /// <summary>Wraps a real store with a deterministic pause at the next write boundary.</summary>
        private sealed class PausableSettingsStore : ISettingsStore
        {
            /// <summary>Persists actual files for restart assertions.</summary>
            private readonly ISettingsStore _inner;
            /// <summary>Signals the write selected for pausing.</summary>
            private TaskCompletionSource? _started;
            /// <summary>Releases the selected write.</summary>
            private TaskCompletionSource? _release;
            /// <summary>Records whether the next write should pause.</summary>
            private bool _pauseNext;

            /// <summary>Wraps the persistence adapter.</summary>
            /// <param name="inner">The actual settings adapter.</param>
            public PausableSettingsStore(ISettingsStore inner)
            {
                _inner = inner;
            }

            /// <summary>Selects the next write for an explicit deterministic pause.</summary>
            public void PauseNextSave()
            {
                _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pauseNext = true;
            }

            /// <summary>Waits until the selected write reaches its boundary.</summary>
            /// <returns>A bounded task representing the reached boundary.</returns>
            public Task WaitUntilPausedAsync()
            {
                return _started!.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            /// <summary>Allows the selected write to continue.</summary>
            public void ReleaseSave()
            {
                _release!.TrySetResult();
            }

            /// <inheritdoc />
            public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            {
                return _inner.LoadAsync(cancellationToken);
            }

            /// <inheritdoc />
            public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                if (_pauseNext)
                {
                    _pauseNext = false;
                    _started!.TrySetResult();
                    await _release!.Task.WaitAsync(cancellationToken);
                }

                await _inner.SaveAsync(settings, cancellationToken);
            }
        }

        /// <summary>Serves API metadata and byte fixtures entirely in memory.</summary>
        private sealed class WorkspaceTransport : HttpMessageHandler
        {
            /// <summary>Gets or sets the source revision returned by metadata requests.</summary>
            public string Revision { get; set; } = "revision-one";
            /// <summary>Gets or sets whether the library request waits for cancellation.</summary>
            public bool BlockLibrary { get; init; }
            /// <summary>Gets whether single-item information omits its source hash.</summary>
            public bool OmitDetailRevision { get; init; }
            /// <summary>Signals entry to the optionally blocked library request.</summary>
            public TaskCompletionSource LibraryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets the total number of fixture requests.</summary>
            public int RequestCount { get; private set; }
            /// <summary>Gets the number of requested audio or cover files.</summary>
            public int MediaRequestCount { get; private set; }

            /// <summary>Handles fixture requests without using a socket.</summary>
            /// <param name="request">The outgoing request.</param>
            /// <param name="cancellationToken">The composed network lifetime.</param>
            /// <returns>The fixture response.</returns>
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/info")
                {
                    return Json(new { id = "workspace-server", name = "Workspace server" });
                }

                if (path == "/api/maichart/list")
                {
                    LibraryStarted.TrySetResult();
                    if (BlockLibrary)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }

                    return Json(new[] { new { id = "music-1", title = "Fixture music", artist = "Fixture artist", hash = Revision } });
                }

                if (path == "/api/maichart/music-1/info")
                {
                    var audio = AudioBytes(Revision);
                    byte[] cover = [10, 20, 30];
                    return Json(new
                    {
                        metadata = new { id = "music-1", title = "Fixture music", artist = "Fixture artist", hash = OmitDetailRevision ? null : Revision },
                        resources = new
                        {
                            track = new { url = $"https://media.example.test/audio/{Revision}.mp3", sha256 = Convert.ToHexStringLower(SHA256.HashData(audio)), length = audio.Length },
                            thumbnail = new { url = $"https://media.example.test/cover/{Revision}.png", sha256 = Convert.ToHexStringLower(SHA256.HashData(cover)), length = cover.Length }
                        }
                    });
                }

                if (path.StartsWith("/audio/", StringComparison.Ordinal))
                {
                    MediaRequestCount++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(AudioBytes(Path.GetFileNameWithoutExtension(path)))
                    };
                }

                if (path.StartsWith("/cover/", StringComparison.Ordinal))
                {
                    MediaRequestCount++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([10, 20, 30]) };
                }

                throw new InvalidOperationException("Unexpected workspace fixture request: " + path);
            }

            /// <summary>Returns distinguishable byte fixtures for source versions.</summary>
            /// <param name="revision">The requested revision.</param>
            /// <returns>The audio file bytes, without invoking a media decoder.</returns>
            private static byte[] AudioBytes(string revision)
            {
                return Encoding.UTF8.GetBytes("audio-fixture:" + revision);
            }

            /// <summary>Serializes one fixture response.</summary>
            /// <param name="value">The response payload.</param>
            /// <returns>The in-memory JSON response.</returns>
            private static HttpResponseMessage Json(object value)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
                };
            }
        }
    }
}
