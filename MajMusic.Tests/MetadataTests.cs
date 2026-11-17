#nullable enable

using MajMusic.Audio.Desktop;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Metadata;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks progressive-library metadata lookup independently of playback and remote downloads.</summary>
    internal static class MetadataTests
    {
        /// <summary>Validates persistent fingerprints, independent metadata identity and cancellation without native audio.</summary>
        /// <returns>A task representing hardware-independent metadata regressions.</returns>
        public static async Task RunAsync()
        {
            var root = CreateTemporaryRoot();
            try
            {
                var source = Path.Combine(root, "source");
                Directory.CreateDirectory(source);
                var wave = Path.Combine(source, "track.wav");
                DesktopAudioTests.WriteQuietWave(wave, 2);
                var originalBytes = await File.ReadAllBytesAsync(wave);
                var originalTime = File.GetLastWriteTimeUtc(wave);
                var track = LocalTrack(wave);
                var parser = new FakeReader();
                var cache = Path.Combine(root, "cache");
                var provider = new CachedTrackMetadataProvider(parser, cache);
                TestAssert.True(!Directory.Exists(cache), "Constructing metadata services must not perform I/O.");

                var result = await provider.EnrichAsync(track);
                TestAssert.Equal(1, parser.ReadCount, "The first lookup parses the complete local file once.");
                TestAssert.Equal(TimeSpan.FromSeconds(2), result.Duration, "Audio duration must exist before a playback command.");
                TestAssert.Equal(1_411_200L, result.AudioInfo?.BitrateBitsPerSecond, "Parsed bitrate is available to a library row.");
                TestAssert.Equal(track.Title, result.Title, "Audio metadata cannot replace a valid maidata title.");
                TestAssert.Equal(track.Id, result.Id, "Audio metadata cannot change a source entry identity.");
                TestAssert.Equal(wave, result.Audio.LocalPath, "Metadata lookup must retain the read-only original path.");
                TestAssert.Equal(new FileInfo(wave).Length, result.AudioMetadataLength, "Metadata carries its complete-file fingerprint.");

                await provider.EnrichAsync(result);
                TestAssert.Equal(1, parser.ReadCount, "An unchanged enriched row must not be parsed again.");
                var reopenedParser = new FakeReader { RejectReads = true };
                var reopened = new CachedTrackMetadataProvider(reopenedParser, cache);
                var fromDisk = await reopened.EnrichAsync(track);
                TestAssert.Equal(result.Duration, fromDisk.Duration, "A new provider reuses persisted metadata before playback.");
                TestAssert.Equal(0, reopenedParser.ReadCount, "A persisted cache hit must not initialize a native parser.");

                var repository = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
                await repository.UpsertTracksAsync([result]);
                var persisted = (await repository.SearchAsync(null)).Single();
                TestAssert.Equal(result.Duration, persisted.Duration, "Duration survives database serialization.");
                TestAssert.Equal(result.AudioInfo, persisted.AudioInfo, "Audio stream properties survive database serialization.");
                TestAssert.Equal(result.AudioMetadataLastWriteUtcTicks, persisted.AudioMetadataLastWriteUtcTicks,
                    "File fingerprints survive database serialization.");

                var online = track with
                {
                    Id = "remote:one",
                    RemoteId = "one",
                    EndpointId = "guest-session",
                    SourceName = "Test source",
                    Audio = new MediaResource { RemoteUri = new Uri("https://example.invalid/music.mp3") }
                };
                var unknown = await provider.EnrichAsync(online);
                TestAssert.Equal(online, unknown, "An uncached online row remains unknown rather than downloading the audio.");
                TestAssert.Equal(1, parser.ReadCount, "Remote-only metadata lookup must never reach the local parser.");
                var afterBytes = await File.ReadAllBytesAsync(wave);
                TestAssert.True(originalBytes.SequenceEqual(afterBytes), "Reading metadata must not change source bytes.");
                TestAssert.Equal(originalTime, File.GetLastWriteTimeUtc(wave), "Reading metadata must not change source timestamps.");
                TestAssert.Equal(1, Directory.GetFiles(source).Length, "No cache or temporary files may be written into the song directory.");

                File.SetLastWriteTimeUtc(wave, originalTime.AddSeconds(3));
                await provider.EnrichAsync(result);
                TestAssert.Equal(2, parser.ReadCount, "A changed source timestamp invalidates both in-memory and persisted properties.");
                await using (var stream = new FileStream(wave, FileMode.Append, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(new byte[] { 0, 0, 0, 0 });
                }

                await provider.EnrichAsync(track);
                TestAssert.Equal(3, parser.ReadCount, "Changed source length invalidates cached properties.");
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => provider.EnrichAsync(track, cancellation.Token));
                TestAssert.Equal(3, parser.ReadCount, "Canceled metadata work must not parse source media.");
                var missing = track with { Audio = track.Audio with { LocalPath = Path.Combine(source, "missing.wav") } };
                TestAssert.Equal(missing, await provider.EnrichAsync(missing), "A missing source remains an unresolved entry.");
                await VerifyParserUpgradeAsync(root, track);
                await VerifyInvalidCacheAsync(root, track);
                await VerifyChangedDuringReadAsync(root, track);
                await provider.DisposeAsync();
                await provider.DisposeAsync();
                TestAssert.True(parser.IsDisposed, "Provider disposal releases its owned metadata parser exactly once.");
                TestAssert.Equal(1, parser.DisposeCount, "Repeated provider disposal shares the same parser-release task.");
                await TestAssert.ThrowsAsync<ObjectDisposedException>(() => provider.EnrichAsync(track));
                await reopened.DisposeAsync();
            }
            finally
            {
                DeleteTemporaryRoot(root);
            }
        }

        /// <summary>Updates existing local metadata on startup without touching the audio or its saved path.</summary>
        /// <param name="root">The isolated fixture root.</param>
        /// <param name="track">The original local fixture entry.</param>
        /// <returns>A task representing the versioned database and disk-cache upgrade.</returns>
        private static async Task VerifyParserUpgradeAsync(string root, MusicTrack track)
        {
            var data = Path.Combine(root, "upgraded-data");
            var cache = Path.Combine(data, "metadata");
            await using var legacy = new CachedTrackMetadataProvider(new FakeReader(), cache);
            var stale = await legacy.EnrichAsync(track);
            var remote = stale with
            {
                Id = "online:uncached",
                RemoteId = "uncached",
                SourceId = "test",
                Audio = new MediaResource { RemoteUri = new Uri("https://example.invalid/track") }
            };
            var repository = new SqliteLibraryRepository(Path.Combine(data, "library.db"));
            await repository.UpsertTracksAsync([stale, remote]);
            var path = track.Audio.LocalPath!;
            var original = await File.ReadAllBytesAsync(path);
            var originalTime = File.GetLastWriteTimeUtc(path);
            using var workspace = new MusicWorkspace(data);
            var parser = new FakeReader { CacheRevision = 1, Duration = TimeSpan.FromSeconds(1), Bitrate = 128_000 };
            var provider = new CachedTrackMetadataProvider(parser, cache);
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()),
                dispatcher: action => action(), metadata: provider);
            await model.InitializeAsync();
            await model.AudioMetadataUpgrade;

            var updated = (await repository.SearchAsync(null)).Single(item => item.Id == track.Id);
            TestAssert.Equal(1, parser.ReadCount, "The upgrade parses existing audio once and ignores tracks without a local file.");
            TestAssert.Equal(1, updated.AudioMetadataRevision, "Upgraded metadata records its parser revision in the library.");
            TestAssert.Equal(TimeSpan.FromSeconds(1), updated.Duration, "An old persisted duration must be recalculated.");
            TestAssert.Equal(128_000L, updated.AudioInfo?.BitrateBitsPerSecond, "An old on-disk bitrate must be recalculated.");
            TestAssert.Equal(path, updated.Audio.LocalPath, "Refreshing properties retains the original validated local file.");
            var upgradedBytes = await File.ReadAllBytesAsync(path);
            TestAssert.True(original.SequenceEqual(upgradedBytes), "The upgrade leaves the current audio bytes intact.");
            TestAssert.Equal(originalTime, File.GetLastWriteTimeUtc(path), "The upgrade does not write to the music file.");
            TestAssert.True(!workspace.IsOnline, "Local property upgrades do not require enabling online mode.");
            TestAssert.Equal(0, (await workspace.FindOutdatedAudioMetadataAsync(parser.CacheRevision)).Count,
                "The same parser revision must not queue already upgraded metadata again.");

            await using var reopened = new CachedTrackMetadataProvider(new FakeReader { CacheRevision = 1, RejectReads = true }, cache);
            var fromDisk = await reopened.EnrichAsync(track);
            TestAssert.Equal(updated.AudioInfo, fromDisk.AudioInfo, "A subsequent scan reuses the upgraded disk record.");
        }

        /// <summary>Checks the independent real parser while a separate native playback owner remains active.</summary>
        /// <returns>A task representing explicitly selected native metadata integration.</returns>
        public static async Task RunNativeAsync()
        {
            var root = CreateTemporaryRoot();
            try
            {
                var wave = Path.Combine(root, "metadata.wav");
                DesktopAudioTests.WriteQuietWave(wave, 5);
                var originalTime = File.GetLastWriteTimeUtc(wave);
                await using var backend = new LibVlcPlaybackBackend(useDummyAudioOutput: true);
                await backend.OpenAsync(wave);
                await backend.PlayAsync();
                var before = backend.Snapshot;
                await using var reader = new LocalAudioMetadataReader();
                var metadata = await reader.ReadAsync(wave);
                TestAssert.True(metadata.Duration is { TotalSeconds: > 4.8 and < 5.2 }, "Metadata parser reads duration without playing the scanned file.");
                TestAssert.Equal(44100, metadata.AudioInfo?.SampleRateHz, "Independent parser obtains sample rate.");
                TestAssert.Equal(2, metadata.AudioInfo?.Channels, "Independent parser obtains channel count.");
                TestAssert.True(metadata.AudioInfo?.BitrateBitsPerSecond is > 1_400_000 and < 1_430_000,
                    "Independent parser obtains actual PCM bitrate or a marked average estimate.");
                TestAssert.Equal(PlaybackState.Playing, backend.Snapshot.State, "Metadata reads must not pause or replace the actual player.");
                TestAssert.True(backend.Snapshot.Position >= before.Position, "Metadata parsing cannot reset the actual player's clock.");
                await backend.PauseAsync();
                var paused = backend.Snapshot.Position;
                await reader.ReadAsync(wave);
                TestAssert.Equal(PlaybackState.Paused, backend.Snapshot.State, "Metadata parsing must not resume paused playback.");
                TestAssert.Equal(paused, backend.Snapshot.Position, "Metadata parsing must not seek the playback owner.");
                TestAssert.Equal(originalTime, File.GetLastWriteTimeUtc(wave), "The native parser leaves source modification time unchanged.");
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(wave, cancellation.Token));
                await reader.DisposeAsync();
                await TestAssert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadAsync(wave));
                Console.WriteLine("Independent native metadata parsing passed without starting or changing the playback owner (dummy output).");
            }
            finally
            {
                DeleteTemporaryRoot(root);
            }
        }

        /// <summary>Checks that disposable damaged cache data cannot block library enrichment.</summary>
        /// <param name="root">The isolated fixture root.</param>
        /// <param name="track">An existing source entry.</param>
        /// <returns>A task representing corrupt-cache recovery.</returns>
        private static async Task VerifyInvalidCacheAsync(string root, MusicTrack track)
        {
            var cache = Path.Combine(root, "bad-cache");
            var parser = new FakeReader();
            var provider = new CachedTrackMetadataProvider(parser, cache);
            await provider.EnrichAsync(track);
            var path = Directory.GetFiles(cache).Single();
            await File.WriteAllTextAsync(path, "not-json");
            await provider.EnrichAsync(track);
            TestAssert.Equal(2, parser.ReadCount, "A damaged disposable cache is reparsed instead of failing the library.");
            var file = new FileInfo(track.Audio.LocalPath!);
            await File.WriteAllTextAsync(path, $"{{\"FileLength\":{file.Length},\"LastWriteUtcTicks\":{file.LastWriteTimeUtc.Ticks},\"Metadata\":null}}");
            await provider.EnrichAsync(track);
            TestAssert.Equal(3, parser.ReadCount, "A null metadata cache record is not a valid hit.");
        }

        /// <summary>Rejects properties parsed while the source itself was changing.</summary>
        /// <param name="root">The isolated fixture root.</param>
        /// <param name="track">An existing source entry.</param>
        /// <returns>A task representing fingerprint-race validation.</returns>
        private static async Task VerifyChangedDuringReadAsync(string root, MusicTrack track)
        {
            var parser = new FakeReader
            {
                BeforeReturn = path => File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2))
            };
            var cache = Path.Combine(root, "changed-cache");
            var provider = new CachedTrackMetadataProvider(parser, cache);
            var result = await provider.EnrichAsync(track);
            TestAssert.True(result.Duration is null && result.AudioInfo is null && result.AudioMetadataLength is null,
                "Changing files cannot publish metadata for a mismatched byte snapshot.");
            TestAssert.True(!Directory.Exists(cache), "A changing source must not publish a persistent cache record.");
        }

        /// <summary>Builds a local entry whose title comes from chart metadata.</summary>
        /// <param name="path">The complete local fixture path.</param>
        /// <returns>An unparsed library entry.</returns>
        private static MusicTrack LocalTrack(string path)
        {
            return new MusicTrack { Id = "local:metadata", Title = "Title from maidata", Audio = new MediaResource { LocalPath = path } };
        }

        /// <summary>Creates a test-owned temporary directory.</summary>
        /// <returns>The normalized absolute fixture path.</returns>
        private static string CreateTemporaryRoot()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-metadata-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>Removes only the verified temporary directory created by this test group.</summary>
        /// <param name="root">The fixture directory to remove.</param>
        /// <exception cref="InvalidOperationException">The directory is outside the test-owned area.</exception>
        private static void DeleteTemporaryRoot(string root)
        {
            var fullPath = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullPath).StartsWith("MajMusic-metadata-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to clean outside the metadata fixture directory.");
            }

            Directory.Delete(fullPath, true);
        }

        /// <summary>Returns deterministic media properties while counting actual parse requests.</summary>
        private sealed class FakeReader : IAudioMetadataReader, IAsyncDisposable
        {
            /// <inheritdoc />
            public int CacheRevision { get; init; }
            /// <summary>Gets the duration supplied by this parser revision.</summary>
            public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2);
            /// <summary>Gets the bitrate supplied by this parser revision.</summary>
            public long Bitrate { get; init; } = 1_411_200;
            /// <summary>Gets the number of requests made to this parser.</summary>
            public int ReadCount { get; private set; }
            /// <summary>Gets whether the owning provider released this parser.</summary>
            public bool IsDisposed { get; private set; }
            /// <summary>Gets how often parser disposal was requested.</summary>
            public int DisposeCount { get; private set; }
            /// <summary>Gets whether an unexpected parser call must fail.</summary>
            public bool RejectReads { get; init; }
            /// <summary>Gets an optional source mutation performed before a read completes.</summary>
            public Action<string>? BeforeReturn { get; init; }

            /// <inheritdoc />
            public Task<AudioMetadata> ReadAsync(string localPath, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (RejectReads)
                {
                    throw new InvalidOperationException("A cache hit unexpectedly initialized a media parser.");
                }

                ReadCount++;
                BeforeReturn?.Invoke(localPath);
                return Task.FromResult(new AudioMetadata
                {
                    Duration = Duration,
                    AudioInfo = new AudioStreamInfo { SampleRateHz = 44100, Channels = 2, BitrateBitsPerSecond = Bitrate }
                });
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                IsDisposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
