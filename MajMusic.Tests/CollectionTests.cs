#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Collections;

namespace MajMusic.Tests
{
    /// <summary>Checks Maj JSON compatibility, exact chart references and read-only source handling.</summary>
    internal static class CollectionTests
    {
        /// <summary>Runs portable collection regressions in an isolated temporary root.</summary>
        /// <returns>A task representing all collection regressions.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-collections-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                await VerifyRoundTripAsync(Path.Combine(root, "roundtrip"));
                await VerifyDirectoryAndFavoritesAsync(Path.Combine(root, "directory"));
                await VerifyNewPlaylistAsync();
                await VerifyInvalidAndCanceledAsync(Path.Combine(root, "invalid"));
            }
            finally
            {
                var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-collections-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the collection test root.");
                }

                Directory.Delete(root, true);
            }
        }

        /// <summary>Checks lossless portable fields, duplicate entries and unavailable reference preservation.</summary>
        /// <param name="root">The isolated case directory.</param>
        /// <returns>A task representing round-trip checks.</returns>
        private static async Task VerifyRoundTripAsync(string root)
        {
            Directory.CreateDirectory(root);
            var service = new MajCollectionService();
            var first = Track("first", "Same title", Hash("first chart"));
            var second = Track("second", "Same title", Hash("second chart"));
            var missing = Hash("unavailable chart");
            var source = Path.Combine(root, "中文列表.json");
            var input = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Name"] = "中文列表",
                ["SongHashs"] = new[] { first.MajChartHash, missing, second.MajChartHash, first.MajChartHash },
                ["IsPlayList"] = true,
                ["Description"] = "Portable description",
                ["Mods"] = new { custom = new[] { 1, 2 } },
                ["CustomValue"] = 1,
                ["customValue"] = 2,
                ["SongLevels"] = new[] { 4, 3, 2, 1 }
            });
            await File.WriteAllTextAsync(source, input, new UTF8Encoding(true));
            var originalBytes = await File.ReadAllBytesAsync(source);
            var originalWriteTime = File.GetLastWriteTimeUtc(source);
            var playlist = await service.ReadFileAsync(source, [second, first]);
            TestAssert.Equal("中文列表", playlist.Name, "Portable playlist names must not depend on file names.");
            TestAssert.True(playlist.TrackIds.SequenceEqual(new[] { first.Id, second.Id, first.Id }), "Hash resolution preserves order and duplicates.");
            TestAssert.Equal(4, playlist.MajCollection!.Entries.Count, "Unavailable references must remain stored.");
            TestAssert.True(playlist.MajCollection.Entries[1].TrackId is null, "Unmatched hashes must not be matched by title.");
            TestAssert.Equal(Path.GetFullPath(source), playlist.MajCollection.SourceFilePath, "Local source protection retains the original path.");

            await using var output = new MemoryStream();
            await service.WriteAsync(output, playlist, [first, second]);
            using var exported = JsonDocument.Parse(output.ToArray());
            using var original = JsonDocument.Parse(input);
            TestAssert.Equal(playlist.Id, exported.RootElement.GetProperty("MajMusicPlaylistId").GetString(),
                "Explicit export retains the Music identity without changing standard Maj fields.");
            TestAssert.Equal(playlist.IsFavorite, exported.RootElement.GetProperty("MajMusicIsFavorite").GetBoolean(),
                "Explicit export retains the favorite role independently of the standard Maj playlist fields.");
            var portable = JsonNode.Parse(output.ToArray())!.AsObject();
            portable.Remove("MajMusicPlaylistId");
            portable.Remove("MajMusicIsFavorite");
            using var portableDocument = JsonDocument.Parse(portable.ToJsonString());
            TestAssert.True(JsonElement.DeepEquals(original.RootElement, portableDocument.RootElement), "Unchanged collections round-trip all portable fields.");
            TestAssert.True(!exported.RootElement.TryGetProperty("SourceFilePath", out _), "Local source paths must never leak into portable JSON.");

            await using var reorderedOutput = new MemoryStream();
            await service.WriteAsync(reorderedOutput, playlist with { Name = "Reordered", TrackIds = [second.Id, first.Id] }, [first, second]);
            using var reordered = JsonDocument.Parse(reorderedOutput.ToArray());
            var reorderedHashes = reordered.RootElement.GetProperty("SongHashs").EnumerateArray().Select(element => element.GetString()).ToArray();
            TestAssert.True(reorderedHashes.SequenceEqual(new[] { second.MajChartHash, missing, first.MajChartHash }),
                "Known reorder/removal must retain unavailable entries without losing their relative order.");

            var repeated = await service.ReadFileAsync(source, [first, second]);
            TestAssert.Equal(playlist.Id, repeated.Id, "Importing the same source updates the same local playlist identity.");
            var afterBytes = await File.ReadAllBytesAsync(source);
            TestAssert.True(originalBytes.SequenceEqual(afterBytes), "Portable import/export must not change source bytes.");
            TestAssert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(source), "Read-only conversion must not touch source write timestamps.");
            TestAssert.Equal(1, Directory.GetFiles(root).Length, "Stream export must not create any source-side file.");
        }

        /// <summary>Checks standard directory discovery, favorites recognition and per-file error isolation.</summary>
        /// <param name="root">The isolated song root.</param>
        /// <returns>A task representing directory checks.</returns>
        private static async Task VerifyDirectoryAndFavoritesAsync(string root)
        {
            var collection = Path.Combine(root, "majcollection");
            Directory.CreateDirectory(collection);
            var track = Track("one", "Local", Hash("local chart"));
            var json = JsonSerializer.Serialize(new { Name = "A list", SongHashs = new[] { track.MajChartHash }, IsPlayList = true });
            await File.WriteAllTextAsync(Path.Combine(collection, "list.json"), json);
            await File.WriteAllTextAsync(Path.Combine(collection, "bad.json"), "{ broken");
            await File.WriteAllTextAsync(Path.Combine(root, "MyFavorites.json"), json);
            await File.WriteAllTextAsync(Path.Combine(root, "unrelated.json"), "{ private non-collection data");
            var service = new MajCollectionService();
            var result = await service.ReadDirectoryAsync(root, [track]);
            TestAssert.Equal(2, result.Playlists.Count, "A song root imports standard collections and portable favorites only.");
            TestAssert.Equal(1, result.Issues.Count, "A corrupt collection must not prevent other files from loading.");
            var favorite = result.Playlists.Single(playlist => playlist.IsFavorite);
            TestAssert.Equal(MajCollectionService.FavoritesPlaylistId, favorite.Id, "Favorites use one stable built-in identity.");
            TestAssert.Equal(1, favorite.TrackIds.Count, "Portable favorites must resolve chart hashes.");
            var direct = await service.ReadDirectoryAsync(collection, [track]);
            TestAssert.Equal(1, direct.Playlists.Count, "Selecting majcollection directly imports its own files without reading outside it.");
            TestAssert.True(direct.Playlists.All(playlist => !playlist.IsFavorite), "A regular collection name alone cannot imply favorites.");
        }

        /// <summary>Checks newly created playlists and refuses to silently omit unexportable music.</summary>
        /// <returns>A task representing new playlist export checks.</returns>
        private static async Task VerifyNewPlaylistAsync()
        {
            var service = new MajCollectionService();
            var local = Track("local", "Local", Hash("local chart"));
            var remoteHash = Hash("remote chart");
            var remote = new MusicTrack
            {
                Id = "remote",
                Title = "Remote",
                RemoteId = "remote-id",
                Revision = remoteHash,
                Audio = new MediaResource()
            };
            var playlist = new MusicPlaylist { Id = "new", Name = "New list", TrackIds = [remote.Id, local.Id] };
            await using var output = new MemoryStream();
            await service.WriteAsync(output, playlist, [local, remote]);
            using var document = JsonDocument.Parse(output.ToArray());
            TestAssert.True(document.RootElement.GetProperty("IsPlayList").GetBoolean(), "New playlists must emit Maj's playlist flag.");
            var hashes = document.RootElement.GetProperty("SongHashs").EnumerateArray().Select(element => element.GetString()).ToArray();
            TestAssert.True(hashes.SequenceEqual(new[] { remoteHash, local.MajChartHash }), "New lists export chart identities in playlist order.");

            await using var rejected = new MemoryStream();
            var audioOnly = Track("audio-only", "Audio only", null);
            await TestAssert.ThrowsAsync<InvalidDataException>(() => service.WriteAsync(rejected,
                playlist with { TrackIds = [audioOnly.Id] }, [audioOnly]));
            TestAssert.Equal(0L, rejected.Length, "Unsupported entries must be reported before writing any output.");
        }

        /// <summary>Checks malformed schemas and cancellation without changing source documents.</summary>
        /// <param name="root">The isolated source directory.</param>
        /// <returns>A task representing invalid-input checks.</returns>
        private static async Task VerifyInvalidAndCanceledAsync(string root)
        {
            Directory.CreateDirectory(root);
            var service = new MajCollectionService();
            var file = Path.Combine(root, "invalid.json");
            foreach (var json in new[]
            {
                "[]", "{}", "{\"Name\":\"A\",\"SongHashs\":null}",
                "{\"Name\":\"A\",\"SongHashs\":[5]}",
                "{\"Name\":\"A\",\"name\":\"B\",\"SongHashs\":[]}",
                "{\"Name\":\"A\",\"SongHashs\":[],\"IsPlayList\":0}"
            })
            {
                await File.WriteAllTextAsync(file, json);
                await TestAssert.ThrowsAsync<InvalidDataException>(() => service.ReadFileAsync(file, []));
                TestAssert.Equal(json, await File.ReadAllTextAsync(file), "Invalid source data remains untouched.");
            }

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => service.ReadFileAsync(file, [], canceled.Token));
            await using var output = new MemoryStream();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => service.WriteAsync(output,
                new MusicPlaylist { Id = "empty", Name = "Empty" }, [], canceled.Token));
            TestAssert.Equal(0L, output.Length, "A canceled export must not touch the destination.");
        }

        /// <summary>Builds one test music entry without performing filesystem access.</summary>
        /// <param name="id">The local test identity.</param>
        /// <param name="title">The test title.</param>
        /// <param name="hash">The exact portable chart identity.</param>
        /// <returns>The test entry.</returns>
        private static MusicTrack Track(string id, string title, string? hash)
        {
            return new MusicTrack { Id = id, Title = title, MajChartHash = hash, Audio = new MediaResource() };
        }

        /// <summary>Computes the same original-byte MD5 Base64 form used by Maj collections.</summary>
        /// <param name="content">The original fixture text.</param>
        /// <returns>The portable hash.</returns>
        private static string Hash(string content)
        {
            return Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(content)));
        }
    }
}
