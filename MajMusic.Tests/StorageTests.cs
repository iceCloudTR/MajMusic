#nullable enable

using MajMusic.Core.Models;
using MajMusic.Infrastructure.Import;
using MajMusic.Infrastructure.Storage;
using System.Security.Cryptography;
using System.Text;

namespace MajMusic.Tests
{
    /// <summary>Exercises persistence, literal search, offline defaults, and non-destructive media imports.</summary>
    internal static class StorageTests
    {
        /// <summary>Runs the storage regressions inside an isolated temporary directory.</summary>
        /// <returns>A task representing all storage assertions.</returns>
        public static async Task RunAsync()
        {
            var temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-storage-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                await VerifyRepositoryAsync(temporaryRoot);
                await VerifySettingsAsync(temporaryRoot);
                await VerifyImportAsync(temporaryRoot);
                await VerifyReadOnlyScannerAsync(temporaryRoot);
            }
            finally
            {
                var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!temporaryRoot.StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(temporaryRoot).StartsWith("MajMusic-storage-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean a directory outside the storage test workspace.");
                }

                Directory.Delete(temporaryRoot, true);
            }
        }

        /// <summary>Checks nested read-only references, text titles, stable path identities and skipped corrupt metadata.</summary>
        /// <param name="root">The isolated test directory.</param>
        /// <returns>A task representing recursive scan assertions.</returns>
        private static async Task VerifyReadOnlyScannerAsync(string root)
        {
            var source = Path.Combine(root, "readonly-source");
            var nested = Path.Combine(source, "group", "numeric-folder-001");
            var deep = Path.Combine(source, "group", "deeper", "numeric-folder-002");
            var corrupt = Path.Combine(source, "bad-metadata");
            var fallback = Path.Combine(source, "fallback-title");
            foreach (var directory in new[] { source, nested, deep, corrupt, fallback })
            {
                Directory.CreateDirectory(directory);
            }

            var sameAudio = "shared audio fixture"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(source, "track.wav"), "root audio"u8.ToArray());
            await File.WriteAllTextAsync(Path.Combine(source, "maidata.txt"), "&title=Authoritative = Root\n&artist=Root artist\n");
            await File.WriteAllBytesAsync(Path.Combine(nested, "TRACK.MP3"), sameAudio);
            await File.WriteAllTextAsync(Path.Combine(nested, "maidata.txt"), "&title=Text title A\n&artist=Artist A\n", new UTF8Encoding(true));
            await File.WriteAllBytesAsync(Path.Combine(nested, "BG.PNG"), "cover fixture"u8.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(deep, "track.mp3"), sameAudio);
            await File.WriteAllTextAsync(Path.Combine(deep, "maidata.txt"), "&TITLE=文本标题 B\n&ARTIST=Artist B\n", Encoding.Unicode);
            await File.WriteAllBytesAsync(Path.Combine(corrupt, "track.wav"), sameAudio);
            await File.WriteAllBytesAsync(Path.Combine(corrupt, "maidata.txt"), [0xFF, 0xFF, 0x00]);
            await File.WriteAllBytesAsync(Path.Combine(fallback, "track.ogg"), sameAudio);
            await File.WriteAllTextAsync(Path.Combine(fallback, "maidata.txt"), "&artist=Fallback artist\n&inote_1=\n");

            var link = Path.Combine(source, "cycle-link");
            var linked = false;
            try
            {
                Directory.CreateSymbolicLink(link, source);
                linked = true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // Some Windows accounts cannot create symbolic links; other scanner assertions still run.
            }

            var sourceFiles = new[]
            {
                Path.Combine(source, "track.wav"), Path.Combine(source, "maidata.txt"),
                Path.Combine(nested, "TRACK.MP3"), Path.Combine(nested, "maidata.txt"), Path.Combine(nested, "BG.PNG"),
                Path.Combine(deep, "track.mp3"), Path.Combine(deep, "maidata.txt"),
                Path.Combine(corrupt, "track.wav"), Path.Combine(corrupt, "maidata.txt"),
                Path.Combine(fallback, "track.ogg"), Path.Combine(fallback, "maidata.txt")
            };
            var before = new Dictionary<string, (string Hash, DateTime Modified)>();
            foreach (var file in sourceFiles)
            {
                before[file] = (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))), File.GetLastWriteTimeUtc(file));
            }

            var scanner = new LocalMusicScanner();
            var result = await scanner.ScanAsync(source);
            TestAssert.Equal(4, result.Tracks.Count, "Nested songs and root songs are indexed while corrupt metadata is skipped");
            TestAssert.True(result.ScannedDirectoryCount >= 7, "Intermediate and song directories are visited recursively");
            var first = result.Tracks.Single(track => track.Title == "Text title A");
            var second = result.Tracks.Single(track => track.Title == "文本标题 B");
            TestAssert.True(first.Id != second.Id, "Identical audio in distinct metadata directories retains independent identities");
            TestAssert.True(first.Audio.LocalPath == Path.Combine(nested, "TRACK.MP3") && first.Audio.Sha256 is null,
                "Indexed audio references its original path without computing an audio hash");
            TestAssert.Equal(Path.Combine(nested, "BG.PNG"), first.Cover?.LocalPath, "Optional covers reference the original file");
            TestAssert.True(second.Cover is null, "Missing cover does not prevent indexing");
            TestAssert.Equal("Artist B", second.Artist, "BOM-marked metadata supplies the credited artist");
            TestAssert.True(result.Tracks.Any(track => track.Title == "Authoritative = Root"), "Text titles override directory names and preserve equals signs");
            TestAssert.True(result.Tracks.Any(track => track.Title == "fallback-title"), "Only missing usable text titles fall back to the directory name");
            TestAssert.Equal(Convert.ToBase64String(MD5.HashData(await File.ReadAllBytesAsync(Path.Combine(deep, "maidata.txt")))),
                second.MajChartHash, "Collection identity preserves the exact original metadata bytes including its BOM");
            TestAssert.True(result.Issues.Any(issue => issue.Path == corrupt), "Corrupt metadata is reported without hiding good songs");
            if (linked)
            {
                TestAssert.True(result.Issues.Any(issue => issue.Path == link), "A cyclic link is reported and never followed");
            }

            var repeated = await scanner.ScanAsync(source);
            TestAssert.True(result.Tracks.Select(track => track.Id).SequenceEqual(repeated.Tracks.Select(track => track.Id)),
                "Repeated scans produce stable path identities without duplicate entries");
            if (OperatingSystem.IsWindows())
            {
                var differentCasing = await scanner.ScanAsync(source.ToUpperInvariant());
                TestAssert.True(result.Tracks.Select(track => track.Id).SequenceEqual(differentCasing.Tracks.Select(track => track.Id)),
                    "Windows path casing does not create new identities");
            }

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync(source, canceled.Token));
            foreach (var file in sourceFiles)
            {
                TestAssert.Equal(before[file].Hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))), "Scanning preserves source bytes");
                TestAssert.Equal(before[file].Modified, File.GetLastWriteTimeUtc(file), "Scanning preserves source modification times");
            }

            TestAssert.True(!Directory.Exists(Path.Combine(source, "Library")), "Read-only scanning creates no managed source copies");
        }

        /// <summary>Checks database restarts, source isolation, literal patterns, ordered lists, and canceled writes.</summary>
        /// <param name="root">The isolated test directory.</param>
        /// <returns>A task representing repository assertions.</returns>
        private static async Task VerifyRepositoryAsync(string root)
        {
            var databasePath = Path.Combine(root, "music.db");
            var repository = new SqliteLibraryRepository(databasePath);
            var localTrack = CreateTrack("local:a", "100%_Song", "local");
            var remoteTrack = CreateTrack("server:b", "Another song", "server");
            await repository.UpsertTracksAsync([localTrack, remoteTrack]);
            var reopened = new SqliteLibraryRepository(databasePath);
            TestAssert.Equal(2, (await reopened.SearchAsync(null)).Count, "Library survives reopening");
            TestAssert.Equal(1, (await reopened.SearchAsync("%")).Count, "Percent is a literal keyword");
            TestAssert.Equal(1, (await reopened.SearchAsync("_")).Count, "Underscore is a literal keyword");
            TestAssert.Equal(0, (await reopened.SearchAsync("\\")).Count, "Escape character is a literal keyword");
            TestAssert.Equal(1, (await reopened.SearchAsync("ARTIST")).Count, "Artist search is case insensitive");
            TestAssert.Equal(1, (await reopened.SearchAsync("标签")).Count, "Tags are searchable");
            TestAssert.Equal(0, (await reopened.SearchAsync("' OR 1=1 --")).Count, "Search text cannot become SQL");

            await reopened.UpsertTracksAsync([remoteTrack with { Title = "Updated" }]);
            TestAssert.Equal(2, (await reopened.SearchAsync(null)).Count, "One source update preserves other sources");
            TestAssert.Equal("100%_Song", (await reopened.SearchAsync("100%"))[0].Title, "Existing local metadata remains intact");
            var playlist = new MusicPlaylist
            {
                Id = "playlist:one",
                Name = "My songs",
                TrackIds = [remoteTrack.Id, localTrack.Id, remoteTrack.Id]
            };
            await reopened.SavePlaylistAsync(playlist);
            await reopened.SavePlaylistAsync(new MusicPlaylist { Id = "playlist:two", Name = "Second" });
            var playlists = await new SqliteLibraryRepository(databasePath).GetPlaylistsAsync();
            TestAssert.Equal(2, playlists.Count, "Multiple playlists survive reopening");
            TestAssert.True(playlists[0].TrackIds.SequenceEqual(playlist.TrackIds), "Playlist ordering and duplicate references are preserved");
            await reopened.SavePlaylistAsync(playlist with { Name = "Renamed" });
            TestAssert.Equal(2, (await reopened.GetPlaylistsAsync()).Count, "Playlist update does not duplicate its identity");
            await reopened.DeletePlaylistAsync(playlist.Id);
            TestAssert.Equal(1, (await reopened.GetPlaylistsAsync()).Count, "Only the requested playlist is removed");
            TestAssert.Equal(2, (await reopened.SearchAsync(null)).Count, "Deleting a playlist preserves music metadata");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => reopened.UpsertTracksAsync(
                [CreateTrack("canceled", "Canceled", "local")], cancellation.Token));
            TestAssert.Equal(2, (await reopened.SearchAsync(null)).Count, "A canceled write publishes no new entry");
        }

        /// <summary>Checks default offline behavior, round trips, secret exclusion, and corrupt-file preservation.</summary>
        /// <param name="root">The isolated test directory.</param>
        /// <returns>A task representing settings assertions.</returns>
        private static async Task VerifySettingsAsync(string root)
        {
            var path = Path.Combine(root, "preferences", "settings.json");
            var store = new JsonSettingsStore(path);
            var defaults = await store.LoadAsync();
            TestAssert.True(!defaults.OnlineEnabled, "New installation defaults to offline");
            TestAssert.Equal(0, defaults.Endpoints.Count, "No endpoint is bundled");
            TestAssert.True(!File.Exists(path), "Reading defaults does not write a settings file");

            await store.SaveAsync(defaults);
            var json = await File.ReadAllTextAsync(path);
            TestAssert.True(!json.Contains("password", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("token", StringComparison.OrdinalIgnoreCase), "Settings do not contain credential fields");
            var configured = defaults with
            {
                OnlineEnabled = true,
                ActiveEndpointId = "example",
                Endpoints = [new EndpointConfiguration { Id = "example", Name = "Example", BaseUri = new Uri("https://example.invalid/api/") }]
            };
            await store.SaveAsync(configured);
            var restored = await new JsonSettingsStore(path).LoadAsync();
            TestAssert.True(restored.OnlineEnabled, "Explicit online preference survives reopening");
            TestAssert.Equal("example", restored.ActiveEndpointId, "Selected endpoint survives reopening");
            TestAssert.Equal(new Uri("https://example.invalid/api/"), restored.Endpoints[0].BaseUri, "API path prefix survives reopening");
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(defaults with
            {
                Endpoints = [new EndpointConfiguration { Id = "bad", Name = "Bad", BaseUri = new Uri("https://user:password@example.invalid/") }]
            }));
            TestAssert.Equal("example", (await store.LoadAsync()).ActiveEndpointId, "Rejected settings do not overwrite valid settings");

            const string broken = "{ definitely not valid JSON";
            await File.WriteAllTextAsync(path, broken);
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            TestAssert.Equal(broken, await File.ReadAllTextAsync(path), "Corrupt preferences are not silently replaced");
            await File.WriteAllTextAsync(path, "{\"password\":\"must-not-be-loaded\"}");
            await TestAssert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            TestAssert.Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length, "Settings leave no temporary files");
        }

        /// <summary>Checks import metadata, optional covers, content reuse, source preservation, and cancellation.</summary>
        /// <param name="root">The isolated test directory.</param>
        /// <returns>A task representing import assertions.</returns>
        private static async Task VerifyImportAsync(string root)
        {
            var source = Path.Combine(root, "Original song");
            var library = Path.Combine(root, "ManagedLibrary");
            Directory.CreateDirectory(source);
            var audioPath = Path.Combine(source, "TRACK.MP3");
            var coverPath = Path.Combine(source, "BG.PNG");
            byte[] audioBytes = [1, 2, 3, 4, 5, 6];
            byte[] coverBytes = [7, 8, 9];
            await File.WriteAllBytesAsync(audioPath, audioBytes);
            await File.WriteAllBytesAsync(coverPath, coverBytes);
            await File.WriteAllTextAsync(Path.Combine(source, "maidata.txt"), "&title=Imported title\n&artist=Imported artist\n&inote_5=not parsed\n");
            var importer = new LocalMusicImporter(library);
            var track = await importer.ImportDirectoryAsync(source);
            TestAssert.Equal("Imported title", track.Title, "Directory import reads title metadata");
            TestAssert.Equal("Imported artist", track.Artist, "Directory import reads artist metadata");
            TestAssert.True(track.Audio.LocalPath is not null && track.Cover?.LocalPath is not null, "Audio and optional cover have managed paths");
            TestAssert.True((await File.ReadAllBytesAsync(track.Audio.LocalPath!)).SequenceEqual(audioBytes), "Managed audio matches the source");
            TestAssert.True((await File.ReadAllBytesAsync(audioPath)).SequenceEqual(audioBytes), "Import preserves the original audio");
            TestAssert.True((await File.ReadAllBytesAsync(coverPath)).SequenceEqual(coverBytes), "Import preserves the original cover");
            TestAssert.True(track.Audio.LocalPath!.StartsWith(library, StringComparison.OrdinalIgnoreCase), "Imported file belongs to the managed library");
            var repeated = await Task.WhenAll(importer.ImportDirectoryAsync(source), importer.ImportDirectoryAsync(source));
            TestAssert.Equal(track.Id, repeated[0].Id, "Repeated import keeps the same audio identity");
            TestAssert.Equal(track.Audio.LocalPath, repeated[1].Audio.LocalPath, "Concurrent import reuses the completed audio file");
            TestAssert.Equal(2, Directory.GetFiles(library, "*", SearchOption.AllDirectories).Length, "Identical audio and covers are stored once");
            var single = await importer.ImportAudioFileAsync(audioPath);
            TestAssert.Equal(track.Id, single.Id, "File import uses the same content identity");
            TestAssert.True(single.Cover is null, "An independent audio import does not invent a cover");

            var noCoverSource = Path.Combine(root, "No cover");
            Directory.CreateDirectory(noCoverSource);
            await File.WriteAllBytesAsync(Path.Combine(noCoverSource, "track.opus"), [10, 11, 12]);
            var noCover = await importer.ImportDirectoryAsync(noCoverSource);
            TestAssert.Equal("No cover", noCover.Title, "Missing metadata falls back to the directory name");
            TestAssert.True(noCover.Cover is null, "Missing cover does not prevent importing audio");
            var emptyAudio = Path.Combine(root, "empty.mp3");
            await File.WriteAllBytesAsync(emptyAudio, []);
            await TestAssert.ThrowsAsync<InvalidDataException>(() => importer.ImportAudioFileAsync(emptyAudio));
            await TestAssert.ThrowsAsync<NotSupportedException>(() => importer.ImportAudioFileAsync(Path.Combine(root, "unsupported.exe")));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => importer.ImportDirectoryAsync(source, cancellation.Token));
            TestAssert.Equal(0, Directory.GetFiles(library, "*.tmp", SearchOption.AllDirectories).Length, "Failed and canceled imports publish no partial files");
        }

        /// <summary>Creates a metadata fixture without opening or decoding audio.</summary>
        /// <param name="id">The source-scoped identity.</param>
        /// <param name="title">The searchable title.</param>
        /// <param name="source">The origin identity.</param>
        /// <returns>The test metadata.</returns>
        private static MusicTrack CreateTrack(string id, string title, string source)
        {
            return new MusicTrack
            {
                Id = id,
                Title = title,
                Artist = source == "local" ? "ARTIST" : "Other",
                SourceId = source,
                Audio = new MediaResource { LocalPath = "not-opened.mp3" },
                Tags = source == "local" ? ["标签"] : []
            };
        }
    }
}
