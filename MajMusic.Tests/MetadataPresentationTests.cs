#nullable enable

using System.Collections.Concurrent;
using System.ComponentModel;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks progressive music-row metadata and playback ownership without native audio or HTTP requests.</summary>
    internal static class MetadataPresentationTests
    {
        /// <summary>Validates failed-source fallback, canceled-page retries and stale playback metadata isolation.</summary>
        /// <returns>A task representing the completed presentation regressions.</returns>
        public static async Task RunAsync()
        {
            await ValidateLocalFallbackAndRemoteRetryAsync();
            await ValidateUnavailableSourceBatchAsync();
            await ValidateCanceledPageRetryAsync();
            await ValidatePlaybackOwnershipAsync(true);
            await ValidatePlaybackOwnershipAsync(false);
        }

        /// <summary>Bounds repeated requests to a failed source without delaying local metadata or preventing later retries.</summary>
        /// <returns>A task representing the bounded failed-source batch.</returns>
        private static async Task ValidateUnavailableSourceBatchAsync()
        {
            var workspace = new MetadataWorkspace([Track("first", "v1"), Track("second", "v1")]) { FailFirstRemote = true };
            var local = new LocalPropertiesProvider();
            await using var viewModel = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()),
                action => action(), metadata: local);
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(2, local.ReadCount, "An unavailable endpoint cannot prevent the other local row from being parsed.");
            TestAssert.True(viewModel.VisibleTracks.All(track => track.AudioInfo?.BitrateBitsPerSecond == 192000),
                "Every available local bitrate remains visible when its remote source fails.");
            TestAssert.Equal(1, workspace.RemoteCalls.Values.Sum(), "One failed source should not receive a failed request for every row in a page.");
            await TestAssert.ExecuteAsync(viewModel.SearchCommand);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(viewModel.VisibleTracks.All(track => track.Duration == TimeSpan.FromSeconds(95)),
                "A later search may retry all rows whose source was previously unavailable.");
        }

        /// <summary>Keeps cached audio properties visible when optional source information fails and permits a later retry.</summary>
        /// <returns>A task representing local fallback and remote retry checks.</returns>
        private static async Task ValidateLocalFallbackAndRemoteRetryAsync()
        {
            var workspace = new MetadataWorkspace([Track("cached", "v1")]) { FailFirstRemote = true };
            var local = new LocalPropertiesProvider();
            await using var viewModel = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()),
                action => action(), metadata: local);
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(1, local.ReadCount, "Visible rows must be parsed without requiring a selection or playback.");
            TestAssert.Equal(1, workspace.RemoteCalls.GetValueOrDefault("cached"), "Missing duration should attempt optional source metadata.");
            TestAssert.Equal(192000L, viewModel.VisibleTracks.Single().AudioInfo?.BitrateBitsPerSecond,
                "A remote metadata failure cannot hide properties from an already cached local file.");
            TestAssert.True(viewModel.VisibleTracks.Single().Duration is null, "Failed remote metadata cannot invent a music duration.");
            TestAssert.True(viewModel.SelectedTrack is null, "Progressive metadata must not require selecting the row.");

            await TestAssert.ExecuteAsync(viewModel.SearchCommand);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(2, workspace.RemoteCalls.GetValueOrDefault("cached"), "A failed source lookup must be retried by a later search.");
            TestAssert.Equal(TimeSpan.FromSeconds(95), viewModel.VisibleTracks.Single().Duration, "A later successful source lookup must fill the duration.");
            TestAssert.Equal(192000L, viewModel.VisibleTracks.Single().AudioInfo?.BitrateBitsPerSecond,
                "Remote duration enrichment must preserve the available local bitrate.");
        }

        /// <summary>Allows a song canceled on an obsolete page to be enriched when that page is reopened.</summary>
        /// <returns>A task representing page cancellation and retry validation.</returns>
        private static async Task ValidateCanceledPageRetryAsync()
        {
            var tracks = Enumerable.Range(0, 9).Select(index => Track(index.ToString(), "v1")).ToArray();
            var workspace = new MetadataWorkspace(tracks) { BlockFirstRemote = true };
            await using var viewModel = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), action => action());
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            await workspace.FirstRemoteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var obsolete = viewModel.MetadataRefresh;
            viewModel.NextLibraryPageCommand.Execute(null);
            await workspace.FirstRemoteCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(obsolete, viewModel.MetadataRefresh).WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal("8", viewModel.VisibleTracks.Single().Id, "The new page must remain visible after an old-page request is canceled.");
            TestAssert.True(viewModel.Tracks[0].Duration is null, "Canceled page metadata cannot publish an incomplete result.");

            viewModel.PreviousLibraryPageCommand.Execute(null);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(2, workspace.RemoteCalls.GetValueOrDefault("0"), "Reopening the old page must retry its canceled source request.");
            TestAssert.Equal(TimeSpan.FromSeconds(95), viewModel.VisibleTracks[0].Duration, "The retried row must receive available duration metadata.");
        }

        /// <summary>Prevents an old playback object's metadata from replacing newer rows or refreshed resource descriptors.</summary>
        /// <param name="changeRevision">Whether the replacement is a new source revision rather than a refreshed same-version descriptor.</param>
        /// <returns>A task representing playback-to-library ownership validation.</returns>
        private static async Task ValidatePlaybackOwnershipAsync(bool changeRevision)
        {
            var original = Track("playing", "v1");
            var workspace = new MetadataWorkspace([original]) { ReturnRemoteMetadata = false };
            var backend = new MetadataPlaybackBackend();
            await using var viewModel = new MainViewModel(workspace, new PlaybackService(backend), action => action());
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            viewModel.SelectedTrack = viewModel.Tracks.Single();
            await viewModel.PlaySelectedAsync();
            var replacement = original with
            {
                Revision = changeRevision ? "v2" : "v1",
                Title = "Refreshed title",
                Audio = original.Audio with
                {
                    LocalPath = Path.Combine(Path.GetTempPath(), "new-resource.mp3"),
                    RemoteUri = new Uri("https://example.invalid/refreshed.mp3")
                },
                Duration = TimeSpan.FromSeconds(180),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 48000 }
            };
            workspace.ReplaceTrack(replacement);
            await TestAssert.ExecuteAsync(viewModel.SearchCommand);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(replacement.Revision, viewModel.SelectedTrack?.Revision, "A refresh must retain the selected identity using its latest indexed revision.");
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainViewModel.AudioDetails) && viewModel.AudioDetails.Contains("129", StringComparison.Ordinal))
                {
                    observed.TrySetResult();
                }
            }

            viewModel.PropertyChanged += OnPropertyChanged;
            try
            {
                backend.PublishMetadata(TimeSpan.FromSeconds(61), 129000);
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                viewModel.PropertyChanged -= OnPropertyChanged;
            }

            var selected = viewModel.SelectedTrack ?? throw new InvalidOperationException("The selected refreshed row was lost.");
            TestAssert.Equal(replacement.Revision, selected.Revision, "Old playback metadata cannot restore an obsolete selected revision.");
            TestAssert.Equal(replacement.Title, selected.Title, "Playback metadata must preserve the refreshed display title.");
            TestAssert.Equal(replacement.Audio, selected.Audio, "Playback metadata cannot replace current source paths or URLs with its old descriptor.");
            TestAssert.Equal(replacement.Audio, viewModel.VisibleTracks.Single().Audio, "Visible-row metadata merges preserve current resource ownership.");
            if (changeRevision)
            {
                TestAssert.Equal(replacement.Duration, selected.Duration, "Properties for an old revision cannot overwrite the newer row's duration.");
                TestAssert.Equal(320000L, selected.AudioInfo?.BitrateBitsPerSecond, "Properties for an old revision cannot overwrite the newer row's bitrate.");
            }
            else
            {
                TestAssert.Equal(TimeSpan.FromSeconds(61), selected.Duration, "Same-version authoritative playback properties can update audio fields.");
                TestAssert.Equal(129000L, selected.AudioInfo?.BitrateBitsPerSecond, "Same-version native bitrate can update the selected row.");
            }
        }

        /// <summary>Creates an online entry with a synthetic already-cached resource path.</summary>
        /// <param name="id">The stable source-session identity.</param>
        /// <param name="revision">The indexed source version.</param>
        /// <returns>A music entry that requires no real file access.</returns>
        private static MusicTrack Track(string id, string revision)
        {
            return new MusicTrack
            {
                Id = id,
                Title = "Track " + id,
                RemoteId = id,
                Revision = revision,
                SourceId = "server",
                EndpointId = "endpoint",
                SourceName = "Test source",
                Audio = new MediaResource
                {
                    LocalPath = Path.Combine(Path.GetTempPath(), "metadata-ui-" + id + ".mp3"),
                    RemoteUri = new Uri("https://example.invalid/audio.mp3")
                }
            };
        }

        /// <summary>Provides a local bitrate while leaving missing duration available for optional source enrichment.</summary>
        private sealed class LocalPropertiesProvider : ITrackMetadataProvider
        {
            /// <summary>Gets how many visible rows were read.</summary>
            public int ReadCount { get; private set; }

            /// <inheritdoc />
            public Task<MusicTrack> EnrichAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadCount++;
                return Task.FromResult(track with { AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 192000, SampleRateHz = 44100 } });
            }
        }

        /// <summary>Models only local row storage and optional metadata failures for view-model regressions.</summary>
        private sealed class MetadataWorkspace : IMusicWorkspace, IOnlineMusicWorkspace
        {
            /// <summary>Stores immutable music rows in search order.</summary>
            private readonly List<MusicTrack> _tracks;
            /// <summary>Stores local favorites created during view initialization.</summary>
            private readonly List<MusicPlaylist> _playlists = new();
            /// <summary>Stores non-secret source preferences.</summary>
            private AppSettings _settings = new()
            {
                OnlineEnabled = true,
                ActiveEndpointId = "endpoint",
                Endpoints = [new EndpointConfiguration { Id = "endpoint", Name = "Test source", BaseUri = new Uri("https://example.invalid/api/") }]
            };
            /// <summary>Counts all optional metadata requests atomically across cancellation continuations.</summary>
            private int _remoteCount;

            /// <summary>Creates a metadata-only workspace without file or network access.</summary>
            /// <param name="tracks">The ordered music rows exposed by search.</param>
            public MetadataWorkspace(IEnumerable<MusicTrack> tracks)
            {
                _tracks = tracks.ToList();
            }

            /// <summary>Gets whether the first source request should fail.</summary>
            public bool FailFirstRemote { get; init; }
            /// <summary>Gets whether the first source request should wait for page cancellation.</summary>
            public bool BlockFirstRemote { get; init; }
            /// <summary>Gets whether the simulated source supplies optional audio metadata.</summary>
            public bool ReturnRemoteMetadata { get; init; } = true;
            /// <summary>Counts source lookups for each music identity.</summary>
            public ConcurrentDictionary<string, int> RemoteCalls { get; } = new(StringComparer.Ordinal);
            /// <summary>Signals that the canceled-page fixture has begun its request.</summary>
            public TaskCompletionSource FirstRemoteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Signals cancellation of the obsolete page request.</summary>
            public TaskCompletionSource FirstRemoteCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <inheritdoc />
            public bool IsOnline => _settings.OnlineEnabled;
            /// <inheritdoc />
            public string DataDirectory => Path.Combine(Path.GetTempPath(), "MajMusic-metadata-ui");

            /// <summary>Replaces a saved row to model synchronization while an older version is playing.</summary>
            /// <param name="track">The newly indexed row.</param>
            public void ReplaceTrack(MusicTrack track)
            {
                var index = _tracks.FindIndex(item => item.Id == track.Id);
                _tracks[index] = track;
            }

            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_settings);
            }

            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                _settings = settings;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled)
            {
                _settings = _settings with { OnlineEnabled = enabled };
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<MusicTrack>>(_tracks.Where(item => string.IsNullOrWhiteSpace(query)
                    || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
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
                return Task.FromResult<IReadOnlyList<MusicPlaylist>>(_playlists.ToArray());
            }

            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                _playlists.RemoveAll(item => item.Id == playlist.Id);
                _playlists.Add(playlist);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(0);
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

            /// <inheritdoc />
            public IReadOnlyList<EndpointSession> GetEndpointSessions()
            {
                return [new EndpointSession { EndpointId = "endpoint" }];
            }

            /// <inheritdoc />
            public Task LoginAsync(EndpointConfiguration endpoint, string username, string password, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task UseGuestAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetCloudPlaylistsAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<MusicPlaylist>>(Array.Empty<MusicPlaylist>());
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> GetRecommendationsAsync(EndpointConfiguration endpoint, int page = 0, int pageSize = 50, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<MusicTrack>>(Array.Empty<MusicTrack>());
            }

            /// <inheritdoc />
            public async Task<MusicTrack> EnrichTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                var call = Interlocked.Increment(ref _remoteCount);
                RemoteCalls.AddOrUpdate(track.Id, 1, (_, count) => count + 1);
                if (BlockFirstRemote && call == 1)
                {
                    FirstRemoteStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        FirstRemoteCanceled.TrySetResult();
                        throw;
                    }
                }

                if (FailFirstRemote && call == 1)
                {
                    throw new HttpRequestException("Simulated optional metadata failure.");
                }

                return ReturnRemoteMetadata ? track with { Duration = TimeSpan.FromSeconds(95) } : track;
            }

            /// <inheritdoc />
            public Task UpdateTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = _tracks.FindIndex(item => item.Id == track.Id && item.Revision == track.Revision && item.EndpointId == track.EndpointId);
                if (index >= 0)
                {
                    _tracks[index] = _tracks[index] with { Duration = track.Duration, AudioInfo = track.AudioInfo };
                }

                return Task.CompletedTask;
            }
        }

        /// <summary>Publishes controlled native-style snapshots without file parsing or audio output.</summary>
        private sealed class MetadataPlaybackBackend : IPlaybackBackend
        {
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
                Snapshot = new PlaybackSnapshot
                {
                    State = PlaybackState.Paused,
                    Duration = TimeSpan.FromSeconds(60),
                    AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 128000, SampleRateHz = 44100 }
                };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task PlayAsync(CancellationToken cancellationToken = default)
            {
                Snapshot = Snapshot with { State = PlaybackState.Playing };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task PauseAsync(CancellationToken cancellationToken = default)
            {
                Snapshot = Snapshot with { State = PlaybackState.Paused };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                Snapshot = Snapshot with { State = PlaybackState.Stopped };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
            {
                Snapshot = Snapshot with { Position = position };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            /// <summary>Raises an audio-properties update while retaining the same loaded playback owner.</summary>
            /// <param name="duration">The backend's refreshed actual duration.</param>
            /// <param name="bitrate">The backend's refreshed actual bitrate.</param>
            public void PublishMetadata(TimeSpan duration, long bitrate)
            {
                Snapshot = Snapshot with { Duration = duration, AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = bitrate, SampleRateHz = 44100 } };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
