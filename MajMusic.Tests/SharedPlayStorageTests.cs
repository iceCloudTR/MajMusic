#nullable enable

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;
using Microsoft.Data.Sqlite;

namespace MajMusic.Tests
{
    /// <summary>Checks offline discovery of files completed by a separate MajdataPlay process after Music has already started.</summary>
    internal static class SharedPlayStorageTests
    {
        /// <summary>Runs an isolated external-download regression without using user configuration, accounts or audio playback.</summary>
        /// <returns>A task representing discovery, source ownership and real card availability.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-shared-play-" + Guid.NewGuid().ToString("N"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var game = Path.Combine(root, "game");
                var data = Path.Combine(root, "music");
                Directory.CreateDirectory(game);
                var maidata = Encoding.UTF8.GetBytes("&title=共享课程讲数学\n&artist=Offline fixture\n&first=0\n&lv_1=1\n&inote_1=(120){4}1,2,3,4,E\n");
                var chartHash = Convert.ToBase64String(MD5.HashData(maidata));
                var endpoint = new EndpointConfiguration
                {
                    Id = "music-slot",
                    Name = "Different Music Endpoint Name",
                    BaseUri = new Uri("https://shared-play.example.test/api/"),
                    ServerId = "shared-server"
                };
                var track = new MusicTrack
                {
                    Id = "shared-play-song",
                    Title = "共享课程讲数学",
                    Artist = "Offline fixture",
                    SourceId = endpoint.ServerId!,
                    EndpointId = endpoint.Id,
                    RemoteId = "5ced5c30-505d-47a5-9a88-f0f027b611f8",
                    Revision = chartHash,
                    MajChartHash = chartHash,
                    Audio = new MediaResource
                    {
                        RemoteUri = new Uri(endpoint.BaseUri, "maichart/5ced5c30-505d-47a5-9a88-f0f027b611f8/track"),
                        CacheVersion = chartHash
                    }
                };
                using var transport = new RejectNetworkHandler();
                using var client = new HttpClient(transport, false);
                var credentials = new RejectCredentials();
                using var workspace = await CreateWorkspaceAsync(data,
                    new AppSettings { UseMajdataPlayStorage = true, MajdataPlayDirectory = game, Endpoints = [endpoint] },
                    track, client, credentials, timeout.Token);
                var initial = (await workspace.SearchAsync("讲数学", timeout.Token)).Single();
                TestAssert.True(initial.Audio.LocalPath is null && initial.Audio.Sha256 is null,
                    "Music starts with a cloud-only descriptor and no completed game media.");

                var thumbnailWorkspace = CreateThumbnailWorkspace();
                await using var thumbnails = new CoverThumbnailCache(thumbnailWorkspace, timeout.Token);
                using var card = new SongCardViewModel(initial, thumbnails);
                await card.ActivateAsync();
                TestAssert.True(!card.IsDownloaded, "The already-visible real card starts without local audio.");

                // Simulate a different application completing a legacy download, without restarting or rescanning Music.
                var legacy = Path.Combine(game, "Cache", "Net", "MajdataNET", BitConverter.ToString(Convert.FromBase64String(chartHash)));
                Directory.CreateDirectory(legacy);
                var chartPath = Path.Combine(legacy, "maidata.txt");
                var audioPath = Path.Combine(legacy, "track.mp3");
                var audioBytes = "complete independently downloaded fixture audio"u8.ToArray();
                await File.WriteAllBytesAsync(chartPath, maidata, timeout.Token);
                await File.WriteAllBytesAsync(audioPath, audioBytes, timeout.Token);
                var expectedSha = Convert.ToHexStringLower(SHA256.HashData(audioBytes));
                var originalAudio = await FingerprintAsync(audioPath, timeout.Token);
                var originalChart = await FingerprintAsync(chartPath, timeout.Token);
                TestAssert.True(!File.Exists(audioPath + ".sha256"), "The game fixture has no application-specific SHA sidecar.");

                foreach (var query in new[] { track.Title, "讲数学" })
                {
                    var discovered = (await workspace.SearchAsync(query, timeout.Token)).Single();
                    AssertDiscovery(track, discovered, audioPath, expectedSha);
                    card.UpdateTrack(discovered);
                    await card.ResourceCompletion;
                    AssertLocalCard(card);
                }

