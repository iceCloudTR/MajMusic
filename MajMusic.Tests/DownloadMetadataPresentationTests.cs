#nullable enable

using System.ComponentModel;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Metadata;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks immediate local metadata after download without another viewport or audio playback operation.</summary>
    internal static class DownloadMetadataPresentationTests
    {
        /// <summary>Runs slow-parser, supersession, stale-query, retry and bounded-retention regressions.</summary>
        /// <returns>A task representing controlled local-only metadata verification.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-download-metadata-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                await ValidateImmediatePresentationAsync(Path.Combine(root, "immediate"));
                await ValidateQueryAndRevisionOwnershipAsync(Path.Combine(root, "ownership"));
                await ValidateSupersededFileAsync(Path.Combine(root, "superseded"));
                await ValidateStaleSnapshotAsync(Path.Combine(root, "snapshot"));
                await ValidateRetryAndBoundsAsync(Path.Combine(root, "retention"));
                await ValidateShutdownAsync(Path.Combine(root, "shutdown"));
            }
            finally
            {
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                TestAssert.True(root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(root).StartsWith("MajMusic-download-metadata-", StringComparison.Ordinal), "Fixture cleanup must stay in its generated temporary directory.");
                Directory.Delete(root, true);
            }
        }

        /// <summary>Publishes a completed private file on its stable card, selected detail and playing timeline.</summary>
        /// <param name="root">The generated isolated file and metadata-cache directory.</param>
        /// <returns>A task representing immediate asynchronous publication.</returns>
        private static async Task ValidateImmediatePresentationAsync(string root)
        {
            var path = await AudioFileAsync(root, "completed");
            var workspace = new ProgressWorkspace();
            var track = Track("first");
            workspace.Inner.Library.Add(track);
            var started = NewSignal();
            var release = NewSignal();
            var reader = new NativeReader
            {
                Read = async (_, token) =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return Properties(137);
                }
            };
            var backend = new MetadataBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: static (_, _) => Task.FromResult("synthetic-loaded-owner"));
            await using var view = await CreateAsync(workspace, reader, root, playback);
            var card = view.CardTracks[0];
            await view.EnsureBrowseMetadataAsync(card, CancellationToken.None);
            TestAssert.Equal(0, reader.Calls, "An uncached row cannot ask its local native reader to download audio.");
            view.SelectedTrack = track;
            await view.PlaySelectedAsync();
            backend.Pulse(TimeSpan.FromSeconds(17));
            var queue = playback.Queue.Tracks;
            var navigation = view.BrowseNavigationVersion;
            workspace.Emit(track, "download", AudioDownloadState.Preparing);
            workspace.Emit(track, "download", AudioDownloadState.Completed, path);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(card.IsDownloaded && card.Track.Duration is null && !view.IsBusy,
                "The completed fill must appear while optional file metadata is still being parsed.");
            release.TrySetResult();
            await view.DownloadMetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(ReferenceEquals(card, view.CardTracks[0]) && card.Track.Duration == TimeSpan.FromSeconds(137)
                && card.Track.AudioInfo?.BitrateBitsPerSecond == 192000 && card.Track.Audio.LocalPath == path,
                "The already visible card must immediately receive metadata and the new local file without being recreated.");
            TestAssert.True(view.SelectedTrack?.Duration == TimeSpan.FromSeconds(137)
                && view.SelectedTrack.AudioInfo?.SampleRateHz == 48000 && view.VisibleTracks[0].Duration == TimeSpan.FromSeconds(137),
                "Selected and visible row properties must update without clicking, searching or changing pages.");
            TestAssert.True(view.DurationText == "02:17" && view.AudioDetails.Contains("192", StringComparison.Ordinal)
                && view.PositionSeconds == 17, "Downloaded properties must fill unknown current playback fields while retaining its position.");
            var tick = NewSignal();
            void OnClockChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainViewModel.PositionSeconds) && view.PositionSeconds == 18)
                {
                    tick.TrySetResult();
                }
            }
            view.PropertyChanged += OnClockChanged;
            try
            {
                backend.Pulse(TimeSpan.FromSeconds(18));
                await tick.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                view.PropertyChanged -= OnClockChanged;
            }
            TestAssert.True(view.DurationText == "02:17" && view.HasAudioDetails && view.PositionSeconds == 18,
                $"The next backend clock tick must retain locally parsed fields that the backend does not supply (duration {view.DurationText}, audio {view.HasAudioDetails}, position {view.PositionSeconds}).");
            workspace.Emit(track, "download", AudioDownloadState.Completed, path);
            await view.DownloadMetadataRefresh;
            TestAssert.True(reader.Calls == 1 && workspace.AudioResolveCalls == 0 && backend.OpenCount == 1
                && ReferenceEquals(queue, playback.Queue.Tracks) && navigation == view.BrowseNavigationVersion,
                "Metadata completion cannot re-download, re-open audio, replace the queue, restart navigation or parse the same operation again.");
            TestAssert.Equal(TimeSpan.FromSeconds(137), workspace.Inner.Library[0].Duration, "Parsed download metadata must also reach local persistence.");
            var nativeProperties = NewSignal();
            void OnNativePropertiesChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainViewModel.AudioDetails) && view.AudioDetails.Contains("320", StringComparison.Ordinal))
                {
                    nativeProperties.TrySetResult();
                }
            }
            view.PropertyChanged += OnNativePropertiesChanged;
            try
            {
                backend.PublishProperties(TimeSpan.FromSeconds(61), 320000);
                await nativeProperties.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                view.PropertyChanged -= OnNativePropertiesChanged;
            }
            TestAssert.True(view.DurationText == "01:01" && view.AudioDetails.Contains("320", StringComparison.Ordinal)
                && backend.OpenCount == 1, "Authoritative native decoder properties must supersede optional parsed fields without reopening playback.");
            workspace.Inner.SearchOverride = (_, _) => Task.FromResult<IReadOnlyList<MusicTrack>>([track]);
            await TestAssert.ExecuteAsync(view.ShowAllCommand);
            await view.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.Tracks[0].Duration == TimeSpan.FromSeconds(61) && view.Tracks[0].AudioInfo?.BitrateBitsPerSecond == 320000,
                "A retained completed-file overlay cannot restore older parsed values after native metadata has superseded them.");
        }

        /// <summary>Retains current selection and source revision when an older file finishes after query navigation.</summary>
        /// <param name="root">The generated isolated audio directory.</param>
        /// <returns>A task representing late-result identity guards.</returns>
        private static async Task ValidateQueryAndRevisionOwnershipAsync(string root)
        {
            var path = await AudioFileAsync(root, "old");
            var first = Track("first");
            var second = Track("second") with { Duration = TimeSpan.FromSeconds(44) };
            var workspace = new ProgressWorkspace();
            workspace.Inner.Library.AddRange([first, second]);
            var started = NewSignal();
            var release = NewSignal();
            var reader = new NativeReader
            {
                Read = async (_, _) =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return Properties(200);
                }
            };
            await using var view = await CreateAsync(workspace, reader, root);
            workspace.Emit(first, "old-download", AudioDownloadState.Preparing);
            workspace.Emit(first, "old-download", AudioDownloadState.Completed, path);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var oldCompletion = view.DownloadMetadataRefresh;
            view.BeginSearchCommand.Execute(null);
            view.SearchText = second.Title;
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            view.SelectedTrack = view.Tracks.Single();
            var replacement = first with { Revision = "v2", Duration = TimeSpan.FromSeconds(88) };
            workspace.Inner.Library[0] = replacement;
            release.TrySetResult();
            await oldCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.Tracks.Single().Id == second.Id && view.SelectedTrack?.Id == second.Id
                && view.SelectedTrack.Duration == TimeSpan.FromSeconds(44), "An older file result cannot replace a newly searched or selected song.");
            view.SearchText = first.Title;
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.Tracks.Single().Revision == "v2" && view.Tracks[0].Duration == TimeSpan.FromSeconds(88),
                "A delayed result for a downloaded old revision cannot overwrite its newer source revision.");
            TestAssert.Equal(0, workspace.AudioResolveCalls, "Query navigation cannot turn metadata recovery into another audio request.");
        }

        /// <summary>Rejects a canceled parser that ignores cancellation when a newer completed file owns the same song.</summary>
        /// <param name="root">The generated isolated audio directory.</param>
        /// <returns>A task representing operation and file supersession.</returns>
        private static async Task ValidateSupersededFileAsync(string root)
        {
            var oldPath = await AudioFileAsync(root, "first");
            var newPath = await AudioFileAsync(root, "second");
            var track = Track("same");
            var workspace = new ProgressWorkspace();
            workspace.Inner.Library.Add(track);
            var started = NewSignal();
            var release = NewSignal();
            var reader = new NativeReader
            {
                Read = async (path, _) =>
                {
                    if (path == oldPath)
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        return Properties(61);
                    }
                    return Properties(222);
                }
            };
            await using var view = await CreateAsync(workspace, reader, root);
            workspace.Emit(track, "obsolete", AudioDownloadState.Preparing);
            workspace.Emit(track, "obsolete", AudioDownloadState.Completed, oldPath);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var obsolete = view.DownloadMetadataRefresh;
            workspace.Emit(track, "latest", AudioDownloadState.Preparing);
            workspace.Emit(track, "latest", AudioDownloadState.Completed, newPath);
            var latest = view.DownloadMetadataRefresh;
            release.TrySetResult();
            await Task.WhenAll(obsolete, latest).WaitAsync(TimeSpan.FromSeconds(3));
            workspace.Emit(track, "obsolete", AudioDownloadState.Completed, oldPath);
            TestAssert.True(view.Tracks[0].Duration == TimeSpan.FromSeconds(222) && view.CardTracks[0].Track.Audio.LocalPath == newPath,
                "Only the latest accepted operation may publish its file metadata, even when an obsolete native reader eventually returns.");
        }

        /// <summary>Prevents a catalog read captured before completion from replacing metadata that was already displayed.</summary>
        /// <param name="root">The generated isolated audio directory.</param>
        /// <returns>A task representing metadata-preserving query publication.</returns>
        private static async Task ValidateStaleSnapshotAsync(string root)
        {
            var path = Path.Combine(root, "ready.mp3");
            var track = Track("snapshot") with { Audio = new MediaResource { LocalPath = path } };
            var workspace = new ProgressWorkspace();
            workspace.Inner.Library.Add(track);
            await using var view = await CreateAsync(workspace, new NativeReader(), root);
            await AudioFileAsync(root, "ready");
            var started = NewSignal();
            var release = NewSignal();
            workspace.Inner.SearchOverride = async (_, _) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return new[] { track };
            };
            var reload = TestAssert.ExecuteAsync(view.ShowAllCommand);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            workspace.Emit(track, "ready", AudioDownloadState.Preparing);
            workspace.Emit(track, "ready", AudioDownloadState.Completed, path);
            await view.DownloadMetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            release.TrySetResult();
            await reload.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.Tracks[0].Duration == TimeSpan.FromSeconds(137) && view.CardTracks[0].Track.AudioInfo?.BitrateBitsPerSecond == 192000,
                "A previously captured same-mode catalog snapshot cannot make a completed duration disappear.");
        }

        /// <summary>Allows local parser retry and bounds retained overlays independently of how many songs are downloaded.</summary>
        /// <param name="root">The generated isolated audio directory.</param>
        /// <returns>A task representing failed-native recovery and bounded retention.</returns>
        private static async Task ValidateRetryAndBoundsAsync(string root)
        {
            var path = await AudioFileAsync(root, "retry");
            var track = Track("retry");
            var workspace = new ProgressWorkspace();
            workspace.Inner.Library.Add(track);
            var calls = 0;
            var reader = new NativeReader { Read = (_, _) => ++calls == 1 ? throw new IOException("Synthetic unreadable tags.") : Task.FromResult(Properties(137)) };
            var backend = new MetadataBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: static (_, _) => Task.FromResult("synthetic-loaded-owner"));
            await using var view = await CreateAsync(workspace, reader, root, playback);
            view.SelectedTrack = track;
            await view.PlaySelectedAsync();
            workspace.Emit(track, "failed-tags", AudioDownloadState.Preparing);
            workspace.Emit(track, "failed-tags", AudioDownloadState.Completed, path);
            await view.DownloadMetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.CardTracks[0].IsDownloaded && view.Tracks[0].Duration is null,
                "Optional parser failure must leave the completed audio available without inventing a duration.");
            workspace.Emit(track, "retry-tags", AudioDownloadState.Preparing);
            workspace.Emit(track, "retry-tags", AudioDownloadState.Completed, path);
            await view.DownloadMetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(TimeSpan.FromSeconds(137), view.Tracks[0].Duration, "A later completed-file observation must retry failed metadata.");
            for (var index = 0; index < 66; index++)
            {
                var another = Track("background-" + index);
                workspace.Emit(another, "background-" + index, AudioDownloadState.Preparing);
                workspace.Emit(another, "background-" + index, AudioDownloadState.Completed, path);
                await view.DownloadMetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            }
            TestAssert.True(view.RetainedDownloadMetadataCount <= 64 && workspace.AudioResolveCalls == 0,
                "Completed-file overlays must stay bounded while the workspace retains long-term metadata.");
            var tick = NewSignal();
            void OnClockChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainViewModel.PositionSeconds) && view.PositionSeconds == 21)
                {
                    tick.TrySetResult();
                }
            }
            view.PropertyChanged += OnClockChanged;
            try
            {
                backend.Pulse(TimeSpan.FromSeconds(21));
                await tick.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                view.PropertyChanged -= OnClockChanged;
            }
            TestAssert.True(view.DurationText == "02:17" && view.HasAudioDetails && backend.OpenCount == 1,
                "Bounded background-download retention cannot evict the current native owner's otherwise unknown playback metadata.");
        }

        /// <summary>Cancels and drains a native parser at shutdown without publishing its late result.</summary>
        /// <param name="root">The generated isolated audio directory.</param>
        /// <returns>A task representing the complete download metadata lifecycle.</returns>
        private static async Task ValidateShutdownAsync(string root)
        {
            var path = await AudioFileAsync(root, "closing");
            var track = Track("closing");
            var workspace = new ProgressWorkspace();
            workspace.Inner.Library.Add(track);
            var started = NewSignal();
            var canceled = NewSignal();
            var release = NewSignal();
            var reader = new NativeReader
            {
                Read = async (_, token) =>
                {
                    using var registration = token.Register(() => canceled.TrySetResult());
                    started.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return Properties(137);
                }
            };
            var view = await CreateAsync(workspace, reader, root);
            try
            {
                workspace.Emit(track, "closing", AudioDownloadState.Preparing);
                workspace.Emit(track, "closing", AudioDownloadState.Completed, path);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var closing = view.DisposeAsync().AsTask();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(!closing.IsCompleted, "Shutdown must observe and drain the still-running native read.");
                release.TrySetResult();
                await closing.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.RetainedDownloadMetadataCount == 0 && workspace.Inner.Library[0].Duration is null
                    && workspace.AudioResolveCalls == 0, "A native result returned after shutdown cannot publish, persist or start audio.");
            }
            finally
            {
                release.TrySetResult();
                await view.DisposeAsync();
            }
        }

        /// <summary>Creates a plain presentation whose real file cache uses a controlled native-reader substitute.</summary>
        /// <param name="workspace">The source-scoped local index and progress source.</param>
        /// <param name="reader">The controlled local native reader.</param>
        /// <param name="root">The generated metadata cache directory.</param>
        /// <param name="playback">The optional observed playback owner.</param>
        /// <returns>The initialized presentation owned by the caller.</returns>
        private static async Task<MainViewModel> CreateAsync(ProgressWorkspace workspace, NativeReader reader, string root, PlaybackService? playback = null)
        {
            var provider = new CachedTrackMetadataProvider(reader, Path.Combine(root, "metadata"));
            var view = new MainViewModel(workspace, playback ?? new PlaybackService(new UnavailablePlaybackBackend()), static action => action(), metadata: provider);
            await view.InitializeAsync();
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            await view.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            return view;
        }

        /// <summary>Creates one synthetic complete local resource for real file fingerprint and cache handling.</summary>
        /// <param name="root">The generated audio directory.</param>
        /// <param name="name">The synthetic file identity.</param>
        /// <returns>The fully qualified local file path.</returns>
        private static async Task<string> AudioFileAsync(string root, string name)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, name + ".mp3");
            await File.WriteAllTextAsync(path, "controlled native metadata fixture");
            return path;
        }

        /// <summary>Creates an uncached remote descriptor without a real source or account.</summary>
        /// <param name="id">The deterministic song identity.</param>
        /// <returns>The generated source-owned track.</returns>
        private static MusicTrack Track(string id) => CardDownloadPresentationTests.Track(id, null);

        /// <summary>Creates decoded properties without opening any playback device.</summary>
        /// <param name="seconds">The controlled actual duration.</param>
        /// <returns>The fake native decoder's audio properties.</returns>
        private static AudioMetadata Properties(int seconds) => new()
        {
            Duration = TimeSpan.FromSeconds(seconds),
            AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 192000, SampleRateHz = 48000, Channels = 2 }
        };

        /// <summary>Creates deterministic asynchronous reader and query gates.</summary>
        /// <returns>A signal that never executes continuations inline.</returns>
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Replaces only the platform file parser while retaining the production file-fingerprint metadata cache.</summary>
        private sealed class NativeReader : IAudioMetadataReader
        {
            /// <summary>Retains the controlled native-read count.</summary>
            private int _calls;
            /// <summary>Gets the actual native-file parse count.</summary>
            public int Calls => Volatile.Read(ref _calls);
            /// <summary>Gets or sets the optional controlled native read.</summary>
            public Func<string, CancellationToken, Task<AudioMetadata>>? Read { get; init; }
            /// <inheritdoc />
            public Task<AudioMetadata> ReadAsync(string localPath, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _calls);
                return Read?.Invoke(localPath, cancellationToken) ?? Task.FromResult(Properties(137));
            }
        }

        /// <summary>Publishes local progress and persistence while rejecting every attempt to resolve audio again.</summary>
        private sealed class ProgressWorkspace : IMusicWorkspace, IOnlineMusicWorkspace, IAudioDownloadProgressSource
        {
            /// <summary>Retains an existing narrow local-library fixture.</summary>
            public LibraryExperienceTests.ExperienceWorkspace Inner { get; } = new();
            /// <summary>Gets attempted audio resolutions made by metadata recovery.</summary>
            public int AudioResolveCalls { get; private set; }
            /// <inheritdoc />
            public AudioDownloadProgress? CurrentAudioDownloadProgress { get; private set; }
            /// <inheritdoc />
            public event EventHandler<AudioDownloadProgress>? AudioDownloadProgressChanged;
            /// <summary>Publishes a deterministic resource observation without downloading a file.</summary>
            /// <param name="track">The source revision owning the observation.</param>
            /// <param name="operation">The operation identity.</param>
            /// <param name="state">The accepted transfer phase.</param>
            /// <param name="path">The completed local resource, when available.</param>
            public void Emit(MusicTrack track, string operation, AudioDownloadState state, string? path = null)
            {
                CurrentAudioDownloadProgress = CardDownloadPresentationTests.Progress(track, operation, state, 100, path);
                AudioDownloadProgressChanged?.Invoke(this, CurrentAudioDownloadProgress);
            }
            /// <inheritdoc />
            public bool IsOnline => Inner.IsOnline;
            /// <inheritdoc />
            public string DataDirectory => Inner.DataDirectory;
            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Inner.LoadSettingsAsync(cancellationToken);
            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Inner.SaveSettingsAsync(settings, cancellationToken);
            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled) => Inner.SetOnlineEnabled(enabled);
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default) => Inner.SearchAsync(query, cancellationToken);
            /// <inheritdoc />
            public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default) => Inner.ImportDirectoryAsync(directory, cancellationToken);
            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default) => Inner.IndexDirectoryAsync(directory, cancellationToken);
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default) => Inner.GetPlaylistsAsync(cancellationToken);
            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default) => Inner.SavePlaylistAsync(playlist, cancellationToken);
            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default) => Inner.SynchronizeAsync(endpoint, cancellationToken);
            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                AudioResolveCalls++;
                throw new InvalidOperationException("Metadata recovery cannot resolve audio.");
            }
            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            /// <inheritdoc />
            public IReadOnlyList<EndpointSession> GetEndpointSessions() => Inner.GetEndpointSessions();
            /// <inheritdoc />
            public Task LoginAsync(EndpointConfiguration endpoint, string username, string password, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No real login is permitted.");
            /// <inheritdoc />
            public Task UseGuestAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetCloudPlaylistsAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default) => Inner.GetCloudPlaylistsAsync(endpoint, cancellationToken);
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> GetRecommendationsAsync(EndpointConfiguration endpoint, int page = 0, int pageSize = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MusicTrack>>([]);
            /// <inheritdoc />
            public Task<MusicTrack> EnrichTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No remote metadata is permitted.");
            /// <inheritdoc />
            public Task UpdateTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                var current = Inner.Library.FirstOrDefault(item => item.Id == track.Id);
                return current is not null && current.SourceId == track.SourceId && current.EndpointId == track.EndpointId && current.Revision == track.Revision
                    ? Inner.UpdateTrackMetadataAsync(track, cancellationToken) : Task.CompletedTask;
            }
        }

        /// <summary>Reports a real logical playback owner whose optional decoder metadata remains unknown.</summary>
        private sealed class MetadataBackend : IPlaybackBackend
        {
            /// <summary>Gets explicitly opened native resources.</summary>
            public int OpenCount { get; private set; }
            /// <inheritdoc />
            public bool IsAvailable => true;
            /// <inheritdoc />
            public PlaybackCapabilities Capabilities => PlaybackCapabilities.Playback | PlaybackCapabilities.Seeking;
            /// <inheritdoc />
            public PlaybackSnapshot Snapshot { get; private set; } = new();
            /// <inheritdoc />
            public event EventHandler? StateChanged;
            /// <inheritdoc />
            public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
            {
                OpenCount++;
                Snapshot = new PlaybackSnapshot { State = PlaybackState.Stopped };
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task PlayAsync(CancellationToken cancellationToken = default) => SetState(PlaybackState.Playing);
            /// <inheritdoc />
            public Task PauseAsync(CancellationToken cancellationToken = default) => SetState(PlaybackState.Paused);
            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken = default) => SetState(PlaybackState.Stopped);
            /// <inheritdoc />
            public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
            {
                Pulse(position);
                return Task.CompletedTask;
            }
            /// <summary>Publishes a backend clock update without supplying optional duration or bitrate.</summary>
            /// <param name="position">The authoritative native position.</param>
            public void Pulse(TimeSpan position)
            {
                Snapshot = Snapshot with { Position = position };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            /// <summary>Publishes authoritative native properties after optional local metadata has already appeared.</summary>
            /// <param name="duration">The native decoder's actual duration.</param>
            /// <param name="bitrate">The native decoder's actual bitrate.</param>
            public void PublishProperties(TimeSpan duration, long bitrate)
            {
                Snapshot = Snapshot with { Duration = duration, AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = bitrate } };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            /// <inheritdoc />
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            /// <summary>Changes only the native transport state.</summary>
            /// <param name="state">The requested native lifecycle state.</param>
            /// <returns>A completed native operation.</returns>
            private Task SetState(PlaybackState state)
            {
                Snapshot = Snapshot with { State = state };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }
        }
    }
}
