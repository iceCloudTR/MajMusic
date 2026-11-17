#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Collections;
using MajMusic.Infrastructure.Metadata;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;
using Microsoft.Data.Sqlite;

namespace MajMusic.Tests
{
    /// <summary>Checks independent media ownership, old shared-setting retirement and explicit source and playlist exchange boundaries.</summary>
    internal static class IndependentMediaStorageTests
    {
        /// <summary>Runs isolated migrations, foreign-file refusal, genuine cache downloads, cleanup and explicit Maj exchange.</summary>
        /// <returns>A task representing all independent storage regressions.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-independent-media-" + Guid.NewGuid().ToString("N")));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await ValidateRetiredSharedSettingsAsync(Path.Combine(root, "retired"), true, timeout.Token);
                await ValidateRetiredSharedSettingsAsync(Path.Combine(root, "absent"), false, timeout.Token);
                await ValidateManagedDownloadsAndCleanupAsync(Path.Combine(root, "owned"), timeout.Token);
                await ValidateIndependentDocumentsAsync(Path.Combine(root, "documents"), timeout.Token);
                await ValidateExplicitSourceAndExchangeAsync(Path.Combine(root, "explicit"), timeout.Token);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-independent-media-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Fixture cleanup must remain in its uniquely generated temporary directory.");
                }
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Ignores retired Play locations and rejects a persisted remote resource pointing at another application's file.</summary>
        /// <param name="root">The generated private and foreign roots.</param>
        /// <param name="existing">Whether a separate application already owns files and a database.</param>
        /// <param name="token">Cancels fixture I/O and local workspace operations.</param>
        /// <returns>A task representing the complete foreign-storage boundary.</returns>
        private static async Task ValidateRetiredSharedSettingsAsync(string root, bool existing, CancellationToken token)
        {
            var data = Path.Combine(root, "music");
            var foreign = Path.Combine(root, "other-application");
            var database = Path.Combine(foreign, "Runtime", "runtime.db");
            var audio = Path.Combine(foreign, "Cache", "Net", "Other source", "legacy-chart", "track.mp3");
            var image = Path.Combine(Path.GetDirectoryName(audio)!, "bg.jpg");
            Dictionary<string, FileFingerprint>? original = null;
            if (existing)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
                await File.WriteAllBytesAsync(audio, FixtureTransport.AudioBytes("foreign"), token);
                await File.WriteAllBytesAsync(image, FixtureTransport.ImageBytes, token);
                await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(audio)!, "maidata.txt"), "&title=Foreign legacy chart\n", token);
                Directory.CreateDirectory(Path.Combine(foreign, "CacheNet"));
                await File.WriteAllBytesAsync(Path.Combine(foreign, "CacheNet", "track.mp3"), FixtureTransport.AudioBytes("cache-net"), token);
                await CreateForeignDatabaseAsync(database, token);
                await CreateSongAsync(Path.Combine(foreign, "MaiCharts", "pack", "song"), token);
                await File.WriteAllTextAsync(Path.Combine(foreign, "MaiCharts", "MyFavorites.json"),
                    "{\"Name\":\"Other app favorites\",\"SongHashs\":[],\"IsPlayList\":true}", token);
                original = await SnapshotAsync(foreign, token);
            }

            Directory.CreateDirectory(data);
            var legacy = JsonSerializer.SerializeToNode(new AppSettings { Endpoints = [Endpoint()] }, CamelCase())!.AsObject();
            legacy["useMajdataPlayStorage"] = true;
            legacy["majdataPlayDirectory"] = foreign;
            legacy["mediaStorageDirectory"] = foreign;
            var settingsPath = Path.Combine(data, "settings.json");
            await File.WriteAllTextAsync(settingsPath, legacy.ToJsonString(), token);
            var track = RemoteTrack("stale") with
            {
                Audio = new MediaResource { LocalPath = audio, RemoteUri = new Uri(Endpoint().BaseUri, "maichart/stale/track"), Sha256 = Digest(FixtureTransport.AudioBytes("foreign")) },
                Cover = new MediaResource { LocalPath = image, RemoteUri = new Uri(Endpoint().BaseUri, "maichart/stale/image"), Sha256 = Digest(FixtureTransport.ImageBytes) }
            };
            await new SqliteLibraryRepository(Path.Combine(data, "library.db")).UpsertTracksAsync([track], token);
            using var transport = new FixtureTransport { RejectAll = true };
            using var client = new HttpClient(transport, false);
            FileStream? lockedDatabase = existing ? new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            try
            {
                using var workspace = new MusicWorkspace(data, client, client, credentialStore: new RejectCredentials());
                var settings = await workspace.LoadSettingsAsync(token);
                TestAssert.True(settings.MediaStorageDirectory is null && !workspace.IsOnline,
                    "An old shared-mode selection must retire to the independent Music root without enabling a source.");
                var found = (await workspace.SearchAsync(null, token)).Single();
                TestAssert.True(found.Id == track.Id && found.RemoteId == track.RemoteId && found.Audio.LocalPath is null
                    && found.Cover?.LocalPath is null, "Search must detach foreign remote-media paths without importing the other application's database or songs.");
                TestAssert.Equal(0, (await workspace.GetPlaylistsAsync(token)).Count,
                    "An old media setting cannot silently import another application's favorites or collections.");
                await using var thumbnails = new CoverThumbnailCache(workspace, token);
                using var card = new SongCardViewModel(track, thumbnails);
                await card.ActivateAsync().WaitAsync(token);
                TestAssert.True(!card.IsDownloaded && card.DownloadPercent == 0 && card.Track.Audio.LocalPath is null,
                    "A visible card must reject an old persisted Play path even when that foreign file still exists.");
                var reader = new CountingMetadataReader();
                await using var view = await CreateMetadataViewAsync(workspace, reader, data, token);
                var staleCard = view.CardTracks[0];
                staleCard.UpdateTrack(track);
                await view.EnsureBrowseMetadataAsync(staleCard, token);
                TestAssert.Equal(0, reader.Calls, "A stale visible remote card must not hand the other application's file to the native metadata reader.");
                await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.ResolveAudioAsync(track, false, token));
                var removed = await workspace.RemoveTrackMediaAsync(track, token);
                TestAssert.True(!removed.AudioRemoved && !removed.CoverRemoved && removed.DeletedFileCount == 0,
                    "Explicit local cleanup must never remove an unowned foreign audio or cover file.");
                await workspace.ClearCacheAsync(token);
                await workspace.SavePlaylistAsync(new MusicPlaylist { Id = "independent-list", Name = "Private list", TrackIds = [track.Id] }, token);
                TestAssert.True(!Directory.Exists(Path.Combine(data, "MaiCharts")),
                    "Saving a local playlist must only update Music's database, without automatic Maj mirrors.");
                TestAssert.Equal(0, transport.Requests.Count, "Legacy retirement and foreign-file refusal must remain entirely offline.");
            }
            finally
            {
                lockedDatabase?.Dispose();
            }
            if (original is not null)
            {
                AssertSameSnapshot(original, await SnapshotAsync(foreign, token), "Old settings cannot read into Music's catalog or modify any foreign database, cache or source.");
            }
            else
            {
                TestAssert.True(!Directory.Exists(foreign), "Retiring an absent external root must not create its folders, database or cache.");
            }

            legacy["useMajdataPlayStorage"] = false;
            await File.WriteAllTextAsync(settingsPath, legacy.ToJsonString(), token);
            TestAssert.True((await new JsonSettingsStore(settingsPath).LoadAsync(token)).MediaStorageDirectory is null,
                "A legacy media path equal to the removed shared root must also retire when its mode flag was false.");
            legacy["unrecognizedFixtureOption"] = true;
            await File.WriteAllTextAsync(settingsPath, legacy.ToJsonString(), token);
            await TestAssert.ThrowsAsync<InvalidDataException>(() => new JsonSettingsStore(settingsPath).LoadAsync(token));
        }

        /// <summary>Downloads through actual Music ownership records, reuses files after restart and exercises individual and age-based cleanup.</summary>
        /// <param name="root">The generated Music configuration and media roots.</param>
        /// <param name="token">Cancels generated network and local operations.</param>
        /// <returns>A task representing complete independent local-media lifetime.</returns>
        private static async Task ValidateManagedDownloadsAndCleanupAsync(string root, CancellationToken token)
        {
            var data = Path.Combine(root, "configuration");
            var media = Path.Combine(root, "media");
            using var transport = new FixtureTransport();
            using var client = new HttpClient(transport, false);
            var tracks = new List<MusicTrack>();
            var playlist = new MusicPlaylist { Id = "independent-downloads", Name = "Downloaded songs" };
            using (var workspace = new MusicWorkspace(data, client, client, credentialStore: new RejectCredentials()))
            {
                var settings = await workspace.LoadSettingsAsync(token);
                await workspace.SaveSettingsAsync(settings with { OnlineEnabled = true, Endpoints = [Endpoint()], MediaStorageDirectory = media }, token);
                foreach (var id in new[] { "manual", "old", "middle", "recent" })
                {
                    var track = RemoteTrack(id);
                    await new SqliteLibraryRepository(Path.Combine(data, "library.db")).UpsertTracksAsync([track], token);
                    var path = await workspace.ResolveAudioAsync(track, true, token);
                    var stored = (await workspace.SearchAsync(track.Title, token)).Single();
                    TestAssert.True(IsInside(path, Path.Combine(media, "Cache")) && File.Exists(path)
                        && Digest(await File.ReadAllBytesAsync(path, token)) == Digest(FixtureTransport.AudioBytes(id)),
                        "Every downloaded audio must be a complete object in the configured Music cache, with verified fixture bytes.");
                    TestAssert.True(stored.Cover?.LocalPath is { } cover && IsInside(cover, Path.Combine(media, "Cache")) && File.Exists(cover),
                        "Explicit downloads must retain their cover in the same independently managed media root.");
                    tracks.Add(stored);
                }
                playlist = playlist with { TrackIds = tracks.Select(track => track.Id).ToArray() };
                await workspace.SavePlaylistAsync(playlist, token);
                await workspace.UpdateOnlineEnabledAsync(false, token);
            }
            TestAssert.True(!File.Exists(Path.Combine(media, "Runtime", "runtime.db")) && !Directory.Exists(Path.Combine(media, "MaiCharts")),
                "Normal Music downloads and playlists must not create Play database or automatic collection hierarchy.");
            var requests = transport.Requests.Count;
            transport.RejectAll = true;
            using var reopened = new MusicWorkspace(data, client, client, credentialStore: new RejectCredentials());
            await reopened.LoadSettingsAsync(token);
            foreach (var track in tracks)
            {
                TestAssert.Equal(track.Audio.LocalPath, await reopened.ResolveAudioAsync(track, false, token), "A restarted offline workspace reuses its own completed audio.");
                TestAssert.Equal(track.Cover!.LocalPath, await reopened.ResolveCoverAsync(track, token), "A restarted offline workspace reuses its own retained cover.");
            }
            TestAssert.Equal(requests, transport.Requests.Count, "Independent offline cache reuse must perform no new HTTP request.");
            var manual = tracks.Single(track => track.RemoteId == "manual");
            var deleted = await reopened.RemoveTrackMediaAsync(manual, token);
            TestAssert.True(deleted.AudioRemoved && !File.Exists(manual.Audio.LocalPath)
                && (await reopened.GetPlaylistsAsync(token)).Single().TrackIds.SequenceEqual(playlist.TrackIds),
                "Deleting one managed local file must preserve the song's metadata and playlist membership.");

            var repository = new SqliteLibraryRepository(Path.Combine(data, "library.db"));
            var ages = new Dictionary<string, int> { ["old"] = 40, ["middle"] = 10, ["recent"] = 1 };
            await repository.UpsertTracksAsync(tracks.Where(track => ages.ContainsKey(track.RemoteId!))
                .Select(track => track with { LocalMediaSavedAt = DateTimeOffset.UtcNow.AddDays(-ages[track.RemoteId!]) }).ToArray(), token);
            await using var view = new MainViewModel(reopened, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await view.InitializeAsync().WaitAsync(token);
            TestAssert.True(tracks.Where(track => ages.ContainsKey(track.RemoteId!)).All(track => File.Exists(track.Audio.LocalPath)),
                "Old complete music must survive startup until an explicit cleanup command is executed.");
            foreach (var days in new int?[] { 30, 7, null })
            {
                view.SelectedLocalMediaCleanup = view.LocalMediaCleanupChoices.Single(choice => choice.Days == days);
                await TestAssert.ExecuteAsync(view.CleanupLocalMediaCommand).WaitAsync(token);
                var expected = days == 30 ? new[] { "middle", "recent" } : days == 7 ? new[] { "recent" } : Array.Empty<string>();
                var remaining = (await reopened.SearchAsync(null, token)).Where(track => track.Audio.LocalPath is { } path && File.Exists(path))
                    .Select(track => track.RemoteId!).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                TestAssert.True(remaining.SequenceEqual(expected), "Manual cleanup must remove precisely the chosen age range and retain newer managed audio.");
            }
            TestAssert.True((await reopened.SearchAsync(null, token)).Count == 4
                && (await reopened.GetPlaylistsAsync(token)).Single().TrackIds.SequenceEqual(playlist.TrackIds),
                "All-file cleanup must preserve the complete searchable catalog and its ordered playlist references.");
            TestAssert.Equal(requests, transport.Requests.Count, "File cleanup cannot trigger online fetches or re-download deleted media.");
        }

        /// <summary>Reuses Music-owned documents across charts without reading or deleting an unclaimed provider file.</summary>
        /// <param name="root">The generated private configuration root.</param>
        /// <param name="token">Cancels generated provider and HTTP operations.</param>
        /// <returns>A task representing independent SAF publication and ownership boundaries.</returns>
        private static async Task ValidateIndependentDocumentsAsync(string root, CancellationToken token)
        {
            var documents = new MemoryMediaDocuments();
            using var transport = new FixtureTransport { RejectAll = true };
            using var client = new HttpClient(transport, false);
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: new RejectCredentials(),
                documentStorage: documents, mediaStorageAccess: documents);
            var settings = await workspace.LoadSettingsAsync(token);
            await workspace.SaveSettingsAsync(settings with { Endpoints = [Endpoint()], MediaStorageDirectory = MemoryMediaDocuments.Root }, token);
            var repository = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
            var foreign = RemoteTrack("foreign-document") with { Cover = null };
            foreign = foreign with { Audio = foreign.Audio with { LocalPath = MemoryMediaDocuments.ForeignReference } };
            await repository.UpsertTracksAsync([foreign], token);
            var filtered = await workspace.ResolveLocalTrackAsync(foreign, token);
            TestAssert.True(filtered.Audio.LocalPath is null && filtered.Audio.ManagedStorageRoot is null,
                "A raw provider URI without a Music ownership marker cannot become managed local audio.");
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.ResolveAudioAsync(foreign, false, token));
            var removed = await workspace.RemoveTrackMediaAsync(foreign, token);
            TestAssert.True(!removed.AudioRemoved && removed.DeletedFileCount == 0
                && documents.ForeignReadCount == 0 && documents.ForeignDeleteCount == 0,
                "Rejected provider references must not be opened or deleted even when the selected provider grants access.");

            transport.RejectAll = false;
            await workspace.UpdateOnlineEnabledAsync(true, token);
            var first = RemoteTrack("document-first") with { Cover = null };
            var second = RemoteTrack("document-second") with { Cover = null };
            await repository.UpsertTracksAsync([first, second], token);
            var firstReference = await workspace.ResolveAudioAsync(first, false, token);
            var secondReference = await workspace.ResolveAudioAsync(second, false, token);
            var stored = await workspace.SearchAsync("Generated document-", token);
            TestAssert.True(first.RemoteId != second.RemoteId && first.Audio.Sha256 == second.Audio.Sha256
                && firstReference == secondReference && documents.PublishCount == 1
                && transport.Requests.Count(path => path.EndsWith("/track", StringComparison.Ordinal)) == 1,
                "Two charts sharing audio must reuse the first Music-owned document without a second transfer or publication.");
            TestAssert.True(stored.Count == 2 && stored.All(track => track.Audio.LocalPath == firstReference
                && track.Audio.ManagedStorageRoot == MemoryMediaDocuments.Root),
                "Each chart must retain its own private ownership claim for the shared provider document.");
            var requests = transport.Requests.Count;
            await workspace.UpdateOnlineEnabledAsync(false, token);
            transport.RejectAll = true;
            foreach (var track in stored)
            {
                TestAssert.Equal(firstReference, await workspace.ResolveAudioAsync(track, false, token),
                    "Owned provider audio must remain available offline for each distinct chart.");
            }
            TestAssert.True(transport.Requests.Count == requests && documents.PublishCount == 1
                && documents.ForeignReadCount == 0 && documents.ForeignDeleteCount == 0 && documents.ScanCount == 0,
                "Provider reuse must stay offline and must never discover, read or modify unclaimed documents.");
        }

        /// <summary>Keeps explicit local imports read-only while retaining standard Maj JSON as an intentional export operation.</summary>
        /// <param name="root">The generated user source, configuration and export directories.</param>
        /// <param name="token">Cancels explicit local operations.</param>
        /// <returns>A task representing read-only source preservation and compatible playlist roundtrip.</returns>
        private static async Task ValidateExplicitSourceAndExchangeAsync(string root, CancellationToken token)
        {
            var source = Path.Combine(root, "user-source");
            var song = Path.Combine(source, "nested", "chart");
            await CreateSongAsync(song, token);
            var original = await SnapshotAsync(source, token);
            var data = Path.Combine(root, "music");
            using var transport = new FixtureTransport { RejectAll = true };
            using var client = new HttpClient(transport, false);
            using var workspace = new MusicWorkspace(data, client, client, credentialStore: new RejectCredentials());
            await workspace.LoadSettingsAsync(token);
            var track = (await workspace.IndexDirectoryAsync(source, token)).Tracks.Single();
            TestAssert.True(track.Title == "Explicit chart title" && track.RemoteId is null && track.MajChartHash is not null,
                "Only an explicit import may index original standard chart files and their canonical text title.");
            TestAssert.Equal(Path.Combine(song, "track.mp3"), await workspace.ResolveAudioAsync(track, false, token), "Imported local playback must keep the exact source reference.");
            var reader = new CountingMetadataReader();
            await using var view = await CreateMetadataViewAsync(workspace, reader, data, token);
            TestAssert.True(reader.Calls > 0 && view.Tracks.Single().Duration == TimeSpan.FromSeconds(131),
                "Explicit local imports must retain normal local metadata parsing after the remote-path ownership guard is applied.");
            var playlist = new MusicPlaylist { Id = Guid.NewGuid().ToString("N"), Name = "Explicit portable list", TrackIds = [track.Id] };
            await workspace.SavePlaylistAsync(playlist, token);
            TestAssert.True(!Directory.Exists(Path.Combine(data, "MaiCharts")), "Saving a local list cannot create an automatic Maj playlist mirror.");
            var export = Path.Combine(root, "exports", "selected-list.json");
            var exchange = new MajCollectionExchange(workspace);
            await exchange.ExportAsync(export, playlist, token);
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(export, token)))
            {
                TestAssert.True(document.RootElement.GetProperty("Name").GetString() == playlist.Name
                    && document.RootElement.GetProperty("SongHashs").EnumerateArray().Single().GetString() == track.MajChartHash
                    && document.RootElement.GetProperty("IsPlayList").GetBoolean(),
                    "An explicitly exported list must contain the standard Maj fields and the original chart hash.");
            }
            var roundtrip = await new MajCollectionService().ReadFileAsync(export, [track], token);
            TestAssert.True(roundtrip.TrackIds.SequenceEqual([track.Id]), "An explicit standard Maj export must resolve back to the same local song.");
            await workspace.ClearCacheAsync(token);
            await workspace.DeletePlaylistAsync(playlist, token);
            AssertSameSnapshot(original, await SnapshotAsync(source, token), "Playback, local cleanup and playlist deletion must leave imported source files untouched.");
            TestAssert.True(File.Exists(export) && transport.Requests.Count == 0, "Explicit exports remain user-owned documents and never require a source request.");
        }

        /// <summary>Creates ordinary standard chart files as an external user's original import source.</summary>
        /// <param name="directory">The generated original song directory.</param>
        /// <param name="token">Cancels fixture writes.</param>
        /// <returns>A task representing complete fixture source creation.</returns>
        private static async Task CreateSongAsync(string directory, CancellationToken token)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "track.mp3"), "original imported audio"u8.ToArray(), token);
            await File.WriteAllTextAsync(Path.Combine(directory, "maidata.txt"), "&title=Explicit chart title\n&artist=Generated artist\n&inote_1=(120){4}1,E\n", token);
            await File.WriteAllBytesAsync(Path.Combine(directory, "bg.png"), FixtureTransport.ImageBytes, token);
        }

        /// <summary>Uses the production file-fingerprint cache with a counting native-reader substitute.</summary>
        /// <param name="workspace">The real independently owned local workspace.</param>
        /// <param name="reader">The observed local-only reader.</param>
        /// <param name="data">The generated private metadata-cache root.</param>
        /// <param name="token">Cancels local initialization.</param>
        /// <returns>The initialized presentation owned by the caller.</returns>
        private static async Task<MainViewModel> CreateMetadataViewAsync(MusicWorkspace workspace, CountingMetadataReader reader, string data, CancellationToken token)
        {
            var metadata = new CachedTrackMetadataProvider(reader, Path.Combine(data, "MetadataFixture"));
            var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action(), metadata: metadata);
            await view.InitializeAsync().WaitAsync(token);
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate.WaitAsync(token);
            await view.MetadataRefresh.WaitAsync(token);
            return view;
        }

        /// <summary>Creates an unrelated database containing an enticing public chart row that must never be imported.</summary>
        /// <param name="path">The generated foreign database path.</param>
        /// <param name="token">Cancels fixture construction.</param>
        /// <returns>A task representing the closed sentinel database.</returns>
        private static async Task CreateForeignDatabaseAsync(string path, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=DELETE;
                CREATE TABLE OnlineServers(Endpoint TEXT,Server TEXT);
                CREATE TABLE OnlineChartCache(ChartId TEXT,Server TEXT,ChartHash TEXT,Detail TEXT,TrackSha256 TEXT);
                INSERT INTO OnlineServers VALUES('https://independent.example.test/api/','independent-fixture');
                INSERT INTO OnlineChartCache VALUES('foreign-db-only','independent-fixture','revision-foreign','{"id":"foreign-db-only","title":"Foreign database-only song","hash":"revision-foreign"}',NULL);
                """;
            await command.ExecuteNonQueryAsync(token);
        }

        /// <summary>Reads exact source fingerprints without collecting any real user data.</summary>
        /// <param name="root">The generated external source tree.</param>
        /// <param name="token">Cancels fixture reading.</param>
        /// <returns>Relative names, lengths, write times and SHA-256 digests for every generated file.</returns>
        private static async Task<Dictionary<string, FileFingerprint>> SnapshotAsync(string root, CancellationToken token)
        {
            var result = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                result.Add(Path.GetRelativePath(root, file), new FileFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, Digest(await File.ReadAllBytesAsync(file, token))));
            }
            return result;
        }

        /// <summary>Compares generated external trees including new sidecars and removed files.</summary>
        /// <param name="expected">The original generated files.</param>
        /// <param name="actual">The files after application operations.</param>
        /// <param name="message">The intended ownership boundary.</param>
        private static void AssertSameSnapshot(IReadOnlyDictionary<string, FileFingerprint> expected, IReadOnlyDictionary<string, FileFingerprint> actual, string message)
        {
            TestAssert.True(expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value), message);
        }

        /// <summary>Creates a generated endpoint without remembered accounts or credentials.</summary>
        /// <returns>The synthetic source identity.</returns>
        private static EndpointConfiguration Endpoint() => new() { Id = "independent-slot", Name = "Generated source", ServerId = "independent-fixture", BaseUri = new Uri("https://independent.example.test/api/") };
        /// <summary>Creates a lazy online entry with complete fixture resource descriptors.</summary>
        /// <param name="id">The synthetic chart identifier.</param>
        /// <returns>The independently scoped music entry.</returns>
        private static MusicTrack RemoteTrack(string id) => new()
        {
            Id = "independent:" + id, RemoteId = id, SourceId = Endpoint().ServerId!, EndpointId = Endpoint().Id,
            Revision = "revision-" + id, MajChartHash = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(id))),
            Title = "Generated " + id, Duration = TimeSpan.FromSeconds(90),
            Audio = new MediaResource { RemoteUri = new Uri(Endpoint().BaseUri, "maichart/" + id + "/track"), Sha256 = Digest(FixtureTransport.AudioBytes(id)), Length = FixtureTransport.AudioBytes(id).LongLength, CacheVersion = "revision-" + id },
            Cover = new MediaResource { RemoteUri = new Uri(Endpoint().BaseUri, "maichart/" + id + "/image"), Sha256 = Digest(FixtureTransport.ImageBytes), Length = FixtureTransport.ImageBytes.LongLength, CacheVersion = "revision-" + id }
        };
        /// <summary>Computes a fixture's actual content digest.</summary>
        /// <param name="bytes">The generated complete file bytes.</param>
        /// <returns>The lowercase SHA-256 identity.</returns>
        private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        /// <summary>Checks exact ordinary filesystem containment.</summary>
        /// <param name="path">The produced media path.</param>
        /// <param name="root">The expected independent cache root.</param>
        /// <returns>Whether the produced path remains inside that cache.</returns>
        private static bool IsInside(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        /// <summary>Matches the actual saved application preference naming policy.</summary>
        /// <returns>Options for generated legacy preference documents.</returns>
        private static JsonSerializerOptions CamelCase() => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        /// <summary>Retains exact generated external-file state.</summary>
        /// <param name="Length">The complete byte count.</param>
        /// <param name="WriteTicks">The original UTC last-write time.</param>
        /// <param name="Sha256">The complete content digest.</param>
        private sealed record FileFingerprint(long Length, long WriteTicks, string Sha256);

        /// <summary>Observes whether an actual file would reach a native metadata parser without loading any codec.</summary>
        private sealed class CountingMetadataReader : IAudioMetadataReader
        {
            /// <summary>Gets the number of real file parses requested through the production cache.</summary>
            public int Calls { get; private set; }
            /// <inheritdoc />
            public Task<AudioMetadata> ReadAsync(string localPath, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls++;
                return Task.FromResult(new AudioMetadata { Duration = TimeSpan.FromSeconds(131), AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 128000 } });
            }
        }

        /// <summary>Serves generated resources without sockets, real logins or user files.</summary>
        private sealed class FixtureTransport : HttpMessageHandler
        {
            /// <summary>Retains only generated request paths.</summary>
            public ConcurrentQueue<string> Requests { get; } = new();
            /// <summary>Gets or sets whether all requests must fail after offline reuse begins.</summary>
            public bool RejectAll { get; set; }
            /// <summary>Gets a complete generated one-pixel cover.</summary>
            public static byte[] ImageBytes { get; } = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jEuoAAAAASUVORK5CYII=");
            /// <summary>Creates unique complete bytes for one generated audio resource.</summary>
            /// <param name="id">The fixture music identity.</param>
            /// <returns>The owned synthetic audio bytes, never decoded or played.</returns>
            public static byte[] AudioBytes(string id) => Encoding.UTF8.GetBytes("independently owned audio resource "
                + (id is "document-first" or "document-second" ? "shared-document-content" : id));
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = request.RequestUri!.AbsolutePath;
                Requests.Enqueue(path);
                if (RejectAll)
                {
                    throw new InvalidOperationException("An offline independent-storage operation attempted HTTP.");
                }
                if (path == "/api/info")
                {
                    return Task.FromResult(Json(new { id = "independent-fixture", name = "Generated source" }));
                }
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 && parts[0] == "api" && parts[1] == "maichart")
                {
                    var id = parts[2];
                    if (parts[3] == "info")
                    {
                        return Task.FromResult(Json(new
                        {
                            metadata = new { id, title = "Generated " + id, hash = "revision-" + id },
                            resources = new { track = new { sha256 = Digest(AudioBytes(id)), length = AudioBytes(id).Length }, thumbnail = new { sha256 = Digest(ImageBytes), length = ImageBytes.Length } }
                        }));
                    }
                    if (parts[3] is "track" or "image")
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(parts[3] == "track" ? AudioBytes(id) : ImageBytes) });
                    }
                }
                throw new InvalidOperationException("Unexpected generated independent-storage request.");
            }
            /// <summary>Serializes one generated API response.</summary>
            /// <param name="value">The nonsecret response fixture.</param>
            /// <returns>The owned JSON HTTP response.</returns>
            private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        }

        /// <summary>Provides synthetic writable media and explicitly readable documents without native storage or user files.</summary>
        private sealed class MemoryMediaDocuments : IMediaStorageAccess, IReadOnlyDocumentStorage
        {
            /// <summary>Identifies the selected synthetic writable tree.</summary>
            public const string Root = "content://independent-storage.example/tree/music";
            /// <summary>Identifies an accessible file which Music has never published or claimed.</summary>
            public const string ForeignReference = Root + "/document/foreign-audio";
            /// <summary>Stores complete bytes by opaque provider reference.</summary>
            private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
            /// <summary>Maps immutable relative object names to provider references.</summary>
            private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.Ordinal);
            /// <summary>Gets the number of completed Music publications.</summary>
            public int PublishCount { get; private set; }
            /// <summary>Gets attempts to open the unclaimed file.</summary>
            public int ForeignReadCount { get; private set; }
            /// <summary>Gets attempts to delete the unclaimed file.</summary>
            public int ForeignDeleteCount { get; private set; }
            /// <summary>Gets unrequested automatic provider scans.</summary>
            public int ScanCount { get; private set; }
            /// <summary>Creates a readable foreign file without an ownership marker.</summary>
            public MemoryMediaDocuments()
            {
                var bytes = FixtureTransport.AudioBytes("foreign-document");
                var hash = Digest(bytes);
                _files[ForeignReference] = bytes;
                _paths["Cache/Objects/" + hash[..2] + "/" + hash] = ForeignReference;
            }
            /// <inheritdoc />
            public bool CanHandle(string? reference) => reference?.StartsWith("content://", StringComparison.Ordinal) == true;
            /// <inheritdoc />
            public Task<string?> FindAsync(string root, string relativePath, bool directory, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TestAssert.Equal(Root, root, "Provider operations must stay inside the explicitly selected Music root.");
                var path = relativePath.Replace('\\', '/');
                TestAssert.True(!path.StartsWith("/", StringComparison.Ordinal) && !path.Split('/').Contains(".."),
                    "Provider operations cannot escape the selected root.");
                return Task.FromResult(!directory && _paths.TryGetValue(path, out var reference) ? reference : null);
            }
            /// <inheritdoc />
            public Task<Stream> OpenReadAsync(string reference, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reference == ForeignReference)
                {
                    ForeignReadCount++;
                }
                return Task.FromResult<Stream>(new MemoryStream(_files[reference], false));
            }
            /// <inheritdoc />
            public async Task<string> PublishFileAsync(string root, string relativePath, string localSourcePath, CancellationToken cancellationToken = default)
            {
                var existing = await FindAsync(root, relativePath, false, cancellationToken);
                TestAssert.True(existing is null, "A Music publication cannot overwrite an existing provider object.");
                var bytes = await File.ReadAllBytesAsync(localSourcePath, cancellationToken);
                var reference = Root + "/document/" + Digest(bytes);
                TestAssert.True(_paths.TryAdd(relativePath.Replace('\\', '/'), reference) && _files.TryAdd(reference, bytes),
                    "One immutable object must only be published once.");
                PublishCount++;
                return reference;
            }
            /// <inheritdoc />
            public async Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
            {
                var reference = await FindAsync(root, relativePath, false, cancellationToken);
                if (reference == ForeignReference)
                {
                    ForeignDeleteCount++;
                }
                if (reference is not null)
                {
                    _paths.TryRemove(relativePath.Replace('\\', '/'), out _);
                    _files.TryRemove(reference, out _);
                }
            }
            /// <inheritdoc />
            public async Task<string> ResolveAudioAsync(string reference, CancellationToken cancellationToken = default)
            {
                await using var stream = await OpenReadAsync(reference, cancellationToken);
                return reference;
            }
            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MediaResource resource, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
            /// <inheritdoc />
            public Task<LocalLibraryScanResult> ScanAsync(string directory, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanCount++;
                return Task.FromResult(new LocalLibraryScanResult { RootDirectory = directory });
            }
            /// <inheritdoc />
            public Task<DocumentFileReference> GetFileReferenceAsync(string reference, CancellationToken cancellationToken = default)
                => Task.FromResult(new DocumentFileReference(reference, "generated-audio.bin"));
            /// <inheritdoc />
            public async IAsyncEnumerable<DocumentFileReference> EnumerateCollectionFilesAsync(string directory, bool includeDirectFiles,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                yield break;
            }
        }

        /// <summary>Rejects every attempt to access an operating-system credential vault.</summary>
        private sealed class RejectCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Generated storage fixtures cannot access credentials.");
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Generated storage fixtures cannot access credentials.");
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Generated storage fixtures cannot access credentials.");
        }
    }
}
