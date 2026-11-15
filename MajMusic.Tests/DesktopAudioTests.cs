#nullable enable

using System.Diagnostics;
using System.Text;
using LibVLCSharp.Shared;
using MajMusic.Audio.Desktop;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;

namespace MajMusic.Tests
{
    /// <summary>Explicitly exercises the real desktop decoder; ordinary regression runs do not require audio hardware.</summary>
    internal static class DesktopAudioTests
    {
        /// <summary>Checks real WAV/MP3 decoding, native timelines, pause, seek, switching, completion and disposal with null output.</summary>
        /// <returns>A task representing the explicitly selected native regressions.</returns>
        public static async Task RunAsync()
        {
            var unknown = new AudioStreamInfo();
            TestAssert.True(unknown.CodecName is null && unknown.BitrateBitsPerSecond is null
                && unknown.SampleRateHz is null && unknown.Channels is null && unknown.FileSizeBytes is null,
                "Unknown audio properties must remain absent rather than inventing quality information.");
            TestAssert.True(!unknown.IsBitrateEstimated, "An unknown bitrate cannot be labeled as an estimate.");
            var root = CreateTemporaryRoot();
            try
            {
                var wave = Path.Combine(root, "本地 测试 track.wav");
                WriteQuietWave(wave, 6);
                var mp3 = Path.Combine(root, "encoded-track.mp3");
                await EncodeMp3Async(wave, mp3);
                await VerifyBackendAsync(root, wave, mp3);
                await VerifyQueueAsync(wave, mp3);
                Console.WriteLine("Native LibVLC WAV/MP3 checks passed using dummy audio output; this does not validate a physical device.");
            }
            finally
            {
                DeleteTemporaryRoot(root);
            }
        }

        /// <summary>Requests a short low-level tone through the system's default audio output without changing system volume.</summary>
        /// <returns>A task representing native playback, pause and seek through the default output route.</returns>
        public static async Task RunOutputAsync()
        {
            var root = CreateTemporaryRoot();
            try
            {
                var wave = Path.Combine(root, "quiet-output.wav");
                WriteQuietWave(wave, 3);
                await using var backend = new LibVlcPlaybackBackend();
                await backend.OpenAsync(wave);
                await backend.PlayAsync();
                await WaitUntilAsync(() => backend.Snapshot.Position > TimeSpan.FromMilliseconds(350));
                await backend.PauseAsync();
                var paused = backend.Snapshot.Position;
                await Task.Delay(250);
                TestAssert.True(Math.Abs((backend.Snapshot.Position - paused).TotalMilliseconds) < 100,
                    "Default-output playback must retain its paused native position.");
                await backend.SeekAsync(TimeSpan.FromMilliseconds(900));
                await backend.PlayAsync();
                await WaitUntilAsync(() => backend.Snapshot.Position > TimeSpan.FromMilliseconds(1200));
                await backend.StopAsync();
                TestAssert.Equal(PlaybackState.Stopped, backend.Snapshot.State, "Default-output test must stop its own tone.");
                Console.WriteLine("Default audio output accepted native playback/pause/seek/stop; audible and device-routing acceptance remains external.");
            }
            finally
            {
                DeleteTemporaryRoot(root);
            }
        }

