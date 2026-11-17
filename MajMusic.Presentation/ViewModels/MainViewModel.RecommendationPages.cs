#nullable enable

using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Represents one numbered page of an already cached recommendation batch.</summary>
    /// <param name="Number">The one-based page label.</param>
    /// <param name="IsSelected">Whether this page owns the current recommendation view.</param>
    /// <param name="SelectCommand">Selects this cached page without contacting its source.</param>
    public sealed record RecommendationPageItem(int Number, bool IsSelected, AsyncCommand SelectCommand);

    /// <summary>Browses a bounded account-owned recommendation batch in local fifty-song pages.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Matches the visible recommendation page and the requested server page size.</summary>
        private const int RecommendationPageSize = 50;
        /// <summary>Bounds one account batch independently of a compatible source's continuation behavior.</summary>
        private const int MaximumRecommendationPages = 3;
        /// <summary>Retains the small numbered-page binding projection.</summary>
        private IReadOnlyList<RecommendationPageItem> _recommendationPages = Array.Empty<RecommendationPageItem>();
        /// <summary>Allows at most one automatic batch attempt for an account during each distinct browsing intent.</summary>
        private readonly Dictionary<string, long> _recommendationAttemptVersions = new(StringComparer.Ordinal);

        /// <summary>Gets the numbered pages of the currently attached account batch.</summary>
        public IReadOnlyList<RecommendationPageItem> RecommendationPages => _recommendationPages;
        /// <summary>Gets the one-based selected recommendation page.</summary>
        public int RecommendationPageNumber => _recommendationPageIndex + 1;
        /// <summary>Gets the number of available local pages, at most three.</summary>
        public int RecommendationPageCount => ((_recommendationTracks?.Count ?? 0) + RecommendationPageSize - 1) / RecommendationPageSize;
        /// <summary>Gets the number of entries already loaded for the attached account.</summary>
        public int RecommendationTotalCount => _recommendationTracks?.Count ?? 0;
        /// <summary>Gets completion of the current bounded source batch, including its optional prefetched pages.</summary>
        public Task RecommendationBatchUpdate => _recommendationRequest;
        /// <summary>Gets the complete number of cards expected on the selected local page, independently of a warm prefix.</summary>
        public int RecommendationCurrentPageTrackCount => Math.Clamp(RecommendationTotalCount - _recommendationPageIndex * RecommendationPageSize, 0, RecommendationPageSize);
        /// <summary>Gets whether the current recommendation view needs a numbered footer.</summary>
        public bool HasRecommendationPages => IsRecommendationsMode && RecommendationPageCount > 1;
        /// <summary>Gets the command opening the previous cached page.</summary>
        public AsyncCommand PreviousRecommendationPageCommand { get; private set; } = null!;
        /// <summary>Gets the command opening the next cached page.</summary>
        public AsyncCommand NextRecommendationPageCommand { get; private set; } = null!;

        /// <summary>Creates transport-independent local page actions.</summary>
        private void InitializeRecommendationPagingCommands()
        {
            PreviousRecommendationPageCommand = CreateRecommendationPageCommand(() => _recommendationPageIndex - 1);
            NextRecommendationPageCommand = CreateRecommendationPageCommand(() => _recommendationPageIndex + 1);
        }

        /// <summary>Creates an observed action whose asynchronous work belongs to the shared home-update lifecycle.</summary>
        /// <param name="pageIndex">Supplies the requested zero-based page when the action is invoked.</param>
        /// <returns>A command that cannot select a missing page or outlive the model.</returns>
        private AsyncCommand CreateRecommendationPageCommand(Func<int> pageIndex)
        {
            return new AsyncCommand(() =>
            {
                SelectRecommendationPage(pageIndex());
                return Task.CompletedTask;
            }, ReportError, () => !Volatile.Read(ref _isDisposing) && IsRecommendationsMode
                && pageIndex() >= 0 && pageIndex() < RecommendationPageCount && pageIndex() != _recommendationPageIndex);
        }

        /// <summary>Starts the existing horizontal surface transition before reading the selected cached page.</summary>
        /// <param name="pageIndex">The requested zero-based page in the current account batch.</param>
        private void SelectRecommendationPage(int pageIndex)
        {
            if (!IsRecommendationsMode || pageIndex < 0 || pageIndex >= RecommendationPageCount
                || pageIndex == _recommendationPageIndex || CurrentRecommendationOwner() is not { } owner
                || _attachedRecommendationOwner != owner || !_homeSnapshots.TryGetValue(owner, out var snapshot))
            {
                return;
            }

            var direction = Math.Sign(pageIndex - _recommendationPageIndex);
            _recommendationPageIndex = pageIndex;
            _homeSnapshots[owner] = snapshot with { PageIndex = pageIndex };
            _homeVersion++;
            _homeUpdateCancellation?.Cancel();
            var loadingOwner = BeginBrowseLoad(true, direction, notifyPlaceholder: false);
            if (TryActivateBrowsePage())
            {
                _residentBrowseVersion = _homeVersion;
                FinishBrowseLoad(loadingOwner);
            }
            else
            {
                OnPropertyChanged(nameof(IsBrowsePlaceholderVisible));
            }
            NotifyHomeState();
            QueueHomeUpdate(false);
            QueueWarmBrowsePreviews();
        }

        /// <summary>Copies only the current visible page while keeping the complete batch available to the playback queue.</summary>
        /// <returns>The selected page in the source's original order, or an empty array.</returns>
        private MusicTrack[] CurrentRecommendationPageTracks()
        {
            return _recommendationTracks?.Skip(_recommendationPageIndex * RecommendationPageSize).Take(RecommendationPageSize).ToArray() ?? [];
        }

        /// <summary>Fetches at most three server pages and exposes the first page before requesting the remaining batch.</summary>
        /// <param name="endpoint">The independently authenticated source.</param>
        /// <param name="owner">The selected endpoint/account cache identity.</param>
        /// <param name="refresh">Whether an explicit refresh starts a fresh batch instead of continuing saved pages.</param>
        /// <param name="cancellationToken">Cancels an obsolete account or closed view.</param>
        /// <returns>A task representing this bounded background batch.</returns>
        private async Task LoadRecommendationBatchAsync(EndpointConfiguration endpoint, string owner, bool refresh, CancellationToken cancellationToken)
        {
            var previous = !refresh ? _homeSnapshots.GetValueOrDefault(owner) : null;
            var collected = previous is null ? new List<MusicTrack>(RecommendationPageSize * MaximumRecommendationPages) : new List<MusicTrack>(previous.Tracks);
            var identities = collected.Select(track => (track.Id, track.SourceId, track.EndpointId)).ToHashSet();
            for (var pageIndex = previous?.NextServerPage ?? 0; pageIndex < MaximumRecommendationPages; pageIndex++)
            {
                var received = await _onlineWorkspace!.GetRecommendationsAsync(endpoint, pageIndex, RecommendationPageSize, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!OnlineEnabled || CurrentRecommendationOwner() != owner)
                {
                    return;
                }

                if (pageIndex > 0 && _homeSnapshots.TryGetValue(owner, out var current) && current.NextServerPage == pageIndex)
                {
                    collected = new List<MusicTrack>(current.Tracks);
                }

                var previousCount = collected.Count;
                foreach (var track in received)
                {
                    if (identities.Add((track.Id, track.SourceId, track.EndpointId)))
                    {
                        collected.Add(track);
                        if (collected.Count == RecommendationPageSize * MaximumRecommendationPages)
                        {
                            break;
                        }
                    }
                }

                var selectedPage = pageIndex == 0 ? 0 : _homeSnapshots.GetValueOrDefault(owner)?.PageIndex ?? 0;
                var tracks = new List<MusicTrack>(collected);
                var complete = received.Count < RecommendationPageSize || collected.Count == previousCount
                    || collected.Count >= RecommendationPageSize * MaximumRecommendationPages || pageIndex == MaximumRecommendationPages - 1;
                var previousPage = IsRecommendationsMode ? CurrentRecommendationPageTracks() : Array.Empty<MusicTrack>();
                _homeSnapshots[owner] = new RecommendationSnapshot(tracks, selectedPage, pageIndex + 1, complete);
                AttachRecommendationSnapshot();
                TryExtendRecommendationPlayback(owner);
                QueueWarmBrowsePreviews();
                if (IsRecommendationsMode && (pageIndex == 0
                    || !previousPage.Select(track => (track.Id, track.SourceId, track.EndpointId))
                        .SequenceEqual(CurrentRecommendationPageTracks().Select(track => (track.Id, track.SourceId, track.EndpointId)))))
                {
                    await ReloadLibraryCoreAsync(cancellationToken, preservePage: pageIndex > 0);
                }

                if (complete)
                {
                    break;
                }
            }

            if (_previewLibrary is { } library && collected.Any(track => !library.ContainsKey(track.Id)))
            {
                var indexed = await _workspace.SearchAsync(null, cancellationToken);
                var refreshed = await Task.Run(() => indexed.ToDictionary(track => track.Id, StringComparer.Ordinal), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (CurrentRecommendationOwner() == owner && ReferenceEquals(library, _previewLibrary))
                {
                    UpdatePreviewLibrary(refreshed);
                }
            }
        }

        /// <summary>Refreshes numbered pages and availability without replacing the current cards or scroll offset.</summary>
        private void NotifyRecommendationPagination()
        {
            var residentPage = GetBrowsePage(HomeBrowseMode.Recommendations);
            residentPage.SetRecommendationFooter(residentPage.IsReady && residentPage.CardTracks.Count == RecommendationCurrentPageTrackCount
                && RecommendationPageCount > 1);
            if (_recommendationPages.Count != RecommendationPageCount
                || _recommendationPages.Count > 0 && !_recommendationPages[_recommendationPageIndex].IsSelected)
            {
                var pages = new RecommendationPageItem[RecommendationPageCount];
                for (var index = 0; index < pages.Length; index++)
                {
                    var selectedIndex = index;
                    pages[index] = new RecommendationPageItem(index + 1, index == _recommendationPageIndex,
                        CreateRecommendationPageCommand(() => selectedIndex));
                }

                _recommendationPages = pages;
                OnPropertyChanged(nameof(RecommendationPages));
            }
            OnPropertyChanged(nameof(RecommendationPageNumber));
            OnPropertyChanged(nameof(RecommendationPageCount));
            OnPropertyChanged(nameof(RecommendationTotalCount));
            OnPropertyChanged(nameof(RecommendationCurrentPageTrackCount));
            OnPropertyChanged(nameof(HasRecommendationPages));
            OnPropertyChanged(nameof(HasMoreRecommendations));
            OnPropertyChanged(nameof(BrowseSummary));
            PreviousRecommendationPageCommand?.NotifyCanExecuteChanged();
            NextRecommendationPageCommand?.NotifyCanExecuteChanged();
        }
    }
}
