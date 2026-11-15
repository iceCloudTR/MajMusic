#nullable enable

using System.Security.Cryptography;
using System.Text.Json;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Collections;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Tests
{
    /// <summary>Exercises local playlist membership and origin labels independently of online account ownership.</summary>
    internal static class PlaylistTests
    {
        /// <summary>Checks local, online and mixed labels, unavailable references and unrestricted local persistence.</summary>
        /// <returns>A task representing playlist classification and storage regressions.</returns>
        public static async Task RunAsync()
        {
            var local = Track("local-song", false);
            var online = Track("server-a-song", true);
            var otherAccount = Track("server-b-song", true) with { SourceId = "another-account" };
            var tracks = new[] { local, online, otherAccount }.ToDictionary(track => track.Id, StringComparer.Ordinal);
            var playlist = new MusicPlaylist { Id = "local-list", Name = "My playlist" };
            TestAssert.Equal(PlaylistContentKind.Empty, PlaylistContentClassifier.Classify(playlist, tracks), "An empty list has no origin");
            TestAssert.Equal(PlaylistContentKind.Local,
                PlaylistContentClassifier.Classify(playlist with { TrackIds = [local.Id] }, tracks), "Local files produce a local label");
            TestAssert.Equal(PlaylistContentKind.Online,
                PlaylistContentClassifier.Classify(playlist with { TrackIds = [online.Id, otherAccount.Id] }, tracks),
                "Downloaded online entries and entries from another account retain their online origin");

            var mixed = playlist with { TrackIds = [online.Id, local.Id, otherAccount.Id, local.Id] };
            var summary = PlaylistContentClassifier.GetSummary(mixed, tracks);
            TestAssert.Equal(PlaylistContentKind.Mixed, summary.Kind, "A local playlist can contain local and online entries");
            TestAssert.Equal(2, summary.LocalCount, "Repeated ordered local occurrences remain visible in counts");
            TestAssert.Equal(2, summary.OnlineCount, "Different source accounts contribute independent entries");
            TestAssert.Equal(0, summary.UnresolvedCount, "Available mixed members are all resolved");
            TestAssert.True(!mixed.IsCloud, "Online-origin music does not turn a local playlist into a cloud collection");

            var unavailable = mixed with
            {
                TrackIds = mixed.TrackIds.Append("removed-song").ToArray(),
                MajCollection = new MajCollectionDefinition { Entries = [new MajCollectionEntry { Hash = "unavailable-hash" }] }
            };
            var unknownSummary = PlaylistContentClassifier.GetSummary(unavailable, tracks);
            TestAssert.Equal(PlaylistContentKind.Mixed, unknownSummary.Kind, "Unknown references do not override known mixed origins");
            TestAssert.Equal(2, unknownSummary.UnresolvedCount, "Missing metadata and unresolved portable references remain distinct entries");
            TestAssert.Equal(PlaylistContentKind.Unresolved,
                PlaylistContentClassifier.Classify(unavailable with { TrackIds = Array.Empty<string>() }, tracks),
                "Unresolved portable references are not incorrectly labeled local or online");
            TestAssert.True((playlist with { EndpointId = "endpoint-a", RemoteId = "cloud-list" }).IsCloud,
                "Cloud ownership comes from remote list identity");

            var presented = mixed with { ContentKind = PlaylistContentKind.Mixed, UnresolvedCount = 3 };
            using (var document = JsonDocument.Parse(JsonSerializer.Serialize(presented)))
            {
                TestAssert.True(!document.RootElement.TryGetProperty("ContentKind", out _)
                    && !document.RootElement.TryGetProperty("UnresolvedCount", out _)
                    && !document.RootElement.TryGetProperty("IsCloud", out _), "Derived labels are not persisted as stale metadata");
            }

            var fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-playlists-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(fixture);
            try
            {
                var databasePath = Path.Combine(fixture, "library.db");
                var repository = new SqliteLibraryRepository(databasePath);
                await repository.UpsertTracksAsync(tracks.Values.ToArray());
                await repository.SavePlaylistAsync(mixed);
                var reopened = new SqliteLibraryRepository(databasePath);
                var restored = (await reopened.GetPlaylistsAsync()).Single();
                TestAssert.True(restored.TrackIds.SequenceEqual(mixed.TrackIds),
                    "A persisted local list retains arbitrary mixed sources, account identities, order and duplicates");
                TestAssert.Equal(PlaylistContentKind.Mixed,
                    PlaylistContentClassifier.Classify(restored, (await reopened.SearchAsync(null)).ToDictionary(track => track.Id, StringComparer.Ordinal)),
                    "A reopened local list receives a fresh mixed label");

                await using var export = new MemoryStream();
                await new MajCollectionService().WriteAsync(export, mixed, tracks.Values.ToArray());
                using var portable = JsonDocument.Parse(export.ToArray());
                var expected = mixed.TrackIds.Select(id => tracks[id].MajChartHash).ToArray();
                TestAssert.True(portable.RootElement.GetProperty("SongHashs").EnumerateArray().Select(value => value.GetString()).SequenceEqual(expected),
                    "Native Maj export preserves every portable mixed-source occurrence in playlist order");
            }
            finally
            {
                var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fixture.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(fixture).StartsWith("MajMusic-playlists-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the playlist fixture.");
                }

                Directory.Delete(fixture, true);
            }
        }

        /// <summary>Creates metadata without reading source files or making network requests.</summary>
        /// <param name="id">The stable library identity.</param>
        /// <param name="online">Whether the entry originated from a remote resource.</param>
        /// <returns>The test metadata with a portable chart identity and an intentionally nonexistent local path.</returns>
        private static MusicTrack Track(string id, bool online)
        {
            return new MusicTrack
            {
                Id = id,
                Title = id,
                RemoteId = online ? id : null,
                SourceId = online ? "endpoint-account" : "local",
                MajChartHash = Convert.ToBase64String(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(id))),
                Audio = new MediaResource { LocalPath = Path.Combine(Path.GetTempPath(), id + ".mp3") }
            };
        }
    }
}
