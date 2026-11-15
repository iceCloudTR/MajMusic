#nullable enable

using System.Security.Cryptography;
using System.Text.Json;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Tests
{
    /// <summary>Checks independent music and collection-index configuration without decoding audio or contacting a server.</summary>
    internal static class DirectorySourceTests
    {
        /// <summary>Runs migration, independent directory roles, delayed binding and read-only export regressions.</summary>
        /// <returns>A task representing all directory-source cases.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-directory-sources-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                await ValidateMigrationAsync(Path.Combine(root, "migration"));
                await ValidateIndependentRolesAsync(Path.Combine(root, "roles"));
                await ValidateLateResolutionAsync(Path.Combine(root, "late-resolution"));
                await ValidateAccountSettingsAsync(Path.Combine(root, "account-validation"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-directory-sources-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the directory-source fixture.");
                }

                Directory.Delete(root, true);
            }
        }

        /// <summary>Migrates untyped roots once and keeps independent roles for one physical path.</summary>
        /// <param name="root">The isolated settings fixture.</param>
        /// <returns>A task representing migration and persistence checks.</returns>
        private static async Task ValidateMigrationAsync(string root)
        {
            Directory.CreateDirectory(root);
            var music = Path.Combine(root, "music");
            var file = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { localMusicDirectories = new[] { music, music + Path.DirectorySeparatorChar } }));
            var store = new JsonSettingsStore(file);
            var migrated = await store.LoadAsync();
            TestAssert.Equal(1, migrated.LocalDirectorySources.Count, "A repeated legacy root migrates to one configured directory.");
            TestAssert.Equal(LocalDirectoryKind.MusicLibrary, migrated.LocalDirectorySources.Single().Kind,
                "Legacy music roots cannot implicitly create a collection-index source.");
            var sourceId = migrated.LocalDirectorySources.Single().Id;
            await store.SaveAsync(migrated);
            TestAssert.Equal(sourceId, (await store.LoadAsync()).LocalDirectorySources.Single().Id,
                "Migrated directory identity remains stable across restart.");

            var shared = new LocalDirectorySource { Id = "music", Path = music, Kind = LocalDirectoryKind.MusicLibrary };
            var typed = LocalDirectorySettings.NormalizeLegacy(new AppSettings
            {
                LocalDirectorySources =
                [
                    shared,
                    shared with { Id = "duplicate", Path = music + Path.DirectorySeparatorChar },
                    shared with { Id = "index", Kind = LocalDirectoryKind.CollectionIndex }
                ]
            });
            TestAssert.Equal(2, typed.LocalDirectorySources.Count, "One physical directory may independently serve music and explicit collection roles.");
            if (OperatingSystem.IsWindows())
            {
                var sameCase = LocalDirectorySettings.Normalize(typed with
                {
                    LocalDirectorySources = typed.LocalDirectorySources.Append(shared with { Id = "case", Path = music.ToUpperInvariant() }).ToArray()
                });
                TestAssert.Equal(2, sameCase.LocalDirectorySources.Count, "Windows path spelling does not create duplicate role/path configurations.");
            }

            await store.SaveAsync(migrated with { LocalDirectorySources = Array.Empty<LocalDirectorySource>() });
            TestAssert.Equal(0, (await store.LoadAsync()).LocalDirectorySources.Count,
                "Removing the last typed source cannot resurrect its retained legacy path at restart.");
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new AppSettings
            {
                LocalDirectorySources = [shared with { Path = "relative" }]
            }));
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new AppSettings
            {
                LocalDirectorySources = [shared with { Kind = (LocalDirectoryKind)99 }]
            }));
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new AppSettings { LocalDirectoryFormatVersion = 2 }));
            TestAssert.True(!Directory.Exists(music), "Normalizing directory configuration cannot create a source directory.");
        }

        /// <summary>Only reads the configured data type while preserving source bytes and rejecting source output destinations.</summary>
        /// <param name="root">The independent-role fixture root.</param>
        /// <returns>A task representing real workspace and source-role checks.</returns>
        private static async Task ValidateIndependentRolesAsync(string root)
        {
            var musicPath = Path.Combine(root, "source", "music");
            var indexPath = Path.Combine(root, "source", "separate-index");
            var hash = CreateSong(Path.Combine(musicPath, "nested", "one"), "Text-authoritative music");
            var embedded = Path.Combine(musicPath, "majcollection");
            Directory.CreateDirectory(embedded);
            await WriteCollectionAsync(Path.Combine(embedded, "never-auto-import.json"), "Embedded list", hash);
            Directory.CreateDirectory(indexPath);
            await WriteCollectionAsync(Path.Combine(indexPath, "explicit.json"), "Explicit list", hash);
            await File.WriteAllTextAsync(Path.Combine(indexPath, "invalid.json"), "{ not JSON");
            CreateSong(indexPath, "Index folder audio must not be indexed");
            var before = Snapshot(Path.Combine(root, "source"));

            using var handler = new RejectingTransport();
            using var transport = new HttpClient(handler);
            using var workspace = new MusicWorkspace(Path.Combine(root, "data"), transport, transport);
            await workspace.LoadSettingsAsync();
            var exchange = new MajCollectionExchange(workspace);
            var service = new LocalDirectoryService(workspace, exchange);
            var music = new LocalDirectorySource { Id = "music", Path = musicPath, Kind = LocalDirectoryKind.MusicLibrary };
            var index = new LocalDirectorySource { Id = "index", Path = indexPath, Kind = LocalDirectoryKind.CollectionIndex };
            var musicResult = await service.ScanAsync(music);
            TestAssert.Equal(1, musicResult.TrackCount, "A music source recursively indexes its own song tree.");
            TestAssert.Equal(0, musicResult.PlaylistCount, "A music source never imports its embedded majcollection implicitly.");
            TestAssert.Equal(0, (await workspace.GetPlaylistsAsync()).Count, "Music discovery cannot publish embedded collection JSON.");
            var indexed = (await workspace.SearchAsync(null)).Single();
            TestAssert.Equal("Text-authoritative music", indexed.Title, "Directory role changes preserve txt-title precedence.");

            var listResult = await service.ScanAsync(index);
            TestAssert.Equal(1, listResult.PlaylistCount, "An explicit index directory may have any filesystem name.");
            TestAssert.Equal(1, listResult.Issues.Count, "An invalid JSON is isolated while valid collection documents remain available.");
            TestAssert.Equal(0, listResult.TrackCount, "Collection-index discovery cannot scan audio alongside its JSON.");
            TestAssert.Equal(1, (await workspace.SearchAsync(null)).Count, "Audio inside an index-only directory is not added to the library.");
            var playlist = (await workspace.GetPlaylistsAsync()).Single();
            TestAssert.Equal("Explicit list", playlist.Name, "Only the explicitly selected index source contributes lists.");
            TestAssert.Equal(indexed.Id, playlist.TrackIds.Single(), "A separate index source resolves previously indexed maidata hashes.");

            var preferences = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(LocalDirectorySettings.Normalize(preferences with
            {
                LocalMusicDirectories = Array.Empty<string>(),
                LocalDirectorySources = [music, index]
            }));
            var manuallyCreated = new MusicPlaylist { Id = "manual", Name = "Manual", TrackIds = [indexed.Id] };
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => exchange.ExportAsync(Path.Combine(musicPath, "new.json"), manuallyCreated));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => exchange.ExportAsync(Path.Combine(indexPath, "new.json"), manuallyCreated));
            var missing = await service.ScanAsync(music with { Id = "missing", Path = Path.Combine(root, "unavailable") });
            TestAssert.Equal(1, missing.Issues.Count, "A missing configured source returns an isolated directory issue.");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => service.ScanAsync(music, canceled.Token));

            preferences = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(preferences with { LocalDirectorySources = Array.Empty<LocalDirectorySource>() });
            TestAssert.Equal(1, (await workspace.SearchAsync(null)).Count, "Removing a directory configuration cannot delete indexed music.");
            TestAssert.Equal(1, (await workspace.GetPlaylistsAsync()).Count, "Removing a directory configuration cannot delete local playlists.");
            TestAssert.True(before.SequenceEqual(Snapshot(Path.Combine(root, "source"))), "All source file bytes, paths, timestamps and attributes stay unchanged.");
            TestAssert.True(!Directory.Exists(Path.Combine(workspace.DataDirectory, "Library")), "Neither directory role creates managed audio copies.");
            TestAssert.Equal(0, handler.RequestCount, "Independent local directory roles never contact a remote source.");
        }

        /// <summary>Rebinds saved unknown references after music indexing without rereading the source document.</summary>
        /// <param name="root">The delayed-binding fixture root.</param>
        /// <returns>A task representing reverse-order source configuration.</returns>
        private static async Task ValidateLateResolutionAsync(string root)
        {
            var musicPath = Path.Combine(root, "music");
            var indexPath = Path.Combine(root, "majcollection");
            var hash = CreateSong(Path.Combine(musicPath, "song"), "Later music");
            Directory.CreateDirectory(indexPath);
            var document = Path.Combine(indexPath, "late.json");
            await WriteCollectionAsync(document, "Imported before music", hash);
            using var workspace = new MusicWorkspace(Path.Combine(root, "data"));
            await workspace.LoadSettingsAsync();
            var service = new LocalDirectoryService(workspace, new MajCollectionExchange(workspace));
            var index = new LocalDirectorySource { Id = "index", Path = indexPath, Kind = LocalDirectoryKind.CollectionIndex };
            await service.ScanAsync(index);
            var unresolved = (await workspace.GetPlaylistsAsync()).Single();
            TestAssert.Equal(0, unresolved.TrackIds.Count, "A list may be configured before its audio library.");
            TestAssert.Equal(1, unresolved.MajCollection!.Entries.Count(entry => entry.TrackId is null), "Unknown portable references remain retained.");
            await File.WriteAllTextAsync(document, "{ deliberately invalid after explicit import");
            var changedDocument = File.ReadAllBytes(document);
            var music = new LocalDirectorySource { Id = "music", Path = musicPath, Kind = LocalDirectoryKind.MusicLibrary };
            var scan = await service.ScanAsync(music);
            TestAssert.Equal(0, scan.PlaylistCount, "The music pass does not read a previously configured JSON directory.");
            TestAssert.Equal(0, scan.Issues.Count, "Invalid current JSON cannot affect a music-only scan.");
            var rebound = (await workspace.GetPlaylistsAsync()).Single();
            TestAssert.Equal("Imported before music", rebound.Name, "Stored list metadata is used when newly indexed music becomes available.");
            TestAssert.Equal(1, rebound.TrackIds.Count, "The stored unknown reference is resolved against the newly indexed music.");
            TestAssert.True(changedDocument.SequenceEqual(File.ReadAllBytes(document)), "Delayed resolution cannot rewrite the source JSON.");
            var indexAgain = await service.ScanAsync(index);
            TestAssert.Equal(1, indexAgain.Issues.Count, "A bad majcollection document is reported once per configured scan.");
        }

        /// <summary>Rejects malformed account preferences before a non-secret settings document is published.</summary>
        /// <param name="root">The isolated validation directory.</param>
        /// <returns>A task representing account metadata validation.</returns>
        private static async Task ValidateAccountSettingsAsync(string root)
        {
            var store = new JsonSettingsStore(Path.Combine(root, "settings.json"));
            var account = new SavedEndpointAccount { Id = "account", Username = "player" };
            var endpoint = new EndpointConfiguration { Id = "source", Name = "Source", BaseUri = new Uri("https://example.invalid/api/"), Accounts = [account] };
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new AppSettings
            {
                Endpoints = [endpoint with { Accounts = [account with { AutoLogin = true }] }]
            }));
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new AppSettings
            {
                Endpoints = [endpoint with { SelectedAccountId = "absent" }]
            }));
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new AppSettings
            {
                Endpoints = [endpoint with { Accounts = [account, account with { Id = "other" }] }]
            }));
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new AppSettings
            {
                Endpoints = [endpoint with
                {
                    Accounts =
                    [
                        account with { RememberPassword = true, AutoLogin = true },
                        account with { Id = "other", Username = "other-player", RememberPassword = true, AutoLogin = true }
                    ]
                }]
            }));
            await store.SaveAsync(new AppSettings { Endpoints = [endpoint with { SelectedAccountId = account.Id }] });
            var restored = (await store.LoadAsync()).Endpoints.Single();
            TestAssert.Equal(account.Id, restored.SelectedAccountId, "A selected account without an automatic login preference is a valid persistent choice.");
        }

        /// <summary>Creates a chart-directory fixture without requiring valid decoded audio.</summary>
        /// <param name="directory">The isolated song directory.</param>
        /// <param name="title">The authoritative text title.</param>
        /// <returns>The exact portable maidata identity.</returns>
        private static string CreateSong(string directory, string title)
        {
            Directory.CreateDirectory(directory);
            var text = System.Text.Encoding.UTF8.GetBytes("&title=" + title + "\n&artist=Fixture artist\n");
            File.WriteAllBytes(Path.Combine(directory, "maidata.txt"), text);
            File.WriteAllBytes(Path.Combine(directory, "track.mp3"), "read-only fixture"u8.ToArray());
            return Convert.ToBase64String(MD5.HashData(text));
        }

        /// <summary>Writes a generated portable list into the test-owned source fixture.</summary>
        /// <param name="path">The fixture JSON destination.</param>
        /// <param name="name">The portable collection name.</param>
        /// <param name="hash">The exact maidata identity.</param>
        /// <returns>A task representing fixture creation.</returns>
        private static Task WriteCollectionAsync(string path, string name, string hash)
        {
            return File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Name = name, SongHashs = new[] { hash }, IsPlayList = true }));
        }

        /// <summary>Captures small generated source fixtures for read-only verification.</summary>
        /// <param name="root">The isolated source tree.</param>
        /// <returns>Stable path, metadata and content signatures.</returns>
        private static string[] Snapshot(string root)
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            {
                var file = new FileInfo(path);
                return Path.GetRelativePath(root, path) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks + "|"
                    + file.Attributes + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }).ToArray();
        }

        /// <summary>Rejects all requests so local source regressions cannot accidentally use a live endpoint.</summary>
        private sealed class RejectingTransport : HttpMessageHandler
        {
            /// <summary>Gets the number of requests that reached the rejecting transport.</summary>
            public int RequestCount { get; private set; }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                throw new InvalidOperationException("A local directory scan attempted an HTTP request.");
            }
        }
    }
}
