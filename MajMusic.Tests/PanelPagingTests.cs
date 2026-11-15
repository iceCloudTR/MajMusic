#nullable enable

using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks bounded directory and playlist navigation with large persisted collections.</summary>
    internal static class PanelPagingTests
    {
        /// <summary>Runs capacity, traversal, resize and refreshed-selection regressions without reading source folders.</summary>
        /// <returns>A task representing the completed panel cases.</returns>
        public static async Task RunAsync()
        {
            await ValidatePagingAndRefreshAsync();
            await ValidateShrinkingAndEmptyPanelsAsync();
        }

        /// <summary>Visits every saved configuration in both directions and keeps refreshed rows tied to the current collections.</summary>
        /// <returns>A task representing complete panel traversal and selection checks.</returns>
        private static async Task ValidatePagingAndRefreshAsync()
        {
            var workspace = Workspace(27, 24);
            var backend = new LibraryExperienceTests.ExperienceBackend();
            await using var view = new MainViewModel(workspace, new PlaybackService(backend), static action => action());
            await view.InitializeAsync();
            view.SetPlaylistViewportHeight(158);
            view.SetDirectoryViewportHeight(158);
            TestAssert.Equal(3, view.VisiblePlaylists.Count, "The allocated playlist region must contain three complete rows.");
            TestAssert.Equal(3, view.VisibleDirectorySources.Count, "The allocated directory region must contain three complete rows.");
            await ValidateTraversalAsync(view, view.NextPlaylistPageCommand, view.PreviousPlaylistPageCommand,
                () => view.VisiblePlaylists.Select(item => item.Id).ToArray(), view.Playlists.Select(item => item.Id).ToArray(), 3, "Playlists");
            await ValidateTraversalAsync(view, view.NextDirectoryPageCommand, view.PreviousDirectoryPageCommand,
                () => view.VisibleDirectorySources.Select(item => item.Id).ToArray(), view.DirectorySources.Select(item => item.Id).ToArray(), 3, "Directories");

            await ExecuteAsync(view, view.NextPlaylistPageCommand);
            await ExecuteAsync(view, view.NextPlaylistPageCommand);
            await ExecuteAsync(view, view.NextDirectoryPageCommand);
            await ExecuteAsync(view, view.NextDirectoryPageCommand);
            var playlistAnchor = view.VisiblePlaylists[0].Id;
            var directoryAnchor = view.VisibleDirectorySources[0].Id;
            view.SetPlaylistViewportHeight(106);
            view.SetDirectoryViewportHeight(106);
            TestAssert.True(view.VisiblePlaylists.Count == 2 && view.VisiblePlaylists[0].Id == playlistAnchor,
                "Resizing the playlist region preserves its reading position while fitting two complete rows.");
            TestAssert.True(view.VisibleDirectorySources.Count == 2 && view.VisibleDirectorySources[0].Id == directoryAnchor,
                "Resizing the directory region preserves its reading position while fitting two complete rows.");
            view.SelectedPlaylist = view.VisiblePlaylists[1];
            view.SelectedDirectory = view.VisibleDirectorySources[1];
            var oldPlaylist = view.SelectedPlaylist;
            var oldDirectory = view.SelectedDirectory;
            foreach (var playlist in workspace.LocalPlaylists.Values.ToArray())
            {
                workspace.LocalPlaylists[playlist.Id] = playlist with { Name = playlist.Name + " refreshed" };
            }

            workspace.Settings = workspace.Settings with
            {
                LocalDirectorySources = workspace.Settings.LocalDirectorySources.Select(item => item with { Name = item.Name + " refreshed" }).ToArray()
            };
            await view.InitializeAsync();
            TestAssert.True(view.SelectedPlaylist?.Id == oldPlaylist?.Id && !ReferenceEquals(oldPlaylist, view.SelectedPlaylist)
                && ReferenceEquals(view.SelectedPlaylist, view.Playlists.Single(item => item.Id == oldPlaylist!.Id)),
                "Playlist refresh must retain the selected identity and bind to its replacement record.");
            TestAssert.True(view.SelectedDirectory?.Id == oldDirectory?.Id && !ReferenceEquals(oldDirectory, view.SelectedDirectory)
                && ReferenceEquals(view.SelectedDirectory, view.DirectorySources.Single(item => item.Id == oldDirectory!.Id)),
                "Directory refresh must retain the selected identity and bind to its replacement record.");
            TestAssert.True(view.VisiblePlaylists.All(row => view.Playlists.Any(item => ReferenceEquals(item, row))),
                "Visible playlist rows must reference the refreshed collection rather than obsolete records.");
            TestAssert.True(view.VisibleDirectorySources.All(row => view.DirectorySources.Any(item => ReferenceEquals(item, row))),
                "Visible directory rows must reference the refreshed collection rather than obsolete records.");
            TestAssert.Equal(playlistAnchor, view.VisiblePlaylists[0].Id, "Playlist refresh preserves a valid current page.");
            TestAssert.Equal(directoryAnchor, view.VisibleDirectorySources[0].Id, "Directory refresh preserves a valid current page.");
            TestAssert.Equal(0, backend.OpenCount, "Panel navigation, resizing and refresh cannot open an audio resource.");
            TestAssert.Equal(0, workspace.RecommendationRequests.Count, "Local panel navigation cannot request an online feed.");
        }

        /// <summary>Clamps stale page positions after saved rows disappear and handles unmeasured viewports.</summary>
        /// <returns>A task representing panel boundary checks.</returns>
        private static async Task ValidateShrinkingAndEmptyPanelsAsync()
        {
            var workspace = Workspace(15, 14);
            await using var view = new MainViewModel(workspace, new PlaybackService(new LibraryExperienceTests.ExperienceBackend()), static action => action());
            await view.InitializeAsync();
            view.SetPlaylistViewportHeight(54);
            view.SetDirectoryViewportHeight(54);
            TestAssert.True(view.VisiblePlaylists.Count == 1 && view.VisibleDirectorySources.Count == 1,
                "Small panel regions must use one row per page.");
            while (view.NextPlaylistPageCommand.CanExecute(null))
            {
                await ExecuteAsync(view, view.NextPlaylistPageCommand);
            }

            while (view.NextDirectoryPageCommand.CanExecute(null))
            {
                await ExecuteAsync(view, view.NextDirectoryPageCommand);
            }

            workspace.LocalPlaylists.Clear();
            workspace.Settings = workspace.Settings with { LocalDirectorySources = Array.Empty<LocalDirectorySource>() };
            await view.InitializeAsync();
            TestAssert.True(view.Playlists.Count == 1 && view.VisiblePlaylists.Count == 1 && view.VisiblePlaylists[0].IsFavorite,
                "Removing saved lists leaves the required favorites row visible on a valid page.");
            TestAssert.True(ReferenceEquals(view.SelectedPlaylist, view.Playlists[0]),
                "Removing the selected playlist must select a record from the refreshed collection.");
            TestAssert.True(view.DirectorySources.Count == 0 && view.VisibleDirectorySources.Count == 0 && view.SelectedDirectory is null,
                "An empty source configuration must not retain an obsolete directory selection.");
            TestAssert.Equal("1 / 1", view.PlaylistPageText, "A shortened playlist collection clamps its old page index.");
            TestAssert.Equal("1 / 1", view.DirectoryPageText, "An empty directory panel has a stable single-page indicator.");
            TestAssert.True(!view.PreviousPlaylistPageCommand.CanExecute(null) && !view.NextPlaylistPageCommand.CanExecute(null)
                && !view.PreviousDirectoryPageCommand.CanExecute(null) && !view.NextDirectoryPageCommand.CanExecute(null),
                "Empty and single-row panels must disable both page directions.");
            foreach (var height in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
            {
                view.SetPlaylistViewportHeight(height);
                view.SetDirectoryViewportHeight(height);
                TestAssert.True(view.VisiblePlaylists.Count == 1 && view.VisibleDirectorySources.Count == 0,
                    "An unmeasured viewport must retain bounded, valid panel contents.");
            }
        }

        /// <summary>Checks exact forward and backward coverage using only public navigation controls.</summary>
        /// <param name="view">The owner of the navigation commands.</param>
        /// <param name="next">The following-page action.</param>
        /// <param name="previous">The preceding-page action.</param>
        /// <param name="visibleIds">The current page's visible identities.</param>
        /// <param name="expectedIds">All saved identities in display order.</param>
        /// <param name="capacity">The maximum complete rows allowed in the allocated region.</param>
        /// <param name="panel">The panel name used in assertion messages.</param>
        /// <returns>A task representing complete traversal back to the initial page.</returns>
        private static async Task ValidateTraversalAsync(MainViewModel view, AsyncCommand next, AsyncCommand previous,
            Func<IReadOnlyList<string>> visibleIds, IReadOnlyList<string> expectedIds, int capacity, string panel)
        {
            TestAssert.True(!previous.CanExecute(null), panel + " must disable previous navigation on the first page.");
            var forward = new List<string>();
            do
            {
                var page = visibleIds();
                TestAssert.True(page.Count is > 0 && page.Count <= capacity, panel + " must never expose offscreen rows.");
                forward.AddRange(page);
                if (!next.CanExecute(null))
                {
                    break;
                }

                await ExecuteAsync(view, next);
            }
            while (forward.Count <= expectedIds.Count);
            TestAssert.True(forward.SequenceEqual(expectedIds), panel + " forward navigation must visit every row once in order.");
            var backward = new List<IReadOnlyList<string>> { visibleIds() };
            while (previous.CanExecute(null) && backward.Count <= expectedIds.Count)
            {
                await ExecuteAsync(view, previous);
                backward.Insert(0, visibleIds());
            }

            TestAssert.True(backward.SelectMany(page => page).SequenceEqual(expectedIds), panel + " backward navigation must visit the same complete ordered collection.");
            TestAssert.True(!previous.CanExecute(null), panel + " must return to its initial page boundary.");
        }

        /// <summary>Creates many typed source configurations and portable list headers without creating source files.</summary>
        /// <param name="directories">The number of directory configurations.</param>
        /// <param name="playlists">The number of non-favorite playlist records.</param>
        /// <returns>The isolated in-memory application workspace.</returns>
        private static LibraryExperienceTests.ExperienceWorkspace Workspace(int directories, int playlists)
        {
            var workspace = new LibraryExperienceTests.ExperienceWorkspace
            {
                Settings = new AppSettings
                {
                    LocalDirectorySources = Enumerable.Range(0, directories).Select(index => new LocalDirectorySource
                    {
                        Id = $"directory-{index:00}",
                        Name = $"Source {index:00}",
                        Path = Path.Combine(Path.GetTempPath(), "MajMusic-panel-fixture", $"source-{index:00}"),
                        Kind = index % 2 == 0 ? LocalDirectoryKind.MusicLibrary : LocalDirectoryKind.CollectionIndex
                    }).ToArray()
                }
            };
            foreach (var index in Enumerable.Range(0, playlists))
            {
                var playlist = new MusicPlaylist { Id = $"playlist-{index:00}", Name = $"Playlist {index:00}" };
                workspace.LocalPlaylists[playlist.Id] = playlist;
            }

            return workspace;
        }

        /// <summary>Executes one available public panel action and observes its return to idle.</summary>
        /// <param name="view">The command owner.</param>
        /// <param name="command">The navigation action to execute.</param>
        /// <returns>A task representing action completion.</returns>
        private static async Task ExecuteAsync(MainViewModel view, AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The requested panel action must be available.");
            command.Execute(null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (view.IsBusy)
            {
                await Task.Delay(1, cancellation.Token);
            }

            TestAssert.True(string.IsNullOrEmpty(view.Status), "Panel navigation must complete without reporting an operation failure.");
        }
    }
}
