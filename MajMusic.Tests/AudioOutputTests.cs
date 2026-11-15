#nullable enable

using System.Reflection;
using LibVLCSharp.Shared;
using MajMusic.Audio.Desktop;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Tests
{
    /// <summary>Checks persisted audio preferences and explicitly selected native controls without audible output.</summary>
    internal static class AudioOutputTests
    {
        /// <summary>Checks defaults, settings round trips and invalid-value preservation without loading native audio.</summary>
        /// <returns>A task representing hardware-independent settings validation.</returns>
        public static async Task RunAsync()
        {
            var defaults = new AppSettings();
            TestAssert.Equal(100, defaults.AudioOutput.Volume, "Application audio defaults to an unamplified volume.");
            TestAssert.True(!defaults.AudioOutput.Muted && defaults.AudioOutput.DeviceId is null,
                "A new installation starts unmuted on the system-default route.");
            await using (var unavailable = new PlaybackService(new UnavailablePlaybackBackend()))
            {
                TestAssert.True(unavailable.AudioOutput is null, "Backends without output controls remain compatible with the optional capability.");
            }

            var root = CreateRoot();
            try
            {
                var path = Path.Combine(root, "preferences.json");
                var store = new JsonSettingsStore(path);
                var settings = new AppSettings
                {
                    AudioOutput = new AudioOutputPreferences { Volume = 37, Muted = true, DeviceId = "native-device-1" }
                };
                await store.SaveAsync(settings);
                var savedBytes = await File.ReadAllBytesAsync(path);
                var reopened = await new JsonSettingsStore(path).LoadAsync();
                TestAssert.Equal(settings.AudioOutput, reopened.AudioOutput, "Audio device and levels survive a settings-store restart.");
                TestAssert.True(!reopened.OnlineEnabled && reopened.Endpoints.Count == 0,
                    "Persisting audio controls must not enable or create online sources.");
                await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings with
                {
                    AudioOutput = settings.AudioOutput with { Volume = -1 }
                }));
                await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings with
                {
                    AudioOutput = settings.AudioOutput with { Volume = 101 }
                }));
                await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings with
                {
                    AudioOutput = settings.AudioOutput with { DeviceId = "invalid\0native" }
                }));
                await TestAssert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings with { AudioOutput = null! }));
                var afterInvalid = await File.ReadAllBytesAsync(path);
                TestAssert.True(savedBytes.SequenceEqual(afterInvalid),
                    "Invalid audio preferences cannot replace a valid settings document.");

                var legacy = Path.Combine(root, "legacy.json");
                await File.WriteAllTextAsync(legacy, "{\"onlineEnabled\":false,\"endpoints\":[]}");
                TestAssert.Equal(defaults.AudioOutput, (await new JsonSettingsStore(legacy).LoadAsync()).AudioOutput,
                    "Older settings files receive compatible application-audio defaults.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        /// <summary>Checks native levels, route fallback, media-clock retention and idle device discovery using silent output.</summary>
        /// <returns>A task representing explicitly selected native audio controls.</returns>
        public static async Task RunNativeAsync()
        {
            var root = CreateRoot();
            try
            {
                var first = Path.Combine(root, "first.wav");
                var second = Path.Combine(root, "second.wav");
                DesktopAudioTests.WriteQuietWave(first, 6);
                DesktopAudioTests.WriteQuietWave(second, 7);
                await using (var backend = new LibVlcPlaybackBackend(useDummyAudioOutput: true))
                {
                    var idleDevices = await backend.GetOutputDevicesAsync();
                    TestAssert.Equal(1, idleDevices.Count(device => device.Id is null), "Device discovery must provide exactly one system-default entry.");
                    TestAssert.Equal(PlaybackState.Empty, backend.Snapshot.State, "Idle enumeration must not prepare or play media.");
                    await backend.SetVolumeAsync(42);
                    await backend.SetMutedAsync(true);
                    TestAssert.Equal(42, backend.Preferences.Volume, "Volume can be configured before a song is opened.");
                    await backend.OpenAsync(first);
                    var observed = UseMemoryOutput(backend);
                    await backend.PlayAsync();
                    await observed.WaitForAsync(0, true);
                    await backend.SetMutedAsync(false);
                    await observed.WaitForAsync(0.42f, false);
                    await backend.SetVolumeAsync(63);
                    await observed.WaitForAsync(0.63f, false);
                    await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.SetVolumeAsync(-1));
                    await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.SetVolumeAsync(101));
                    using (var cancellation = new CancellationTokenSource())
                    {
                        cancellation.Cancel();
                        await TestAssert.ThrowsAsync<OperationCanceledException>(() => backend.SetVolumeAsync(20, cancellation.Token));
                    }

                    TestAssert.Equal(PlaybackState.Playing, backend.Snapshot.State, "Canceled or invalid output controls must not stop the current music.");
                    await backend.SetOutputDeviceAsync("generated-unavailable-device");
                    TestAssert.True(backend.Preferences.DeviceId is null && !string.IsNullOrWhiteSpace(backend.OutputDeviceNotice),
                        "An unavailable stored device falls back to the system default with an actionable result.");
                    TestAssert.Equal(PlaybackState.Playing, backend.Snapshot.State, "Route fallback must preserve playing state.");
                    await backend.PauseAsync();
                    var paused = backend.Snapshot.Position;
                    await backend.GetOutputDevicesAsync();
                    await backend.SetVolumeAsync(77);
                    await backend.SetOutputDeviceAsync(null);
                    TestAssert.True(backend.OutputDeviceNotice is null, "An explicit valid route choice clears the old fallback notice.");
                    TestAssert.Equal(PlaybackState.Paused, backend.Snapshot.State, "Enumeration and route changes must preserve pause.");
                    TestAssert.Equal(paused, backend.Snapshot.Position, "Output controls must not seek the paused media.");
                    await backend.SetMutedAsync(true);
                    await backend.OpenAsync(second);
                    var nextObserved = UseMemoryOutput(backend);
                    await backend.PlayAsync();
                    await nextObserved.WaitForAsync(0, true);
                    await backend.SetMutedAsync(false);
                    await nextObserved.WaitForAsync(0.77f, false);
                    await backend.StopAsync();
                }

                await using (var enumeration = new LibVlcPlaybackBackend())
                {
                    var devices = await enumeration.GetOutputDevicesAsync();
                    TestAssert.Equal(1, devices.Count(device => device.Id is null), "Real native discovery includes a single default route.");
                    TestAssert.Equal(PlaybackState.Empty, enumeration.Snapshot.State,
                        "Real-device discovery alone must never start audio or open a file.");
                    Console.WriteLine($"Native output controls reached the memory sink without audible playback; discovered {devices.Count - 1} concrete device(s) without playback.");
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        /// <summary>Reads native test state after a serialized command has completed.</summary>
        /// <param name="backend">The generated fixture's backend.</param>
        /// <returns>The existing native player, without creating or mutating it.</returns>
        /// <exception cref="InvalidOperationException">The fixture did not prepare a player.</exception>
        private static MediaPlayer NativePlayer(LibVlcPlaybackBackend backend)
        {
            return typeof(LibVlcPlaybackBackend).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(backend) as MediaPlayer
                ?? throw new InvalidOperationException("The native audio fixture has no prepared player.");
        }

        /// <summary>Overrides the fixture's output with a native memory sink that records volume requests and cannot reach a sound device.</summary>
        /// <param name="backend">The prepared, stopped fixture backend.</param>
        /// <returns>The observed native volume callbacks retained for the fixture lifetime.</returns>
        private static NativeOutputObservation UseMemoryOutput(LibVlcPlaybackBackend backend)
        {
            var player = NativePlayer(backend);
            var observed = new NativeOutputObservation();
            player.SetAudioFormat("S16N", 44100, 2);
            player.SetAudioCallbacks(observed.PlayCallback, null!, null!, null!, null!);
            player.SetVolumeCallback(observed.VolumeCallback);
            TestAssert.True(player.SetAudioOutput("amem"), "The explicit validation sink must be selected before playback.");
            return observed;
        }

        /// <summary>Retains native callback delegates and records requested levels without forwarding any audio to a device.</summary>
        private sealed class NativeOutputObservation
        {
            /// <summary>Stores the last native volume scalar.</summary>
            private float _volume = float.NaN;
            /// <summary>Stores the last native mute state.</summary>
            private bool _muted;
            /// <summary>Creates callbacks whose lifetime is retained by the fixture.</summary>
            public NativeOutputObservation()
            {
                PlayCallback = static (_, _, _, _) => { };
                VolumeCallback = (_, volume, muted) =>
                {
                    Volatile.Write(ref _volume, volume);
                    Volatile.Write(ref _muted, muted);
                };
            }

            /// <summary>Gets the memory sink that discards samples without accessing physical output.</summary>
            public MediaPlayer.LibVLCAudioPlayCb PlayCallback { get; }
            /// <summary>Gets the native-level observation callback.</summary>
            public MediaPlayer.LibVLCVolumeCb VolumeCallback { get; }

            /// <summary>Waits for the native output module to receive an expected application level.</summary>
            /// <param name="volume">The expected scalar from zero to one.</param>
            /// <param name="muted">The expected mute state.</param>
            /// <returns>A task representing the observed native callback.</returns>
            public async Task WaitForAsync(float volume, bool muted)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (Math.Abs(Volatile.Read(ref _volume) - volume) > 0.001f || float.IsNaN(Volatile.Read(ref _volume))
                    || Volatile.Read(ref _muted) != muted)
                {
                    await Task.Delay(10, deadline.Token);
                }
            }
        }

        /// <summary>Creates one isolated test-owned directory.</summary>
        /// <returns>The fully qualified generated root.</returns>
        private static string CreateRoot()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-audio-controls-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>Deletes only a verified generated directory under the operating-system temporary root.</summary>
        /// <param name="root">The generated directory to remove.</param>
        /// <exception cref="InvalidOperationException">The directory is outside the owned validation scope.</exception>
        private static void DeleteRoot(string root)
        {
            var fullPath = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullPath).StartsWith("MajMusic-audio-controls-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside audio-control validation.");
            }

            Directory.Delete(fullPath, true);
        }
    }
}
