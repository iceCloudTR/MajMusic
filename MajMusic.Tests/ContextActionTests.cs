#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;
using Microsoft.Data.Sqlite;

namespace MajMusic.Tests
{
    /// <summary>Checks captured context targets against real local persistence and an isolated audio coordinator.</summary>
    internal static class ContextActionTests
    {
        /// <summary>Runs exact-target playback, membership, rename, export, deletion and expired-menu regressions.</summary>
        /// <returns>A task representing completed local action checks without network or native audio.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-context-actions-" + Guid.NewGuid().ToString("N")));
            try
            {
                await ValidateActionsAsync(root);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-context-actions-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated context-action fixture.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Executes captured menu targets after the selected row and insertion destination have changed.</summary>
        /// <param name="root">The isolated application and export directory.</param>
        /// <returns>A task representing persisted target ownership and selected-view repair.</returns>
        private static async Task ValidateActionsAsync(string root)
        {
            using var handler = new LocalTransport();
            using var client = new HttpClient(handler);
            using var workspace = new MusicWorkspace(Path.Combine(root, "data"), client, client, credentialStore: new NoCredentials());
            await workspace.LoadSettingsAsync();
            var tracks = Enumerable.Range(1, 3).Select(Track).ToArray();
            var library = new SqliteLibraryRepository(Path.Combine(root, "data", "library.db"));
            await library.UpsertTracksAsync(tracks);
            var opened = new MusicPlaylist { Id = "opened", Name = "Opened", TrackIds = [tracks[0].Id, tracks[1].Id] };
            var other = new MusicPlaylist { Id = "other", Name = "Other", TrackIds = [tracks[2].Id] };
            var favorites = new MusicPlaylist { Id = "local:favorites", Name = "Favorites", IsFavorite = true, TrackIds = [tracks[0].Id] };
            await workspace.SavePlaylistAsync(opened);
            await workspace.SavePlaylistAsync(other);
            await workspace.SavePlaylistAsync(favorites);
            var backend = new LibraryExperienceTests.ExperienceBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: static (_, _) => Task.FromResult("generated-context-audio"));
            await using var model = new MainViewModel(workspace, playback, static action => action(), new MajCollectionExchange(workspace));
            await model.InitializeAsync();
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));

            model.SelectedTrack = tracks[2];
            await model.PlayTrackFromMenuAsync(tracks[1]);
            TestAssert.True(playback.Queue.Current?.Id == tracks[1].Id && playback.Queue.Tracks.Count == 3 && backend.OpenCount == 1,
                "A song context action must start its captured song rather than a different inspected row.");
            model.SelectedPlaylist = model.Playlists.Single(item => item.Id == other.Id);
            await model.PlayPlaylistFromMenuAsync(opened);
            TestAssert.True(playback.Queue.Tracks.Select(item => item.Id).SequenceEqual(opened.TrackIds)
                && playback.Queue.Current?.Id == tracks[0].Id && model.SelectedPlaylist?.Id == other.Id,
                "Playing a captured playlist must retain its saved order and leave the unrelated insertion target unchanged.");

            model.SelectedTrack = tracks[0];
            await model.ToggleTrackFavoriteFromMenuAsync(tracks[1]);
            TestAssert.True(model.IsTrackFavoriteForMenu(tracks[1]) && model.IsTrackFavoriteForMenu(tracks[0])
                && model.SelectedTrack?.Id == tracks[0].Id,
                "Favoriting a captured song must not toggle another row or move the inspector selection.");
            await model.ToggleTrackFavoriteFromMenuAsync(tracks[1]);
            TestAssert.True(!model.IsTrackFavoriteForMenu(tracks[1]) && model.IsTrackFavoriteForMenu(tracks[0]),
                "The same captured favorite action must remove only its intended member.");

            await model.AddTrackToPlaylistFromMenuAsync(tracks[0], other);
            await model.AddTrackToPlaylistFromMenuAsync(tracks[1], other);
            await model.RenamePlaylistFromMenuAsync(other, "Renamed from menu");
            var renamed = (await workspace.GetPlaylistsAsync()).Single(item => item.Id == other.Id);
            TestAssert.True(renamed.Name == "Renamed from menu"
                && renamed.TrackIds.SequenceEqual([tracks[2].Id, tracks[0].Id, tracks[1].Id]),
                "An older open menu must use the latest membership instead of discarding intervening additions during rename.");

