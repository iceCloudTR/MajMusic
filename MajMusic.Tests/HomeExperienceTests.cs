#nullable enable

using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks mode-based home content, progressive search and stable inspector ownership.</summary>
    internal static class HomeExperienceTests
    {
        /// <summary>Runs home-mode and viewport regressions without HTTP, audio output or file access.</summary>
        /// <returns>A task representing the completed cases.</returns>
        public static async Task RunAsync()
        {
            await ValidateOfflineModesAsync();
            await ValidateHomePlaylistSelectionAsync();
            await ValidateAccountSnapshotsAsync();
            await ValidateLateSearchAsync();
            await ValidateFeedDuringSearchAsync();
            await ValidateLargeLibraryPublicationAsync();
            await ValidateInterruptedModeLoadAsync();
            await ValidateLoadingOwnershipAsync();
        }

        /// <summary>Publishes a large catalog and its row grouping as complete snapshots instead of one event per song.</summary>
        /// <returns>A task representing large-library, playlist ordering and collection notification assertions.</returns>
        private static async Task ValidateLargeLibraryPublicationAsync()
        {
            var workspace = Workspace(24000);
            await using var view = View(workspace, new LibraryExperienceTests.ExperienceBackend());
            await view.InitializeAsync();
            var trackChanges = 0;
            var rowChanges = 0;
            view.Tracks.CollectionChanged += (_, _) => trackChanges++;
            view.BrowseRows.CollectionChanged += (_, _) => rowChanges++;
            await ExecuteHomeAsync(view, view.ShowLibraryModeCommand);
            TestAssert.True(view.Tracks.Count == 24000 && view.BrowseRows.Count == 12000,
                "The complete catalog must remain available through the virtualized card rows.");
            TestAssert.True(trackChanges == 1 && rowChanges == 1,
                "A large library must publish exactly one track snapshot and one row snapshot, independent of its song count.");
            TestAssert.Equal(0, view.MaterializedCardCount,
                "Publishing a large catalog and grouping its rows must not construct invisible song presentations.");
            var last = view.CardTracks[^1];
            TestAssert.True(view.MaterializedCardCount == 1 && view.GetBrowseCardIndex(last) == 23999,
                "An explicit jump to the final song must materialize only that song and locate it without walking the catalog.");
            view.SelectedTrack = last.Track;
            TestAssert.True(ReferenceEquals(last, view.SelectedBrowseCard) && view.MaterializedCardCount == 1,
                "Selection and its sidebar lookup must not materialize unrelated catalog entries.");
            trackChanges = 0;
            rowChanges = 0;
            view.SelectedPlaylist = view.Playlists.Single(item => item.Id == "ordered");
            await ExecuteHomeAsync(view, view.ShowPlaylistsModeCommand);
            TestAssert.True(view.Tracks.Select(item => item.Id).SequenceEqual(["local-04", "local-02"])
                && trackChanges == 1 && rowChanges == 1,
                "Switching from a large library to a small ordered playlist must replace both snapshots once.");
        }

        /// <summary>Shows the target's loading state immediately and prevents an obsolete read from hiding its replacement skeleton.</summary>
        /// <returns>A task representing synchronous loading, request ownership and completion assertions.</returns>
        private static async Task ValidateLoadingOwnershipAsync()
        {
            var workspace = Workspace(5);
            await using var view = View(workspace, new LibraryExperienceTests.ExperienceBackend());
            await view.InitializeAsync();
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            workspace.SearchOverride = (_, _) =>
            {
                TestAssert.True(view.IsBrowseLoading, "The loading presentation must already be active when storage starts.");
                if (Interlocked.Increment(ref reads) == 1)
                {
                    firstStarted.TrySetResult();
                    return first.Task;
                }

                secondStarted.TrySetResult();
                return second.Task;
            };
            try
            {
                var version = view.BrowseTransitionVersion;
                view.ShowLibraryModeCommand.Execute(null);
                var obsolete = view.HomeUpdate;
                TestAssert.True(view.IsBrowseLoading && view.BrowseTransitionVersion > version,
                    "Choosing a mode must start its surface transition and loading state in the same input callback.");
                await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                view.SelectedPlaylist = view.Playlists.Single(item => item.Id == "ordered");
                view.ShowPlaylistsModeCommand.Execute(null);
                var current = view.HomeUpdate;
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                first.SetResult([Track("obsolete", "Old catalog")]);
                await obsolete.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.IsBrowseLoading && view.IsPlaylistsMode,
                    "An obsolete query's completion cannot remove the newer target's skeleton.");
                second.SetResult(workspace.Library.ToArray());
                await current.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(!view.IsBrowseLoading && view.Tracks.Select(item => item.Id).SequenceEqual(["local-04", "local-02"]),
                    "Only the latest complete target may finish loading and expose its ordered content.");
            }
            finally
            {
                first.TrySetResult([]);
                second.TrySetResult([]);
            }
        }

        /// <summary>Keeps mode navigation available while an earlier catalog read waits and rejects its late result.</summary>
        /// <returns>A task representing cancellation, repeated navigation and latest-result ownership assertions.</returns>
        private static async Task ValidateInterruptedModeLoadAsync()
        {
            var workspace = Workspace(5);
            await using var view = View(workspace, new LibraryExperienceTests.ExperienceBackend());
            await view.InitializeAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            var obsoleteToken = CancellationToken.None;
            workspace.SearchOverride = (_, token) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    obsoleteToken = token;
                    started.TrySetResult();
                    return release.Task;
                }

                return Task.FromResult<IReadOnlyList<MusicTrack>>(workspace.Library.ToArray());
            };
            try
            {
                view.ShowLibraryModeCommand.Execute(null);
                var obsoleteUpdate = view.HomeUpdate;
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.ShowLibraryModeCommand.CanExecute(null),
                    "A pending catalog read cannot hold its navigation command disabled.");
                await ExecuteHomeAsync(view, view.ShowRecommendationsModeCommand);
                TestAssert.True(obsoleteToken.IsCancellationRequested && view.IsRecommendationsMode && view.Tracks.Count == 0,
                    "Changing modes must cancel an obsolete catalog query without waiting for its storage result.");
                await ExecuteHomeAsync(view, view.ShowLibraryModeCommand);
                TestAssert.True(view.IsLibraryMode && view.Tracks.Count == 5,
                    "Returning to the library must start a current query while the obsolete read still waits.");
                release.SetResult([Track("obsolete", "Obsolete catalog")]);
                await obsoleteUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.IsLibraryMode && view.Tracks.Count == 5 && view.Tracks.All(item => item.Id != "obsolete"),
                    "An older result must never replace the latest explicitly selected mode.");
            }
            finally
            {
                release.TrySetResult([]);
            }
        }

        /// <summary>Starts with recommendations and keeps the inspector stable while explicitly browsing local content.</summary>
        /// <returns>A task representing mode, search and page assertions.</returns>
        private static async Task ValidateOfflineModesAsync()
        {
            var workspace = Workspace(20);
            var backend = new LibraryExperienceTests.ExperienceBackend();
            await using var view = View(workspace, backend);
            await view.InitializeAsync();
            TestAssert.True(view.IsRecommendationsMode && !view.IsSearchExpanded && view.Tracks.Count == 0,
                "Offline startup must show the recommendation mode without substituting the indexed library.");
            TestAssert.Equal(0, workspace.RecommendationRequests.Count, "Offline startup must not request a feed.");
            await ExecuteHomeAsync(view, view.BeginSearchCommand);
            TestAssert.True(view.IsSearchExpanded && !view.IsSearching && view.Tracks.Count == 0,
                "Expanding an empty search field keeps recommendation content.");
            view.SearchText = "Local 02";
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.IsSearching && view.Tracks.Single().Id == "local-02", "Nonempty search reads the entire indexed library.");
            view.SelectedVisibleTrack = view.VisibleTracks.Single();
            var inspected = view.SelectedTrack;
            await ExecuteHomeAsync(view, view.CancelSearchCommand);
            TestAssert.True(view.IsRecommendationsMode && !view.IsSearchExpanded && view.Tracks.Count == 0,
                "Canceling search restores recommendations.");
            TestAssert.True(ReferenceEquals(inspected, view.SelectedTrack) && view.SelectedVisibleTrack is null,
                "Changing content retains the inspected music without selecting a nonexistent visible row.");

            view.SelectedPlaylist = view.Playlists.Single(item => item.Id == "ordered");
            await ExecuteHomeAsync(view, view.ShowPlaylistsModeCommand);
            TestAssert.True(view.Tracks.Select(item => item.Id).SequenceEqual(["local-04", "local-02"]),
                "The playlist mode follows the selected list's saved order.");
            view.SetLibraryViewportHeight(64);
            TestAssert.Equal(1, view.VisiblePageSize, "A one-row viewport must contain one complete song.");
            await ExecuteObservedAsync(view, view.NextLibraryPageCommand);
            TestAssert.Equal("local-02", view.VisibleTracks.Single().Id, "A small viewport uses a page rather than offscreen rows.");
            view.SelectedVisibleTrack = null;
            TestAssert.Equal(inspected?.Id, view.SelectedTrack?.Id, "A row unselection from page replacement cannot clear the inspector.");
            view.SetLibraryViewportHeight(168);
            await ExecuteHomeAsync(view, view.ShowLibraryModeCommand);
            TestAssert.True(view.IsLibraryMode && view.Tracks.Count == 20 && view.VisibleTracks.Count == 3,
                "The explicit library mode stays available offline and fits its current viewport.");
            TestAssert.Equal(0, backend.OpenCount, "Home browsing and search cannot start playback.");
            TestAssert.Equal(0, workspace.RecommendationRequests.Count, "Offline modes and search remain local.");
        }

        /// <summary>Opens a home playlist in one selection without coupling later sidebar target changes to browsing.</summary>
        /// <returns>A task representing the playlist-selection assertions.</returns>
        private static async Task ValidateHomePlaylistSelectionAsync()
        {
            var workspace = Workspace(5);
            await using var view = View(workspace, new LibraryExperienceTests.ExperienceBackend());
            await view.InitializeAsync();
            view.SelectedHomePlaylist = view.Playlists.Single(item => item.Id == "ordered");
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.IsPlaylistsMode && view.Tracks.Select(item => item.Id).SequenceEqual(["local-04", "local-02"]),
                "Selecting a home playlist must immediately open its saved contents.");
            view.SelectedPlaylist = view.Playlists.Single(item => item.IsFavorite);
            TestAssert.True(view.SelectedHomePlaylist?.Id == "ordered" && view.Tracks.Count == 2,
                "Changing the sidebar insertion target cannot replace the playlist currently being browsed.");
            view.SelectedHomePlaylist = view.SelectedPlaylist;
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.SelectedHomePlaylist?.IsFavorite == true && view.Tracks.Count == 0,
                "Selecting a different home playlist opens that playlist without another action.");
        }

        /// <summary>Keeps finite recommendation snapshots separate across independently selected accounts.</summary>
        /// <returns>A task representing account-owned feed assertions.</returns>
        private static async Task ValidateAccountSnapshotsAsync()
        {
            var workspace = Workspace(3);
            var first = Endpoint("a", "Alice");
            var second = Endpoint("b", "Bob");
            workspace.Settings = new AppSettings { OnlineEnabled = true, ActiveEndpointId = "a", Endpoints = [first, second] };
            workspace.Sessions["a"] = new EndpointSession { EndpointId = "a", Username = "Alice", IsLoggedIn = true };
            workspace.Sessions["b"] = new EndpointSession { EndpointId = "b", Username = "Bob", IsLoggedIn = true };
            workspace.Recommendations[("a", 0)] = [Track("a-feed", "Alice recommendation", first)];
            workspace.Recommendations[("b", 0)] = [Track("b-feed", "Bob recommendation", second)];
            await using var view = View(workspace, new LibraryExperienceTests.ExperienceBackend());
            await view.InitializeAsync();
            await view.EnsureHomeRecommendationsAsync();
            TestAssert.Equal("a-feed", view.Tracks.Single().Id, "The home list displays the authenticated owner's actual feed.");
            await ExecuteHomeAsync(view, view.BeginSearchCommand);
            view.SearchText = "Local";
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(3, view.Tracks.Count, "Searching is not limited to the current recommendation feed.");
            view.SelectedEndpointProfile = view.EndpointProfiles.Single(item => item.Configuration.Id == "b");
            TestAssert.True(view.IsSearching && view.Tracks.Count == 3, "Changing the recommendation source cannot replace active search results.");
            await ExecuteHomeAsync(view, view.CancelSearchCommand);
            TestAssert.Equal("b-feed", view.Tracks.Single().Id, "Closing search restores only the selected account's feed.");
            view.SelectedEndpointProfile = view.EndpointProfiles.Single(item => item.Configuration.Id == "a");
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal("a-feed", view.Tracks.Single().Id, "Returning to an account restores its own finite snapshot.");
            TestAssert.Equal(2, workspace.RecommendationRequests.Count, "Returning to a loaded snapshot need not refetch it.");
            await ExecuteObservedAsync(view, view.GuestCommand);
            TestAssert.True(view.IsRecommendationsMode && view.Tracks.Count == 0,
                "Guest keeps the recommendation mode without exposing the preceding private snapshot.");
        }

        /// <summary>Rejects a slow query when a newer search already owns the content.</summary>
        /// <returns>A task representing late-query isolation.</returns>
        private static async Task ValidateLateSearchAsync()
        {
            var workspace = Workspace(3);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var view = View(workspace, new LibraryExperienceTests.ExperienceBackend());
            await view.InitializeAsync();
            await ExecuteHomeAsync(view, view.BeginSearchCommand);
            workspace.SearchOverride = (query, _) =>
            {
                if (query == "old")
                {
                    started.TrySetResult();
                    return release.Task;
                }

                return Task.FromResult<IReadOnlyList<MusicTrack>>(workspace.Library.Where(item => query is null || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
            };
            try
            {
                view.SearchText = "old";
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var obsolete = view.HomeUpdate;
                view.SearchText = "Local 02";
                await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                release.TrySetResult([Track("late", "Old result")]);
                await obsolete.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.Equal("local-02", view.Tracks.Single().Id, "An old completed search cannot replace the newest result.");
            }
            finally
            {
                release.TrySetResult(Array.Empty<MusicTrack>());
            }
        }

        /// <summary>Allows search to complete while a recommendation request is still pending.</summary>
        /// <returns>A task representing feed/search independence.</returns>
        private static async Task ValidateFeedDuringSearchAsync()
        {
            var workspace = Workspace(3);
            var endpoint = Endpoint("a", "Alice");
            workspace.Settings = new AppSettings { OnlineEnabled = true, ActiveEndpointId = "a", Endpoints = [endpoint] };
            workspace.Sessions["a"] = new EndpointSession { EndpointId = "a", Username = "Alice", IsLoggedIn = true };
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.RecommendationOverride = (_, _, _) =>
            {
                started.TrySetResult();
                return release.Task;
            };
            await using var view = View(workspace, new LibraryExperienceTests.ExperienceBackend());
            await view.InitializeAsync();
            var feed = view.EnsureHomeRecommendationsAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                view.BeginSearchCommand.Execute(null);
                var expanding = view.HomeUpdate;
                view.SearchText = "Local 02";
                await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.Equal("local-02", view.Tracks.Single().Id, "Search does not wait for recommendation network work.");
                release.TrySetResult([Track("feed", "Recommended song", endpoint)]);
                await Task.WhenAll(feed, expanding).WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.IsSearching && view.Tracks.Single().Id == "local-02", "A returning feed cannot steal an active search view.");
                await ExecuteHomeAsync(view, view.CancelSearchCommand);
                TestAssert.Equal("feed", view.Tracks.Single().Id, "Closing search displays the feed that arrived in the background.");
            }
            finally
            {
                release.TrySetResult(Array.Empty<MusicTrack>());
            }
        }

        /// <summary>Creates a deterministic local catalog and one ordered playlist.</summary>
        /// <param name="count">The number of local entries.</param>
        /// <returns>The isolated in-memory workspace.</returns>
        private static LibraryExperienceTests.ExperienceWorkspace Workspace(int count)
        {
            var workspace = new LibraryExperienceTests.ExperienceWorkspace();
            workspace.Library.AddRange(Enumerable.Range(0, count).Select(index => Track($"local-{index:00}", $"Local {index:00}")));
            workspace.LocalPlaylists["ordered"] = new MusicPlaylist { Id = "ordered", Name = "Ordered", TrackIds = ["local-04", "local-02"] };
            return workspace;
        }

        /// <summary>Creates a view with immediate presentation notifications and a synthetic playback clock.</summary>
        /// <param name="workspace">The local and online test source.</param>
        /// <param name="backend">The controlled transport owner.</param>
        /// <returns>The view model owned by the test.</returns>
        private static MainViewModel View(LibraryExperienceTests.ExperienceWorkspace workspace, LibraryExperienceTests.ExperienceBackend backend)
        {
            return new MainViewModel(workspace, new PlaybackService(backend), static action => action());
        }

        /// <summary>Creates a stable account choice at an illustrative endpoint.</summary>
        /// <param name="id">The source identity.</param>
        /// <param name="username">The remembered account name.</param>
        /// <returns>The endpoint carrying no password.</returns>
        private static EndpointConfiguration Endpoint(string id, string username)
        {
            return new EndpointConfiguration
            {
                Id = id,
                Name = id,
                BaseUri = new Uri("https://example.invalid/api/"),
                Accounts = [new SavedEndpointAccount { Id = "account", Username = username }],
                SelectedAccountId = "account"
            };
        }

        /// <summary>Creates one catalog item without reading audio.</summary>
        /// <param name="id">The local entry identity.</param>
        /// <param name="title">The displayed title.</param>
        /// <param name="endpoint">The optional online owner.</param>
        /// <returns>The immutable test entry.</returns>
        private static MusicTrack Track(string id, string title, EndpointConfiguration? endpoint = null)
        {
            return new MusicTrack
            {
                Id = id,
                Title = title,
                RemoteId = endpoint is null ? null : id,
                EndpointId = endpoint?.Id,
                SourceName = endpoint?.Name,
                SourceId = endpoint?.Id ?? "local",
                Audio = new MediaResource { LocalPath = Path.Combine(Path.GetTempPath(), "home-" + id + ".mp3") }
            };
        }

        /// <summary>Executes a navigation command and awaits its merged home update.</summary>
        /// <param name="view">The observed view model.</param>
        /// <param name="command">The home action.</param>
        /// <returns>A task representing home completion.</returns>
        private static async Task ExecuteHomeAsync(MainViewModel view, AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The home action must be available.");
            command.Execute(null);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            await view.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
        }

        /// <summary>Executes an existing busy-aware command and observes its completion.</summary>
        /// <param name="view">The observed view model.</param>
        /// <param name="command">The local or source action.</param>
        /// <returns>A task representing command and metadata completion.</returns>
        private static async Task ExecuteObservedAsync(MainViewModel view, AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The action must be available.");
            command.Execute(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (view.IsBusy)
            {
                await Task.Delay(5, timeout.Token);
            }

            await view.MetadataRefresh.WaitAsync(timeout.Token);
        }
    }
}