                TestAssert.True(originalAudio == await FingerprintAsync(audioPath, timeout.Token)
                    && originalChart == await FingerprintAsync(chartPath, timeout.Token),
                    "Discovering a different process's completed download must not rewrite its audio or maidata.");
                TestAssert.True(!File.Exists(audioPath + ".sha256") && !File.Exists(Path.Combine(game, "Runtime", "runtime.db")),
                    "Read-only legacy discovery cannot create sidecars or register metadata into the game's database.");
                TestAssert.True(!File.Exists(Path.Combine(data, "Cache", "Objects", expectedSha[..2], expectedSha)),
                    "Search must reference the game's original audio instead of making another private copy.");
                TestAssert.Equal(0, transport.RequestCount, "Discovery and card activation must perform no HTTP.");
                TestAssert.Equal(0, credentials.Calls, "Offline shared-media discovery cannot access a credential vault.");
                await ValidateRejectedLegacyAsync(Path.Combine(root, "wrong-maidata"), true, timeout.Token);
                await ValidateRejectedLegacyAsync(Path.Combine(root, "unfinished-chunk"), false, timeout.Token);
                await ValidateAmbiguousLegacyAsync(Path.Combine(root, "ambiguous"), timeout.Token);
                await ValidateFileOnlyPublicationAsync(Path.Combine(root, "existing-play-database"), true, timeout.Token);
                await ValidateFileOnlyPublicationAsync(Path.Combine(root, "absent-play-database"), false, timeout.Token);
            }
            finally
            {
                DeleteGeneratedFixture(root);
            }
        }

        /// <summary>Publishes complete offline media through the real audio path without registering anything in another application's database.</summary>
        /// <param name="root">The generated publication fixture.</param>
        /// <param name="existingDatabase">Whether Play already owns a compatible database with unrelated sentinel data.</param>
        /// <param name="token">Cancels generated-file setup and media resolution.</param>
        /// <returns>A task representing first publication, reuse and independent database ownership.</returns>
        private static async Task ValidateFileOnlyPublicationAsync(string root, bool existingDatabase, CancellationToken token)
        {
            var (endpoint, cloud, maidata) = CreateLegacySeed();
            var game = Path.Combine(root, "game");
            var data = Path.Combine(root, "music");
            Directory.CreateDirectory(game);
            var database = Path.Combine(game, "Runtime", "runtime.db");
            PlayDatabaseFingerprint? original = null;
            if (existingDatabase)
            {
                await CreatePlaySentinelDatabaseAsync(database, token);
                original = await CapturePlayDatabaseAsync(database, token);
            }

            var audioBytes = "complete private audio ready for file-only prefetch"u8.ToArray();
            var coverBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jEuoAAAAASUVORK5CYII=");
            var audio = await WritePrivateMediaAsync(data, cloud.Audio, audioBytes, token);
            var cover = await WritePrivateMediaAsync(data, new MediaResource
            {
                RemoteUri = new Uri(endpoint.BaseUri, "maichart/" + cloud.RemoteId + "/image"),
                CacheVersion = cloud.Revision
            }, coverBytes, token);
            var chart = await WritePrivateMediaAsync(data, new MediaResource
            {
                RemoteUri = new Uri(endpoint.BaseUri, "maichart/" + cloud.RemoteId + "/chart"),
                CacheVersion = cloud.Revision
            }, maidata, token);
            var seed = cloud with { Audio = audio, Cover = cover, Chart = chart };
            using var transport = new RejectNetworkHandler();
            using var client = new HttpClient(transport, false);
            var credentials = new RejectCredentials();
            using var workspace = await CreateWorkspaceAsync(data,
                new AppSettings { UseMajdataPlayStorage = true, MajdataPlayDirectory = game, Endpoints = [endpoint] },
                seed, client, credentials, token);

            var resolved = await workspace.ResolveAudioAsync(seed, true, token);
            var stored = await new SqliteLibraryRepository(Path.Combine(data, "library.db")).FindTrackAsync(seed.Id, token)
                ?? throw new InvalidOperationException("Music must retain its own local-media index.");
            var expectedAudio = CanonicalFixturePath(game, audio.Sha256!);
            TestAssert.True(SamePath(resolved, expectedAudio) && stored.Audio.LocalPath is { } saved && SamePath(saved, expectedAudio)
                && stored.Audio.Sha256 == audio.Sha256 && stored.LocalMediaSavedAt is not null,
                "Explicit prefetch must publish the canonical audio object and retain its availability in Music's own index.");
            TestAssert.True(stored.RemoteId == seed.RemoteId && stored.SourceId == seed.SourceId && stored.EndpointId == seed.EndpointId,
                "File-only publication cannot turn an online song into a separate local-import identity.");
            TestAssert.True(stored.Cover is not null && stored.Chart is not null,
                "Explicit offline prefetch must retain its available optional artwork and chart descriptors.");
            foreach (var resource in new[] { stored.Audio, stored.Cover!, stored.Chart! })
            {
                TestAssert.True(resource.LocalPath is { } path && resource.Sha256 is { } hash
                    && SamePath(path, CanonicalFixturePath(game, hash))
                    && (await FingerprintAsync(path, token)).Sha256 == hash,
                    "Audio, optional artwork and optional maidata must remain complete canonical files.");
            }
            TestAssert.True(SamePath(await workspace.ResolveAudioAsync(stored, true, token), expectedAudio),
                "A repeated explicit prefetch must reuse the complete shared file without registering Play metadata.");
            if (original is not null)
            {
                TestAssert.True(original == await CapturePlayDatabaseAsync(database, token),
                    "File-only prefetch must preserve Play's database bytes, schema, sentinel and existing public table contents.");
            }
            else
            {
                TestAssert.True(!File.Exists(database) && !File.Exists(database + "-wal") && !File.Exists(database + "-shm")
                    && !File.Exists(database + "-journal"), "File-only prefetch cannot create a Play database or SQLite side files.");
            }
            TestAssert.True(transport.RequestCount == 0 && credentials.Calls == 0,
                "Publishing and reusing completed private media must stay offline and avoid credentials.");
        }

        /// <summary>Seeds a completed private object while retaining real resource digests and ownership descriptors.</summary>
        /// <param name="data">The generated Music data directory.</param>
        /// <param name="resource">The remote identity of this audio, image or chart.</param>
        /// <param name="bytes">The complete fixture bytes.</param>
        /// <param name="token">Cancels file creation.</param>
        /// <returns>The same resource with a validated private local path and both content digests.</returns>
        private static async Task<MediaResource> WritePrivateMediaAsync(string data, MediaResource resource, byte[] bytes, CancellationToken token)
        {
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var path = CanonicalFixturePath(data, sha256);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, token);
            return resource with { LocalPath = path, Sha256 = sha256, Md5 = Convert.ToHexStringLower(MD5.HashData(bytes)), Length = bytes.LongLength };
        }

        /// <summary>Locates the shared immutable object hierarchy used by both applications.</summary>
        /// <param name="root">The generated Music or game root.</param>
        /// <param name="sha256">The normalized full content digest.</param>
        /// <returns>The canonical object path.</returns>
        private static string CanonicalFixturePath(string root, string sha256)
        {
            return Path.Combine(root, "Cache", "Objects", sha256[..2], sha256);
        }

        /// <summary>Creates a fully compatible generated Play schema so obsolete resource-registration code would really modify it.</summary>
        /// <param name="database">The generated game database.</param>
        /// <param name="token">Cancels fixture creation.</param>
        /// <returns>A task representing a closed database containing unrelated sentinel data.</returns>
        private static async Task CreatePlaySentinelDatabaseAsync(string database, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=DELETE;
                PRAGMA user_version=73;
                CREATE TABLE OnlineChartCache (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ChartId VARCHAR, Server VARCHAR, ChartHash VARCHAR, Detail VARCHAR, LevelHashes VARCHAR,
                    IsResolved INTEGER, MaidataMd5 VARCHAR, MaidataSha256 VARCHAR, TrackMd5 VARCHAR, TrackSha256 VARCHAR,
                    VideoMd5 VARCHAR, VideoSha256 VARCHAR, CoverArtMd5 VARCHAR, CoverArtSha256 VARCHAR,
                    ThumbnailMd5 VARCHAR, ThumbnailSha256 VARCHAR, IsListed INTEGER, ListOrder BIGINT,
                    IsDeletedOnServer INTEGER, UpdatedAtTicks BIGINT
                );
                CREATE UNIQUE INDEX IX_OnlineChartCache_ChartId_Server ON OnlineChartCache(ChartId, Server);
                CREATE TABLE OnlineServers (Endpoint VARCHAR PRIMARY KEY, Server VARCHAR, UpdatedAtTicks BIGINT);
                CREATE TABLE FixtureSentinel (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);
                INSERT INTO FixtureSentinel VALUES (1, 'Owned only by the game');
                CREATE VIEW FixtureSentinelView AS SELECT Id, Value FROM FixtureSentinel;
                """;
            await command.ExecuteNonQueryAsync(token);
        }

        /// <summary>Captures independent physical, schema and logical sentinels from a genuinely read-only connection.</summary>
        /// <param name="database">The generated game-owned database.</param>
        /// <param name="token">Cancels read-only inspection.</param>
        /// <returns>A value-comparable fingerprint capable of detecting unrelated table, schema and metadata writes.</returns>
        private static async Task<PlayDatabaseFingerprint> CapturePlayDatabaseAsync(string database, CancellationToken token)
        {
            var file = await FingerprintAsync(database, token);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(token);
            var schema = new StringBuilder();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT type, name, tbl_name, COALESCE(sql, '') FROM sqlite_master ORDER BY type, name;";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        schema.Append(reader.GetString(index)).Append('\0');
                    }
                    schema.Append('\n');
                }
            }
            await using var logical = connection.CreateCommand();
            logical.CommandText = "SELECT Value, (SELECT COUNT(*) FROM OnlineChartCache), (SELECT COUNT(*) FROM OnlineServers) FROM FixtureSentinel WHERE Id=1;";
            await using var values = await logical.ExecuteReaderAsync(token);
            TestAssert.True(await values.ReadAsync(token), "The game-owned sentinel row must remain present.");
            return new PlayDatabaseFingerprint(file, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema.ToString()))),
                values.GetString(0), values.GetInt64(1), values.GetInt64(2));
        }

        /// <summary>Rejects a misleading chart-directory name and an unfinished transfer without falsely promoting a card.</summary>
        /// <param name="root">The generated negative fixture.</param>
        /// <param name="wrongMaidata">Whether to place a wrong chart with final audio, rather than only an unfinished audio chunk.</param>
        /// <param name="token">Cancels isolated filesystem and search work.</param>
        /// <returns>A task representing the source-identity and incomplete-file rejection.</returns>
        private static async Task ValidateRejectedLegacyAsync(string root, bool wrongMaidata, CancellationToken token)
        {
            var (endpoint, track, maidata) = CreateLegacySeed();
            var game = Path.Combine(root, "game");
            var legacy = LegacyFixturePath(game, "AnotherPlayName", track.Revision!);
            Directory.CreateDirectory(legacy);
            await File.WriteAllBytesAsync(Path.Combine(legacy, "maidata.txt"),
                wrongMaidata ? Encoding.UTF8.GetBytes("&title=A different chart\n&inote_1=(160){4}8,7,6,5,E\n") : maidata, token);
            await File.WriteAllBytesAsync(Path.Combine(legacy, wrongMaidata ? "track.mp3" : "track.mp3.chunk"),
                "not a verified complete source audio"u8.ToArray(), token);
            using var transport = new RejectNetworkHandler();
            using var client = new HttpClient(transport, false);
            var credentials = new RejectCredentials();
            using var workspace = await CreateWorkspaceAsync(Path.Combine(root, "music"),
                new AppSettings { UseMajdataPlayStorage = true, MajdataPlayDirectory = game, Endpoints = [endpoint] },
                track, client, credentials, token);
            var found = (await workspace.SearchAsync(track.Title, token)).Single();
            TestAssert.True(found.Audio.LocalPath is null && found.Audio.Sha256 is null && found.RemoteId == track.RemoteId,
                wrongMaidata ? "A matching directory label cannot substitute for the actual maidata MD5."
                    : "An unfinished .chunk cannot be attached as a complete audio file.");
            await using var thumbnails = new CoverThumbnailCache(CreateThumbnailWorkspace(), token);
            using var card = new SongCardViewModel(found, thumbnails);
            await card.ActivateAsync();
            TestAssert.True(!card.IsDownloaded && !card.HasDownloadStatus,
                "Rejected shared files must leave the actual visible song card in its cloud state.");
            TestAssert.True(transport.RequestCount == 0 && credentials.Calls == 0,
                "Rejecting wrong or incomplete shared files cannot fall back to HTTP or credentials.");
        }

        /// <summary>Rejects conflicting anonymous server files, while a trusted resource digest can select the correct bytes.</summary>
        /// <param name="root">The generated ambiguity fixture.</param>
        /// <param name="token">Cancels isolated filesystem and search work.</param>
        /// <returns>A task representing ambiguity rejection and known-digest disambiguation.</returns>
        private static async Task ValidateAmbiguousLegacyAsync(string root, CancellationToken token)
        {
            var (endpoint, track, maidata) = CreateLegacySeed();
            var game = Path.Combine(root, "game");
            var first = LegacyFixturePath(game, "AnonymousSourceA", track.Revision!);
            var second = LegacyFixturePath(game, "AnonymousSourceB", track.Revision!);
            var correctBytes = "second independently downloaded complete audio"u8.ToArray();
            foreach (var directory in new[] { first, second })
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(Path.Combine(directory, "maidata.txt"), maidata, token);
            }
            await File.WriteAllBytesAsync(Path.Combine(first, "track.mp3"), "different server's complete audio"u8.ToArray(), token);
            var correctPath = Path.Combine(second, "track.mp3");
            await File.WriteAllBytesAsync(correctPath, correctBytes, token);
            var correctSha = Convert.ToHexStringLower(SHA256.HashData(correctBytes));
            var settings = new AppSettings { UseMajdataPlayStorage = true, MajdataPlayDirectory = game, Endpoints = [endpoint] };
            using var transport = new RejectNetworkHandler();
            using var client = new HttpClient(transport, false);
            var credentials = new RejectCredentials();
            using (var ambiguous = await CreateWorkspaceAsync(Path.Combine(root, "unknown"), settings, track, client, credentials, token))
            {
                var found = (await ambiguous.SearchAsync(track.Title, token)).Single();
                TestAssert.True(found.Audio.LocalPath is null && found.Audio.Sha256 is null && found.RemoteId == track.RemoteId,
                    "Equal chart identities cannot choose between conflicting anonymous endpoint audio bytes.");
            }
            var expected = track with { Audio = track.Audio with { Sha256 = correctSha } };
            using (var known = await CreateWorkspaceAsync(Path.Combine(root, "known"), settings, expected, client, credentials, token))
            {
                var found = (await known.SearchAsync(track.Title, token)).Single();
                AssertDiscovery(expected, found, correctPath, correctSha);
                await using var thumbnails = new CoverThumbnailCache(CreateThumbnailWorkspace(), token);
                using var card = new SongCardViewModel(found, thumbnails);
                await card.ActivateAsync();
                AssertLocalCard(card);
            }
            TestAssert.True(transport.RequestCount == 0 && credentials.Calls == 0,
                "Anonymous-file ambiguity and digest disambiguation remain entirely offline.");
        }

        /// <summary>Builds an account-free cloud descriptor with a chart identity derived from exactly the fixture bytes.</summary>
        /// <returns>The configured endpoint, cloud-only song and its exact standard maidata bytes.</returns>
        private static (EndpointConfiguration Endpoint, MusicTrack Track, byte[] Maidata) CreateLegacySeed()
        {
            var maidata = Encoding.UTF8.GetBytes("&title=Shared negative fixture\n&artist=Offline fixture\n&inote_1=(120){4}1,2,3,4,E\n");
            var revision = Convert.ToBase64String(MD5.HashData(maidata));
            var endpoint = new EndpointConfiguration
            {
                Id = "negative-slot",
                Name = "Configured name without matching cache directory",
                BaseUri = new Uri("https://shared-negative.example.test/api/"),
                ServerId = "negative-server"
            };
            return (endpoint, new MusicTrack
            {
                Id = "shared-negative-song",
                Title = "Shared negative fixture",
                SourceId = endpoint.ServerId!,
                EndpointId = endpoint.Id,
                RemoteId = "f02a4e69-2089-4c5c-b8bb-e83fe9777c67",
                Revision = revision,
                MajChartHash = revision,
                Audio = new MediaResource { RemoteUri = new Uri(endpoint.BaseUri, "maichart/f02a4e69-2089-4c5c-b8bb-e83fe9777c67/track"), CacheVersion = revision }
            }, maidata);
        }

        /// <summary>Creates a legacy path with the real MD5 directory naming convention rather than an arbitrary test shortcut.</summary>
        /// <param name="game">The generated game root.</param>
        /// <param name="endpointName">The intentionally different display-name directory.</param>
        /// <param name="revision">The exact Base64 MD5 identity.</param>
        /// <returns>The generated legacy chart path.</returns>
        private static string LegacyFixturePath(string game, string endpointName, string revision)
        {
            return Path.Combine(game, "Cache", "Net", endpointName, BitConverter.ToString(Convert.FromBase64String(revision)));
        }

        /// <summary>Validates explicitly supplied PC files using an isolated metadata seed and no user-database initialization.</summary>
        /// <returns>A task representing read-only live discovery and a sanitized artifact report.</returns>
        public static async Task RunLiveAsync()
        {
            TestAssert.True(OperatingSystem.IsWindows(), "The explicit shared-play live group is a Windows PC check.");
            var game = RequiredPath("MAJMUSIC_SHARED_PLAY_ROOT");
            var settingsPath = RequiredPath("MAJMUSIC_SHARED_PLAY_SETTINGS");
            var libraryPath = RequiredPath("MAJMUSIC_SHARED_PLAY_LIBRARY");
            var expectedAudio = RequiredPath("MAJMUSIC_SHARED_PLAY_AUDIO");
            var chartHash = RequiredValue("MAJMUSIC_SHARED_PLAY_CHART_HASH");
            var expectedSha = RequiredValue("MAJMUSIC_SHARED_PLAY_AUDIO_SHA256").ToLowerInvariant();
            TestAssert.True(expectedSha.Length == 64 && expectedSha.All(Uri.IsHexDigit), "An explicit complete audio SHA-256 is required.");
            TestAssert.True(IsInside(expectedAudio, game), "The explicitly permitted audio must be inside the explicitly permitted game root.");
            var chartPath = Path.Combine(Path.GetDirectoryName(expectedAudio)!, "maidata.txt");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var token = timeout.Token;
            var originalAudio = await FingerprintAsync(expectedAudio, token);
            var originalChart = await FingerprintAsync(chartPath, token);
            var originalSettings = await FingerprintAsync(settingsPath, token);
            var originalLibrary = await FingerprintAsync(libraryPath, token);
            var gameDatabase = Path.Combine(game, "Runtime", "runtime.db");
            var originalGameDatabase = File.Exists(gameDatabase) ? await FingerprintAsync(gameDatabase, token) : null;
            TestAssert.True(originalAudio.Sha256 == expectedSha, "The explicitly supplied real audio must match its independently supplied SHA-256.");
            var chartBytes = await File.ReadAllBytesAsync(chartPath, token);
            TestAssert.True(Convert.ToBase64String(MD5.HashData(chartBytes)) == chartHash,
                "The real maidata content must match the explicitly supplied chart identity.");

            var settings = await ReadSanitizedSettingsAsync(settingsPath, token);
            var configuredRoot = settings.UseMajdataPlayStorage ? settings.MajdataPlayDirectory : settings.MediaStorageDirectory;
            TestAssert.True(configuredRoot is not null && SamePath(configuredRoot, game),
                "The input preferences must select the explicitly supplied shared media root.");
            var original = await ReadTargetDocumentAsync(libraryPath, chartHash, token);
            var endpoint = settings.Endpoints.FirstOrDefault(item => item.ServerId == original.SourceId)
                ?? settings.Endpoints.FirstOrDefault(item => original.Audio.RemoteUri is { } uri && BelongsTo(uri, item.BaseUri))
                ?? throw new InvalidOperationException("The target must belong to one of the explicitly sanitized endpoint addresses.");
            var seed = original with
            {
                EndpointId = endpoint.Id,
                Audio = original.Audio with { LocalPath = null, Sha256 = null, Md5 = null, Length = null },
                LocalMediaSavedAt = null,
                AudioMetadataLength = null,
                AudioMetadataLastWriteUtcTicks = null
            };

            var artifactsBase = Environment.GetEnvironmentVariable("MAJMUSIC_SHARED_PLAY_ARTIFACTS") is { Length: > 0 } explicitArtifacts
                ? Path.GetFullPath(explicitArtifacts)
                : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "shared-play-live"));
            TestAssert.True(!SamePath(artifactsBase, game) && !IsInside(artifactsBase, game)
                && !SamePath(artifactsBase, Path.GetDirectoryName(settingsPath)!) && !IsInside(artifactsBase, Path.GetDirectoryName(settingsPath)!),
                "Live validation artifacts must remain outside game data and private user configuration.");
            var artifacts = Path.Combine(artifactsBase, Guid.NewGuid().ToString("N"));
            using var transport = new RejectNetworkHandler();
            using var client = new HttpClient(transport, false);
            var credentials = new RejectCredentials();
            using (var workspace = await CreateWorkspaceAsync(artifacts, settings, seed, client, credentials, token))
            {
                foreach (var query in new[] { seed.Title, "讲数学" }.Distinct(StringComparer.Ordinal))
                {
                    var found = (await workspace.SearchAsync(query, token)).Single(item => item.Id == seed.Id);
                    AssertDiscovery(seed, found, expectedAudio, expectedSha);
                    await using var thumbnails = new CoverThumbnailCache(CreateThumbnailWorkspace(), token);
                    using var card = new SongCardViewModel(found, thumbnails);
                    await card.ActivateAsync();
                    AssertLocalCard(card);
                }
            }

            TestAssert.True(originalAudio == await FingerprintAsync(expectedAudio, token)
                && originalChart == await FingerprintAsync(chartPath, token), "The real game's audio and maidata must remain byte-for-byte unchanged.");
            TestAssert.True(originalSettings == await FingerprintAsync(settingsPath, token)
                && originalLibrary == await FingerprintAsync(libraryPath, token), "The original Music settings and database must remain unchanged.");
            if (originalGameDatabase is not null)
            {
                TestAssert.True(originalGameDatabase == await FingerprintAsync(gameDatabase, token), "The real game database must remain unchanged.");
            }
            TestAssert.Equal(0, transport.RequestCount, "The live PC validation must issue zero HTTP requests.");
            TestAssert.Equal(0, credentials.Calls, "The live PC validation must never access a credential vault.");
            var report = JsonSerializer.Serialize(new
            {
                ReadOnlySourcesUnchanged = true,
                ExactAndPartialTitleSearchFoundLocalAudio = true,
                AudioSha256Verified = true,
                AudioLengthBytes = originalAudio.Length,
                OnlineIdentityPreserved = true,
                CardShowsLocalAudio = true,
                NativePlaybackPerformed = false,
                HttpRequests = transport.RequestCount,
                CredentialCalls = credentials.Calls,
                SavedAccounts = 0
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(artifacts, "read-only-validation.json"), report, token);
            Console.WriteLine("Validated real shared audio, exact/partial search and local card state; HTTP 0, vault 0, source files unchanged.");
        }

        /// <summary>Creates only a generated or explicitly isolated application database, never the real user's repository.</summary>
        /// <param name="data">The new application artifact directory.</param>
        /// <param name="settings">The account-free, offline preferences.</param>
        /// <param name="seed">One cloud-only source descriptor.</param>
        /// <param name="client">The transport rejecting every attempted request.</param>
        /// <param name="credentials">The adapter rejecting every credential operation.</param>
        /// <param name="token">Cancels fixture preparation.</param>
        /// <returns>An already-started workspace containing only the isolated seed.</returns>
        private static async Task<MusicWorkspace> CreateWorkspaceAsync(string data, AppSettings settings, MusicTrack seed,
            HttpClient client, RejectCredentials credentials, CancellationToken token)
        {
            Directory.CreateDirectory(data);
            await new JsonSettingsStore(Path.Combine(data, "settings.json")).SaveAsync(settings with { OnlineEnabled = false }, token);
            await new SqliteLibraryRepository(Path.Combine(data, "library.db")).UpsertTracksAsync([seed], token);
            var workspace = new MusicWorkspace(data, client, client, credentialStore: credentials);
            try
            {
                await workspace.LoadSettingsAsync(token);
                TestAssert.True(!workspace.IsOnline, "The isolated shared-media workspace must remain offline.");
                return workspace;
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        /// <summary>Omits optional artwork work so validating a real card never publishes a cover into the user's game tree.</summary>
        /// <returns>A test-only null-artwork adapter used by the actual thumbnail and card implementations.</returns>
        private static CardDownloadPresentationTests.DownloadWorkspace CreateThumbnailWorkspace()
        {
            return new CardDownloadPresentationTests.DownloadWorkspace
            {
                ResolveCoverOverride = static (_, _) => Task.FromResult<string?>(null)
            };
        }

        /// <summary>Checks resource discovery without exposing source paths or account addresses in assertion output.</summary>
        /// <param name="seed">The cloud-only source descriptor.</param>
        /// <param name="found">The same source revision returned by search.</param>
        /// <param name="audioPath">The independently supplied complete audio file.</param>
        /// <param name="sha256">Its independently verified digest.</param>
        private static void AssertDiscovery(MusicTrack seed, MusicTrack found, string audioPath, string sha256)
        {
            TestAssert.True(found.Id == seed.Id && found.RemoteId == seed.RemoteId && found.SourceId == seed.SourceId
                && found.EndpointId == seed.EndpointId && found.Revision == seed.Revision,
                "Local resource discovery must retain the online song, endpoint, server and revision identities.");
            TestAssert.True(found.Audio.LocalPath is { } local && SamePath(local, audioPath),
                "Search must immediately attach the independently completed game audio without playback or a restart.");
            TestAssert.True(string.Equals(found.Audio.Sha256, sha256, StringComparison.OrdinalIgnoreCase),
                "The discovered audio digest must describe the real completed bytes.");
        }

        /// <summary>Checks the same public state bound by real desktop and mobile song cards.</summary>
        /// <param name="card">The activated real song-card view model.</param>
        private static void AssertLocalCard(SongCardViewModel card)
        {
            TestAssert.True(card.IsDownloaded && card.HasDownloadStatus && card.DownloadPercent == 100
                && card.DownloadStatus == "已在本地" && !card.IsDownloading,
                "An activated card must show the complete discovered audio as local without starting a transfer.");
        }

        /// <summary>Reads only endpoint labels, public addresses, server identities and the three media-location fields.</summary>
        /// <param name="path">The explicitly authorized settings document.</param>
        /// <param name="token">Cancels the read.</param>
        /// <returns>New offline preferences with generated slot identities and no account selections or credentials.</returns>
        private static async Task<AppSettings> ReadSanitizedSettingsAsync(string path, CancellationToken token)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            var endpoints = new List<EndpointConfiguration>();
            if (root.TryGetProperty("endpoints", out var configured) && configured.ValueKind == JsonValueKind.Array)
            {
                foreach (var endpoint in configured.EnumerateArray())
                {
                    if (!Uri.TryCreate(ReadString(endpoint, "baseUri"), UriKind.Absolute, out var uri)
                        || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0)
                    {
                        continue;
                    }
                    endpoints.Add(new EndpointConfiguration
                    {
                        Id = "read-only-slot-" + endpoints.Count,
                        Name = ReadString(endpoint, "name") ?? "Shared source",
                        BaseUri = uri,
                        ServerId = ReadString(endpoint, "serverId")
                    });
                }
            }
            return new AppSettings
            {
                OnlineEnabled = false,
                Endpoints = endpoints,
                UseMajdataPlayStorage = root.TryGetProperty("useMajdataPlayStorage", out var shared) && shared.ValueKind == JsonValueKind.True,
                MajdataPlayDirectory = ReadString(root, "majdataPlayDirectory"),
                MediaStorageDirectory = ReadString(root, "mediaStorageDirectory")
            };
        }

        /// <summary>Selects one public target descriptor through a genuinely read-only SQLite connection without repository initialization.</summary>
        /// <param name="path">The explicitly authorized original Music database.</param>
        /// <param name="chartHash">The public maidata identity of the requested song.</param>
        /// <param name="token">Cancels database access.</param>
        /// <returns>The target's public online metadata document.</returns>
        private static async Task<MusicTrack> ReadTargetDocumentAsync(string path, string chartHash, CancellationToken token)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 2
            }.ToString());
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT document FROM tracks
                WHERE json_extract(document, '$.MajChartHash') = $hash OR json_extract(document, '$.Revision') = $hash
                    OR json_extract(document, '$.majChartHash') = $hash OR json_extract(document, '$.revision') = $hash
                ORDER BY id;
                """;
            command.Parameters.AddWithValue("$hash", chartHash);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var track = JsonSerializer.Deserialize<MusicTrack>(reader.GetString(0), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (track?.RemoteId is not null)
                {
                    return track;
                }
            }
            throw new InvalidOperationException("The explicitly requested online chart is absent from the read-only metadata database.");
        }

        /// <summary>Reads one permitted string field without deserializing account or credential models.</summary>
        /// <param name="value">The containing JSON object.</param>
        /// <param name="name">The allowed public property name.</param>
        /// <returns>The string value, or null when absent.</returns>
        private static string? ReadString(JsonElement value, string name)
        {
            return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        }

        /// <summary>Checks a media route against one public API prefix without reading account selections.</summary>
        /// <param name="resource">The track's absolute media address.</param>
        /// <param name="endpoint">The public configured API root.</param>
        /// <returns>Whether the route belongs to that endpoint.</returns>
        private static bool BelongsTo(Uri resource, Uri endpoint)
        {
            return resource.Scheme.Equals(endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
                && resource.Authority.Equals(endpoint.Authority, StringComparison.OrdinalIgnoreCase)
                && resource.AbsolutePath.StartsWith(endpoint.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal);
        }

        /// <summary>Requires an explicitly supplied environment value instead of embedding real user paths or identities in source.</summary>
        /// <param name="name">The required environment-variable name.</param>
        /// <returns>The nonempty explicit value.</returns>
        private static string RequiredValue(string name)
        {
            return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value : throw new InvalidOperationException("The explicit live group requires " + name + ".");
        }

        /// <summary>Normalizes one explicitly authorized ordinary file or directory path.</summary>
        /// <param name="name">The environment-variable name.</param>
        /// <returns>The absolute path.</returns>
        private static string RequiredPath(string name)
        {
            return Path.GetFullPath(RequiredValue(name));
        }

        /// <summary>Compares ordinary PC paths without printing them.</summary>
        /// <param name="first">The first path.</param>
        /// <param name="second">The second path.</param>
        /// <returns>Whether both refer to the same lexical path.</returns>
        private static bool SamePath(string first, string second)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Protects real data from artifact creation or fixture cleanup.</summary>
        /// <param name="path">The candidate path.</param>
        /// <param name="directory">The protected parent directory.</param>
        /// <returns>Whether the candidate is a strict child.</returns>
        private static bool IsInside(string path, string directory)
        {
            return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Captures original bytes and timestamps without modifying the inspected source file.</summary>
        /// <param name="path">The explicitly allowed source file.</param>
        /// <param name="token">Cancels the read.</param>
        /// <returns>The byte length, last-write timestamp and SHA-256.</returns>
        private static async Task<FileFingerprint> FingerprintAsync(string path, CancellationToken token)
        {
            var modified = File.GetLastWriteTimeUtc(path).Ticks;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            return new FileFingerprint(length, modified, sha256);
        }

        /// <summary>Deletes only a generated synthetic fixture after resolving its complete absolute path.</summary>
        /// <param name="root">The generated synthetic test directory.</param>
        private static void DeleteGeneratedFixture(string root)
        {
            var full = Path.GetFullPath(root);
            if (!IsInside(full, Path.GetTempPath()) || !Path.GetFileName(full).StartsWith("MajMusic-shared-play-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing cleanup outside the generated shared-play fixture.");
            }
            if (Directory.Exists(full))
            {
                Directory.Delete(full, true);
            }
        }

        /// <summary>Retains a private in-memory fingerprint without serializing user data or source paths.</summary>
        /// <param name="Length">The file length in bytes.</param>
        /// <param name="LastWriteTicks">The original UTC write timestamp.</param>
        /// <param name="Sha256">The digest of the original bytes.</param>
        private sealed record FileFingerprint(long Length, long LastWriteTicks, string Sha256);

        /// <summary>Detects physical writes as well as schema or row changes in a generated game-owned database.</summary>
        /// <param name="File">The complete original file fingerprint.</param>
        /// <param name="SchemaSha256">The digest of every SQLite schema definition.</param>
        /// <param name="Sentinel">The unrelated game-owned sentinel value.</param>
        /// <param name="ChartRows">The existing public chart row count.</param>
        /// <param name="ServerRows">The existing public server row count.</param>
        private sealed record PlayDatabaseFingerprint(FileFingerprint File, string SchemaSha256, string Sentinel, long ChartRows, long ServerRows);

        /// <summary>Records and rejects every attempted request before any socket or real source can be used.</summary>
        private sealed class RejectNetworkHandler : HttpMessageHandler
        {
            /// <summary>Counts rejected attempts independently of their caller's exception handling.</summary>
            private int _requestCount;
            /// <summary>Gets the total attempted HTTP operations.</summary>
            public int RequestCount => Volatile.Read(ref _requestCount);
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requestCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
        }

        /// <summary>Prevents even accidental use of an operating-system account vault by this offline harness.</summary>
        private sealed class RejectCredentials : ICredentialStore
        {
            /// <summary>Counts rejected calls without retaining their keys or values.</summary>
            private int _calls;
            /// <summary>Gets the number of attempted credential operations.</summary>
            public int Calls => Volatile.Read(ref _calls);
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _calls);
                throw new InvalidOperationException("Read-only shared-media validation cannot access credentials.");
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _calls);
                throw new InvalidOperationException("Read-only shared-media validation cannot write credentials.");
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _calls);
                throw new InvalidOperationException("Read-only shared-media validation cannot remove credentials.");
            }
        }
    }
}
