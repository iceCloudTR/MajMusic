#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks transfer of visible metadata ownership and preservation of parsed local information.</summary>
    internal static class BrowseMetadataTests
    {
        /// <summary>Runs overlapping viewport, waiter cancellation and optional remote-failure regressions.</summary>
        /// <returns>A task representing completed metadata cases.</returns>
        public static async Task RunAsync()
        {
            await ValidateCanceledOwnerRetryAsync();
            await ValidateCanceledWaiterAsync();
            await ValidateLocalResultBeforeRemoteFailureAsync();
        }

        /// <summary>Retries a canceled owner's metadata request for a second control that remains visible.</summary>
        /// <returns>A task representing ownership transfer.</returns>
        private static async Task ValidateCanceledOwnerRetryAsync()
        {
            var workspace = new MetadataWorkspace();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new ControlledMetadataProvider
            {
                Read = async (track, call, token) =>
                {
                    if (call == 1)
                    {
                        started.TrySetResult();
                        await Task.Delay(Timeout.Infinite, token);
                    }

                    return Complete(track);
                }
            };
            await using var view = await CreateViewAsync(workspace, provider);
            var card = view.CardTracks.Single();
            using var firstControl = new CancellationTokenSource();
            using var secondControl = new CancellationTokenSource();
            var first = view.EnsureBrowseMetadataAsync(card, firstControl.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second = view.EnsureBrowseMetadataAsync(card, secondControl.Token);
            TestAssert.True(!second.IsCompleted, "A second visible control must observe the pending request rather than permanently skipping metadata.");
            firstControl.Cancel();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(2, provider.Calls, "The remaining visible control must retry after the original request owner detaches.");
            TestAssert.True(card.Track.Duration == TimeSpan.FromSeconds(123) && card.Track.AudioInfo?.BitrateBitsPerSecond == 192000,
                "A canceled first viewport owner cannot leave the still-visible song without available metadata.");
            await view.EnsureBrowseMetadataAsync(card, secondControl.Token);
            TestAssert.Equal(2, provider.Calls, "Completed metadata must be reused without another visible read.");
        }

        /// <summary>Ends one waiting viewport without canceling the request still owned by another visible control.</summary>
        /// <returns>A task representing independent waiter cancellation.</returns>
        private static async Task ValidateCanceledWaiterAsync()
        {
            var workspace = new MetadataWorkspace();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new ControlledMetadataProvider
            {
                Read = async (track, _, token) =>
                {
                    await release.Task.WaitAsync(token);
                    return Complete(track);
                }
            };
            await using var view = await CreateViewAsync(workspace, provider);
            var card = view.CardTracks.Single();
            using var owner = new CancellationTokenSource();
            using var waiter = new CancellationTokenSource();
            var first = view.EnsureBrowseMetadataAsync(card, owner.Token);
            var second = view.EnsureBrowseMetadataAsync(card, waiter.Token);
            waiter.Cancel();
            await second.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(!first.IsCompleted && provider.Calls == 1, "Canceling a waiting control cannot cancel the current visible owner's read.");
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(TimeSpan.FromSeconds(123), card.Track.Duration, "The original visible control must still receive its completed metadata.");
        }

        /// <summary>Publishes and persists locally parsed properties before an optional remote read can fail.</summary>
        /// <returns>A task representing partial local-result preservation.</returns>
        private static async Task ValidateLocalResultBeforeRemoteFailureAsync()
        {
            var workspace = new MetadataWorkspace { FailRemote = true };
            var provider = new ControlledMetadataProvider
            {
                Read = (track, _, _) => Task.FromResult(track with { Duration = TimeSpan.FromSeconds(123) })
            };
            var backend = new LibraryExperienceTests.ExperienceBackend();
            await using var view = await CreateViewAsync(workspace, provider, backend);
            view.OnlineEnabled = true;
            var card = view.CardTracks.Single();
            await view.EnsureBrowseMetadataAsync(card, CancellationToken.None);
            TestAssert.Equal(1, workspace.RemoteRequests, "An incomplete local result may request the remaining optional online properties.");
            TestAssert.True(workspace.LocalWasPublishedBeforeRemote && workspace.SavedMetadata.Any(track => track.Duration == TimeSpan.FromSeconds(123)),
                "Available local metadata must be persisted before the optional remote request begins.");
            TestAssert.True(card.Track.Duration == TimeSpan.FromSeconds(123) && view.Tracks.Single().Duration == TimeSpan.FromSeconds(123),
                "A failed optional remote request cannot erase the already parsed local duration from either presentation.");
            TestAssert.True(view.SelectedTrack is null && backend.OpenCount == 0 && workspace.AudioRequests == 0,
                "Visible metadata reads must remain independent of selecting, preparing or downloading music.");
        }

        /// <summary>Loads a synthetic browse list before enabling the explicitly controlled visible-reader path.</summary>
        /// <param name="workspace">The isolated source and metadata persistence.</param>
        /// <param name="provider">The visible-reader behavior used by the case.</param>
        /// <param name="backend">An optional synthetic audio owner.</param>
        /// <returns>The initialized view model.</returns>
        private static async Task<MainViewModel> CreateViewAsync(MetadataWorkspace workspace, ControlledMetadataProvider provider,
            LibraryExperienceTests.ExperienceBackend? backend = null)
        {
            var view = new MainViewModel(workspace, new PlaybackService(backend ?? new LibraryExperienceTests.ExperienceBackend()),
                static action => action(), metadata: provider);
            await view.InitializeAsync();
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate;
            await view.MetadataRefresh;
            provider.Enabled = true;
            return view;
        }

        /// <summary>Adds complete parsed audio properties to the existing song ownership.</summary>
        /// <param name="track">The source entry retained by the visible card.</param>
        /// <returns>The same entry with complete audio properties.</returns>
        private static MusicTrack Complete(MusicTrack track)
        {
            return track with { Duration = TimeSpan.FromSeconds(123), AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 192000 } };
        }

        /// <summary>Separates initial legacy-page reads from the explicit viewport ownership cases.</summary>
        private sealed class ControlledMetadataProvider : ITrackMetadataProvider
        {
            /// <summary>Counts explicitly enabled reads.</summary>
            private int _calls;
            /// <summary>Gets or sets whether this case's visible-reader behavior is active.</summary>
            public bool Enabled { get; set; }
            /// <summary>Gets the number of reads started by the viewport case.</summary>
            public int Calls => Volatile.Read(ref _calls);
            /// <summary>Gets or sets the controlled local parser operation.</summary>
            public Func<MusicTrack, int, CancellationToken, Task<MusicTrack>> Read { get; init; } = (track, _, _) => Task.FromResult(track);
            /// <inheritdoc />
            public Task<MusicTrack> EnrichAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                return Enabled ? Read(track, Interlocked.Increment(ref _calls), cancellationToken) : Task.FromResult(track);
            }
        }

        /// <summary>Provides a single indexed remote-capable entry without network or native media dependencies.</summary>
        private sealed class MetadataWorkspace : IMusicWorkspace, IOnlineMusicWorkspace
        {
            /// <summary>Stores local index and preference behavior shared with other presentation regressions.</summary>
            private readonly LibraryExperienceTests.ExperienceWorkspace _inner = new();
            /// <summary>Creates one entry whose duration can be supplied by a controlled local reader.</summary>
            public MetadataWorkspace()
            {
                _inner.Library.Add(new MusicTrack
                {
                    Id = "browse-song",
                    Title = "Browse song",
                    RemoteId = "remote-song",
                    SourceId = "source",
                    EndpointId = "endpoint",
                    Audio = new MediaResource { LocalPath = "unopened.mp3" }
                });
            }
            /// <summary>Gets or sets whether optional remote details fail.</summary>
            public bool FailRemote { get; init; }
            /// <summary>Gets how many optional remote reads were attempted.</summary>
            public int RemoteRequests { get; private set; }
            /// <summary>Gets how many forbidden audio resolutions were attempted.</summary>
            public int AudioRequests { get; private set; }
            /// <summary>Gets whether local persistence completed before remote enrichment began.</summary>
            public bool LocalWasPublishedBeforeRemote { get; private set; }
            /// <summary>Gets the sequence of metadata snapshots persisted by the presentation.</summary>
            public List<MusicTrack> SavedMetadata { get; } = new();
            /// <inheritdoc />
            public bool IsOnline => _inner.IsOnline;
            /// <inheritdoc />
            public string DataDirectory => _inner.DataDirectory;
            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                return _inner.LoadSettingsAsync(cancellationToken);
            }
            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                return _inner.SaveSettingsAsync(settings, cancellationToken);
            }
            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled)
            {
                _inner.SetOnlineEnabled(enabled);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                return _inner.SearchAsync(query, cancellationToken);
            }
            /// <inheritdoc />
            public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                return _inner.ImportDirectoryAsync(directory, cancellationToken);
            }
            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                return _inner.IndexDirectoryAsync(directory, cancellationToken);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
            {
                return _inner.GetPlaylistsAsync(cancellationToken);
            }
            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                return _inner.SavePlaylistAsync(playlist, cancellationToken);
            }
            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return _inner.SynchronizeAsync(endpoint, cancellationToken);
            }
            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<string?>(null);
            }
            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                AudioRequests++;
                throw new InvalidOperationException("A visible metadata read requested audio.");
            }
            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default)
            {
                return _inner.ClearCacheAsync(cancellationToken);
            }
            /// <inheritdoc />
            public IReadOnlyList<EndpointSession> GetEndpointSessions()
            {
                return _inner.GetEndpointSessions();
            }
            /// <inheritdoc />
            public Task LoginAsync(EndpointConfiguration endpoint, string username, string password, CancellationToken cancellationToken = default)
            {
                return _inner.LoginAsync(endpoint, username, password, cancellationToken);
            }
            /// <inheritdoc />
            public Task UseGuestAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return _inner.UseGuestAsync(endpoint, cancellationToken);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetCloudPlaylistsAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return _inner.GetCloudPlaylistsAsync(endpoint, cancellationToken);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> GetRecommendationsAsync(EndpointConfiguration endpoint, int page = 0, int pageSize = 50,
                CancellationToken cancellationToken = default)
            {
                return _inner.GetRecommendationsAsync(endpoint, page, pageSize, cancellationToken);
            }
            /// <inheritdoc />
            public Task<MusicTrack> EnrichTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                RemoteRequests++;
                LocalWasPublishedBeforeRemote = SavedMetadata.Any(item => item.Duration == TimeSpan.FromSeconds(123));
                if (FailRemote)
                {
                    throw new InvalidOperationException("Optional remote metadata is temporarily unavailable.");
                }

                return Task.FromResult(track);
            }
            /// <inheritdoc />
            public Task UpdateTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                SavedMetadata.Add(track);
                return _inner.UpdateTrackMetadataAsync(track, cancellationToken);
            }
        }
    }
}
