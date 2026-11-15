#nullable enable

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Downloads;
using MajMusic.Infrastructure.Network;
using MajMusic.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MajMusic.Tests
{
    /// <summary>Checks executable-adjacent storage and one-time migration using generated data only.</summary>
    internal static class PortableDataDirectoryTests
    {
        /// <summary>Validates complete migration, external references, offline cache reuse and failure preservation.</summary>
        /// <returns>A task representing portable-data regressions.</returns>
        public static async Task RunAsync()
        {
            TestAssert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
                Path.TrimEndingDirectorySeparator(DesktopDataDirectory.GetDefaultDirectory()),
                "Desktop default storage must resolve to the executable directory without reading legacy data.");
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-portable-data-" + Guid.NewGuid().ToString("N"));
            try
            {
                await ValidateCompleteMigrationAsync(Path.Combine(root, "complete"));
                await ValidateExistingDestinationAsync(Path.Combine(root, "existing"));
                await ValidateInvalidLegacyAsync(Path.Combine(root, "invalid"));
                await ValidatePublicationConflictAsync(Path.Combine(root, "publication-conflict"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                var resolved = Path.GetFullPath(root);
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("MajMusic-portable-data-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated portable-data fixture.");
                }

                if (Directory.Exists(resolved))
                {
                    Directory.Delete(resolved, true);
                }
            }
        }

        /// <summary>Copies settings, a live SQLite snapshot and managed files while retaining original external music references.</summary>
        /// <param name="root">The generated legacy, executable and external-source fixture parent.</param>
        /// <returns>A task representing complete data and cache preservation.</returns>
        private static async Task ValidateCompleteMigrationAsync(string root)
        {
            var legacy = Path.Combine(root, "legacy");
            var portable = Path.Combine(root, "executable");
            var external = Path.Combine(root, "user-charts");
            var library = Path.Combine(legacy, "Library");
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(external);
            var managedAudio = Path.Combine(library, "managed.mp3");
            var externalAudio = Path.Combine(external, "external.mp3");
            var managedCover = Path.Combine(library, "cover.png");
            await File.WriteAllBytesAsync(managedAudio, "managed fixture audio"u8.ToArray());
            await File.WriteAllBytesAsync(externalAudio, "external read-only fixture audio"u8.ToArray());
            await File.WriteAllBytesAsync(managedCover, "managed fixture image"u8.ToArray());
            var externalSignature = Signature(externalAudio);
            var managedSignature = Signature(managedAudio);
            var settingsStore = new JsonSettingsStore(Path.Combine(legacy, "settings.json"));
            var settings = new AppSettings
            {
                OnlineEnabled = true,
                AudioOutput = new AudioOutputPreferences { Volume = 37, Muted = true },
                LocalMusicDirectories = [library, external],
                LocalDirectorySources =
                [
                    new LocalDirectorySource { Id = "managed", Path = library },
                    new LocalDirectorySource { Id = "external", Path = external }
                ],
                LocalDirectoryFormatVersion = LocalDirectorySettings.CurrentVersion
            };
            await settingsStore.SaveAsync(settings);
            var originalSettings = await File.ReadAllBytesAsync(Path.Combine(legacy, "settings.json"));
            using var transport = new FixtureTransport();
            using var client = new HttpClient(transport);
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            var audio = new MediaResource { RemoteUri = new Uri("https://example.invalid/audio"), CacheVersion = "audio-v1" };
            var image = new MediaResource { RemoteUri = new Uri("https://example.invalid/image"), CacheVersion = "cover-v1" };
            string cachedAudio;
            using (var cache = new MediaCache(Path.Combine(legacy, "Cache"), client, policy))
            {
                cachedAudio = await cache.GetAsync(audio, true);
                await cache.GetAsync(image);
            }

            var originalIndex = await File.ReadAllBytesAsync(Path.Combine(legacy, "Cache", "index.json"));
            var cachedHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(cachedAudio)));
            var repository = new SqliteLibraryRepository(Path.Combine(legacy, "library.db"));
            await repository.UpsertTracksAsync(
            [
                Track("managed", new MediaResource { LocalPath = managedAudio }, new MediaResource { LocalPath = managedCover }),
                Track("external", new MediaResource { LocalPath = externalAudio }),
                Track("cached", audio with { LocalPath = cachedAudio, Sha256 = cachedHash }),
                Track("alias-cover", new MediaResource { LocalPath = externalAudio }, image) with { RemoteId = "remote", SourceId = "fixture-server", EndpointId = "fixture" }
            ]);
            var orderedIds = new[] { "external", "managed", "cached" };
            await repository.SavePlaylistAsync(new MusicPlaylist
            {
                Id = "ordered",
                Name = "Ordered favorites",
                IsFavorite = true,
                TrackIds = orderedIds,
                MajCollection = new MajCollectionDefinition { SourceFilePath = Path.Combine(external, "original-index.json") }
            });
            await repository.SavePlaylistAsync(new MusicPlaylist { Id = "deleted", Name = "Deleted local list" });
            await repository.DeletePlaylistAsync("deleted");
            var metadata = Path.Combine(legacy, "AudioMetadata");
            Directory.CreateDirectory(metadata);
            var metadataBytes = "{\"fileLength\":21,\"lastWriteUtcTicks\":1,\"metadata\":{}}"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(metadata, MetadataKey(managedAudio) + ".json"), metadataBytes);
            Directory.CreateDirectory(Path.Combine(legacy, "Downloads"));
            await File.WriteAllBytesAsync(Path.Combine(legacy, "Downloads", "retained.flac"), "retained managed download"u8.ToArray());

            using (var walOwner = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(legacy, "library.db"), Pooling = false }.ToString()))
            {
                walOwner.Open();
                using var wal = walOwner.CreateCommand();
                wal.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                wal.ExecuteNonQuery();
                using var append = walOwner.CreateCommand();
                append.CommandText = "INSERT INTO playlists(id,name,document) VALUES('wal-list','Live WAL list',$document);";
                append.Parameters.AddWithValue("$document", JsonSerializer.Serialize(new MusicPlaylist { Id = "wal-list", Name = "Live WAL list" }));
                append.ExecuteNonQuery();
                using var unknown = walOwner.CreateCommand();
                unknown.CommandText = "UPDATE tracks SET document = json_set(document, '$.FutureField', 'preserved') WHERE id = 'managed';";
                unknown.ExecuteNonQuery();
                TestAssert.True(File.Exists(Path.Combine(legacy, "library.db-wal")), "The fixture must retain committed records in a live WAL.");
                TestAssert.Equal(portable, DesktopDataDirectory.Prepare(portable, legacy), "The migration must select the executable directory.");
            }

            var retainedSettings = await File.ReadAllBytesAsync(Path.Combine(legacy, "settings.json"));
            var retainedIndex = await File.ReadAllBytesAsync(Path.Combine(legacy, "Cache", "index.json"));
            TestAssert.True(originalSettings.SequenceEqual(retainedSettings) && originalIndex.SequenceEqual(retainedIndex),
                "Portable migration cannot rewrite the previous settings or cache index.");
            TestAssert.Equal(externalSignature, Signature(externalAudio), "External music bytes and timestamps must remain unchanged.");
            TestAssert.Equal(managedSignature, Signature(managedAudio), "The previous managed media must remain unchanged and available.");
            TestAssert.True(File.Exists(Path.Combine(portable, "Downloads", "retained.flac"))
                && !Directory.Exists(Path.Combine(portable, "user-charts")),
                "Migration must copy managed downloads without importing the external music tree.");
            var relocatedMetadata = await File.ReadAllBytesAsync(Path.Combine(portable, "AudioMetadata", MetadataKey(Path.Combine(portable, "Library", "managed.mp3")) + ".json"));
            TestAssert.True(metadataBytes.SequenceEqual(relocatedMetadata),
                "Audio metadata must retain a usable lookup alias after its managed path changes.");
            var restored = await new JsonSettingsStore(Path.Combine(portable, "settings.json")).LoadAsync();
            TestAssert.True(restored.OnlineEnabled && restored.AudioOutput == settings.AudioOutput,
                "Migration must preserve online permission and complete audio preferences.");
            TestAssert.True(restored.LocalDirectorySources.Single(item => item.Id == "managed").Path == Path.Combine(portable, "Library")
                && restored.LocalDirectorySources.Single(item => item.Id == "external").Path == external,
                "Only copied managed source paths may be rebased.");
            using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(portable, "library.db"), Pooling = false }.ToString()))
            {
                check.Open();
                using var command = check.CreateCommand();
                command.CommandText = "SELECT json_extract(document, '$.FutureField') FROM tracks WHERE id='managed';";
                TestAssert.Equal("preserved", (string?)command.ExecuteScalar(), "Migration cannot discard unknown library document fields.");
                command.CommandText = "SELECT COUNT(*) FROM deleted_playlists WHERE id='deleted';";
                TestAssert.Equal(1L, (long)command.ExecuteScalar()!, "The complete SQLite backup must preserve deleted-playlist tombstones.");
            }

            var requests = transport.Requests;
            using (var workspace = new MusicWorkspace(portable, client, client, credentialStore: new RejectingCredentials()))
            {
                await workspace.LoadSettingsAsync();
                workspace.SetOnlineEnabled(false);
                var tracks = (await workspace.SearchAsync(null)).ToDictionary(track => track.Id, StringComparer.Ordinal);
                TestAssert.Equal(4, tracks.Count, "The complete music index must migrate without replacing stable identities.");
                TestAssert.Equal(Path.Combine(portable, "Library", "managed.mp3"), await workspace.ResolveAudioAsync(tracks["managed"], false),
                    "Managed imported audio must resolve beneath the executable after migration.");
                TestAssert.Equal(externalAudio, await workspace.ResolveAudioAsync(tracks["external"], false),
                    "Read-only external audio must retain its original source reference.");
                TestAssert.True((await workspace.ResolveAudioAsync(tracks["cached"], false)).StartsWith(Path.Combine(portable, "Cache") + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                    "Downloaded audio cannot retain a dependency on the previous application-data root.");
                TestAssert.True((await workspace.ResolveCoverAsync(tracks["alias-cover"]))!.StartsWith(Path.Combine(portable, "Cache") + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                    "A migrated URI/revision alias must resolve its cover from the new cache without a known digest.");
                var playlists = await workspace.GetPlaylistsAsync();
                TestAssert.True(playlists.Single(item => item.Id == "ordered").TrackIds.SequenceEqual(orderedIds)
                    && playlists.Any(item => item.Id == "wal-list"),
                    "Playlist order and transactions still in the old WAL must survive migration.");
            }

            TestAssert.Equal(requests, transport.Requests, "All migrated managed and cached media must remain usable offline without network requests.");
            await new JsonSettingsStore(Path.Combine(portable, "settings.json")).SaveAsync(restored with { AudioOutput = new AudioOutputPreferences { Volume = 55 } });
            DesktopDataDirectory.Prepare(portable, legacy);
            TestAssert.Equal(55, (await new JsonSettingsStore(Path.Combine(portable, "settings.json")).LoadAsync()).AudioOutput.Volume,
                "An existing portable data set must never be overwritten by another legacy migration.");
            TestAssert.True(!Directory.EnumerateDirectories(portable, ".majmusic-migration-*").Any(), "Migration staging directories must be removed after publication.");
        }

        /// <summary>Preserves a preexisting portable installation without merging in a different legacy library.</summary>
        /// <param name="root">The generated fixture parent.</param>
        /// <returns>A task representing non-overwrite behavior.</returns>
        private static async Task ValidateExistingDestinationAsync(string root)
        {
            var legacy = Path.Combine(root, "legacy");
            var portable = Path.Combine(root, "portable");
            await new JsonSettingsStore(Path.Combine(legacy, "settings.json")).SaveAsync(new AppSettings { AudioOutput = new AudioOutputPreferences { Volume = 99 } });
            await new JsonSettingsStore(Path.Combine(portable, "settings.json")).SaveAsync(new AppSettings { AudioOutput = new AudioOutputPreferences { Volume = 22 } });
            var original = await File.ReadAllBytesAsync(Path.Combine(portable, "settings.json"));
            DesktopDataDirectory.Prepare(portable, legacy);
            var retained = await File.ReadAllBytesAsync(Path.Combine(portable, "settings.json"));
            TestAssert.True(original.SequenceEqual(retained),
                "An existing portable configuration cannot be replaced by legacy settings.");
        }

        /// <summary>Leaves both old files and the destination untouched when legacy validation fails before publication.</summary>
        /// <param name="root">The generated fixture parent.</param>
        /// <returns>A task representing failed-migration preservation.</returns>
        private static async Task ValidateInvalidLegacyAsync(string root)
        {
            var legacy = Path.Combine(root, "legacy");
            var portable = Path.Combine(root, "portable");
            Directory.CreateDirectory(Path.Combine(legacy, "Library"));
            await File.WriteAllTextAsync(Path.Combine(legacy, "settings.json"), "invalid generated settings");
            await File.WriteAllTextAsync(Path.Combine(legacy, "Library", "preserved.mp3"), "original bytes");
            await TestAssert.ThrowsAsync<InvalidDataException>(() => Task.Run(() => DesktopDataDirectory.Prepare(portable, legacy)));
            TestAssert.True(!File.Exists(Path.Combine(portable, "settings.json")) && !Directory.Exists(Path.Combine(portable, "Library"))
                && !Directory.EnumerateDirectories(portable, ".majmusic-migration-*").Any(),
                "Invalid legacy settings cannot publish a partial portable data set.");
            TestAssert.Equal("original bytes", await File.ReadAllTextAsync(Path.Combine(legacy, "Library", "preserved.mp3")),
                "A failed migration must retain the complete previous source files.");
        }

        /// <summary>Leaves no partial data set when a file conflicts with a later managed-directory publication.</summary>
        /// <param name="root">The generated fixture parent.</param>
        /// <returns>A task representing conflict preservation and successful retry.</returns>
        private static async Task ValidatePublicationConflictAsync(string root)
        {
            var legacy = Path.Combine(root, "legacy");
            var portable = Path.Combine(root, "portable");
            Directory.CreateDirectory(Path.Combine(legacy, "Cache"));
            Directory.CreateDirectory(Path.Combine(legacy, "Downloads"));
            Directory.CreateDirectory(portable);
            await File.WriteAllTextAsync(Path.Combine(legacy, "Cache", "retained.bin"), "original cache bytes");
            await File.WriteAllTextAsync(Path.Combine(legacy, "Downloads", "retained.mp3"), "original download bytes");
            await new JsonSettingsStore(Path.Combine(legacy, "settings.json")).SaveAsync(new AppSettings());
            var repository = new SqliteLibraryRepository(Path.Combine(legacy, "library.db"));
            await repository.SavePlaylistAsync(new MusicPlaylist { Id = "retained", Name = "Retained list" });
            var conflicting = Path.Combine(portable, "Downloads");
            await File.WriteAllTextAsync(conflicting, "preexisting conflicting file");
            await TestAssert.ThrowsAsync<IOException>(() => Task.Run(() => DesktopDataDirectory.Prepare(portable, legacy)));
            TestAssert.True(!Directory.Exists(Path.Combine(portable, "Cache")) && !File.Exists(Path.Combine(portable, "library.db"))
                && !File.Exists(Path.Combine(portable, "settings.json")),
                "A publication conflict cannot leave earlier managed entries that would suppress a later migration retry.");
            TestAssert.Equal("preexisting conflicting file", await File.ReadAllTextAsync(conflicting),
                "Failed publication cannot replace or remove a preexisting conflicting path.");
            TestAssert.Equal("original cache bytes", await File.ReadAllTextAsync(Path.Combine(legacy, "Cache", "retained.bin")),
                "Failed publication cannot remove or rewrite the legacy cache.");
            File.Delete(conflicting);
            DesktopDataDirectory.Prepare(portable, legacy);
            TestAssert.True(Directory.Exists(Path.Combine(portable, "Cache")) && Directory.Exists(Path.Combine(portable, "Downloads"))
                && File.Exists(Path.Combine(portable, "library.db")) && File.Exists(Path.Combine(portable, "settings.json")),
                "Removing a publication conflict must allow the complete legacy data set to migrate on retry.");
        }

        /// <summary>Creates one synthetic index entry without starting playback.</summary>
        /// <param name="id">The stable music identity.</param>
        /// <param name="audio">The local or cached audio descriptor.</param>
        /// <param name="cover">An optional local or aliased cover descriptor.</param>
        /// <returns>The generated music entry.</returns>
        private static MusicTrack Track(string id, MediaResource audio, MediaResource? cover = null)
        {
            return new MusicTrack { Id = id, Title = id, Audio = audio, Cover = cover };
        }

        /// <summary>Captures source bytes and metadata to detect accidental source mutation.</summary>
        /// <param name="path">The generated source file.</param>
        /// <returns>The file's stable content and timestamp signature.</returns>
        private static string Signature(string path)
        {
            var file = new FileInfo(path);
            return file.Length + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Attributes + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }

        /// <summary>Matches the production audio metadata cache's path-based key.</summary>
        /// <param name="path">The old or rebased local media path.</param>
        /// <returns>The expected cache filename stem.</returns>
        private static string MetadataKey(string path)
        {
            var normalized = Path.GetFullPath(path);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized)));
        }

        /// <summary>Provides only generated media bytes without an external HTTP connection.</summary>
        private sealed class FixtureTransport : HttpMessageHandler
        {
            /// <summary>Gets the number of explicitly seeded resource requests.</summary>
            public int Requests { get; private set; }
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fixture " + request.RequestUri!.AbsolutePath)) });
            }
        }

        /// <summary>Rejects any attempt to read or migrate real system credentials.</summary>
        private sealed class RejectingCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Portable migration cannot read system credentials.");
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Portable migration cannot write system credentials.");
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Portable migration cannot delete system credentials.");
            }
        }
    }
}