        /// <summary>Verifies native commands and invalid-input behavior without a sound device.</summary>
        /// <param name="root">The isolated fixture directory.</param>
        /// <param name="wave">The generated local PCM file.</param>
        /// <param name="mp3">The locally encoded MP3 file.</param>
        /// <returns>A task representing real native transport checks.</returns>
        private static async Task VerifyBackendAsync(string root, string wave, string mp3)
        {
            await using (var missing = DesktopPlaybackBackendFactory.Create(Path.Combine(root, "missing-native-runtime")))
            {
                TestAssert.True(!missing.IsAvailable && !string.IsNullOrWhiteSpace(missing.Snapshot.Error),
                    "Missing native dependencies must preserve a useful unavailable reason.");
            }

            var backend = new LibVlcPlaybackBackend(useDummyAudioOutput: true);
            try
            {
                TestAssert.True(backend.IsAvailable, "The native engine must be available for this explicit validation group.");
                await TestAssert.ThrowsAsync<ArgumentException>(() => backend.OpenAsync("https://example.invalid/audio.mp3"));
                await TestAssert.ThrowsAsync<ArgumentException>(() => backend.OpenAsync("relative.wav"));
                await backend.OpenAsync(wave);
                TestAssert.True(backend.Snapshot.Duration is { TotalSeconds: > 5.8 and < 6.2 }, "WAV duration comes from the native parser.");
                var waveInfo = backend.Snapshot.AudioInfo
                    ?? throw new InvalidOperationException("Parsed WAV audio properties are missing.");
                TestAssert.Equal(44100, waveInfo.SampleRateHz, "WAV sample rate must come from its audio stream.");
                TestAssert.Equal(2, waveInfo.Channels, "The generated WAV is stereo.");
                TestAssert.Equal(new FileInfo(wave).Length, waveInfo.FileSizeBytes, "Audio details retain actual complete file length.");
                TestAssert.True(!string.IsNullOrWhiteSpace(waveInfo.CodecName), "The native codec description must be readable.");
                TestAssert.True(waveInfo.BitrateBitsPerSecond is > 1_400_000 and < 1_430_000,
                    "Sixteen-bit stereo PCM at 44.1 kHz must report its stream bitrate or a clearly marked container average.");
                if (!waveInfo.IsBitrateEstimated)
                {
                    TestAssert.Equal(1_411_200L, waveInfo.BitrateBitsPerSecond, "Native PCM stream bitrate must match its sample format.");
                }

                await backend.PlayAsync();
                await WaitUntilAsync(() => backend.Snapshot.Position > TimeSpan.FromMilliseconds(200));
                TestAssert.True(ReferenceEquals(waveInfo, backend.Snapshot.AudioInfo),
                    "Timeline polling must retain parsed audio properties without re-reading file or native tracks.");
                await backend.PauseAsync();
                var paused = backend.Snapshot.Position;
                await Task.Delay(350);
                TestAssert.Equal(PlaybackState.Paused, backend.Snapshot.State, "Pause must report the actual native paused state.");
                TestAssert.True(Math.Abs((backend.Snapshot.Position - paused).TotalMilliseconds) < 100, "Native media time must stop during pause.");
                await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.SeekAsync(TimeSpan.FromSeconds(100)));
                TestAssert.Equal(PlaybackState.Paused, backend.Snapshot.State, "An invalid seek must preserve the native paused state.");
                await backend.SeekAsync(TimeSpan.FromSeconds(2));
                TestAssert.True(Math.Abs(backend.Snapshot.Position.TotalSeconds - 2) < 0.35, "Seek must use native media time.");
                TestAssert.Equal(PlaybackState.Paused, backend.Snapshot.State, "Seeking a paused file must not resume audio.");
                await backend.PlayAsync();
                await WaitUntilAsync(() => backend.Snapshot.Position > TimeSpan.FromSeconds(2.2));
                await backend.StopAsync();
                TestAssert.Equal(PlaybackState.Stopped, backend.Snapshot.State, "Stop must stop the native player.");
                TestAssert.True(ReferenceEquals(waveInfo, backend.Snapshot.AudioInfo), "Stopping retains details of the loaded file.");

                await backend.OpenAsync(mp3);
                TestAssert.True(backend.Snapshot.Duration is { TotalSeconds: > 5.5 and < 6.5 }, "MP3 duration comes from actual native parsing.");
                var mp3Info = backend.Snapshot.AudioInfo
                    ?? throw new InvalidOperationException("Parsed MP3 audio properties are missing.");
                TestAssert.Equal(44100, mp3Info.SampleRateHz, "The native encoder produces 44.1 kHz MP3 audio.");
                TestAssert.Equal(2, mp3Info.Channels, "The MP3 fixture retains its two audio channels.");
                TestAssert.Equal(new FileInfo(mp3).Length, mp3Info.FileSizeBytes, "Switching media replaces file information.");
                TestAssert.True(mp3Info.BitrateBitsPerSecond is > 100_000 and < 160_000,
                    "The encoded MP3 stream should report a plausible metadata or explicitly estimated average bitrate.");
                TestAssert.True(!string.IsNullOrWhiteSpace(mp3Info.CodecName), "MP3 uses the native human-readable codec description.");
                await backend.PlayAsync();
                await WaitUntilAsync(() => backend.Snapshot.Position > TimeSpan.FromMilliseconds(150));
                await backend.PauseAsync();
                await backend.SeekAsync(TimeSpan.FromSeconds(4));
                TestAssert.True(Math.Abs(backend.Snapshot.Position.TotalSeconds - 4) < 0.4, "MP3 seeking reaches the requested native timeline position.");
                await backend.PlayAsync();
                await WaitUntilAsync(() => backend.Snapshot.State == PlaybackState.Completed, TimeSpan.FromSeconds(5));

                var corrupt = Path.Combine(root, "corrupt.mp3");
                await File.WriteAllTextAsync(corrupt, "This is not an audio stream.");
                await TestAssert.ThrowsAsync<InvalidDataException>(() => backend.OpenAsync(corrupt));
                await backend.OpenAsync(wave);
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => backend.PlayAsync(canceled.Token));
                await backend.PlayAsync();
                await backend.PauseAsync();
            }
            finally
            {
                await backend.DisposeAsync();
                await backend.DisposeAsync();
            }

