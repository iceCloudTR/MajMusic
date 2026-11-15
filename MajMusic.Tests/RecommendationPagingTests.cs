#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks bounded account feeds, local fifty-song navigation and independent playback ownership.</summary>
    internal static class RecommendationPagingTests
    {
        /// <summary>Runs controlled feed cases without real services, credentials or music files.</summary>
        /// <returns>A task representing the complete recommendation-page regressions.</returns>
        public static async Task RunAsync()
        {
            await ValidateProgressiveBatchAsync();
            await ValidateGrowingCurrentPageAsync();
            await ValidateSingleResponseAndShortFeedsAsync();
            await ValidateFailedContinuationAsync();
            await ValidateAccountPagesAsync();
            await ValidateLateAccountBatchAsync();
            await ValidatePreparedPagesAsync();
        }

        /// <summary>Exposes fifty songs before later requests complete and navigates all cached pages without new requests.</summary>
        /// <returns>A task representing first-screen, finite paging and full-feed playback assertions.</returns>
        private static async Task ValidateProgressiveBatchAsync()
        {
            var workspace = Workspace("a");
            var nextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.RecommendationOverride = async (endpoint, page, token) =>
            {
                if (page == 1)
                {
                    nextStarted.TrySetResult();
                    await release.Task.WaitAsync(token);
                }

                return Tracks(endpoint.Id, page * 50, 50);
            };
            var backend = new LibraryExperienceTests.ExperienceBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: (track, token) => workspace.ResolveAudioAsync(track, false, token));
            await using var view = new MainViewModel(workspace, playback, static action => action(), metadata: new UpdatedMetadataProvider());
            try
            {
                await view.InitializeAsync();
                _ = view.EnsureHomeRecommendationsAsync();
                await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.Tracks.Count == 50 && view.Tracks[0].Id == "a-000" && view.Tracks[^1].Id == "a-049"
                    && view.RecommendationTotalCount == 50 && !view.IsBrowseLoading && !view.RecommendationBatchUpdate.IsCompleted,
                    "The first complete page must be usable while the second source request is still waiting.");
                await view.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.Equal(TimeSpan.FromMinutes(5), view.Tracks[0].Duration!.Value,
                    "The first visible page must receive genuine independent metadata enrichment before continuation.");
                var cards = view.CardTracks;
                var navigation = view.BrowseNavigationVersion;
                var transition = view.BrowseTransitionVersion;
                release.TrySetResult();
                await view.RecommendationBatchUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.RecommendationTotalCount == 150 && view.RecommendationPageCount == 3
                    && view.RecommendationPageNumber == 1 && view.RecommendationCurrentPageTrackCount == 50
                    && view.HasRecommendationPages && view.RecommendationPages.Select(page => page.Number).SequenceEqual([1, 2, 3]),
                    "A full batch must expose exactly three fifty-song numbered pages.");
                TestAssert.True(ReferenceEquals(cards, view.CardTracks) && view.BrowseNavigationVersion == navigation
                    && view.BrowseTransitionVersion == transition && view.Tracks.Count == 50,
                    "Prefetching the remaining batch cannot replace the current card collection, reset scroll or restart its surface.");
                AssertRequests(workspace, ("a", 0), ("a", 1), ("a", 2));

                view.RecommendationPages[2].SelectCommand.Execute(null);
                TestAssert.True(view.BrowseTransitionVersion > transition && view.BrowseTransitionDirection == 1,
                    "Selecting a later cached page must start the existing forward slide immediately.");
                await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                await view.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
                AssertPage(view, "a", 3, 100, 50);
                view.SelectedTrack = view.Tracks[0];
                await view.PlaySelectedAsync();
                TestAssert.True(playback.Queue.Tracks.Count == 150 && playback.Snapshot.Track?.Id == "a-100" && backend.OpenCount == 1,
                    "Playing from the third visible page must retain all 150 cached recommendations in the playback queue.");
                TestAssert.Equal(TimeSpan.FromMinutes(5), playback.Queue.Tracks[0].Duration!.Value,
                    "Appending later pages cannot discard audio properties already parsed for the first page.");
                view.RecommendationPages[0].SelectCommand.Execute(null);
                TestAssert.Equal(-1, view.BrowseTransitionDirection, "Returning to an earlier page must reverse the existing slide.");
                await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                await view.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
                AssertPage(view, "a", 1, 0, 50);
                TestAssert.True(playback.Queue.Tracks.Count == 150 && playback.Snapshot.Track?.Id == "a-100" && backend.OpenCount == 1,
                    "Browsing another page cannot rebuild the active queue or prepare the current audio again.");
                view.NextRecommendationPageCommand.Execute(null);
                await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                await view.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
                AssertPage(view, "a", 2, 50, 50);
                AssertRequests(workspace, ("a", 0), ("a", 1), ("a", 2));
            }
            finally
            {
                release.TrySetResult();
            }
        }

        /// <summary>Fills a deduplicated short first screen as a later source page contributes new entries.</summary>
        /// <returns>A task representing membership-sensitive in-place publication.</returns>
        private static async Task ValidateGrowingCurrentPageAsync()
        {
            var workspace = Workspace("growing");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.RecommendationOverride = async (endpoint, page, token) =>
            {
                if (page == 0)
                {
                    return Tracks(endpoint.Id, 0, 40).Concat(Tracks(endpoint.Id, 0, 10)).ToArray();
                }

                if (page == 1)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return Tracks(endpoint.Id, 40, 50);
                }

                return Array.Empty<MusicTrack>();
            };
            await using var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            try
            {
                await view.InitializeAsync();
                _ = view.EnsureHomeRecommendationsAsync();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.Tracks.Count == 40 && view.CardTracks.Count == 40 && view.RecommendationCurrentPageTrackCount == 40,
                    "A fifty-entry source response containing ten duplicates must first publish its forty real unique songs.");
                var firstCard = view.CardTracks[0];
                var navigation = view.BrowseNavigationVersion;
                var transition = view.BrowseTransitionVersion;
                release.TrySetResult();
                await view.RecommendationBatchUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                AssertPage(view, "growing", 1, 0, 50);
                TestAssert.True(view.CardTracks.Count == view.RecommendationCurrentPageTrackCount && view.CardTracks.Count == 50
                    && view.RecommendationTotalCount == 90 && view.HasRecommendationPages && !view.IsBrowseLoading,
                    "A later batch must fill the current page immediately so complete-page pagination is reachable without changing modes.");
                TestAssert.True(ReferenceEquals(firstCard, view.CardTracks[0]) && view.BrowseNavigationVersion == navigation
                    && view.BrowseTransitionVersion == transition,
                    "Growing the current page must retain its existing card and scroll generation without starting another slide.");
                AssertRequests(workspace, ("growing", 0), ("growing", 1), ("growing", 2));
            }
            finally
            {
                release.TrySetResult();
            }
        }

        /// <summary>Bounds servers that ignore page size and stops normally on empty, short or repeated responses.</summary>
        /// <returns>A task representing finite source behavior and short local final pages.</returns>
        private static async Task ValidateSingleResponseAndShortFeedsAsync()
        {
            foreach (var count in new[] { 0, 17, 150, 175 })
            {
                var workspace = Workspace("single");
                workspace.RecommendationOverride = (endpoint, _, _) => Task.FromResult(Tracks(endpoint.Id, 0, count));
                await using var view = await CreateLoadedViewAsync(workspace);
                var expected = Math.Min(150, count);
                TestAssert.True(view.RecommendationTotalCount == expected && view.Tracks.Count == Math.Min(50, expected)
                    && view.RecommendationPageCount == (expected + 49) / 50 && view.HasRecommendationPages == (expected > 50),
                    "Empty and short feeds need no footer; a single oversized response must be capped at 150 and locally paged.");
                AssertRequests(workspace, ("single", 0));
            }

            var shortWorkspace = Workspace("short");
            shortWorkspace.RecommendationOverride = (endpoint, page, _) => Task.FromResult(Tracks(endpoint.Id, page * 50, page == 0 ? 50 : 7));
            await using (var view = await CreateLoadedViewAsync(shortWorkspace))
            {
                view.NextRecommendationPageCommand.Execute(null);
                await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                AssertPage(view, "short", 2, 50, 7);
                TestAssert.True(view.RecommendationCurrentPageTrackCount == 7 && !view.NextRecommendationPageCommand.CanExecute(null),
                    "A short final response must form its actual seven-song final page without requesting another page.");
                AssertRequests(shortWorkspace, ("short", 0), ("short", 1));
            }

            var repeating = Workspace("repeat");
            repeating.RecommendationOverride = (endpoint, _, _) => Task.FromResult(Tracks(endpoint.Id, 0, 50));
            await using (var view = await CreateLoadedViewAsync(repeating))
            {
                TestAssert.True(view.RecommendationTotalCount == 50 && view.RecommendationPageCount == 1,
                    "A source repeating the first page cannot manufacture duplicate recommendation pages.");
                AssertRequests(repeating, ("repeat", 0), ("repeat", 1));
            }
        }

        /// <summary>Preserves a usable first page when optional continuation fails.</summary>
        /// <returns>A task representing partial-batch retention and bounded failure.</returns>
        private static async Task ValidateFailedContinuationAsync()
        {
            var workspace = Workspace("failure");
            workspace.RecommendationOverride = (endpoint, page, _) => page == 0
                ? Task.FromResult(Tracks(endpoint.Id, 0, 50))
                : Task.FromException<IReadOnlyList<MusicTrack>>(new HttpRequestException("Generated unavailable source."));
            await using var view = await CreateLoadedViewAsync(workspace);
            AssertPage(view, "failure", 1, 0, 50);
            TestAssert.True(view.RecommendationTotalCount == 50 && !view.HasRecommendationPages && !view.IsBrowseLoading,
                "A failed prefetched page must keep the first real page interactive without an empty second page.");
            await view.EnsureHomeRecommendationsAsync();
            AssertRequests(workspace, ("failure", 0), ("failure", 1));
            workspace.RecommendationOverride = (endpoint, page, _) => Task.FromResult(Tracks(endpoint.Id, page * 50, 50));
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            view.ShowRecommendationsModeCommand.Execute(null);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.RecommendationTotalCount == 150 && view.Tracks.Count == 50,
                "Reopening an interrupted account batch must resume its unfinished server pages while retaining the usable first page.");
            AssertRequests(workspace, ("failure", 0), ("failure", 1), ("failure", 1), ("failure", 2));
        }

        /// <summary>Retains each source account's selected page and allows cached paging after online access is disabled.</summary>
        /// <returns>A task representing scoped page restoration and offline navigation.</returns>
        private static async Task ValidateAccountPagesAsync()
        {
            var workspace = Workspace("a", "b");
            workspace.RecommendationOverride = (endpoint, _, _) => Task.FromResult(Tracks(endpoint.Id, 0, 150));
            await using var view = await CreateLoadedViewAsync(workspace);
            view.RecommendationPages[2].SelectCommand.Execute(null);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            view.SelectedEndpointProfile = view.EndpointProfiles.Single(profile => profile.Configuration.Id == "b");
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            AssertPage(view, "b", 1, 0, 50);
            view.NextRecommendationPageCommand.Execute(null);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            AssertPage(view, "b", 2, 50, 50);
            view.SelectedEndpointProfile = view.EndpointProfiles.Single(profile => profile.Configuration.Id == "a");
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            AssertPage(view, "a", 3, 100, 50);
            view.OnlineEnabled = false;
            view.PreviousRecommendationPageCommand.Execute(null);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            AssertPage(view, "a", 2, 50, 50);
            AssertRequests(workspace, ("a", 0), ("b", 0));
            view.BeginSearchCommand.Execute(null);
            await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.IsLibraryMode && !view.HasRecommendationPages && !view.NextRecommendationPageCommand.CanExecute(null),
                "Opening library search must remove recommendation pagination without changing its saved account page.");
        }

        /// <summary>Rejects a continuation that ignores cancellation after another account becomes current.</summary>
        /// <returns>A task representing account cancellation and late-result isolation.</returns>
        private static async Task ValidateLateAccountBatchAsync()
        {
            var workspace = Workspace("a", "b");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var heldPageRequests = 0;
            CancellationToken obsolete = default;
            workspace.RecommendationOverride = (endpoint, page, token) =>
            {
                if (endpoint.Id == "a" && page == 1 && Interlocked.Increment(ref heldPageRequests) == 1)
                {
                    obsolete = token;
                    started.TrySetResult();
                    return release.Task;
                }

                return Task.FromResult(Tracks(endpoint.Id, endpoint.Id == "a" ? page * 50 : 0, endpoint.Id == "a" ? 50 : 150));
            };
            await using var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            try
            {
                await view.InitializeAsync();
                _ = view.EnsureHomeRecommendationsAsync();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var oldBatch = view.RecommendationBatchUpdate;
                view.SelectedEndpointProfile = view.EndpointProfiles.Single(profile => profile.Configuration.Id == "b");
                await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(obsolete.IsCancellationRequested, "Selecting another account must cancel the previous batch's continuation.");
                AssertPage(view, "b", 1, 0, 50);
                view.SelectedEndpointProfile = view.EndpointProfiles.Single(profile => profile.Configuration.Id == "a");
                await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                AssertPage(view, "a", 1, 0, 50);
                TestAssert.Equal(150, view.RecommendationTotalCount,
                    "Returning to an interrupted account must reuse page zero and fetch only its uncommitted pages one and two.");
                release.TrySetResult(Tracks("a", 999, 50));
                await oldBatch.WaitAsync(TimeSpan.FromSeconds(3));
                view.RecommendationPages[2].SelectCommand.Execute(null);
                await view.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                AssertPage(view, "a", 3, 100, 50);
                AssertRequests(workspace, ("a", 0), ("a", 1), ("b", 0), ("a", 1), ("a", 2));
            }
            finally
            {
                release.TrySetResult(Array.Empty<MusicTrack>());
            }
        }

        /// <summary>Prepares the first twelve covers of each cached page only when a real browsing viewport is present.</summary>
        /// <returns>A task representing bounded per-page speculative artwork scope.</returns>
        private static async Task ValidatePreparedPagesAsync()
        {
            var workspace = Workspace("covers");
            workspace.RecommendationOverride = (endpoint, _, _) => Task.FromResult<IReadOnlyList<MusicTrack>>(
                Tracks(endpoint.Id, 0, 150).Select(track => track with
                {
                    Cover = new MediaResource { LocalPath = "unused-fixture-cover-" + track.Id }
                }).ToArray());
            var requested = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            workspace.ResolveCoverOverride = (track, _) =>
            {
                requested.AddOrUpdate(track.Id, 1, static (_, count) => count + 1);
                return Task.FromResult<string?>(null);
            };
            await using var view = await CreateLoadedViewAsync(workspace);
            TestAssert.Equal(0, requested.Count, "No unattached catalog may start speculative page-cover requests.");
            view.SetBrowseViewportSize(900, 480);
            await view.BrowsePreloadUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            var expected = Enumerable.Range(0, 3).SelectMany(page => Enumerable.Range(page * 50, 12))
                .Select(index => "covers-" + index.ToString("D3")).ToHashSet(StringComparer.Ordinal);
            TestAssert.True(requested.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected) && requested.Values.All(count => count == 1),
                "A visible recommendation batch must warm each page's first twelve covers, never the remaining 114 songs.");
            view.SetBrowseViewportSize(0, 0);
            AssertRequests(workspace, ("covers", 0));
        }

        /// <summary>Creates a loaded view and observes all bounded source requests.</summary>
        /// <param name="workspace">The isolated account-aware source fixture.</param>
        /// <returns>A model whose currently selected source batch has finished.</returns>
        private static async Task<MainViewModel> CreateLoadedViewAsync(LibraryExperienceTests.ExperienceWorkspace workspace)
        {
            var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            try
            {
                await view.InitializeAsync();
                await view.EnsureHomeRecommendationsAsync();
                await view.RecommendationBatchUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                return view;
            }
            catch
            {
                await view.DisposeAsync();
                throw;
            }
        }

        /// <summary>Checks exact membership and the selected numbered-page projection.</summary>
        /// <param name="view">The current recommendation presentation.</param>
        /// <param name="owner">The synthetic account/source prefix.</param>
        /// <param name="page">The expected one-based local page.</param>
        /// <param name="start">The first source entry index.</param>
        /// <param name="count">The expected visible entry count.</param>
        private static void AssertPage(MainViewModel view, string owner, int page, int start, int count)
        {
            TestAssert.True(view.IsRecommendationsMode && view.RecommendationPageNumber == page
                && view.Tracks.Select(track => track.Id).SequenceEqual(Enumerable.Range(start, count).Select(index => owner + "-" + index.ToString("D3")))
                && view.RecommendationPages.Single(item => item.IsSelected).Number == page,
                "The selected recommendation page must contain only its own ordered account entries.");
        }

        /// <summary>Checks the complete finite source request sequence.</summary>
        /// <param name="workspace">The fixture recording request ownership and server pages.</param>
        /// <param name="expected">The only allowed endpoint/page requests.</param>
        private static void AssertRequests(LibraryExperienceTests.ExperienceWorkspace workspace, params (string EndpointId, int Page)[] expected)
        {
            TestAssert.True(workspace.RecommendationRequests.SequenceEqual(expected),
                "Recommendation requests must be bounded to the current batch; local paging cannot request another source page.");
        }

        /// <summary>Creates independently authenticated fixture sources without saved passwords.</summary>
        /// <param name="owners">The synthetic source/account names.</param>
        /// <returns>The in-memory workspace with online access explicitly enabled.</returns>
        private static LibraryExperienceTests.ExperienceWorkspace Workspace(params string[] owners)
        {
            var workspace = new LibraryExperienceTests.ExperienceWorkspace();
            var endpoints = owners.Select(owner => new EndpointConfiguration
            {
                Id = owner,
                Name = owner,
                BaseUri = new Uri("https://" + owner + ".example.test/api/")
            }).ToArray();
            workspace.Settings = new AppSettings { OnlineEnabled = true, ActiveEndpointId = owners[0], Endpoints = endpoints };
            foreach (var owner in owners)
            {
                workspace.Sessions[owner] = new EndpointSession { EndpointId = owner, Username = owner, IsLoggedIn = true };
            }

            return workspace;
        }

        /// <summary>Creates complete source metadata without opening media files.</summary>
        /// <param name="owner">The endpoint/account prefix.</param>
        /// <param name="start">The first source entry index.</param>
        /// <param name="count">The generated entry count.</param>
        /// <returns>The ordered synthetic recommendations.</returns>
        private static IReadOnlyList<MusicTrack> Tracks(string owner, int start, int count)
        {
            return Enumerable.Range(start, count).Select(index => new MusicTrack
            {
                Id = owner + "-" + index.ToString("D3"),
                Title = "Recommendation " + index,
                RemoteId = index.ToString(),
                EndpointId = owner,
                SourceId = "server-" + owner,
                Revision = "fixture",
                Duration = TimeSpan.FromMinutes(3),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000 },
                Audio = new MediaResource { LocalPath = "generated-recommendation-audio" }
            }).ToArray();
        }

        /// <summary>Provides independently parsed properties without opening any synthetic music paths.</summary>
        private sealed class UpdatedMetadataProvider : ITrackMetadataProvider
        {
            /// <inheritdoc />
            public Task<MusicTrack> EnrichAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(track with { Duration = TimeSpan.FromMinutes(5), AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 256000 } });
            }
        }
    }
}
