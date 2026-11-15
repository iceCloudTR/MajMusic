#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Network;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.Converters;
using MajMusic.Presentation.ViewModels;
using Microsoft.Data.Sqlite;

namespace MajMusic.Tests
{
    /// <summary>Checks exact cloud-mirror identity, complete local membership and favorite edit routing.</summary>
    internal static class PlaylistMirrorTests
    {
        /// <summary>Runs real isolated persistence and synthetic source operations without touching user accounts or files.</summary>
        /// <returns>A task representing projection, edit and restart regressions.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-playlist-mirrors-" + Guid.NewGuid().ToString("N")));
            try
            {
                await ValidateLogicalFavoriteAsync(Path.Combine(root, "logical"));
                await ValidatePendingRemovalRestartAsync(Path.Combine(root, "restart"));
                await ValidateAliasManagementAsync(Path.Combine(root, "management"));
                await ValidateScopedSnapshotReadAsync(Path.Combine(root, "read-order"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-playlist-mirrors-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated mirror fixture.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Keeps one source-labeled favorite while editing its complete local backing and preserving unrelated same-name lists.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing exact identity and command routing.</returns>
        private static async Task ValidateLogicalFavoriteAsync(string root)
        {
            using var transport = new CloudCollectionSyncTests.FixtureTransport();
            using var harness = new CloudCollectionSyncTests.SyncHarness(root, transport);
            await SeedFavoriteAsync(harness, root);
            var source = await harness.EndpointAsync("first");
            var cloud = (await harness.Workspace.GetCloudPlaylistsAsync(source)).Single();
            TestAssert.True(cloud.LocalMirrorId == FavoritesId && !cloud.LocalMirrorHasPendingChanges,
                "Only a successfully applied current-account binding may identify the cloud row as the local favorite's mirror.");
            await harness.Workspace.SetCloudCollectionUsageAsync(source, "Alice", false);
            TestAssert.True((await harness.Workspace.GetCloudPlaylistsAsync(await harness.EndpointAsync("first"))).Single().LocalMirrorId is null,
                "A disabled account must not carry its previous cloud-collection permission into editable mirror routing.");
            await harness.Workspace.SetCloudCollectionUsageAsync(await harness.EndpointAsync("first"), "Alice", true);

            transport.AddForeign("a.example.test", "Alice", "我喜欢");
            var unrelated = transport.Owned("a.example.test", "Alice").Single(item => item.Description == "User collection");
            using (var policy = new NetworkAccessPolicy())
            using (var client = new MajNetClient(await harness.EndpointAsync("first"), policy, harness.Client))
            {
                policy.SetEnabled(true);
                await client.LoginAsync("Alice", "Generated fixture password");
                await client.SetCollectionFavoriteAsync(unrelated.Id, true);
            }

            var workspace = new ProjectionWorkspace(harness.Workspace);
            await using var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await view.InitializeAsync();
            await ExecuteAsync(view, view.RefreshCloudPlaylistsCommand);
            var visible = AssertSingleMirror(view);
            TestAssert.True(visible.IsCloud && visible.SourceName?.StartsWith("MajNet", StringComparison.Ordinal) == true
                && MusicSelectLabels.Playlist(visible)?.Contains("MajNet", StringComparison.Ordinal) == true
                && visible.TrackIds.SequenceEqual(["local", "first-track"]),
                "The retained MajNet row must preserve complete local and online membership and expose the actual source label.");
            TestAssert.True(view.Playlists.Any(item => item.RemoteId == unrelated.Id && item.LocalMirrorId is null)
                && !view.CanEditPlaylistForMenu(view.Playlists.Single(item => item.RemoteId == unrelated.Id)),
                "An unrelated cloud collection with the identical name must remain a separate read-only row.");
            await ExecuteAsync(view, view.OpenFavoritesCommand);
            TestAssert.True(view.SelectedHomePlaylist?.Id == visible.Id && view.Tracks.Select(item => item.Id).SequenceEqual(visible.TrackIds),
                "The favorite shortcut must open the source presentation instead of the hidden backing identity.");

            var tracks = (await harness.Workspace.SearchAsync(null)).ToDictionary(track => track.Id, StringComparer.Ordinal);
            view.SelectedTrack = tracks["local"];
            var writes = transport.Writes.Count;
            await view.ToggleTrackFavoriteFromMenuAsync(tracks["first-track"]);
            TestAssert.True(view.SelectedTrack?.Id == "local" && !view.IsTrackFavoriteForMenu(tracks["first-track"])
                && AssertSingleMirror(view).TrackIds.SequenceEqual(["local"]),
                "A captured favorite removal must edit the local backing without moving selection or resurrecting the older cloud song.");
            visible = AssertSingleMirror(view);
            await view.AddTrackToPlaylistFromMenuAsync(tracks["first-track"], visible);
            TestAssert.True(view.IsTrackFavoriteForMenu(tracks["first-track"]), "An explicit add must override a pending mirror removal.");
            await view.RemoveTrackFromPlaylistFromMenuAsync(tracks["first-track"], AssertSingleMirror(view));
            TestAssert.True(!view.IsTrackFavoriteForMenu(tracks["first-track"]) && view.SelectedTrack?.Id == "local",
                "Remove and favorite actions must share the same logical collection while preserving the inspector.");
            TestAssert.True((await harness.Workspace.GetPlaylistsAsync()).Single(item => item.Id == FavoritesId).TrackIds.SequenceEqual(["local"])
                && transport.Writes.Count == writes,
                "Local mirror edits must preserve the actual backing data without issuing direct remote mutations from the projection layer.");

            await harness.Workspace.SynchronizeCloudCollectionsAsync();
            var remote = transport.Owned("a.example.test", "Alice").Single(item => item.Description?.StartsWith("MajMusic · ", StringComparison.Ordinal) == true);
            transport.Replace("a.example.test", remote with { Items = [CloudCollectionSyncTests.FixtureTransport.SecondChart] });
            await ExecuteAsync(view, view.RefreshCloudPlaylistsCommand);
            visible = AssertSingleMirror(view);
            TestAssert.True(visible.TrackIds.SequenceEqual(["local", "second-track"])
                && (await harness.Workspace.GetPlaylistsAsync()).Single(item => item.Id == FavoritesId).TrackIds.SequenceEqual(["local"]),
                "A clean confirmed mirror may expose an additional remote member without mutating local data merely to render the list.");
            await view.AddTrackToPlaylistFromMenuAsync(tracks["first-track"], visible);
            TestAssert.True((await harness.Workspace.GetPlaylistsAsync()).Single(item => item.Id == FavoritesId).TrackIds
                .SequenceEqual(["local", "second-track", "first-track"]),
                "Editing the unified collection must retain its other local and remote members.");
            view.OnlineEnabled = false;
            await view.RemoveTrackFromPlaylistFromMenuAsync(tracks["first-track"], AssertSingleMirror(view));
            TestAssert.True(AssertSingleMirror(view).TrackIds.SequenceEqual(["local", "second-track"])
                && view.Tracks.Select(track => track.Id).SequenceEqual(["local", "second-track"]),
                "The same source row and complete mixed membership must remain usable for offline local edits.");
        }

        /// <summary>Rejects a stale cloud song after local removal survives a process restart before its upload completes.</summary>
        /// <param name="root">The isolated application-data directory retained between fixture instances.</param>
        /// <returns>A task representing durable dirty detection and nonresurrection.</returns>
        private static async Task ValidatePendingRemovalRestartAsync(string root)
        {
            using var transport = new CloudCollectionSyncTests.FixtureTransport();
            using (var harness = new CloudCollectionSyncTests.SyncHarness(root, transport))
            {
                await SeedFavoriteAsync(harness, root);
                await using var view = new MainViewModel(new ProjectionWorkspace(harness.Workspace), new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
                await view.InitializeAsync();
                await ExecuteAsync(view, view.RefreshCloudPlaylistsCommand);
                var track = (await harness.Workspace.SearchAsync(null)).Single(item => item.Id == "first-track");
                await view.ToggleTrackFavoriteFromMenuAsync(track);
                TestAssert.True(transport.Owned("a.example.test", "Alice").Single().Items.Count == 1,
                    "The restart boundary must retain the original remote member while its local removal is already persisted.");
            }

            using (var reopened = new CloudCollectionSyncTests.SyncHarness(root, transport))
            {
                await reopened.InitializeAsync();
                await reopened.EnableAccountAsync("first", "Alice");
                var cloud = (await reopened.Workspace.GetCloudPlaylistsAsync(await reopened.EndpointAsync("first"))).Single();
                TestAssert.True(cloud.LocalMirrorId == FavoritesId && cloud.LocalMirrorHasPendingChanges && cloud.TrackIds.Contains("first-track"),
                    "After restart, the applied fingerprint must identify the cloud response as older than the persisted local projection.");
                await using var view = new MainViewModel(new ProjectionWorkspace(reopened.Workspace), new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
                await view.InitializeAsync();
                await ExecuteAsync(view, view.RefreshCloudPlaylistsCommand);
                TestAssert.True(AssertSingleMirror(view).TrackIds.SequenceEqual(["local"]),
                    "Reopening an old cloud snapshot cannot reintroduce the removed song into the logical favorite.");
                var second = (await reopened.Workspace.SearchAsync(null)).Single(item => item.Id == "second-track");
                await view.AddTrackToPlaylistFromMenuAsync(second, AssertSingleMirror(view));
                TestAssert.True((await reopened.Workspace.GetPlaylistsAsync()).Single(item => item.Id == FavoritesId).TrackIds.SequenceEqual(["local", "second-track"]),
                    "A subsequent edit must not persist a song resurrected from an older server snapshot.");
            }
        }

        /// <summary>Renames and deletes one captured source presentation through its shared local backing core.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing immediate alias naming and active-view repair.</returns>
        private static async Task ValidateAliasManagementAsync(string root)
        {
            using var transport = new CloudCollectionSyncTests.FixtureTransport();
            using var harness = new CloudCollectionSyncTests.SyncHarness(root, transport);
            await SeedFavoriteAsync(harness, root);
            await harness.Workspace.SavePlaylistAsync(new MusicPlaylist { Id = "editable-mirror", Name = "Original", TrackIds = ["local", "first-track"] });
            await harness.Workspace.SynchronizeCloudCollectionsAsync();
            await using var view = new MainViewModel(new ProjectionWorkspace(harness.Workspace), new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await view.InitializeAsync();
            await ExecuteAsync(view, view.RefreshCloudPlaylistsCommand);
            var alias = view.Playlists.Single(item => item.LocalMirrorId == "editable-mirror");
            view.SelectedHomePlaylist = alias;
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(5));
            view.SelectedPlaylist = AssertSingleMirror(view);
            var writes = transport.Writes.Count;
            TestAssert.True(view.CanRenamePlaylistForMenu(alias) && view.CanDeletePlaylistForMenu(alias),
                "A verified ordinary source mirror must keep its locally owned management actions.");
            await view.RenamePlaylistFromMenuAsync(alias, "Renamed locally");
            TestAssert.True(view.Playlists.Single(item => item.Id == alias.Id).Name == "Renamed locally"
                && view.LibraryViewTitle == "Renamed locally" && transport.Writes.Count == writes,
                "Renaming the local backing must update its source presentation and opened title before a delayed upload confirms it.");
            await view.DeletePlaylistFromMenuAsync(alias);
            TestAssert.True(view.Playlists.All(item => item.LocalMirrorId != "editable-mirror")
                && (await harness.Workspace.GetPlaylistsAsync()).All(item => item.Id != "editable-mirror")
                && view.SelectedHomePlaylist?.IsFavorite == true && view.SelectedPlaylist?.IsFavorite == true,
                "Deleting the captured ordinary alias must remove its local backing and repair the active view to the surviving source favorite.");
            TestAssert.True(transport.Owned("a.example.test", "Alice").Count == 2 && transport.Writes.Count == writes,
                "Presentation management cannot directly delete any remote collection before the normal owned-mirror sync runs.");
        }

        /// <summary>Prevents an old remote response from being paired with a newer successful upload fingerprint.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing per-account snapshot/upload serialization.</returns>
        private static async Task ValidateScopedSnapshotReadAsync(string root)
        {
            using var transport = new CloudCollectionSyncTests.FixtureTransport();
            using var harness = new CloudCollectionSyncTests.SyncHarness(root, transport);
            await SeedFavoriteAsync(harness, root);
            transport.BlockNextFavoriteRead("a.example.test");
            var oldRead = harness.Workspace.GetCloudPlaylistsAsync(await harness.EndpointAsync("first"));
            try
            {
                await transport.FavoriteReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await harness.Workspace.SavePlaylistAsync(new MusicPlaylist
                {
                    Id = FavoritesId, Name = "我喜欢", IsFavorite = true, TrackIds = ["local", "second-track"]
                });
                var writes = transport.Writes.Count;
                var upload = harness.Workspace.SynchronizeCloudCollectionsAsync();
                TestAssert.True(!upload.IsCompleted && transport.Writes.Count == writes,
                    "An upload of the same account must wait until its already captured source response has been annotated.");
                transport.ReleaseFavoriteRead.TrySetResult();
                var stale = (await oldRead.WaitAsync(TimeSpan.FromSeconds(5))).Single();
                TestAssert.True(stale.LocalMirrorHasPendingChanges && stale.TrackIds.SequenceEqual(["first-track"]),
                    "The old response must retain its old applied boundary and cannot import a member removed before a later upload.");
                TestAssert.True((await upload.WaitAsync(TimeSpan.FromSeconds(5))).Single().Succeeded,
                    "The waiting upload must complete after the source read releases the shared account slot without deadlocking.");
                var fresh = (await harness.Workspace.GetCloudPlaylistsAsync(await harness.EndpointAsync("first"))).Single();
                TestAssert.True(!fresh.LocalMirrorHasPendingChanges && fresh.TrackIds.SequenceEqual(["second-track"]),
                    "A later fresh response must expose the newly confirmed membership and current applied fingerprint.");
            }
            finally
            {
                transport.ReleaseFavoriteRead.TrySetResult();
            }
        }

        /// <summary>Creates a confirmed source mirror containing only the online projection of a complete mixed favorite.</summary>
        /// <param name="harness">The generated real workspace and fake transport.</param>
        /// <param name="root">The explicit fixture database directory.</param>
        /// <returns>A task representing seeded metadata and one confirmed upload.</returns>
        private static async Task SeedFavoriteAsync(CloudCollectionSyncTests.SyncHarness harness, string root)
        {
            await harness.InitializeAsync();
            var settings = await harness.Workspace.LoadSettingsAsync();
            await harness.Workspace.SaveSettingsAsync(settings with
            {
                Endpoints = settings.Endpoints.Select(endpoint => endpoint.Id == "first" ? endpoint with { Name = "MajNet" } : endpoint).ToArray()
            });
            var repository = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
            var tracks = await repository.SearchAsync(null);
            await repository.UpsertTracksAsync(tracks.Select(track => track with
            {
                Revision = track.RemoteId is { } remote && Guid.TryParse(remote, out _) ? CloudCollectionSyncTests.FixtureTransport.Hash(remote) : track.Revision,
                Duration = TimeSpan.FromMinutes(3), AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000 }
            }).ToArray());
            await harness.Workspace.SavePlaylistAsync(new MusicPlaylist { Id = FavoritesId, Name = "我喜欢", IsFavorite = true, TrackIds = ["local", "first-track"] });
            await harness.EnableAccountAsync("first", "Alice");
            TestAssert.True((await harness.Workspace.SynchronizeCloudCollectionsAsync()).Single().Succeeded,
                "The mirror fixture must begin with a genuinely applied scoped binding rather than a name-based guess.");
        }

        /// <summary>Checks that only the MajNet presentation of the logical favorite remains visible.</summary>
        /// <param name="view">The initialized shared presentation.</param>
        /// <returns>The single retained source-owned favorite row.</returns>
        private static MusicPlaylist AssertSingleMirror(MainViewModel view)
        {
            var favorite = view.Playlists.Single(item => item.IsFavorite);
            TestAssert.True(favorite.IsCloud && favorite.LocalMirrorId == FavoritesId && view.Playlists.All(item => item.Id != FavoritesId),
                "A confirmed mirror must retain the MajNet row and hide only its exact duplicate local presentation.");
            return favorite;
        }

        /// <summary>Observes an ordinary user command independently of unrelated background view work.</summary>
        /// <param name="view">The command owner.</param>
        /// <param name="command">The enabled user action.</param>
        /// <returns>A task representing its completed foreground operation.</returns>
        private static async Task ExecuteAsync(MainViewModel view, AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The requested fixture action must be available.");
            command.Execute(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (view.IsBusy || !command.CanExecute(null))
            {
                await Task.Delay(1, timeout.Token);
            }

            await view.MetadataRefresh.WaitAsync(timeout.Token);
        }

        /// <summary>Identifies the built-in complete local backing preserved beneath its source presentation.</summary>
        private const string FavoritesId = "local:favorites";

        /// <summary>Uses real isolated storage and source reads while intentionally omitting the automatic-upload capability.</summary>
        /// <param name="inner">The generated workspace whose fake server can deliberately retain an older snapshot.</param>
        private sealed class ProjectionWorkspace(MusicWorkspace inner) : IMusicWorkspace, IOnlineMusicWorkspace, IPlaylistManagementWorkspace
        {
            /// <inheritdoc />
            public bool IsOnline => inner.IsOnline;
            /// <inheritdoc />
            public string DataDirectory => inner.DataDirectory;
            /// <inheritdoc />
            public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                return (await inner.LoadSettingsAsync(cancellationToken)) with { OnlineEnabled = true };
            }
            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                return inner.SaveSettingsAsync(settings, cancellationToken);
            }
            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled)
            {
                inner.SetOnlineEnabled(enabled);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                return inner.SearchAsync(query, cancellationToken);
            }
            /// <inheritdoc />
            public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("The generated mirror fixture does not import music.");
            }
            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("The generated mirror fixture does not scan directories.");
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
            {
                return inner.GetPlaylistsAsync(cancellationToken);
            }
            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                return inner.SavePlaylistAsync(playlist, cancellationToken);
            }
            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return inner.SynchronizeAsync(endpoint, cancellationToken);
            }
            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<string?>(null);
            }
            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("The generated mirror fixture cannot open audio.");
            }
            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default)
            {
                return inner.ClearCacheAsync(cancellationToken);
            }
            /// <inheritdoc />
            public IReadOnlyList<EndpointSession> GetEndpointSessions()
            {
                return inner.GetEndpointSessions();
            }
            /// <inheritdoc />
            public Task LoginAsync(EndpointConfiguration endpoint, string username, string password, CancellationToken cancellationToken = default)
            {
                return inner.LoginAsync(endpoint, username, password, cancellationToken);
            }
            /// <inheritdoc />
            public Task UseGuestAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return inner.UseGuestAsync(endpoint, cancellationToken);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetCloudPlaylistsAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return inner.GetCloudPlaylistsAsync(endpoint, cancellationToken);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> GetRecommendationsAsync(EndpointConfiguration endpoint, int page = 0, int pageSize = 50,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<MusicTrack>>(Array.Empty<MusicTrack>());
            }
            /// <inheritdoc />
            public Task<MusicTrack> EnrichTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(track);
            }
            /// <inheritdoc />
            public Task UpdateTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                return inner.UpdateTrackMetadataAsync(track, cancellationToken);
            }
            /// <inheritdoc />
            public Task DeletePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                return inner.DeletePlaylistAsync(playlist, cancellationToken);
            }
            /// <inheritdoc />
            public Task RemoveFromPlaylistAsync(string playlistId, MusicTrack track, CancellationToken cancellationToken = default)
            {
                return inner.RemoveFromPlaylistAsync(playlistId, track, cancellationToken);
            }
            /// <inheritdoc />
            public Task<bool> IsPlaylistDeletedAsync(string playlistId, CancellationToken cancellationToken = default)
            {
                return inner.IsPlaylistDeletedAsync(playlistId, cancellationToken);
            }
        }
    }
}
