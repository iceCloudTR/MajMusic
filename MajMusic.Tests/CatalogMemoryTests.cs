#nullable enable

using System.Runtime.CompilerServices;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks resident catalog snapshots without eagerly materializing the whole library.</summary>
    internal static class CatalogMemoryTests
    {
        /// <summary>Exercises large metadata collection and resident library and playlist navigation.</summary>
        /// <returns>A task representing completed catalog-retention regressions.</returns>
        public static async Task RunAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var catalog = new GeneratedCatalog();
            var workspace = new LibraryExperienceTests.ExperienceWorkspace { SearchOverride = catalog.SearchAsync };
            var tailIds = Enumerable.Range(9900, 100).Select(GeneratedCatalog.TrackId).ToArray();
            var repeatedIds = new[] { "missing-generated-song", GeneratedCatalog.TrackId(7000) }
                .Concat(Enumerable.Range(7000, 100).Select(GeneratedCatalog.TrackId)).ToArray();
            workspace.LocalPlaylists["tail"] = new MusicPlaylist { Id = "tail", Name = "Tail songs", TrackIds = tailIds };
            workspace.LocalPlaylists["repeated"] = new MusicPlaylist { Id = "repeated", Name = "Repeated songs", TrackIds = repeatedIds };
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await model.InitializeAsync().WaitAsync(timeout.Token);
            TestAssert.True(model.IsRecommendationsMode && model.Tracks.Count == 0 && catalog.ReadCount == 1,
                "Default offline recommendations must not expose the catalog used only to classify local playlists.");

            CollectUnreferencedMetadata();
            TestAssert.True(catalog.IsAlive(0) && catalog.IsAlive(5000) && catalog.IsAlive(9999) && model.MaterializedCardCount == 0,
                "The resident library keeps the ordered descriptors but does not materialize cards before a viewport is attached.");
            TestAssert.True(model.Playlists.Single(playlist => playlist.Id == "tail").TrackIds.SequenceEqual(tailIds)
                && model.Playlists.Single(playlist => playlist.Id == "repeated").TrackIds.SequenceEqual(repeatedIds),
                "Sparse artwork preparation cannot truncate full saved membership or remove unresolved and repeated playlist entries.");

            model.SetBrowseViewportSize(900, 600);
            await model.BrowsePreloadUpdate.WaitAsync(timeout.Token);
            model.SelectedPlaylist = model.Playlists.Single(playlist => playlist.Id == "tail");
            await model.BrowsePreloadUpdate.WaitAsync(timeout.Token);
            await AssertPreparedNavigationAsync(model, workspace, catalog, HomeBrowseMode.Playlists, tailIds, 100, timeout.Token);

            model.SelectedPlaylist = model.Playlists.Single(playlist => playlist.Id == "repeated");
            await model.BrowsePreloadUpdate.WaitAsync(timeout.Token);
            await AssertPreparedNavigationAsync(model, workspace, catalog, HomeBrowseMode.Playlists,
                repeatedIds.Where(id => id != "missing-generated-song").ToArray(), repeatedIds.Length - 1, timeout.Token);
            await AssertPreparedNavigationAsync(model, workspace, catalog, HomeBrowseMode.Library,
                Enumerable.Range(0, GeneratedCatalog.TrackCount).Select(GeneratedCatalog.TrackId).ToArray(), GeneratedCatalog.TrackCount, timeout.Token);
            TestAssert.True(model.MaterializedCardCount >= MainViewModel.ResidentCardLimit && model.MaterializedCardCount < 100,
                "Resident prefixes retain actual card models without realizing thousands of offscreen songs.");
            await AssertLibraryAfterSearchAsync(model, workspace, catalog, timeout.Token);
            model.SetBrowseViewportSize(0, 0);
            GC.KeepAlive(model);
        }

        private static async Task AssertLibraryAfterSearchAsync(MainViewModel model, LibraryExperienceTests.ExperienceWorkspace workspace,
            GeneratedCatalog catalog, CancellationToken token)
        {
            var searches = 0;
            workspace.SearchOverride = (query, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref searches);
                return query is not null
                    ? Task.FromResult<IReadOnlyList<MusicTrack>>([new() { Id = "search-result", Title = "Search result", Audio = new MediaResource() }])
                    : catalog.SearchAsync(query, cancellationToken);
            };

            try
            {
                model.BeginSearchCommand.Execute(null);
                model.SearchText = "Search result";
                await model.HomeUpdate.WaitAsync(token);
                TestAssert.True(model.Tracks.Single().Id == "search-result", "A real search must be allowed to replace the library list.");
                model.ShowPlaylistsModeCommand.Execute(null);
                model.ShowLibraryModeCommand.Execute(null);
                await model.HomeUpdate.WaitAsync(token);
                TestAssert.True(model.IsLibraryMode && model.Tracks.Count == GeneratedCatalog.TrackCount && searches == 1,
                    "Rapid navigation out of search and back must restore the complete warmed library without another storage read.");
            }
            finally
            {
                workspace.SearchOverride = catalog.SearchAsync;
            }
        }

        /// <summary>Checks that a warm mode moves its ready page without querying or replacing its rows.</summary>
        /// <param name="model">The initialized view model retaining complete resident pages.</param>
        /// <param name="workspace">The generated source whose next read can be detected.</param>
        /// <param name="catalog">The fixture that supplies complete library generations.</param>
        /// <param name="mode">The requested browsing target.</param>
        /// <param name="expectedTracks">The exact complete resident page in saved order.</param>
        /// <param name="completeCount">The full number of resolved songs expected after publication.</param>
        /// <param name="token">Cancels a stalled fixture.</param>
        /// <returns>A task representing resident page and full-membership verification.</returns>
        private static async Task AssertPreparedNavigationAsync(MainViewModel model, LibraryExperienceTests.ExperienceWorkspace workspace,
            GeneratedCatalog catalog, HomeBrowseMode mode, string[] expectedTracks, int completeCount, CancellationToken token)
        {
            var reads = 0;
            workspace.SearchOverride = (query, cancellationToken) =>
            {
                Interlocked.Increment(ref reads);
                return catalog.SearchAsync(query, cancellationToken);
            };
            try
            {
                if (mode == HomeBrowseMode.Playlists)
                {
                    model.ShowPlaylistsModeCommand.Execute(null);
                }
                else
                {
                    model.ShowLibraryModeCommand.Execute(null);
                }

                TestAssert.True(model.Tracks.Select(track => track.Id).SequenceEqual(expectedTracks),
                    "The complete ordered catalog must already be in place in the same turn as its warm mode switch.");
                var page = model.GetBrowsePage(mode);
                var cards = page.CardTracks;
                var row = page.Rows[0];
                var version = page.NavigationVersion;
                await model.HomeUpdate.WaitAsync(token);
                TestAssert.True(model.Tracks.Count == completeCount && ReferenceEquals(cards, page.CardTracks)
                    && ReferenceEquals(row, page.Rows[0]) && page.NavigationVersion == version && reads == 0,
                    "Warm modes retain the original cards and rows without reading or replacing the catalog after input.");
            }
            finally
            {
                workspace.SearchOverride = catalog.SearchAsync;
            }
        }

        /// <summary>Collects metadata whose only prior owners were completed catalog reads.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CollectUnreferencedMetadata()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>Creates independent descriptor graphs like SQLite document deserialization without keeping their result lists.</summary>
        private sealed class GeneratedCatalog
        {
            /// <summary>Provides enough independent metadata to expose accidentally retained full catalogs.</summary>
            public const int TrackCount = 10000;
            /// <summary>Observes each descriptor without extending its lifetime.</summary>
            private WeakReference<MusicTrack>[] _observed = [];
            /// <summary>Gets the number of complete independent catalog generations.</summary>
            public int ReadCount { get; private set; }

            /// <summary>Creates the stable generated identity for one catalog position.</summary>
            /// <param name="index">The zero-based generated position.</param>
            /// <returns>The source-independent fixture identity.</returns>
            public static string TrackId(int index)
            {
                return "memory-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            }

            /// <summary>Checks whether one generated descriptor remains reachable after completed reads.</summary>
            /// <param name="index">The zero-based observed descriptor.</param>
            /// <returns>Whether the descriptor still has an actual strong owner.</returns>
            public bool IsAlive(int index)
            {
                return _observed[index].TryGetTarget(out _);
            }

            /// <summary>Produces a complete independent catalog without a source-side strong metadata cache.</summary>
            /// <param name="query">The unused query; this fixture exercises complete library reads.</param>
            /// <param name="token">Cancels generation.</param>
            /// <returns>A fresh full descriptor graph like one SQLite document query.</returns>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var tracks = new MusicTrack[TrackCount];
                var observed = new WeakReference<MusicTrack>[TrackCount];
                for (var index = 0; index < tracks.Length; index++)
                {
                    var id = TrackId(index);
                    var track = new MusicTrack
                    {
                        Id = id,
                        Title = "Generated music " + id,
                        Artist = "Generated artist " + id,
                        Description = new string((char)('a' + index % 26), 1024) + id,
                        Tags = ["generated-tag-" + id],
                        Audio = new MediaResource { LocalPath = "generated-audio-" + id }
                    };
                    tracks[index] = track;
                    observed[index] = new WeakReference<MusicTrack>(track);
                }

                _observed = observed;
                ReadCount++;
                return Task.FromResult<IReadOnlyList<MusicTrack>>(tracks);
            }
        }
    }
}