            model.SelectedHomePlaylist = model.Playlists.Single(item => item.Id == opened.Id);
            await model.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            model.SelectedPlaylist = model.Playlists.Single(item => item.Id == other.Id);
            model.SelectedTrack = tracks[2];
            TestAssert.True(model.BrowsedPlaylistForMenu?.Id == opened.Id && model.CanRemoveTrackForMenu(tracks[1], opened),
                "Removal availability must refer to the opened list rather than the selected insertion destination.");
            await model.RemoveTrackFromPlaylistFromMenuAsync(tracks[1], opened);
            TestAssert.True(model.Tracks.Select(item => item.Id).SequenceEqual([tracks[0].Id])
                && model.SelectedTrack?.Id == tracks[2].Id && model.SelectedPlaylist?.Id == other.Id,
                "A captured removal must refresh the opened list while preserving unrelated song and destination selections.");
            TestAssert.True((await workspace.GetPlaylistsAsync()).Single(item => item.Id == other.Id).TrackIds.SequenceEqual(renamed.TrackIds),
                "Removing from the opened list cannot change another local playlist.");

            model.SelectedPlaylist = model.Playlists.Single(item => item.Id == opened.Id);
            var export = Path.Combine(root, "captured-export.json");
            await model.ExportPlaylistFromMenuAsync(other, export);
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(export)))
            {
                TestAssert.Equal("Renamed from menu", document.RootElement.GetProperty("Name").GetString(),
                    "The file picker result must export the captured list rather than the row selected afterward.");
            }

            var currentFavorites = model.Playlists.Single(item => item.IsFavorite);
            TestAssert.True(!model.CanRenamePlaylistForMenu(currentFavorites) && !model.CanDeletePlaylistForMenu(currentFavorites),
                "Built-in favorites must not expose rename or whole-list deletion.");
            var cloud = new MusicPlaylist { Id = "unmapped-cloud", Name = "Remote", EndpointId = "remote", RemoteId = "cloud-id" };
            model.Playlists.Add(cloud);
            TestAssert.True(!model.CanEditPlaylistForMenu(cloud) && !model.CanDeletePlaylistForMenu(cloud),
                "An independent cloud collection must not acquire local edit capabilities from its displayed name.");
            await model.RenamePlaylistFromMenuAsync(cloud, "Ignored");
            await model.DeletePlaylistFromMenuAsync(cloud);
            TestAssert.True((await workspace.GetPlaylistsAsync()).All(item => item.Id != cloud.Id),
                "A cloud context action cannot create a local copy as a side effect of unsupported ownership.");

            await model.DeletePlaylistFromMenuAsync(other);
            TestAssert.True((await workspace.GetPlaylistsAsync()).All(item => item.Id != other.Id)
                && model.BrowsedPlaylistForMenu?.Id == opened.Id,
                "Deleting a captured list must not delete or close a different list selected later.");
            await model.AddTrackToPlaylistFromMenuAsync(tracks[1], other);
            await model.RenamePlaylistFromMenuAsync(other, "Expired menu");
            await model.OpenPlaylistFromMenuAsync(other);
            TestAssert.True((await workspace.GetPlaylistsAsync()).All(item => item.Id != other.Id)
                && model.BrowsedPlaylistForMenu?.Id == opened.Id,
                "An expired menu cannot resurrect a deleted list or replace the current browsing destination.");
            TestAssert.Equal(0, handler.Requests, "All local context actions must remain independent of HTTP and credentials.");
        }

        /// <summary>Creates deterministic portable song metadata for fake audio and real JSON export.</summary>
        /// <param name="index">The stable song number.</param>
        /// <returns>A generated local catalog entry.</returns>
        private static MusicTrack Track(int index)
        {
            return new MusicTrack
            {
                Id = "context-" + index, Title = "Context " + index, SourceId = "local",
                MajChartHash = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes("context-chart-" + index))),
                Audio = new MediaResource { LocalPath = "generated-unused-context-audio" }
            };
        }

        /// <summary>Rejects unexpected network activity from local menu operations.</summary>
        private sealed class LocalTransport : HttpMessageHandler
        {
            /// <summary>Gets the number of attempted HTTP operations.</summary>
            public int Requests { get; private set; }
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                throw new InvalidOperationException("Generated context actions cannot use HTTP.");
            }
        }

        /// <summary>Keeps generated local fixtures outside the platform credential store.</summary>
        private sealed class NoCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Generated context actions cannot read credentials.");
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Generated context actions cannot write credentials.");
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Generated context actions cannot delete credentials.");
            }
        }
    }
}
