#nullable enable

using System.Diagnostics;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks audio-setting presentation behavior without native devices, network requests or filesystem writes.</summary>
    internal static class AudioSettingsPresentationTests
    {
        /// <summary>Runs persistence, route, coalescing, transport ownership and shutdown checks.</summary>
        /// <returns>A task representing the completed settings regressions.</returns>
        public static async Task RunAsync()
        {
            await ValidatePersistenceAndIsolationAsync();
            await ValidateUnavailableRouteAsync();
            await ValidateCoalescedVolumeAsync();
            await ValidateTransportOwnershipAsync();
            await ValidateShutdownAsync();
        }

        /// <summary>Applies saved controls locally and patches only audio fields when accounts or directories change independently.</summary>
        /// <returns>A task representing audio persistence and unrelated-setting isolation.</returns>
        private static async Task ValidatePersistenceAndIsolationAsync()
        {
            var memory = new PreferenceMemory
            {
                Settings = CreateSettings() with { AudioOutput = new AudioOutputPreferences { Volume = 64, Muted = true, DeviceId = "speakers" } }
            };
            var backend = new OutputBackend();
            var workspace = new SettingsWorkspace(memory);
            await using (var view = CreateView(workspace, backend))
            {
                await view.InitializeAsync();
                view.ShowLibraryModeCommand.Execute(null);
                await view.HomeUpdate;
                await view.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.CanConfigureAudio, "An optional audio-control backend must enable real audio settings.");
                TestAssert.Equal(64d, view.Volume, "The initial slider must restore saved application volume.");
                TestAssert.True(view.IsMuted && backend.Preferences.Muted, "Saved mute must be applied before a song is played.");
                TestAssert.Equal("speakers", view.SelectedOutputDevice?.Id, "The device selector must restore an available saved route.");
                TestAssert.Equal(64, backend.Preferences.Volume, "Saved volume must reach the audio backend.");
                TestAssert.Equal(0, backend.OpenCount, "Applying audio preferences cannot open any music file.");
                TestAssert.Equal(0, workspace.NetworkCalls, "Audio initialization cannot enable or contact an online source.");
                TestAssert.True(!view.OnlineEnabled && !workspace.IsOnline, "Audio controls must preserve offline mode.");

                var externalEndpoint = memory.Settings.Endpoints.Single() with { Name = "Updated elsewhere", SelectedAccountId = null };
                var additionalDirectory = new LocalDirectorySource
                {
                    Id = "external-index",
                    Path = Path.Combine(Path.GetTempPath(), "MajMusic-audio-settings-index"),
                    Kind = LocalDirectoryKind.CollectionIndex
                };
                memory.Settings = memory.Settings with
                {
                    Endpoints = [externalEndpoint],
                    LocalDirectorySources = memory.Settings.LocalDirectorySources.Append(additionalDirectory).ToArray()
                };
                view.EndpointName = "Unsaved endpoint draft";
                view.EndpointUrl = "https://draft.example.invalid/api/";
                view.Volume = 37;
                view.IsMuted = false;
                view.SelectedOutputDevice = view.OutputDevices.Single(item => item.Id == "headphones");
                await view.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                await ExecuteAsync(view.SaveAudioSettingsCommand, view);
                TestAssert.Equal(37, memory.Settings.AudioOutput.Volume, "The latest application volume must be saved.");
                TestAssert.True(!memory.Settings.AudioOutput.Muted, "The latest mute choice must be saved.");
                TestAssert.Equal("headphones", memory.Settings.AudioOutput.DeviceId, "The selected device must be saved independently.");
                TestAssert.Equal("Updated elsewhere", memory.Settings.Endpoints.Single().Name, "Saving audio cannot submit an unrelated endpoint editor draft.");
                TestAssert.True(memory.Settings.Endpoints.Single().SelectedAccountId is null,
                    "Saving audio cannot restore a stale remembered-account selection over a newer Guest choice.");
                TestAssert.Equal(2, memory.Settings.LocalDirectorySources.Count, "Saving audio cannot overwrite a newly configured index directory.");
                TestAssert.Equal("Unsaved endpoint draft", view.EndpointName, "Changing audio must retain unsaved source editor input.");
                await ExecuteAsync(view.SaveSettingsCommand, view);
                TestAssert.True(memory.Settings.Endpoints.Single().SelectedAccountId is null,
                    "A later source save must not restore an obsolete account choice from the audio editor's cached settings.");
                TestAssert.Equal(2, memory.Settings.LocalDirectorySources.Count,
                    "A later source save must retain directory configuration observed during the audio update.");
                TestAssert.Equal(0, workspace.NetworkCalls, "Audio changes and persistence must remain local.");
            }

            var reopenedBackend = new OutputBackend();
            await using var reopened = CreateView(new SettingsWorkspace(memory), reopenedBackend);
            await reopened.InitializeAsync();
            await reopened.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(37d, reopened.Volume, "Application volume must survive reopening the view.");
            TestAssert.True(!reopened.IsMuted, "Application mute must survive reopening the view.");
            TestAssert.Equal("headphones", reopened.SelectedOutputDevice?.Id, "The audio route must survive reopening the view.");
            TestAssert.Equal(0, reopenedBackend.OpenCount, "Restoring settings cannot start playback after reopening.");
        }

        /// <summary>Falls back from missing routes and refreshes devices without reopening or seeking current audio.</summary>
        /// <returns>A task representing missing-route recovery.</returns>
        private static async Task ValidateUnavailableRouteAsync()
        {
            var memory = new PreferenceMemory
            {
                Settings = CreateSettings() with { AudioOutput = new AudioOutputPreferences { DeviceId = "disconnected" } }
            };
            var backend = new OutputBackend();
            var playback = new PlaybackService(backend);
            await using var view = new MainViewModel(new SettingsWorkspace(memory), playback, action => action());
            await view.InitializeAsync();
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate;
            await view.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.SelectedOutputDevice is { Id: null }, "An unavailable saved device must select the system default item.");
            TestAssert.True(backend.Preferences.DeviceId is null, "An unavailable saved device cannot remain the active backend route.");
            view.SelectedOutputDevice = view.OutputDevices.Single(item => item.Id == "headphones");
            await view.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            view.SelectedTrack = view.Tracks[0];
            await view.PlaySelectedAsync();
            backend.SetPosition(TimeSpan.FromSeconds(17));
            backend.Devices.RemoveAll(item => item.Id == "headphones");
            await ExecuteAsync(view.RefreshOutputDevicesCommand, view);
            TestAssert.True(view.SelectedOutputDevice is { Id: null } && backend.Preferences.DeviceId is null,
                "Refreshing after a device disappears must restore the real default route.");
            TestAssert.Equal(1, backend.OpenCount, "Refreshing devices must not reopen the current song.");
            TestAssert.Equal(TimeSpan.FromSeconds(17), playback.Snapshot.Position, "Refreshing devices cannot reset the current audio position.");
            TestAssert.Equal(PlaybackState.Playing, playback.Snapshot.State, "Refreshing devices cannot pause the playback owner.");
        }

        /// <summary>Coalesces rapid slider values while leaving the library interactive.</summary>
        /// <returns>A task representing bounded volume-update behavior.</returns>
        private static async Task ValidateCoalescedVolumeAsync()
        {
            var backend = new OutputBackend();
            await using var view = CreateView(new SettingsWorkspace(new PreferenceMemory()), backend);
            await view.InitializeAsync();
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate;
            await view.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            var before = backend.VolumeCalls;
            backend.DelayVolume = true;
            view.Volume = 10;
            await backend.VolumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var index = 11; index <= 91; index++)
            {
                view.Volume = index;
            }

            TestAssert.True(!view.IsBusy, "A pending volume command cannot lock library commands or navigation.");
            backend.ReleaseVolume.TrySetResult();
            await view.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(91, backend.Preferences.Volume, "A burst of slider input must converge to its latest value.");
            TestAssert.True(backend.VolumeCalls - before <= 3, "A slider burst cannot submit one native volume command per input value.");
        }

        /// <summary>Keeps the paused playback owner when the browsing selection differs from the transport owner.</summary>
        /// <returns>A task representing the combined play/pause control.</returns>
        private static async Task ValidateTransportOwnershipAsync()
        {
            var backend = new OutputBackend();
            var playback = new PlaybackService(backend);
            await using var view = new MainViewModel(new SettingsWorkspace(new PreferenceMemory()), playback, action => action());
            await view.InitializeAsync();
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate;
            await view.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            view.SelectedTrack = view.Tracks[0];
            await ExecuteAsync(view.TogglePlayPauseCommand, view);
            TestAssert.Equal(PlaybackState.Playing, playback.Snapshot.State, "The combined transport must start the selected music.");
            backend.SetPosition(TimeSpan.FromSeconds(19));
            view.SelectedTrack = view.Tracks[1];
            await ExecuteAsync(view.TogglePlayPauseCommand, view);
            TestAssert.Equal(PlaybackState.Paused, playback.Snapshot.State, "The combined transport must pause the current owner.");
            await ExecuteAsync(view.TogglePlayPauseCommand, view);
            TestAssert.Equal(PlaybackState.Playing, playback.Snapshot.State, "The combined transport must resume paused audio.");
            TestAssert.Equal("first", playback.Snapshot.Track?.Id, "Resuming must not switch to the browsing selection.");
            TestAssert.Equal(TimeSpan.FromSeconds(19), playback.Snapshot.Position, "Resuming must retain the native position.");
            TestAssert.Equal(1, backend.OpenCount, "Pause and resume must not reopen the selected browsing entry.");
        }

        /// <summary>Waits for canceled output commands before disposing their backend.</summary>
        /// <returns>A task representing delayed native-command shutdown.</returns>
        private static async Task ValidateShutdownAsync()
        {
            var backend = new OutputBackend { HoldCanceledCleanup = true };
            var view = CreateView(new SettingsWorkspace(new PreferenceMemory()), backend);
            try
            {
                await view.InitializeAsync();
                view.ShowLibraryModeCommand.Execute(null);
                await view.HomeUpdate;
                await view.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                backend.DelayVolume = true;
                view.Volume = 29;
                await backend.VolumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var disposal = view.DisposeAsync().AsTask();
                await backend.VolumeCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(!disposal.IsCompleted && !backend.IsDisposed,
                    "Shutdown must await the canceled output command before releasing the native owner.");
                backend.ReleaseCanceledCleanup.TrySetResult();
                await disposal.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(backend.IsDisposed, "Drained shutdown must dispose the playback backend.");
            }
            finally
            {
                backend.ReleaseVolume.TrySetResult();
                backend.ReleaseCanceledCleanup.TrySetResult();
                await view.DisposeAsync();
            }
        }

        /// <summary>Creates a view with immediately observed notifications and no platform services.</summary>
        /// <param name="workspace">The in-memory local settings workspace.</param>
        /// <param name="backend">The controlled audio backend.</param>
        /// <returns>The view lifetime that owns the fake audio backend.</returns>
        private static MainViewModel CreateView(SettingsWorkspace workspace, OutputBackend backend)
        {
            return new MainViewModel(workspace, new PlaybackService(backend), action => action());
        }

        /// <summary>Executes an available observed command and waits for audio-setting convergence.</summary>
        /// <param name="command">The view command.</param>
        /// <param name="view">The owning view model.</param>
        /// <returns>A task representing command and settings completion.</returns>
        private static async Task ExecuteAsync(AsyncCommand command, MainViewModel view)
        {
            TestAssert.True(command.CanExecute(null), "The requested audio or transport control must be available.");
            command.Execute(null);
            var deadline = Stopwatch.StartNew();
            while (view.IsBusy)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(3))
                {
                    throw new InvalidOperationException("The view command did not complete.");
                }

                await Task.Delay(5);
            }

            await view.TransportUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            await view.AudioSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Yield();
        }

        /// <summary>Creates valid settings with non-audio state that must survive unrelated updates.</summary>
        /// <returns>The independent source and directory configuration.</returns>
        private static AppSettings CreateSettings()
        {
            return new AppSettings
            {
                ActiveEndpointId = "source",
                Endpoints =
                [
                    new EndpointConfiguration
                    {
                        Id = "source", Name = "Saved endpoint", BaseUri = new Uri("https://example.invalid/api/"),
                        Accounts = [new SavedEndpointAccount { Id = "saved-account", Username = "Remembered user" }],
                        SelectedAccountId = "saved-account"
                    }
                ],
                LocalDirectoryFormatVersion = LocalDirectorySettings.CurrentVersion,
                LocalDirectorySources =
                [
                    new LocalDirectorySource
                    {
                        Id = "music", Path = Path.Combine(Path.GetTempPath(), "MajMusic-audio-settings-library"), Kind = LocalDirectoryKind.MusicLibrary
                    }
                ]
            };
        }

        /// <summary>Retains settings and lists across fake-workspace reopen operations.</summary>
        private sealed class PreferenceMemory
        {
            /// <summary>Gets or sets the last non-secret settings snapshot.</summary>
            public AppSettings Settings { get; set; } = new();
            /// <summary>Gets local lists created by view initialization.</summary>
            public List<MusicPlaylist> Playlists { get; } = new();
        }

        /// <summary>Implements local persistence without files, credentials, devices or HTTP.</summary>
        private sealed class SettingsWorkspace : IMusicWorkspace
        {
            /// <summary>Retains settings across view lifetimes.</summary>
            private readonly PreferenceMemory _memory;
            /// <summary>Provides two independently browsable synthetic music entries.</summary>
            private readonly MusicTrack[] _tracks = [Track("first"), Track("second")];

            /// <summary>Creates an isolated local workspace.</summary>
            /// <param name="memory">The shared non-secret settings and playlist state.</param>
            public SettingsWorkspace(PreferenceMemory memory)
            {
                _memory = memory;
            }

            /// <inheritdoc />
            public bool IsOnline { get; private set; }
            /// <inheritdoc />
            public string DataDirectory => Path.Combine(Path.GetTempPath(), "MajMusic-audio-settings-ui");
            /// <summary>Gets the number of unexpected source operations.</summary>
            public int NetworkCalls { get; private set; }

            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_memory.Settings with { OnlineEnabled = IsOnline });
            }

            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _memory.Settings = settings;
                IsOnline = settings.OnlineEnabled;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled)
            {
                IsOnline = enabled;
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<MusicTrack>>(_tracks);
            }

            /// <inheritdoc />
            public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<MusicPlaylist>>(_memory.Playlists.ToArray());
            }

            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                _memory.Playlists.RemoveAll(item => item.Id == playlist.Id);
                _memory.Playlists.Add(playlist);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                NetworkCalls++;
                throw new InvalidOperationException("Audio settings attempted to contact a source.");
            }

            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<string?>(null);
            }

            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(track.Audio.LocalPath!);
            }

            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            /// <summary>Creates a music identity that needs no filesystem access.</summary>
            /// <param name="id">The local entry identity.</param>
            /// <returns>The synthetic complete local resource.</returns>
            private static MusicTrack Track(string id)
            {
                return new MusicTrack { Id = id, Title = id, Audio = new MediaResource { LocalPath = Path.Combine(Path.GetTempPath(), id + ".wav") } };
            }
        }

        /// <summary>Provides a controllable audio owner and native-output substitute.</summary>
        private sealed class OutputBackend : IPlaybackBackend, IAudioOutputControl
        {
            /// <summary>Gets the mutable available-route fixture.</summary>
            public List<AudioOutputDevice> Devices { get; } =
            [
                new AudioOutputDevice { Name = "System default" },
                new AudioOutputDevice { Id = "speakers", Name = "Speakers" },
                new AudioOutputDevice { Id = "headphones", Name = "Headphones" }
            ];
            /// <summary>Gets whether the playback owner has been released.</summary>
            public bool IsDisposed { get; private set; }
            /// <summary>Gets or sets whether the next volume operation should wait for an explicit release.</summary>
            public bool DelayVolume { get; set; }
            /// <summary>Gets whether canceled native-style cleanup should be held for shutdown assertions.</summary>
            public bool HoldCanceledCleanup { get; init; }
            /// <summary>Gets the number of actual volume requests.</summary>
            public int VolumeCalls { get; private set; }
            /// <summary>Gets the number of opened synthetic audio resources.</summary>
            public int OpenCount { get; private set; }
            /// <summary>Signals entry into a delayed volume operation.</summary>
            public TaskCompletionSource VolumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the delayed volume operation normally.</summary>
            public TaskCompletionSource ReleaseVolume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Signals observed command cancellation.</summary>
            public TaskCompletionSource VolumeCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases canceled native-style cleanup.</summary>
            public TaskCompletionSource ReleaseCanceledCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <inheritdoc />
            public AudioOutputPreferences Preferences { get; private set; } = new();
            /// <inheritdoc />
            public string? OutputDeviceNotice { get; private set; }
            /// <inheritdoc />
            public bool IsAvailable => !IsDisposed;
            /// <inheritdoc />
            public PlaybackCapabilities Capabilities => PlaybackCapabilities.Playback | PlaybackCapabilities.Seeking;
            /// <inheritdoc />
            public PlaybackSnapshot Snapshot { get; private set; } = new();
            /// <inheritdoc />
            public event EventHandler? StateChanged;

            /// <inheritdoc />
            public Task<IReadOnlyList<AudioOutputDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRoute(Preferences.DeviceId);
                return Task.FromResult<IReadOnlyList<AudioOutputDevice>>(Devices.ToArray());
            }

            /// <inheritdoc />
            public Task SetOutputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRoute(deviceId);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VolumeCalls++;
                if (DelayVolume)
                {
                    DelayVolume = false;
                    VolumeStarted.TrySetResult();
                    try
                    {
                        await ReleaseVolume.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        VolumeCanceled.TrySetResult();
                        if (HoldCanceledCleanup)
                        {
                            await ReleaseCanceledCleanup.Task;
                        }

                        throw;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                Preferences = Preferences with { Volume = volume };
            }

            /// <inheritdoc />
            public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Preferences = Preferences with { Muted = muted };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OpenCount++;
                Snapshot = new PlaybackSnapshot { State = PlaybackState.Stopped, Duration = TimeSpan.FromSeconds(120) };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task PlayAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Playing, cancellationToken);
            }

            /// <inheritdoc />
            public Task PauseAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Paused, cancellationToken);
            }

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Stopped, cancellationToken);
            }

            /// <inheritdoc />
            public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetPosition(position);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                IsDisposed = true;
                return ValueTask.CompletedTask;
            }

            /// <summary>Publishes an authoritative synthetic native position.</summary>
            /// <param name="position">The supplied media position.</param>
            public void SetPosition(TimeSpan position)
            {
                Snapshot = Snapshot with { Position = position };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            /// <summary>Applies a requested route or reports a system-default fallback.</summary>
            /// <param name="deviceId">The requested route identity.</param>
            private void ValidateRoute(string? deviceId)
            {
                var exists = Devices.Any(item => item.Id == deviceId);
                Preferences = Preferences with { DeviceId = exists ? deviceId : null };
                OutputDeviceNotice = exists ? null : "The selected audio output is unavailable.";
            }

            /// <summary>Changes transport state without modifying settings or opening another resource.</summary>
            /// <param name="state">The requested transport state.</param>
            /// <param name="cancellationToken">Cancels the command.</param>
            /// <returns>A completed fake transport operation.</returns>
            private Task ChangeStateAsync(PlaybackState state, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Snapshot = Snapshot with { State = state };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }
        }
    }
}
