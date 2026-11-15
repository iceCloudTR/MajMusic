#nullable enable

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Collections;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;
using Microsoft.Data.Sqlite;

namespace MajMusic.Tests
{
    /// <summary>Checks local playlist deletion, membership editing and stable presentation ownership.</summary>
    internal static class PlaylistManagementTests
    {
        /// <summary>Exercises persisted edits without contacting a server, reading credentials or playing audio.</summary>
        /// <returns>A task representing all local playlist management regressions.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-playlist-management-" + Guid.NewGuid().ToString("N")));
            try
            {
                await ValidateImportedPlaylistAsync(Path.Combine(root, "imported"));
                await ValidateOwnershipAndFavoritesAsync(Path.Combine(root, "ownership"));
                await ValidatePresentationAsync(Path.Combine(root, "presentation"));
                await ValidateMigrationAsync(Path.Combine(root, "migration"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-playlist-management-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the playlist-management fixture.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Keeps Maj references and external source files consistent through removal, deletion and rediscovery.</summary>
        /// <param name="root">The isolated directory containing source files and application storage.</param>
        /// <returns>A task representing source protection and restart persistence checks.</returns>
        private static async Task ValidateImportedPlaylistAsync(string root)
        {
            var source = Path.Combine(root, "source");
            var firstHash = await CreateSongAsync(source, "first");
            var secondHash = await CreateSongAsync(source, "second");
            var unknown = Hash("unavailable chart");
            var indexDirectory = Path.Combine(source, "majcollection");
            Directory.CreateDirectory(indexDirectory);
            var index = Path.Combine(indexDirectory, "ordered.json");
            await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new
            {
                Name = "Imported list",
                SongHashs = new[] { firstHash, unknown, firstHash, secondHash },
                IsPlayList = true,
                Description = "Portable field"
            }));
            var before = SnapshotSource(source);
            var data = Path.Combine(root, "data");
            using var transport = new LocalOnlyTransport();
            using var client = new HttpClient(transport, false);
            MusicPlaylist imported;
            MusicTrack first;
            using (var workspace = CreateWorkspace(data, client))
            {
                await workspace.LoadSettingsAsync();
                var music = await workspace.IndexDirectoryAsync(source);
                first = music.Tracks.Single(item => item.MajChartHash == firstHash);
                var second = music.Tracks.Single(item => item.MajChartHash == secondHash);
                var exchange = new MajCollectionExchange(workspace);
                await exchange.ImportIndexDirectoryAsync(indexDirectory);
                imported = (await workspace.GetPlaylistsAsync()).Single();
                var management = (IPlaylistManagementWorkspace)workspace;
                await management.RemoveFromPlaylistAsync(imported.Id, first);
                var edited = (await workspace.GetPlaylistsAsync()).Single();
                TestAssert.True(edited.TrackIds.SequenceEqual([second.Id]), "Removing a song removes every occurrence of that library entry.");
                TestAssert.True(edited.MajCollection!.Entries.Select(entry => entry.Hash).SequenceEqual([unknown, secondHash]),
                    "Removed portable references cannot become unresolved entries that later reappear.");
                TestAssert.True(edited.MajCollection.Entries[0].TrackId is null, "Unrelated unresolved hashes retain their unavailable state.");
                TestAssert.Equal("Portable field", edited.MajCollection.AdditionalProperties["Description"].GetString(), "Membership edits retain portable extension fields.");
                await using var output = new MemoryStream();
                await new MajCollectionService().WriteAsync(output, edited, music.Tracks);
                using var exported = JsonDocument.Parse(output.ToArray());
                TestAssert.True(exported.RootElement.GetProperty("SongHashs").EnumerateArray().Select(item => item.GetString()).SequenceEqual([unknown, secondHash]),
                    "Portable export retains only current members and unrelated unresolved references in order.");
                await exchange.ImportIndexDirectoryAsync(indexDirectory);
                TestAssert.True((await workspace.GetPlaylistsAsync()).Single().TrackIds.SequenceEqual([second.Id]), "Automatic index scanning preserves removed songs.");
            }

            using (var reopened = CreateWorkspace(data, client))
            {
                await reopened.LoadSettingsAsync();
                var edited = (await reopened.GetPlaylistsAsync()).Single();
                TestAssert.True(!edited.TrackIds.Contains(first.Id), "Removed song membership remains saved after restart.");
                var management = (IPlaylistManagementWorkspace)reopened;
                await management.DeletePlaylistAsync(edited);
                TestAssert.Equal(0, (await reopened.GetPlaylistsAsync()).Count, "Deleting a local imported list removes its application copy.");
                TestAssert.Equal(2, (await reopened.SearchAsync(null)).Count, "Deleting a list leaves music-library metadata intact.");
            }

            using (var reopened = CreateWorkspace(data, client))
            {
                await reopened.LoadSettingsAsync();
                var management = (IPlaylistManagementWorkspace)reopened;
                TestAssert.True(await management.IsPlaylistDeletedAsync(imported.Id), "Deleted imported lists retain a restart-safe discovery marker.");
                var exchange = new MajCollectionExchange(reopened);
                await exchange.ImportIndexDirectoryAsync(indexDirectory);
                TestAssert.Equal(0, (await reopened.GetPlaylistsAsync()).Count, "Automatic rediscovery cannot recreate a deleted local list.");
                var restored = await exchange.ImportFileAsync(index);
                TestAssert.True(restored.TrackIds.Count == 3 && !await management.IsPlaylistDeletedAsync(imported.Id),
                    "Explicit file import can restore a deleted list and clear its discovery marker.");
                TestAssert.True(!reopened.IsOnline, "Playlist changes retain the default offline mode.");
            }

            TestAssert.Equal(0, transport.RequestCount, "Local playlist management must never contact a source.");
            AssertSourceUnchanged(before, source);
        }

        /// <summary>Protects remote lists and whole favorites while removing favorite aliases consistently.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing ownership and portable favorite-set checks.</returns>
        private static async Task ValidateOwnershipAndFavoritesAsync(string root)
        {
            using var transport = new LocalOnlyTransport();
            using var client = new HttpClient(transport, false);
            using var workspace = CreateWorkspace(root, client);
            await workspace.LoadSettingsAsync();
            var first = Track("first", Hash("shared chart"));
            var alias = Track("alias", first.MajChartHash!);
            var second = Track("second", Hash("second chart"));
            var unknown = Hash("unavailable chart");
            var library = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
            await library.UpsertTracksAsync([first, alias, second]);
            var favorites = new MusicPlaylist
            {
                Id = MajCollectionService.FavoritesPlaylistId,
                Name = "Favorites",
                IsFavorite = true,
                TrackIds = [first.Id, alias.Id, second.Id],
                MajCollection = new MajCollectionDefinition
                {
                    Entries =
                    [
                        new MajCollectionEntry { Hash = first.MajChartHash!, TrackId = first.Id },
                        new MajCollectionEntry { Hash = first.MajChartHash! },
                        new MajCollectionEntry { Hash = first.MajChartHash!, TrackId = alias.Id },
                        new MajCollectionEntry { Hash = unknown },
                        new MajCollectionEntry { Hash = second.MajChartHash!, TrackId = second.Id }
                    ]
                }
            };
            await workspace.SavePlaylistAsync(favorites);
            var management = (IPlaylistManagementWorkspace)workspace;
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => management.DeletePlaylistAsync(favorites));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => management.DeletePlaylistAsync(favorites with { IsFavorite = false }));
            await management.RemoveFromPlaylistAsync(favorites.Id, alias);
            var remaining = (await workspace.GetPlaylistsAsync()).Single();
            TestAssert.True(remaining.TrackIds.SequenceEqual([second.Id]), "Removing a favorite removes all aliases with the same portable identity.");
            TestAssert.True(remaining.MajCollection!.Entries.Select(entry => entry.Hash).SequenceEqual([unknown, second.MajChartHash]),
                "Favorite removal also clears matching unresolved references while retaining unrelated hashes.");
            var cloud = new MusicPlaylist { Id = "cloud-list", Name = "Cloud", EndpointId = "source", RemoteId = "remote-list", TrackIds = [first.Id] };
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.SavePlaylistAsync(cloud));
            await library.SavePlaylistAsync(cloud);
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => management.DeletePlaylistAsync(cloud));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => management.DeletePlaylistAsync(cloud with { RemoteId = null }));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => management.RemoveFromPlaylistAsync(cloud.Id, first));
            TestAssert.True((await workspace.GetPlaylistsAsync()).Single(item => item.Id == cloud.Id).TrackIds.SequenceEqual([first.Id]),
                "Cloud ownership remains protected when a caller supplies a stale local-looking snapshot.");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => management.RemoveFromPlaylistAsync(favorites.Id, second, canceled.Token));
            TestAssert.True((await workspace.GetPlaylistsAsync()).Single(item => item.Id == favorites.Id).TrackIds.SequenceEqual([second.Id]),
                "A canceled local edit leaves membership unchanged.");
            TestAssert.Equal(0, transport.RequestCount, "Ownership protection and local favorites do not need a source request.");
        }

        /// <summary>Uses the browsed playlist for removal and repairs home ownership after deletion.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing command availability and selection independence.</returns>
        private static async Task ValidatePresentationAsync(string root)
        {
            using var transport = new LocalOnlyTransport();
            using var client = new HttpClient(transport, false);
            using var workspace = CreateWorkspace(root, client);
            await workspace.LoadSettingsAsync();
            var first = Track("first", Hash("shared chart"));
            var alias = Track("alias", first.MajChartHash!);
            var second = Track("second", Hash("second chart"));
            var library = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
            await library.UpsertTracksAsync([first, alias, second]);
            var opened = new MusicPlaylist { Id = "opened", Name = "Opened", TrackIds = [first.Id, second.Id] };
            var target = new MusicPlaylist { Id = "target", Name = "Insertion target", TrackIds = [first.Id, second.Id] };
            await workspace.SavePlaylistAsync(opened);
            await workspace.SavePlaylistAsync(target);
            await workspace.SavePlaylistAsync(new MusicPlaylist
            {
                Id = MajCollectionService.FavoritesPlaylistId,
                Name = "Favorites",
                IsFavorite = true,
                TrackIds = [first.Id, second.Id]
            });
            var playback = new PlaybackService(new UnavailablePlaybackBackend());
            await using var view = new MainViewModel(workspace, playback, action => action());
            await view.InitializeAsync();
            view.SelectedHomePlaylist = view.Playlists.Single(item => item.Id == opened.Id);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            view.SelectedPlaylist = view.Playlists.Single(item => item.Id == target.Id);
            view.SelectedTrack = first;
            TestAssert.True(view.CanRemoveSelectedPlaylistTrack && view.RemoveFromPlaylistCommand.CanExecute(null), "A selected local member exposes the remove action.");
            await ExecuteAsync(view, view.RemoveFromPlaylistCommand);
            TestAssert.True(view.Tracks.Select(track => track.Id).SequenceEqual([second.Id]), "Removing updates the currently browsed contents.");
            TestAssert.Equal(target.Id, view.SelectedPlaylist!.Id, "Removing a song retains the unrelated sidebar insertion target.");
            TestAssert.True((await workspace.GetPlaylistsAsync()).Single(item => item.Id == target.Id).TrackIds.SequenceEqual(target.TrackIds),
                "The sidebar insertion target cannot be mistaken for the removal destination.");
            TestAssert.True(view.IsSelectedFavorite, "Removing from a normal list does not alter favorites.");
            TestAssert.True(!view.CanRemoveSelectedPlaylistTrack, "An inspected song outside the remaining list cannot be removed again.");

            view.SelectedHomePlaylist = view.Playlists.Single(item => item.IsFavorite);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(!view.CanDeleteSelectedPlaylist && !view.DeletePlaylistCommand.CanExecute(null), "Whole favorites deletion stays unavailable.");
            view.SelectedTrack = alias;
            TestAssert.True(view.IsSelectedFavorite && view.CanRemoveSelectedPlaylistTrack, "A favorite alias exposes the same removal action.");
            await ExecuteAsync(view, view.RemoveFromPlaylistCommand);
            TestAssert.True(!view.IsSelectedFavorite && !view.CanRemoveSelectedPlaylistTrack, "Removing a favorite immediately updates its heart and remove action.");

            var cloud = new MusicPlaylist { Id = "cloud", Name = "Cloud", RemoteId = "remote", TrackIds = [second.Id] };
            view.Playlists.Add(cloud);
            view.SelectedHomePlaylist = cloud;
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            view.SelectedTrack = second;
            TestAssert.True(!view.CanDeleteSelectedPlaylist && !view.CanRemoveSelectedPlaylistTrack
                && !view.DeletePlaylistCommand.CanExecute(null) && !view.RemoveFromPlaylistCommand.CanExecute(null), "Cloud lists expose neither local destructive action.");

            view.SelectedHomePlaylist = view.Playlists.Single(item => item.Id == opened.Id);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.CanDeleteSelectedPlaylist, "An ordinary local list exposes deletion.");
            await ExecuteAsync(view, view.DeletePlaylistCommand);
            TestAssert.True(view.IsPlaylistsMode && view.SelectedHomePlaylist?.IsFavorite == true, "Deleting the opened list selects a surviving favorites view.");
            TestAssert.True(view.Playlists.All(item => item.Id != opened.Id), "Deleted lists leave the management catalog.");
            TestAssert.True(view.Tracks.Select(track => track.Id).SequenceEqual([second.Id]), "The repaired home view displays its surviving playlist rather than stale removed contents.");
            TestAssert.Equal(0, transport.RequestCount, "All management commands remain local.");
        }

        /// <summary>Upgrades an existing version-one database without losing playlists or membership.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing the additive deletion-store migration.</returns>
        private static async Task ValidateMigrationAsync(string root)
        {
            var database = Path.Combine(root, "library.db");
            var library = new SqliteLibraryRepository(database);
            var playlist = new MusicPlaylist { Id = "existing", Name = "Existing", TrackIds = ["song"] };
            await library.SavePlaylistAsync(playlist);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE deleted_playlists; PRAGMA user_version = 1;";
                await command.ExecuteNonQueryAsync();
            }

            var migrated = new SqliteLibraryRepository(database);
            TestAssert.True((await migrated.GetPlaylistsAsync()).Single().TrackIds.SequenceEqual(playlist.TrackIds), "Existing membership survives the version-one schema upgrade.");
            await migrated.DeletePlaylistAsync(playlist.Id);
            TestAssert.True(await new SqliteLibraryRepository(database).IsPlaylistDeletedAsync(playlist.Id), "The migrated database persists deletion markers after reopening.");
        }

        /// <summary>Executes an observed busy-state command and waits for its completed local operation.</summary>
        /// <param name="view">The view whose busy lifetime is observed.</param>
        /// <param name="command">The enabled playlist action.</param>
        /// <returns>A task representing the completed command.</returns>
        private static async Task ExecuteAsync(MainViewModel view, AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The playlist action must be enabled before execution.");
            command.Execute(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (view.IsBusy)
            {
                await Task.Delay(5, timeout.Token);
            }

            await view.MetadataRefresh.WaitAsync(timeout.Token);
        }

        /// <summary>Creates a source song whose metadata and audio are only read by the application.</summary>
        /// <param name="source">The isolated source directory.</param>
        /// <param name="name">The song title and directory name.</param>
        /// <returns>The portable hash of its original chart text.</returns>
        private static async Task<string> CreateSongAsync(string source, string name)
        {
            var directory = Path.Combine(source, name);
            Directory.CreateDirectory(directory);
            var chart = Encoding.UTF8.GetBytes($"&title={name}\n&artist=Fixture artist\n");
            await File.WriteAllBytesAsync(Path.Combine(directory, "maidata.txt"), chart);
            await File.WriteAllBytesAsync(Path.Combine(directory, "track.mp3"), Encoding.UTF8.GetBytes("audio fixture " + name));
            return Convert.ToBase64String(MD5.HashData(chart));
        }

        /// <summary>Creates portable metadata without opening an audio file.</summary>
        /// <param name="id">The library identity.</param>
        /// <param name="hash">The shared or independent portable chart identity.</param>
        /// <returns>The local fixture metadata.</returns>
        private static MusicTrack Track(string id, string hash)
        {
            return new MusicTrack { Id = id, Title = id, MajChartHash = hash, Audio = new MediaResource() };
        }

        /// <summary>Hashes a generated chart identity.</summary>
        /// <param name="text">The generated identity text.</param>
        /// <returns>A portable Base64 MD5 identity.</returns>
        private static string Hash(string text)
        {
            return Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(text)));
        }

        /// <summary>Captures source bytes and timestamps so application edits cannot change read-only source data.</summary>
        /// <param name="source">The isolated source directory.</param>
        /// <returns>The file names, content digests and last-write ticks.</returns>
        private static Dictionary<string, (string Hash, long Timestamp)> SnapshotSource(string source)
        {
            return Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(source, path),
                path => (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), File.GetLastWriteTimeUtc(path).Ticks), StringComparer.Ordinal);
        }

        /// <summary>Checks every original source file after local playlist management.</summary>
        /// <param name="expected">The original content and timestamps.</param>
        /// <param name="source">The isolated source directory.</param>
        private static void AssertSourceUnchanged(Dictionary<string, (string Hash, long Timestamp)> expected, string source)
        {
            var actual = SnapshotSource(source);
            TestAssert.Equal(expected.Count, actual.Count, "Playlist edits cannot add or delete original source files.");
            foreach (var pair in expected)
            {
                TestAssert.True(actual.TryGetValue(pair.Key, out var file) && file == pair.Value, "Playlist edits preserve original music and index bytes and timestamps.");
            }
        }

        /// <summary>Creates an isolated workspace without access to the operating-system credential vault.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <param name="client">The transport that rejects network activity.</param>
        /// <returns>The idle local workspace.</returns>
        private static MusicWorkspace CreateWorkspace(string root, HttpClient client)
        {
            return new MusicWorkspace(root, client, client, credentialStore: new UnusedCredentialStore());
        }

        /// <summary>Rejects every network request made by local playlist operations.</summary>
        private sealed class LocalOnlyTransport : HttpMessageHandler
        {
            /// <summary>Gets the number of requests reaching the transport.</summary>
            public int RequestCount { get; private set; }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                throw new InvalidOperationException("Local playlist tests cannot issue HTTP requests.");
            }
        }

        /// <summary>Rejects every credential operation made by local playlist management.</summary>
        private sealed class UnusedCredentialStore : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Local playlist tests cannot read credentials.");
            }

            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Local playlist tests cannot write credentials.");
            }

            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Local playlist tests cannot delete credentials.");
            }
        }
    }
}