            TestAssert.True(!backend.IsAvailable, "Disposed native resources must not remain available.");
            await TestAssert.ThrowsAsync<ObjectDisposedException>(() => backend.PlayAsync());
        }

        /// <summary>Verifies real backend switching and automatic completion through the application playback service.</summary>
        /// <param name="wave">The local WAV fixture.</param>
        /// <param name="mp3">The local MP3 fixture.</param>
        /// <returns>A task representing the real-backend queue checks.</returns>
        private static async Task VerifyQueueAsync(string wave, string mp3)
        {
            var first = Track("wave", wave);
            var second = Track("mp3", mp3);
            await using var playback = new PlaybackService(new LibVlcPlaybackBackend(useDummyAudioOutput: true));
            await playback.SetQueueAsync([first, second]);
            await playback.NextAsync();
            TestAssert.Equal(second.Id, playback.Snapshot.Track?.Id, "Next opens the second real local audio file.");
            await playback.PreviousAsync();
            TestAssert.Equal(first.Id, playback.Snapshot.Track?.Id, "Previous returns to the first real local audio file.");
            await playback.SeekAsync(TimeSpan.FromSeconds(5.5));
            await WaitUntilAsync(() => playback.Snapshot.Track?.Id == second.Id && playback.Snapshot.State == PlaybackState.Playing,
                TimeSpan.FromSeconds(5));
            await playback.PauseAsync();
            TestAssert.Equal(PlaybackState.Paused, playback.Snapshot.State, "Automatic native completion must retain a controllable next file.");
        }

        /// <summary>Creates one local fixture entry.</summary>
        /// <param name="id">The fixture identity.</param>
        /// <param name="path">The local audio file.</param>
        /// <returns>The local music entry.</returns>
        private static MusicTrack Track(string id, string path)
        {
            return new MusicTrack { Id = id, Title = id, Audio = new MediaResource { LocalPath = path } };
        }

        /// <summary>Encodes a generated PCM fixture locally rather than downloading music.</summary>
        /// <param name="wave">The generated source WAV.</param>
        /// <param name="mp3">The destination MP3 file.</param>
        /// <returns>A task representing the complete local encode and flush.</returns>
        private static async Task EncodeMp3Async(string wave, string mp3)
        {
            using var engine = new LibVLC("--quiet", "--no-video", "--no-lua", "--no-metadata-network-access", "--services-discovery=");
            var output = mp3.Replace('\\', '/');
            using var media = new Media(engine, wave, FromType.FromPath,
                $":sout=#transcode{{acodec=mp3,ab=128,channels=2,samplerate=44100}}:std{{access=file,mux=raw,dst='{output}'}}",
                ":no-sout-all", ":sout-keep");
            using var player = new MediaPlayer(media);
            TestAssert.True(player.Play(), "The native encoder must start the self-generated audio fixture.");
            try
            {
                await WaitUntilAsync(() => player.State is VLCState.Ended or VLCState.Error, TimeSpan.FromSeconds(12));
                TestAssert.Equal(VLCState.Ended, player.State, "The local MP3 encode must finish successfully.");
            }
            finally
            {
                player.Stop();
            }

            TestAssert.True(File.Exists(mp3) && new FileInfo(mp3).Length > 100, "The native encoder must produce a nonempty MP3 fixture.");
        }

        /// <summary>Waits for an actual observed state with a bounded deadline.</summary>
        /// <param name="condition">The observed state predicate.</param>
        /// <param name="timeout">An optional deadline.</param>
        /// <returns>A task representing the observed state.</returns>
        /// <exception cref="InvalidOperationException">The state does not occur before the deadline.</exception>
        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition())
            {
                if (elapsed.Elapsed > (timeout ?? TimeSpan.FromSeconds(5)))
                {
                    throw new InvalidOperationException("The expected native audio state did not occur before the deadline.");
                }

                await Task.Delay(30);
            }
        }

        /// <summary>Writes a short, low-amplitude stereo PCM tone for deterministic native decoding.</summary>
        /// <param name="path">The fixture destination.</param>
        /// <param name="seconds">The fixture duration.</param>
        internal static void WriteQuietWave(string path, int seconds)
        {
            const int sampleRate = 44100;
            var samples = sampleRate * seconds;
            var byteLength = samples * 4;
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.ASCII);
            writer.Write("RIFF"u8);
            writer.Write(36 + byteLength);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 4);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(byteLength);
            for (var index = 0; index < samples; index++)
            {
                var sample = (short)(256 * Math.Sin(2 * Math.PI * 440 * index / sampleRate));
                writer.Write(sample);
                writer.Write(sample);
            }
        }

        /// <summary>Creates a uniquely named directory for this explicit native validation run.</summary>
        /// <returns>The absolute temporary fixture directory.</returns>
        private static string CreateTemporaryRoot()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-desktop-audio-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>Removes only the verified fixture directory created for this run.</summary>
        /// <param name="root">The native validation fixture directory.</param>
        /// <exception cref="InvalidOperationException">The path is outside the expected temporary directory.</exception>
        private static void DeleteTemporaryRoot(string root)
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(root);
            if (!fullPath.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullPath).StartsWith("MajMusic-desktop-audio-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to clean outside the native audio validation directory.");
            }

            Directory.Delete(fullPath, true);
        }
    }
}
