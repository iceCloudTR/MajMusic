#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Collections;

namespace MajMusic.Tests
{
    /// <summary>Exercises read-only music roots and Maj playlist exchange through real local persistence.</summary>
    internal static class CollectionWorkflowTests
    {
        /// <summary>Checks import, favorites, local edits, safe external export, restart and source preservation.</summary>
        /// <returns>A task representing the complete local collection workflow.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-collection-workflow-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                await VerifyWorkflowAsync(root);
                await VerifyFavoriteMergeAsync(Path.Combine(root, "favorite-merge"));
                await VerifyCrossRootResolutionAsync(Path.Combine(root, "cross-root-resolution"));
                await VerifyUnregisteredSourceProtectionAsync(Path.Combine(root, "unregistered-source"));
            }
            finally
            {
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-collection-workflow-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the collection workflow fixture.");
                }

                Directory.Delete(root, true);
            }
        }

        /// <summary>Checks read-only protection for a JSON imported before its music root is registered.</summary>
        /// <param name="root">The isolated direct-import fixture directory.</param>
        /// <returns>A task representing source protection and external export checks.</returns>
        private static async Task VerifyUnregisteredSourceProtectionAsync(string root)
        {
            var source = Path.Combine(root, "source");
            var collectionDirectory = Path.Combine(source, "majcollection");
            Directory.CreateDirectory(collectionDirectory);
            var hash = Convert.ToBase64String(MD5.HashData("unavailable source track"u8));
            var listPath = Path.Combine(collectionDirectory, "source-list.json");
            var favoritePath = Path.Combine(source, MajCollectionService.FavoritesFileName);
            var document = JsonSerializer.Serialize(new { Name = "Original list", SongHashs = new[] { hash }, IsPlayList = true });
            await File.WriteAllTextAsync(listPath, document);
            await File.WriteAllTextAsync(favoritePath, document);
            var before = SnapshotSource(source);
            using var handler = new OfflineTransport();
            using var transport = new HttpClient(handler);
            using var workspace = new MusicWorkspace(Path.Combine(root, "application-data"), transport, transport);
            TestAssert.Equal(0, (await workspace.LoadSettingsAsync()).LocalMusicDirectories.Count,
                "Direct JSON import is tested without a registered music root");
            var exchange = new MajCollectionExchange(workspace);
            var playlist = await exchange.ImportFileAsync(listPath);
            var favorites = await exchange.ImportFileAsync(favoritePath);
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => exchange.ExportAsync(Path.Combine(collectionDirectory, "new.json"), playlist));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => exchange.ExportAsync(Path.Combine(source, "another-folder", "new.json"), playlist));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => exchange.ExportAsync(Path.Combine(source, "favorite-copy.json"), favorites));
            TestAssert.True(!Directory.Exists(Path.Combine(source, "another-folder")),
                "A direct import protects its source root before export creates directories");
            var destination = Path.Combine(root, "external", "portable.json");
            await exchange.ExportAsync(destination, playlist);
            using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
            TestAssert.Equal(hash, exported.RootElement.GetProperty("SongHashs")[0].GetString(),
                "Unavailable references from a direct import remain exportable outside the source");
            TestAssert.Equal(0, handler.RequestCount, "Direct collection exchange does not contact online sources");
            AssertSourceUnchanged(before, source);
        }

        /// <summary>Checks that explicit favorites imports merge local members, unknown references and extension fields.</summary>
        /// <param name="root">The isolated merge fixture directory.</param>
        /// <returns>A task representing the merge and read-only assertions.</returns>
        private static async Task VerifyFavoriteMergeAsync(string root)
        {
            var source = Path.Combine(root, "readonly-source");
            var external = Path.Combine(root, "external-exports");
            Directory.CreateDirectory(external);
            var firstHash = await CreateSongAsync(source, "pack", "100", "Local favorite A");
            var secondHash = await CreateSongAsync(source, "pack", "200", "Imported favorite B");
            var retainedUnknown = Convert.ToBase64String(MD5.HashData("old unavailable favorite"u8));
            var incomingUnknown = Convert.ToBase64String(MD5.HashData("new unavailable favorite"u8));
            var incomingPath = Path.Combine(source, MajCollectionService.FavoritesFileName);
            await File.WriteAllTextAsync(incomingPath, JsonSerializer.Serialize(new
            {
                Name = "Incoming favorites",
                SongHashs = new[] { secondHash, secondHash, incomingUnknown },
                IsPlayList = true,
                Description = "Incoming description",
                NewField = "Retained incoming field"
            }));
            var before = SnapshotSource(source);
            using var handler = new OfflineTransport();
            using var transport = new HttpClient(handler);
            using var workspace = new MusicWorkspace(Path.Combine(root, "application-data"), transport, transport);
            var settings = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(settings with { LocalMusicDirectories = [source] });
            var indexed = await workspace.IndexDirectoryAsync(source);
            var first = indexed.Tracks.Single(track => track.MajChartHash == firstHash);
            var second = indexed.Tracks.Single(track => track.MajChartHash == secondHash);
            await workspace.SavePlaylistAsync(new MusicPlaylist
            {
                Id = MajCollectionService.FavoritesPlaylistId,
                Name = "Locally named favorites",
                IsFavorite = true,
                TrackIds = [first.Id],
                MajCollection = new MajCollectionDefinition
                {
                    IsPlayList = true,
                    Entries = [new MajCollectionEntry { Hash = retainedUnknown }],
                    AdditionalProperties = new Dictionary<string, JsonElement>
                    {
                        ["Description"] = JsonSerializer.SerializeToElement("Original description"),
                        ["OldField"] = JsonSerializer.SerializeToElement(42)
                    }
                }
            });

            var exchange = new MajCollectionExchange(workspace);
            var merged = await exchange.ImportFileAsync(incomingPath);
            TestAssert.True(merged.IsFavorite && merged.TrackIds.SequenceEqual(new[] { first.Id, second.Id }),
                "Importing MyFavorites must retain local A and add incoming B only once");
            TestAssert.Equal("Locally named favorites", merged.Name, "An explicit favorite merge retains the local name");
            TestAssert.True(merged.MajCollection!.Entries.Select(entry => entry.Hash).ToHashSet(StringComparer.Ordinal)
                .SetEquals(new[] { firstHash, secondHash, retainedUnknown, incomingUnknown }),
                "Merging favorites retains both unavailable references and locally added portable members");
            TestAssert.Equal(4, merged.MajCollection.Entries.Count, "Favorite portable references have set semantics");
            TestAssert.Equal("Original description", merged.MajCollection.AdditionalProperties["Description"].GetString(),
                "Existing extension fields win a conflicting imported field");
            TestAssert.Equal(42, merged.MajCollection.AdditionalProperties["OldField"].GetInt32(),
                "Existing unknown fields survive a manual favorite merge");
            TestAssert.Equal("Retained incoming field", merged.MajCollection.AdditionalProperties["NewField"].GetString(),
                "New unknown fields are retained from the explicitly imported favorite file");

            var destination = Path.Combine(external, MajCollectionService.FavoritesFileName);
            await exchange.ExportAsync(destination, merged);
            var roundTrip = await new MajCollectionService().ReadFileAsync(destination, indexed.Tracks);
            TestAssert.True(roundTrip.TrackIds.SequenceEqual(new[] { first.Id, second.Id }),
                "Merged favorite membership and local order survive a portable export");
            TestAssert.True(roundTrip.MajCollection!.Entries.Select(entry => entry.Hash).ToHashSet(StringComparer.Ordinal)
                .SetEquals(new[] { firstHash, secondHash, retainedUnknown, incomingUnknown }),
                "Merged favorites export every retained unknown hash");
            TestAssert.Equal("Original description", roundTrip.MajCollection.AdditionalProperties["Description"].GetString(),
                "Merged extension fields survive the portable export");
            TestAssert.True(!workspace.IsOnline && handler.RequestCount == 0, "Manual favorites exchange remains entirely offline");
            AssertSourceUnchanged(before, source);
        }

        /// <summary>Checks unresolved references after indexing another root without any source collection files.</summary>
        /// <param name="root">The isolated multi-root fixture directory.</param>
        /// <returns>A task representing the deferred resolution and local edit assertions.</returns>
        private static async Task VerifyCrossRootResolutionAsync(string root)
        {
            var source = Path.Combine(root, "readonly-root-a");
            var laterSource = Path.Combine(root, "readonly-root-b");
            var collections = Path.Combine(source, "majcollection");
            Directory.CreateDirectory(collections);
            var firstHash = await CreateSongAsync(source, "pack", "100", "Removed known A");
            var thirdHash = await CreateSongAsync(source, "pack", "300", "Reordered known C");
            var fourthHash = await CreateSongAsync(source, "pack", "400", "Reordered known D");
            var laterHash = await CreateSongAsync(laterSource, "another-pack", "200", "Later indexed B");
            var unavailable = Convert.ToBase64String(MD5.HashData("still unavailable chart"u8));
            await File.WriteAllTextAsync(Path.Combine(collections, "deferred.json"), JsonSerializer.Serialize(new
            {
                Name = "Original source name",
                SongHashs = new[] { firstHash, laterHash, laterHash, thirdHash, fourthHash, unavailable },
                IsPlayList = true,
                Description = "Deferred source field"
            }));
            await File.WriteAllTextAsync(Path.Combine(source, MajCollectionService.FavoritesFileName), JsonSerializer.Serialize(new
            {
                Name = "Source favorites",
                SongHashs = new[] { firstHash, laterHash, laterHash, unavailable },
                IsPlayList = true
            }));
            var before = SnapshotSource(source);
            var laterBefore = SnapshotSource(laterSource);
            using var handler = new OfflineTransport();
            using var transport = new HttpClient(handler);
            using var workspace = new MusicWorkspace(Path.Combine(root, "application-data"), transport, transport);
            var settings = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(settings with { LocalMusicDirectories = [source, laterSource] });
            var indexed = await workspace.IndexDirectoryAsync(source);
            var first = indexed.Tracks.Single(track => track.MajChartHash == firstHash);
            var third = indexed.Tracks.Single(track => track.MajChartHash == thirdHash);
            var fourth = indexed.Tracks.Single(track => track.MajChartHash == fourthHash);
            var exchange = new MajCollectionExchange(workspace);
            await exchange.ImportDirectoryAsync(source);
            var initial = await workspace.GetPlaylistsAsync();
            var playlist = initial.Single(item => !item.IsFavorite);
            var favorites = initial.Single(item => item.IsFavorite);
            TestAssert.True(playlist.TrackIds.SequenceEqual(new[] { first.Id, third.Id, fourth.Id }),
                "The initially missing root leaves B unresolved without replacing another song");
            await workspace.SavePlaylistAsync(playlist with { Name = "Local renamed list", TrackIds = [fourth.Id, third.Id] });
            await workspace.SavePlaylistAsync(favorites with { TrackIds = [] });

            var later = await workspace.IndexDirectoryAsync(laterSource);
            var second = later.Tracks.Single(track => track.MajChartHash == laterHash);
            var noSourceCollections = await exchange.ImportDirectoryAsync(laterSource, preserveExisting: true);
            TestAssert.Equal(0, noSourceCollections.Playlists.Count,
                "The second music root has no collection files to trigger a source reload");
            var resolved = await workspace.GetPlaylistsAsync();
            var resolvedPlaylist = resolved.Single(item => item.Id == playlist.Id);
            var resolvedFavorites = resolved.Single(item => item.IsFavorite);
            TestAssert.Equal("Local renamed list", resolvedPlaylist.Name, "Deferred resolution keeps the local rename");
            var expected = new[] { fourth.Id, second.Id, second.Id, third.Id };
            TestAssert.True(resolvedPlaylist.TrackIds.SequenceEqual(expected),
                "Newly available repeated references are inserted around retained anchors without undoing known order edits");
            TestAssert.True(!resolvedPlaylist.TrackIds.Contains(first.Id), "An explicitly removed known entry must never be revived");
            TestAssert.Equal(2, resolvedPlaylist.MajCollection!.Entries.Count(entry => entry.Hash == laterHash && entry.TrackId == second.Id),
                "Each repeated ordinary playlist reference is bound to the newly indexed track");
            TestAssert.True(resolvedPlaylist.MajCollection.Entries.Any(entry => entry.Hash == unavailable && entry.TrackId is null),
                "Other unavailable references stay available for future resolution and portable export");
            TestAssert.Equal("Deferred source field", resolvedPlaylist.MajCollection.AdditionalProperties["Description"].GetString(),
                "Deferred resolution preserves original extension fields");
            TestAssert.True(resolvedFavorites.TrackIds.SequenceEqual(new[] { second.Id }),
                "Deferred favorites add the new hash once without reviving the user's removed known favorite");
            TestAssert.True(resolvedFavorites.MajCollection!.Entries.Any(entry => entry.Hash == unavailable && entry.TrackId is null),
                "Favorites preserve references that remain unavailable");

            await exchange.ImportDirectoryAsync(source, preserveExisting: true);
            var repeated = await workspace.GetPlaylistsAsync();
            TestAssert.True(repeated.Single(item => item.Id == playlist.Id).TrackIds.SequenceEqual(expected),
                "Repeated discovery does not reset reordered members or re-add deleted source references");
            TestAssert.Equal("Local renamed list", repeated.Single(item => item.Id == playlist.Id).Name,
                "Repeated discovery keeps the local name after unresolved entries have bound");
            TestAssert.True(repeated.Single(item => item.IsFavorite).TrackIds.SequenceEqual(new[] { second.Id }),
                "Repeated discovery retains favorite set membership after deferred resolution");
            TestAssert.True(!workspace.IsOnline && handler.RequestCount == 0, "Deferred cross-root resolution remains entirely offline");
            AssertSourceUnchanged(before, source);
            AssertSourceUnchanged(laterBefore, laterSource);
        }

        /// <summary>Checks that a complete original source tree retains its paths, bytes and filesystem metadata.</summary>
        /// <param name="before">The snapshot captured before application operations.</param>
        /// <param name="source">The original source directory.</param>
        private static void AssertSourceUnchanged(IReadOnlyDictionary<string, (string Hash, long Modified, FileAttributes Attributes)> before,
            string source)
        {
            var after = SnapshotSource(source);
            TestAssert.Equal(before.Count, after.Count, "The original source tree retains every original path");
            foreach (var item in before)
            {
                TestAssert.True(after.TryGetValue(item.Key, out var state) && item.Value == state,
                    "Source bytes, attributes and modification times remain unchanged throughout collection exchange");
            }
        }

        /// <summary>Runs the complete workflow with original fixture files and an isolated application database.</summary>
        /// <param name="root">The isolated validation directory.</param>
        /// <returns>A task representing the workflow assertions.</returns>
        private static async Task VerifyWorkflowAsync(string root)
        {
            var source = Path.Combine(root, "readonly-source");
            var data = Path.Combine(root, "application-data");
            var collections = Path.Combine(source, "majcollection");
            var external = Path.Combine(root, "external-exports");
            Directory.CreateDirectory(collections);
            Directory.CreateDirectory(external);
            var firstHash = await CreateSongAsync(source, "pack-one", "100", "Title from text A");
            var secondHash = await CreateSongAsync(source, "pack-two", "200", "Title from text B");
            var unavailable = Convert.ToBase64String(MD5.HashData("unavailable chart fixture"u8));
            var listPath = Path.Combine(collections, "ordered.json");
            var favoritePath = Path.Combine(source, MajCollectionService.FavoritesFileName);
            await File.WriteAllTextAsync(listPath, JsonSerializer.Serialize(new
            {
                Name = "Portable list",
                SongHashs = new[] { secondHash, unavailable, firstHash, secondHash },
                IsPlayList = true,
                Description = "Original extra field"
            }));
            await File.WriteAllTextAsync(favoritePath, JsonSerializer.Serialize(new
            {
                Name = "我的收藏",
                SongHashs = new[] { firstHash },
                IsPlayList = true
            }));
            var before = SnapshotSource(source);
            using var handler = new OfflineTransport();
            using var transport = new HttpClient(handler);
            string playlistId;
            string[] editedFavorites;
            using (var workspace = new MusicWorkspace(data, transport, transport))
            {
                var settings = await workspace.LoadSettingsAsync();
                await workspace.SaveSettingsAsync(settings with { LocalMusicDirectories = [source] });
                var indexed = await workspace.IndexDirectoryAsync(source);
                TestAssert.Equal(2, indexed.Tracks.Count, "Both nested source songs must be indexed without copying");
                TestAssert.True(!Directory.Exists(Path.Combine(data, "Library")), "The collection workflow must not create managed audio copies");
                var first = indexed.Tracks.Single(track => track.MajChartHash == firstHash);
                var second = indexed.Tracks.Single(track => track.MajChartHash == secondHash);
                await workspace.SavePlaylistAsync(new MusicPlaylist
                {
                    Id = MajCollectionService.FavoritesPlaylistId,
                    Name = "我的收藏",
                    IsFavorite = true
                });

                var exchange = new MajCollectionExchange(workspace);
                var imported = await exchange.ImportDirectoryAsync(source);
                TestAssert.Equal(0, imported.Issues.Count, "Valid original collection files must import cleanly");
                TestAssert.Equal(2, imported.Playlists.Count, "The source root's playlist and MyFavorites must both import");
                var persisted = await workspace.GetPlaylistsAsync();
                var favorites = persisted.Single(playlist => playlist.IsFavorite);
                var playlist = persisted.Single(playlist => !playlist.IsFavorite);
                playlistId = playlist.Id;
                TestAssert.Equal(MajCollectionService.FavoritesPlaylistId, favorites.Id, "Portable favorites retain their built-in local identity");
                TestAssert.True(favorites.TrackIds.SequenceEqual(new[] { first.Id }), "An empty built-in placeholder must accept imported favorites");
                TestAssert.True(playlist.TrackIds.SequenceEqual(new[] { second.Id, first.Id, second.Id }), "Portable list ordering and repeated references are preserved");
                TestAssert.Equal(4, playlist.MajCollection!.Entries.Count, "Unavailable portable references remain retained for export");

                var destination = Path.Combine(external, "portable-copy.json");
                await exchange.ExportAsync(destination, playlist);
                var service = new MajCollectionService();
                var roundTrip = await service.ReadFileAsync(destination, indexed.Tracks);
                TestAssert.True(roundTrip.TrackIds.SequenceEqual(playlist.TrackIds), "External export must resolve to the same ordered local music");
                TestAssert.True(roundTrip.MajCollection!.Entries.Select(entry => entry.Hash).SequenceEqual(
                    new[] { secondHash, unavailable, firstHash, secondHash }), "Round-trip export preserves unavailable hashes and their positions");
                TestAssert.Equal("Original extra field", roundTrip.MajCollection.AdditionalProperties["Description"].GetString(),
                    "Unknown source fields survive the facade and persistence round trip");
                var exportedText = await File.ReadAllTextAsync(destination);
                TestAssert.True(!exportedText.Contains(source, StringComparison.OrdinalIgnoreCase), "Portable exports must not leak local source paths");

                var favoriteExport = Path.Combine(external, MajCollectionService.FavoritesFileName);
                await exchange.ExportAsync(favoriteExport, favorites);
                var favoriteRoundTrip = await service.ReadFileAsync(favoriteExport, indexed.Tracks);
                TestAssert.True(favoriteRoundTrip.IsFavorite && favoriteRoundTrip.TrackIds.SequenceEqual(favorites.TrackIds),
                    "MyFavorites export can be read back as the local favorites collection");

                var forbiddenNewPath = Path.Combine(source, "must-not-create", "new-list.json");
                await TestAssert.ThrowsAsync<InvalidOperationException>(() => exchange.ExportAsync(forbiddenNewPath, playlist));
                TestAssert.True(!Directory.Exists(Path.GetDirectoryName(forbiddenNewPath)), "Source protection applies before creating even an export directory");
                await TestAssert.ThrowsAsync<InvalidOperationException>(() => exchange.ExportAsync(listPath, playlist));
                await TestAssert.ThrowsAsync<InvalidOperationException>(() => exchange.ExportAsync(favoritePath, favorites));
                if (OperatingSystem.IsWindows())
                {
                    await TestAssert.ThrowsAsync<InvalidOperationException>(() => exchange.ExportAsync(forbiddenNewPath.ToUpperInvariant(), playlist));
                }

                await TestAssert.ThrowsAsync<IOException>(() => exchange.ExportAsync(destination, favorites));
                TestAssert.Equal(exportedText, await File.ReadAllTextAsync(destination), "An existing external export is never overwritten");
                var existingOutside = Path.Combine(external, "unrelated.json");
                await File.WriteAllTextAsync(existingOutside, "original unrelated bytes");
                await TestAssert.ThrowsAsync<IOException>(() => exchange.ExportAsync(existingOutside, playlist));
                TestAssert.Equal("original unrelated bytes", await File.ReadAllTextAsync(existingOutside), "An unrelated external file is never overwritten");

                editedFavorites = [second.Id, first.Id];
                await workspace.SavePlaylistAsync(playlist with { Name = "Edited in MajMusic", TrackIds = [first.Id] });
                await workspace.SavePlaylistAsync(favorites with { TrackIds = editedFavorites });
                await exchange.ImportDirectoryAsync(source, preserveExisting: true);
                var preserved = await workspace.GetPlaylistsAsync();
                TestAssert.Equal(2, preserved.Count, "Repeated source discovery does not create duplicate lists");
                TestAssert.Equal("Edited in MajMusic", preserved.Single(item => item.Id == playlistId).Name,
                    "Automatic source discovery must not overwrite a user-edited list");
                TestAssert.True(preserved.Single(item => item.IsFavorite).TrackIds.SequenceEqual(editedFavorites),
                    "Automatic source discovery must not overwrite user-edited favorites");
                TestAssert.True(!workspace.IsOnline && handler.RequestCount == 0, "The complete source/index/collection exchange workflow remains offline");
            }

            using (var reopened = new MusicWorkspace(data, transport, transport))
            {
                var restoredSettings = await reopened.LoadSettingsAsync();
                TestAssert.True(restoredSettings.LocalMusicDirectories.SequenceEqual(new[] { source }), "The explicitly selected read-only root survives restart");
                var restored = await reopened.GetPlaylistsAsync();
                TestAssert.Equal("Edited in MajMusic", restored.Single(item => item.Id == playlistId).Name, "Edited portable lists survive restart");
                TestAssert.True(restored.Single(item => item.IsFavorite).TrackIds.SequenceEqual(editedFavorites), "Edited favorites survive restart");
                TestAssert.True(!reopened.IsOnline && handler.RequestCount == 0, "Restart does not contact an online source");
            }

            var after = SnapshotSource(source);
            TestAssert.Equal(before.Count, after.Count, "The original source tree contains exactly the same files and directories");
            foreach (var item in before)
            {
                TestAssert.True(after.TryGetValue(item.Key, out var state) && item.Value == state,
                    "Original source bytes, attributes, modification times and paths remain unchanged");
            }
        }

        /// <summary>Creates one original song fixture with a portable metadata identity.</summary>
        /// <param name="root">The original source root.</param>
        /// <param name="pack">The nested group name.</param>
        /// <param name="folder">The numeric song directory.</param>
        /// <param name="title">The canonical text title.</param>
        /// <returns>The original metadata bytes' Base64 MD5 identity.</returns>
        private static async Task<string> CreateSongAsync(string root, string pack, string folder, string title)
        {
            var directory = Path.Combine(root, pack, folder);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "track.mp3"), Encoding.UTF8.GetBytes("original source audio " + folder));
            var metadata = Encoding.UTF8.GetBytes($"&title={title}\n&artist=Fixture artist\n");
            await File.WriteAllBytesAsync(Path.Combine(directory, "maidata.txt"), metadata);
            return Convert.ToBase64String(MD5.HashData(metadata));
        }

        /// <summary>Records the entire tiny original fixture tree without changing it.</summary>
        /// <param name="source">The fixture source directory.</param>
        /// <returns>Paths, content digests, modification times and attributes for every original item.</returns>
        private static Dictionary<string, (string Hash, long Modified, FileAttributes Attributes)> SnapshotSource(string source)
        {
            var snapshot = new Dictionary<string, (string, long, FileAttributes)>(StringComparer.Ordinal);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Prepend(source))
            {
                snapshot[directory] = (string.Empty, Directory.GetLastWriteTimeUtc(directory).Ticks, File.GetAttributes(directory));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                snapshot[file] = (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), File.GetLastWriteTimeUtc(file).Ticks, File.GetAttributes(file));
            }

            return snapshot;
        }

        /// <summary>Rejects every attempted HTTP operation during local collection handling.</summary>
        private sealed class OfflineTransport : HttpMessageHandler
        {
            /// <summary>Counts unexpected requests across worker threads.</summary>
            private int _requests;

            /// <summary>Gets the total unexpected HTTP request count.</summary>
            public int RequestCount => Volatile.Read(ref _requests);

            /// <summary>Returns a failure without opening a connection.</summary>
            /// <param name="request">The unexpected outgoing request.</param>
            /// <param name="cancellationToken">The request lifetime.</param>
            /// <returns>A failed task identifying the offline workflow regression.</returns>
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requests);
                return Task.FromException<HttpResponseMessage>(new InvalidOperationException("Local collection exchange attempted HTTP."));
            }
        }
    }
}
