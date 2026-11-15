#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks music-list workflows and view lifetime behavior without starting Avalonia or a network service.</summary>
    internal static class PresentationTests
    {
        /// <summary>Runs local-first initialization, playlist, shutdown and audio-state regressions.</summary>
        /// <returns>A task representing the completed cases.</returns>
        public static async Task RunAsync()
        {
            await ValidatePlaylistWorkflowAsync();
            await ValidateReadOnlyIndexAndFavoritesAsync();
            await ValidateShutdownAsync();
            await ValidatePlaybackStateAsync();
            await ValidateDeferredPauseResumeAsync();
            await ValidateTimelineAsync();
            await ValidateUnknownTimelineAsync();
            await ValidateSeekShutdownAsync();
        }

        /// <summary>Checks that delayed visual notifications cannot redirect a rapid pause/resume to the browsing selection.</summary>
        /// <returns>A task representing the authoritative-state command regression.</returns>
        private static async Task ValidateDeferredPauseResumeAsync()
        {
            var workspace = new FakeWorkspace();
            var backend = new FakePlaybackBackend();
            var playback = new PlaybackService(backend);
            var tracks = await workspace.SearchAsync(null);
            await playback.SetQueueAsync(tracks);
            await playback.NextAsync();
            await using var viewModel = new MainViewModel(workspace, playback, _ => { });
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            viewModel.SelectedTrack = viewModel.Tracks[0];
            viewModel.PauseCommand.Execute(null);
            TestAssert.Equal(PlaybackState.Paused, playback.Snapshot.State, "The backend pauses before a delayed visual refresh.");
            viewModel.PlayCommand.Execute(null);
            TestAssert.Equal(tracks[1].Id, playback.Snapshot.Track?.Id,
                "Resume must read the actual paused owner rather than an old visual snapshot.");
            TestAssert.Equal(2, backend.OpenCount, "Delayed notifications cannot reopen the browsing selection.");
            TestAssert.Equal(PlaybackState.Playing, playback.Snapshot.State, "The authoritative paused resource must resume.");
        }

        /// <summary>Checks the new default read-only scan route and local favorite actions.</summary>
        /// <returns>A task representing the workflow checks.</returns>
        private static async Task ValidateReadOnlyIndexAndFavoritesAsync()
        {
            var workspace = new FakeWorkspace();
            await using var viewModel = CreateViewModel(workspace, new UnavailablePlaybackBackend());
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            TestAssert.True(viewModel.Playlists.Any(item => item.IsFavorite), "The local favorites collection must always be available.");
            await viewModel.ImportDirectoryAsync("synthetic-source-root");
            TestAssert.Equal(1, workspace.IndexCount, "The folder action must use recursive read-only indexing.");
            TestAssert.Equal(0, workspace.ManagedImportCount, "The folder action must never invoke managed file copying.");
            TestAssert.True(viewModel.LocalDirectoriesText.Contains("synthetic-source-root", StringComparison.Ordinal),
                "The selected root must be remembered for rescan and source protection.");
            TestAssert.True(!viewModel.OnlineEnabled && workspace.SynchronizationCount == 0, "Indexing must stay local.");
            viewModel.SelectedTrack = viewModel.Tracks[0];
            await TestAssert.ExecuteAsync(viewModel.ToggleFavoriteCommand);
            TestAssert.True(viewModel.IsSelectedFavorite, "Favorite actions must persist the selected music locally.");
            await TestAssert.ExecuteAsync(viewModel.OpenFavoritesCommand);
            TestAssert.Equal(1, viewModel.Tracks.Count, "The favorites view must include the liked music.");
            TestAssert.Equal("MyFavorites.json", viewModel.ExportFileName, "Favorites must suggest Maj's recognized filename.");
            await TestAssert.ExecuteAsync(viewModel.ToggleFavoriteCommand);
            TestAssert.Equal(0, viewModel.Tracks.Count, "Removing a favorite must update the open favorites list.");
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            TestAssert.Equal(3, viewModel.Tracks.Count, "Removing favorites cannot remove library entries.");
        }

        /// <summary>Checks empty-playlist insertion and explicitly opened playlist ordering.</summary>
        /// <returns>A task representing the case.</returns>
        private static async Task ValidatePlaylistWorkflowAsync()
        {
            var workspace = new FakeWorkspace();
            workspace.Playlists.Add(new MusicPlaylist { Id = "empty", Name = "Empty" });
            workspace.Playlists.Add(new MusicPlaylist { Id = "ordered", Name = "Ordered", TrackIds = new[] { "c", "a" } });
            await using var viewModel = CreateViewModel(workspace, new UnavailablePlaybackBackend());
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            TestAssert.Equal(0, workspace.SynchronizationCount, "Initialization must not synchronize any source.");
            TestAssert.True(!viewModel.OnlineEnabled, "A fresh view must be offline.");
            TestAssert.True(!viewModel.PlayCommand.CanExecute(null), "An unavailable audio backend must keep playback disabled.");
            viewModel.SelectedTrack = viewModel.Tracks[0];
            viewModel.SelectedPlaylist = viewModel.Playlists.Single(item => item.Id == "empty");
            TestAssert.Equal(3, viewModel.Tracks.Count, "Choosing an add target must not filter the library.");
            TestAssert.True(viewModel.AddToPlaylistCommand.CanExecute(null), "An empty playlist must accept the selected track.");
            await TestAssert.ExecuteAsync(viewModel.AddToPlaylistCommand);
            TestAssert.Equal("a", workspace.Playlists.Single(item => item.Id == "empty").TrackIds.Single(), "The selected track must be added to an empty playlist.");
            TestAssert.Equal(3, viewModel.Tracks.Count, "Adding to a playlist must preserve library browsing.");
            viewModel.SelectedPlaylist = viewModel.Playlists.Single(item => item.Id == "ordered");
            await TestAssert.ExecuteAsync(viewModel.OpenPlaylistCommand);
            TestAssert.Equal("c,a", string.Join(',', viewModel.Tracks.Select(item => item.Id)), "An explicitly opened playlist must preserve saved order.");
            viewModel.SelectedPlaylist = viewModel.Playlists.Single(item => item.Id == "empty");
            TestAssert.Equal("c,a", string.Join(',', viewModel.Tracks.Select(item => item.Id)), "Changing the add target must not replace the open list.");
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            TestAssert.Equal(3, viewModel.Tracks.Count, "Showing all music must clear only the active filter.");
            TestAssert.Equal("empty", viewModel.SelectedPlaylist?.Id, "Showing all music must preserve the add target.");
        }

        /// <summary>Checks that closing waits for both workspace and cover cleanup and ignores late selection events.</summary>
        /// <returns>A task representing the case.</returns>
        private static async Task ValidateShutdownAsync()
        {
            var workspace = new FakeWorkspace { BlockImport = true, BlockCover = true };
            var backend = new FakePlaybackBackend();
            var viewModel = CreateViewModel(workspace, backend);
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            viewModel.SelectedTrack = viewModel.Tracks[0];
            var import = viewModel.ImportDirectoryAsync("synthetic-directory");
            var cover = viewModel.LoadCoverAsync();
            await Task.WhenAll(workspace.ImportStarted.Task, workspace.CoverStarted.Task).WaitAsync(TimeSpan.FromSeconds(3));
            var close = viewModel.DisposeAsync().AsTask();
            await Task.WhenAll(workspace.ImportCanceled.Task, workspace.CoverCanceled.Task).WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(!close.IsCompleted, "Shutdown must wait until canceled workspace and cover operations finish cleanup.");
            TestAssert.Equal(0, backend.DisposalCount, "Audio resources must remain alive while registered view operations finish.");
            workspace.ReleaseCleanup.TrySetResult();
            await Task.WhenAll(import, cover, close).WaitAsync(TimeSpan.FromSeconds(3));
            await viewModel.DisposeAsync();
            await viewModel.LoadCoverAsync();
            await viewModel.InitializeAsync();
            viewModel.ShowAllCommand.Execute(null);
            TestAssert.Equal(1, backend.DisposalCount, "Repeated shutdown must dispose the backend once.");
            TestAssert.True(viewModel.Cover is null, "The view must clear its bitmap binding before releasing the image.");
            TestAssert.True(!viewModel.IsIdle && !viewModel.SearchCommand.CanExecute(null), "Disposed views must not accept new work.");
        }

        /// <summary>Checks that paused audio resumes in place and actual playback metadata follows queue changes.</summary>
        /// <returns>A task representing the case.</returns>
        private static async Task ValidatePlaybackStateAsync()
        {
            var workspace = new FakeWorkspace();
            var backend = new FakePlaybackBackend();
            await using var viewModel = CreateViewModel(workspace, backend);
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            viewModel.SelectedTrack = viewModel.Tracks[0];
            viewModel.PlayCommand.Execute(null);
            TestAssert.Equal("Track a", viewModel.PlaybackTitle, "Now-playing metadata must come from the actual loaded track.");
            TestAssert.True(viewModel.AudioDetails.Contains("128", StringComparison.Ordinal)
                && viewModel.AudioDetails.Contains("kHz", StringComparison.Ordinal)
                && viewModel.AudioDetails.Contains("声道", StringComparison.Ordinal), "Actual stream properties must be visible.");
            backend.SetPosition(TimeSpan.FromSeconds(27));
            viewModel.PauseCommand.Execute(null);
            TestAssert.Equal("继续", viewModel.PlayButtonText, "A matching paused track must expose resume.");
            viewModel.PlayCommand.Execute(null);
            TestAssert.Equal(1, backend.OpenCount, "Resume must not reopen the audio resource or replace the queue.");
            TestAssert.Equal(TimeSpan.FromSeconds(27), backend.Snapshot.Position, "Resume must preserve the backend media position.");
            viewModel.SelectedTrack = viewModel.Tracks[2];
            TestAssert.Equal("Track a", viewModel.PlaybackTitle, "Browsing a different track must not change now-playing metadata.");
            viewModel.NextCommand.Execute(null);
            TestAssert.Equal("Track b", viewModel.PlaybackTitle, "Queue navigation must update actual playback metadata.");
            TestAssert.Equal("Artist b", viewModel.PlaybackArtist, "Now-playing artist must follow the audio owner.");
            TestAssert.Equal("Track c", viewModel.SelectedTitle, "Queue navigation must not overwrite browsing selection.");
            backend.SetPosition(TimeSpan.FromSeconds(19));
            viewModel.PauseCommand.Execute(null);
            TestAssert.Equal("继续", viewModel.PlayButtonText, "Pause must offer resume independently of the browsing selection.");
            viewModel.PlayCommand.Execute(null);
            TestAssert.Equal("Track b", viewModel.PlaybackTitle, "Resume after switching must keep the actual paused track.");
            TestAssert.Equal(2, backend.OpenCount, "Resume cannot reopen the browsing selection after queue navigation.");
            TestAssert.Equal(TimeSpan.FromSeconds(19), backend.Snapshot.Position, "Queue resume must retain its native position.");
            viewModel.PauseCommand.Execute(null);
            await viewModel.PlaySelectedAsync();
            TestAssert.Equal("Track c", viewModel.PlaybackTitle, "An explicit selected-track action may replace a paused queue entry.");
        }

        /// <summary>Checks that native progress never seeks and user gestures commit exactly once to the captured media.</summary>
        /// <returns>A task representing timeline interaction checks.</returns>
        private static async Task ValidateTimelineAsync()
        {
            var workspace = new FakeWorkspace();
            var backend = new FakePlaybackBackend();
            await using var viewModel = CreateViewModel(workspace, backend);
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            viewModel.SelectedTrack = viewModel.Tracks[0];
            await viewModel.PlaySelectedAsync();
            TestAssert.True(viewModel.CanSeek, "Prepared seekable media must enable its timeline.");
            TestAssert.Equal(120d, viewModel.DurationSeconds, "Timeline maximum must come from the backend duration.");
            TestAssert.Equal("02:00", viewModel.DurationText, "The duration label must display native media time.");
            await ObservePropertyAsync(viewModel, nameof(MainViewModel.PositionSeconds), () => viewModel.PositionSeconds == 12,
                () => backend.SetPosition(TimeSpan.FromSeconds(12)));
            TestAssert.Equal(12d, viewModel.PositionSeconds, "Progress must follow the backend clock.");
            viewModel.PositionSeconds = 80;
            TestAssert.Equal(12d, viewModel.PositionSeconds, "A programmatic slider change outside a gesture cannot seek.");
            TestAssert.Equal(0, backend.SeekCount, "Progress notifications cannot submit playback operations.");

            TestAssert.True(viewModel.BeginSeek(), "A user can start a seek gesture.");
            viewModel.PositionSeconds = 25;
            viewModel.PositionSeconds = 57;
            backend.SetPosition(TimeSpan.FromSeconds(15));
            TestAssert.Equal(57d, viewModel.PositionSeconds, "Native progress must not overwrite a drag preview.");
            TestAssert.Equal(TimeSpan.FromSeconds(15), backend.Snapshot.Position, "Previewing must not move the native clock.");
            TestAssert.Equal(0, backend.SeekCount, "Dragging must defer its native seek until release.");
            await viewModel.CommitSeekAsync();
            await viewModel.CommitSeekAsync();
            TestAssert.Equal(1, backend.SeekCount, "One completed gesture must seek exactly once.");
            TestAssert.Equal(TimeSpan.FromSeconds(57), backend.Snapshot.Position, "The committed position must reach the audio owner.");
            TestAssert.Equal("00:57", viewModel.PositionText, "The position label must follow the confirmed backend time.");

            viewModel.PauseCommand.Execute(null);
            viewModel.BeginSeek();
            viewModel.PositionSeconds = 30;
            await viewModel.CommitSeekAsync();
            TestAssert.Equal(PlaybackState.Paused, backend.Snapshot.State, "Seeking paused audio must not resume it.");
            TestAssert.Equal(1, backend.OpenCount, "Seeking cannot reopen or replace the audio resource.");
            TestAssert.Equal(2, backend.SeekCount, "The paused gesture still submits one seek.");
            viewModel.BeginSeek();
            viewModel.PositionSeconds = 90;
            viewModel.NextCommand.Execute(null);
            TestAssert.True(!viewModel.IsSeeking, "Switching tracks must discard the old preview.");
            await viewModel.CommitSeekAsync();
            TestAssert.Equal(2, backend.SeekCount, "An old gesture must not seek the new track.");
            TestAssert.Equal("Track b", viewModel.PlaybackTitle, "Manual navigation must update the playing title.");
            var advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(MainViewModel.PlaybackTitle) && viewModel.PlaybackTitle == "Track c")
                {
                    advanced.TrySetResult();
                }
            };
            backend.Complete();
            await advanced.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal("Track c", viewModel.PlaybackTitle, "Natural completion must update the playing title.");
            TestAssert.Equal(0d, viewModel.PositionSeconds, "A new track must display its own media position.");
        }

        /// <summary>Checks duration and capability guards without disabling ordinary playback.</summary>
        /// <returns>A task representing unavailable timeline checks.</returns>
        private static async Task ValidateUnknownTimelineAsync()
        {
            var backend = new FakePlaybackBackend();
            await using var viewModel = CreateViewModel(new FakeWorkspace(), backend);
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            viewModel.SelectedTrack = viewModel.Tracks[0];
            await viewModel.PlaySelectedAsync();
            await ObservePropertyAsync(viewModel, nameof(MainViewModel.DurationText), () => viewModel.DurationText == "--:--",
                () => backend.SetDuration(null));
            TestAssert.True(!viewModel.CanSeek && !viewModel.BeginSeek(), "Unknown duration cannot accept a seek gesture.");
            TestAssert.Equal("--:--", viewModel.DurationText, "Unknown duration must remain visibly unknown.");
            await ObservePropertyAsync(viewModel, nameof(MainViewModel.DurationText), () => viewModel.DurationText == "02:00",
                () => backend.SetDuration(TimeSpan.FromMinutes(2)));
            TestAssert.True(viewModel.BeginSeek(), "A discovered duration must enable the timeline.");
            viewModel.PositionSeconds = 500;
            TestAssert.Equal(120d, viewModel.PositionSeconds, "Preview positions must clamp to the known duration.");
            viewModel.CancelSeek();
            await viewModel.CommitSeekAsync();
            TestAssert.Equal(0, backend.SeekCount, "A canceled gesture must not submit a native seek.");

            await using var noSeeking = CreateViewModel(new FakeWorkspace(), new FakePlaybackBackend { SupportsSeeking = false });
            await noSeeking.InitializeAsync();
            await TestAssert.ExecuteAsync(noSeeking.ShowAllCommand);
            noSeeking.SelectedTrack = noSeeking.Tracks[0];
            await noSeeking.PlaySelectedAsync();
            TestAssert.True(noSeeking.CanPlay && !noSeeking.CanSeek, "A nonseekable backend still supports ordinary playback.");
        }

        /// <summary>Checks that closing cancels a committed seek before releasing its backend.</summary>
        /// <returns>A task representing seek-lifetime checks.</returns>
        private static async Task ValidateSeekShutdownAsync()
        {
            var backend = new FakePlaybackBackend { BlockSeek = true };
            var viewModel = CreateViewModel(new FakeWorkspace(), backend);
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            viewModel.SelectedTrack = viewModel.Tracks[0];
            await viewModel.PlaySelectedAsync();
            viewModel.BeginSeek();
            viewModel.PositionSeconds = 45;
            var pending = viewModel.CommitSeekAsync();
            await backend.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await pending;
            await viewModel.CommitSeekAsync();
            TestAssert.Equal(0, backend.SeekCount, "Closing cannot publish a canceled native seek.");
            TestAssert.Equal(1, backend.DisposalCount, "Closing must drain the seek before disposing its backend once.");
            TestAssert.True(!viewModel.IsSeeking && !viewModel.CanSeek, "A closed view cannot retain a timeline interaction.");
        }

        /// <summary>Creates a view model whose backend notifications require no UI dispatcher.</summary>
        /// <param name="workspace">The controllable workspace.</param>
        /// <param name="backend">The controllable audio backend.</param>
        /// <returns>The test view model.</returns>
        private static MainViewModel CreateViewModel(FakeWorkspace workspace, IPlaybackBackend backend)
        {
            return new MainViewModel(workspace, new PlaybackService(backend), static action => action());
        }

        /// <summary>Waits for an eventual backend notification without depending on worker scheduling.</summary>
        /// <param name="viewModel">The observed presentation state.</param>
        /// <param name="property">The property expected to change.</param>
        /// <param name="ready">The condition establishing that the new state is visible.</param>
        /// <param name="publish">The native-clock notification to publish.</param>
        /// <returns>A task representing the observed state change.</returns>
        private static async Task ObservePropertyAsync(MainViewModel viewModel, string property, Func<bool> ready, Action publish)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            System.ComponentModel.PropertyChangedEventHandler handler = (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == property && ready())
                {
                    changed.TrySetResult();
                }
            };
            viewModel.PropertyChanged += handler;
            try
            {
                publish();
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                viewModel.PropertyChanged -= handler;
            }
        }

        /// <summary>Provides in-memory metadata and cancellation barriers without file or network access.</summary>
        private sealed class FakeWorkspace : IMusicWorkspace
        {
            /// <summary>Retains the same settings persistence contract as the production workspace.</summary>
            private AppSettings _savedSettings = new();
            /// <summary>Gets stable music entries used by all workflow cases.</summary>
            private MusicTrack[] Tracks { get; } = new[] { Track("a"), Track("b"), Track("c") };
            /// <summary>Gets locally saved playlists.</summary>
            public List<MusicPlaylist> Playlists { get; } = new();
            /// <summary>Gets or sets whether import waits for cancellation and cleanup.</summary>
            public bool BlockImport { get; init; }
            /// <summary>Gets or sets whether cover resolution waits for cancellation and cleanup.</summary>
            public bool BlockCover { get; init; }
            /// <summary>Signals that import entered its cancelable operation.</summary>
            public TaskCompletionSource ImportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Signals that cover resolution entered its cancelable operation.</summary>
            public TaskCompletionSource CoverStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Signals that import observed close cancellation.</summary>
            public TaskCompletionSource ImportCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Signals that cover resolution observed close cancellation.</summary>
            public TaskCompletionSource CoverCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Allows canceled operations to finish their simulated cleanup.</summary>
            public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets the number of explicit source synchronization requests.</summary>
            public int SynchronizationCount { get; private set; }
            /// <summary>Counts managed-copy requests to protect the read-only default workflow.</summary>
            public int ManagedImportCount { get; private set; }
            /// <summary>Counts recursive source indexing operations.</summary>
            public int IndexCount { get; private set; }
            /// <summary>Gets current explicit online permission.</summary>
            public bool IsOnline { get; private set; }
            /// <summary>Gets a display-only data location without creating it.</summary>
            public string DataDirectory => "synthetic-data";

            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_savedSettings with { OnlineEnabled = IsOnline });
            }

            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                _savedSettings = settings;
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
                return Task.FromResult<IReadOnlyList<MusicTrack>>(Tracks);
            }

            /// <inheritdoc />
            public async Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                ManagedImportCount++;
                if (BlockImport)
                {
                    await WaitForCancellationAsync(ImportStarted, ImportCanceled, cancellationToken);
                }

                return Tracks[0];
            }

            /// <inheritdoc />
            public async Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                IndexCount++;
                if (BlockImport)
                {
                    await WaitForCancellationAsync(ImportStarted, ImportCanceled, cancellationToken);
                }

                return new LocalLibraryScanResult { RootDirectory = Path.GetFullPath(directory), Tracks = new[] { Tracks[0] }, ScannedDirectoryCount = 1 };
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<MusicPlaylist>>(Playlists.ToArray());
            }

            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                Playlists.RemoveAll(item => item.Id == playlist.Id);
                Playlists.Add(playlist);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                SynchronizationCount++;
                return Task.FromResult(0);
            }

            /// <inheritdoc />
            public async Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                if (BlockCover)
                {
                    await WaitForCancellationAsync(CoverStarted, CoverCanceled, cancellationToken);
                }

                return null;
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

            /// <summary>Waits for cancellation and deliberately delays cleanup until the case releases it.</summary>
            /// <param name="started">The operation-start notification.</param>
            /// <param name="canceled">The observed-cancellation notification.</param>
            /// <param name="token">The view lifetime token.</param>
            /// <returns>A task ending with cancellation after cleanup is released.</returns>
            private async Task WaitForCancellationAsync(TaskCompletionSource started, TaskCompletionSource canceled, CancellationToken token)
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    await ReleaseCleanup.Task;
                    throw;
                }
            }

            /// <summary>Creates a local entry without reading its synthetic media path.</summary>
            /// <param name="id">The synthetic identity.</param>
            /// <returns>The synthetic entry.</returns>
            private static MusicTrack Track(string id)
            {
                return new MusicTrack { Id = id, Title = "Track " + id, Artist = "Artist " + id, Audio = new MediaResource { LocalPath = id + ".mp3" } };
            }
        }

        /// <summary>Exposes a controllable media clock without real audio playback.</summary>
        private sealed class FakePlaybackBackend : IPlaybackBackend
        {
            /// <summary>Gets preparation count to distinguish play from resume.</summary>
            public int OpenCount { get; private set; }
            /// <summary>Gets the number of disposal requests.</summary>
            public int DisposalCount { get; private set; }
            /// <summary>Gets the number of actual seek commands submitted by the view.</summary>
            public int SeekCount { get; private set; }
            /// <summary>Gets whether this case exposes seeking capability.</summary>
            public bool SupportsSeeking { get; init; } = true;
            /// <summary>Gets whether a seek waits until cancellation for lifetime validation.</summary>
            public bool BlockSeek { get; init; }
            /// <summary>Signals that a committed seek reached the fake audio owner.</summary>
            public TaskCompletionSource SeekStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <inheritdoc />
            public bool IsAvailable => true;
            /// <inheritdoc />
            public PlaybackCapabilities Capabilities => PlaybackCapabilities.Playback
                | (SupportsSeeking ? PlaybackCapabilities.Seeking : PlaybackCapabilities.None);
            /// <inheritdoc />
            public PlaybackSnapshot Snapshot { get; private set; } = new();
            /// <inheritdoc />
            public event EventHandler? StateChanged;

            /// <inheritdoc />
            public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
            {
                OpenCount++;
                Snapshot = new PlaybackSnapshot
                {
                    State = PlaybackState.Paused,
                    Duration = TimeSpan.FromMinutes(2),
                    AudioInfo = new AudioStreamInfo { CodecName = "Fixture codec", BitrateBitsPerSecond = 128000, SampleRateHz = 44100, Channels = 2 }
                };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task PlayAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Playing);
            }

            /// <inheritdoc />
            public Task PauseAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Paused);
            }

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Stopped);
            }

            /// <inheritdoc />
            public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (BlockSeek)
                {
                    SeekStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                SeekCount++;
                SetPosition(position);
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                DisposalCount++;
                return ValueTask.CompletedTask;
            }

            /// <summary>Sets the authoritative synthetic media position.</summary>
            /// <param name="position">The position to report.</param>
            public void SetPosition(TimeSpan position)
            {
                Snapshot = Snapshot with { Position = position };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            /// <summary>Publishes a newly discovered or unknown native duration.</summary>
            /// <param name="duration">The duration reported by the test audio owner.</param>
            public void SetDuration(TimeSpan? duration)
            {
                Snapshot = Snapshot with { Duration = duration };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            /// <summary>Publishes the natural end of the current resource.</summary>
            public void Complete()
            {
                Snapshot = Snapshot with { State = PlaybackState.Completed };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            /// <summary>Publishes an audio lifecycle transition.</summary>
            /// <param name="state">The lifecycle state to report.</param>
            /// <returns>A completed operation.</returns>
            private Task ChangeStateAsync(PlaybackState state)
            {
                Snapshot = Snapshot with { State = state };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }
        }
    }
}
