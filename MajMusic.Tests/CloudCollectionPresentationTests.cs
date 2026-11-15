#nullable enable

using System.Collections.Specialized;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks cloud refresh publication against account changes during local playlist queries.</summary>
    internal static class CloudCollectionPresentationTests
    {
        /// <summary>Runs the real collection worker through a gated query and a user-initiated account change.</summary>
        /// <returns>A task representing completed account-isolation and selection-preservation assertions.</returns>
        public static async Task RunAsync()
        {
            var workspace = new RefreshWorkspace();
            await using var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            NotifyCollectionChangedEventHandler? observe = null;
            try
            {
                await view.InitializeAsync();
                view.SelectedTrack = workspace.Track;
                await ExecuteAsync(view, view.ToggleFavoriteCommand);
                await WaitUntilAsync(() => workspace.Synchronizations > 0);
                await view.CloudCollectionsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.Playlists.Any(playlist => playlist.Id == RefreshWorkspace.CloudId),
                    "A completed collection worker must populate the signed-in account's cloud snapshot before the race.");
                view.SelectedPlaylist = view.Playlists.Single(playlist => playlist.Id == RefreshWorkspace.CloudId);

                workspace.HoldNextRefresh();
                await ExecuteAsync(view, view.ToggleFavoriteCommand);
                await workspace.QueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var earlierRefresh = view.CloudCollectionsUpdate;
                TestAssert.True(workspace.Synchronizations == 2 && workspace.CloudReads == 2 && view.IsIdle,
                    "The second actual upload worker must reach its cloud refresh and pause at the local query without blocking user commands.");
                TestAssert.True(view.GuestCommand.CanExecute(null), "An account change must remain available during background collection publication.");
                view.GuestCommand.Execute(null);
                await WaitUntilAsync(() => workspace.IsGuest && view.Playlists.All(playlist => playlist.Id != RefreshWorkspace.CloudId));
                TestAssert.True(!view.GuestCommand.CanExecute(null),
                    "The real Guest operation must still be awaiting its playlist refresh while the earlier query owns the reload gate.");

                var currentSelection = view.Playlists.Single(playlist => playlist.Id == RefreshWorkspace.LocalChoiceId);
                view.SelectedPlaylist = currentSelection;
                var stalePublication = false;
                observe = (_, _) =>
                {
                    if (view.Playlists.Any(playlist => playlist.Id == RefreshWorkspace.CloudId))
                    {
                        stalePublication = true;
                    }
                };
                view.Playlists.CollectionChanged += observe;
                workspace.ReleaseQuery.TrySetResult();
                await earlierRefresh.WaitAsync(TimeSpan.FromSeconds(3));
                await WaitUntilAsync(() => view.IsIdle && view.GuestCommand.CanExecute(null));
                await view.CloudCollectionsUpdate.WaitAsync(TimeSpan.FromSeconds(3));

                TestAssert.True(!stalePublication && view.Playlists.All(playlist => playlist.Id != RefreshWorkspace.CloudId),
                    "Releasing an older worker's query must never republish its former account's cloud list, including transient collection events.");
                TestAssert.Equal(RefreshWorkspace.LocalChoiceId, view.SelectedPlaylist?.Id,
                    "A background refresh must retain a local selection made after the account snapshot was cleared.");
                TestAssert.True(workspace.IsGuest && workspace.Synchronizations >= 2 && workspace.CloudReads == 2,
                    "The completed race must retain Guest without refreshing private lists under the replaced session.");
            }
            finally
            {
                workspace.ReleaseQuery.TrySetResult();
                if (observe is not null)
                {
                    view.Playlists.CollectionChanged -= observe;
                }
            }
        }

        /// <summary>Executes one user action without waiting for its independently observed cloud worker.</summary>
        /// <param name="view">The shared command owner.</param>
        /// <param name="command">The user action to execute.</param>
        /// <returns>A task representing the completed foreground command.</returns>
        private static async Task ExecuteAsync(MainViewModel view, AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The requested fixture command must be available.");
            command.Execute(null);
            await WaitUntilAsync(() => view.IsIdle && command.CanExecute(null));
        }

        /// <summary>Waits for an explicit fixture condition without relying on arbitrary scheduling delays.</summary>
        /// <param name="condition">The state required before proceeding.</param>
        /// <returns>A task representing the observed condition, or a bounded timeout failure.</returns>
        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!condition())
            {
                await Task.Delay(1, timeout.Token);
            }
        }

        /// <summary>Adds a controllable upload-refresh boundary to the existing file-free library fixture.</summary>
        private sealed class RefreshWorkspace : IMusicWorkspace, IOnlineMusicWorkspace, ICloudCollectionWorkspace
        {
            /// <summary>Identifies the former account's private cloud list.</summary>
            public const string CloudId = "cloud:source:alice-private";
            /// <summary>Identifies the local choice made during the account change.</summary>
            public const string LocalChoiceId = "local:later-selection";
            /// <summary>Owns the existing in-memory persistence and independent-session behavior.</summary>
            private readonly LibraryExperienceTests.ExperienceWorkspace _inner = new();
            /// <summary>Arms a query gate only after the upload worker reads its cloud snapshot.</summary>
            private int _holdNextRefresh;
            /// <summary>Ensures one classifier query, rather than a preceding foreground query, is held.</summary>
            private int _holdNextQuery;
            /// <summary>Counts real entry into the worker's synchronization method.</summary>
            private int _synchronizations;
            /// <summary>Counts the worker's cloud refreshes.</summary>
            private int _cloudReads;

            /// <summary>Creates account A, its private list and local lists without opening files or sending requests.</summary>
            public RefreshWorkspace()
            {
                var endpoint = new EndpointConfiguration
                {
                    Id = "source",
                    Name = "Generated source",
                    BaseUri = new Uri("https://cloud-presentation.example.invalid/api/"),
                    Accounts = [new SavedEndpointAccount { Id = "alice", Username = "Alice", UseCloudCollections = true }],
                    SelectedAccountId = "alice"
                };
                _inner.Settings = new AppSettings { OnlineEnabled = true, ActiveEndpointId = endpoint.Id, Endpoints = [endpoint] };
                _inner.Sessions[endpoint.Id] = new EndpointSession { EndpointId = endpoint.Id, IsLoggedIn = true, Username = "Alice" };
                _inner.Library.Add(Track);
                _inner.LocalPlaylists["local:favorites"] = new MusicPlaylist { Id = "local:favorites", Name = "Favorites", IsFavorite = true };
                _inner.LocalPlaylists[LocalChoiceId] = new MusicPlaylist { Id = LocalChoiceId, Name = "Later local choice" };
                _inner.Cloud[endpoint.Id] = [new MusicPlaylist
                {
                    Id = CloudId,
                    Name = "Alice private collection",
                    EndpointId = endpoint.Id,
                    RemoteId = "alice-private",
                    TrackIds = [Track.Id]
                }];
            }

            /// <summary>Gets the local song used only to trigger real favorite commands.</summary>
            public MusicTrack Track { get; } = new() { Id = "local-song", Title = "Generated song", Audio = new MediaResource() };
            /// <summary>Signals that the former account's worker is suspended after reading its cloud snapshot.</summary>
            public TaskCompletionSource QueryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the precise query whose continuation previously could publish an obsolete cloud snapshot.</summary>
            public TaskCompletionSource ReleaseQuery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets the number of actual upload worker invocations.</summary>
            public int Synchronizations => Volatile.Read(ref _synchronizations);
            /// <summary>Gets the number of authenticated cloud refreshes.</summary>
            public int CloudReads => Volatile.Read(ref _cloudReads);
            /// <summary>Gets whether the real account-change command has replaced the signed-in identity.</summary>
            public bool IsGuest => _inner.Settings.Endpoints.Single().SelectedAccountId is null && !_inner.Sessions["source"].IsLoggedIn;
            /// <inheritdoc />
            public bool IsOnline => _inner.IsOnline;
            /// <inheritdoc />
            public string DataDirectory => _inner.DataDirectory;

            /// <summary>Arms the next worker-owned cloud refresh without affecting its preceding foreground playlist save.</summary>
            public void HoldNextRefresh()
            {
                Interlocked.Exchange(ref _holdNextRefresh, 1);
            }

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
            public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _holdNextQuery, 0) == 1)
                {
                    QueryStarted.TrySetResult();
                    await ReleaseQuery.Task.WaitAsync(cancellationToken);
                }

                return await _inner.SearchAsync(query, cancellationToken);
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
                return _inner.ResolveCoverAsync(track, cancellationToken);
            }

            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                return _inner.ResolveAudioAsync(track, keepOffline, cancellationToken);
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
            public async Task UseGuestAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                await _inner.UseGuestAsync(endpoint, cancellationToken);
                _inner.Settings = _inner.Settings with
                {
                    Endpoints = _inner.Settings.Endpoints.Select(item => item.Id == endpoint.Id ? item with { SelectedAccountId = null } : item).ToArray()
                };
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetCloudPlaylistsAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _cloudReads);
                if (Interlocked.Exchange(ref _holdNextRefresh, 0) == 1)
                {
                    Interlocked.Exchange(ref _holdNextQuery, 1);
                }

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
                return _inner.EnrichTrackMetadataAsync(track, cancellationToken);
            }

            /// <inheritdoc />
            public Task UpdateTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                return _inner.UpdateTrackMetadataAsync(track, cancellationToken);
            }

            /// <inheritdoc />
            public Task<EndpointConfiguration> SetCloudCollectionUsageAsync(EndpointConfiguration endpoint, string accountId, bool enabled,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("This fixture changes account identity through the user-facing Guest command.");
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<CloudCollectionSyncResult>> SynchronizeCloudCollectionsAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _synchronizations);
                return Task.FromResult<IReadOnlyList<CloudCollectionSyncResult>>(IsGuest ? [] :
                    [new CloudCollectionSyncResult { EndpointId = "source", AccountId = "alice", Succeeded = true, CollectionCount = 1 }]);
            }
        }
    }
}
