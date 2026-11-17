#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    public sealed partial class MainViewModel
    {
        /// <summary>Gets the number of leading card presentations and covers kept ready for each home mode.</summary>
        public const int ResidentCardLimit = 25;
        private const int PreviewTrackLimit = ResidentCardLimit;
        private const int AlternatePreviewCapacity = 4;
        private static readonly TimeSpan CoverPreparationDeadline = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan ResidentCoverDeadline = TimeSpan.FromSeconds(20);
        private IReadOnlyDictionary<string, MusicTrack>? _previewLibrary;
        private readonly Dictionary<PreviewKey, PreparedPreview> _browsePreviews = new();
        private bool _hasBrowseViewport;
        private CancellationTokenSource? _previewWarmCancellation;
        private PreviewSpecification[] _previewWarmTargets = [];
        private Task _browsePreloadUpdate = Task.CompletedTask;
        private long _browseCompletionVersion = -1;
        private Task _browseTransitionCompletion = Task.CompletedTask;

        /// <summary>Gets completion of the latest resident-page preparation batch.</summary>
        public Task BrowsePreloadUpdate => _browsePreloadUpdate;

        /// <summary>Starts warm loading for an attached browsing surface and releases pending requests on detachment.</summary>
        /// <param name="width">The measured browser width, or zero without a viewport.</param>
        /// <param name="height">The measured browser height, or zero without a viewport.</param>
        public void SetBrowseViewportSize(double width, double height)
        {
            var available = double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
            if (_hasBrowseViewport == available)
            {
                return;
            }

            _hasBrowseViewport = available;
            if (available)
            {
                QueueWarmBrowsePreviews();
            }
            else
            {
                _previewWarmCancellation?.Cancel();
            }
        }

        /// <summary>Defers visible data changes until the current navigation has settled.</summary>
        /// <param name="version">The navigation generation owned by the transition.</param>
        /// <param name="completion">The task completed when the surface settles or is superseded.</param>
        public void SetBrowseTransitionCompletion(long version, Task completion)
        {
            ArgumentNullException.ThrowIfNull(completion);
            if (version == _browseTransitionVersion)
            {
                _browseCompletionVersion = version;
                _browseTransitionCompletion = completion;
            }
        }

        private void UpdatePreviewLibrary(IReadOnlyDictionary<string, MusicTrack> library)
        {
            _previewLibrary = library;
            foreach (var key in _browsePreviews.Keys.Where(key => key.Mode != HomeBrowseMode.Recommendations).ToArray())
            {
                _browsePreviews.Remove(key, out var previous);
                previous?.Dispose();
            }

            QueueWarmBrowsePreviews();
        }

        private void QueueWarmBrowsePreviews()
        {
            if (!_hasBrowseViewport || !_homeInitialized || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            var targets = new[] { PreviewFor(HomeBrowseMode.Library), PreviewFor(HomeBrowseMode.Playlists, true) }
                .Concat(Enumerable.Range(0, RecommendationPageCount)
                    .Select(index => PreviewFor(HomeBrowseMode.Recommendations, recommendationPage: index)))
                .OfType<PreviewSpecification>().ToArray();
            if (_previewWarmCancellation is { IsCancellationRequested: false }
                && targets.Length == _previewWarmTargets.Length && targets.Zip(_previewWarmTargets).All(pair => MatchesTarget(pair.First, pair.Second)))
            {
                return;
            }

            _previewWarmCancellation?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _previewWarmCancellation = cancellation;
            _previewWarmTargets = targets;
            var sourceNames = _settings.Endpoints.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
            _browsePreloadUpdate = RunTrackedAsync(async () =>
            {
                var pending = new List<Task<PreparedPreview?>>(targets.Length);
                try
                {
                    await Task.Yield();
                    cancellation.Token.ThrowIfCancellationRequested();
                    _cardCovers ??= new CoverThumbnailCache(_workspace, _lifetime.Token);
                    foreach (var target in targets)
                    {
                        var page = GetBrowsePage(target.Key.Mode);
                        var committed = MatchesTarget(page.Target, target) ? page.Snapshot : null;
                        if (committed is not null && page.ArtworkPrepared)
                        {
                            continue;
                        }

                        if (_browsePreviews.TryGetValue(target.Key, out var cached) && MatchesTarget(cached.Target, target))
                        {
                            if (!ReferenceEquals(page, ActiveBrowsePage) && MatchesTarget(target, PreviewFor(target.Key.Mode, true)))
                            {
                                committed = cached.Snapshot;
                            }
                            else
                            {
                                continue;
                            }
                        }

                        pending.Add(PreparePreviewAsync(target, committed, sourceNames, _cardCovers, cancellation.Token));
                    }

                    while (pending.Count > 0)
                    {
                        var completed = await Task.WhenAny(pending);
                        pending.Remove(completed);
                        var prepared = await completed;
                        if (prepared is null)
                        {
                            continue;
                        }

                        try
                        {
                            await WaitForCurrentBrowseSurfaceAsync(cancellation.Token);
                            cancellation.Token.ThrowIfCancellationRequested();
                            var target = prepared.Target;
                            var page = GetBrowsePage(target.Key.Mode);
                            var current = PreviewFor(target.Key.Mode, true, target.Key.PageIndex);
                            if (!MatchesTarget(target, current))
                            {
                                continue;
                            }

                            var active = ReferenceEquals(page, ActiveBrowsePage);
                            if (MatchesTarget(target, PreviewFor(target.Key.Mode, !active)))
                            {
                                if (MatchesTarget(page.Target, target) || MatchesRecommendationPage(page, prepared))
                                {
                                    AdoptResidentCovers(page, prepared.Covers);
                                    page.Target = target;
                                    page.ArtworkPrepared = true;
                                    continue;
                                }

                                if (!active)
                                {
                                    CommitBrowsePage(page, prepared.Snapshot, false, prepared.Covers, target, artworkPrepared: true);
                                    if (_browsePreviews.Remove(target.Key, out var alternate))
                                    {
                                        alternate.Dispose();
                                    }
                                    continue;
                                }
                            }

                            StorePreview(prepared);
                            prepared = null;
                        }
                        finally
                        {
                            prepared?.Dispose();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    ReportError(exception);
                }
                finally
                {
                    foreach (var incomplete in pending)
                    {
                        try
                        {
                            (await incomplete)?.Dispose();
                        }
                        catch (Exception)
                        {
                        }
                    }

                    if (ReferenceEquals(_previewWarmCancellation, cancellation))
                    {
                        _previewWarmCancellation = null;
                        _previewWarmTargets = [];
                    }

                    cancellation.Dispose();
                }
            });
        }

        private static async Task<PreparedPreview?> PreparePreviewAsync(PreviewSpecification target, BrowseSnapshot? committed,
            IReadOnlyDictionary<string, string> sourceNames, CoverThumbnailCache cache, CancellationToken cancellationToken)
        {
            var snapshot = committed ?? await Task.Run(() => BuildBrowseSnapshot(target.GetTracks(), sourceNames, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            var covers = await cache.PreloadAsync(snapshot.Tracks, ResidentCoverDeadline, cancellationToken, PreviewTrackLimit).ConfigureAwait(false);
            return new PreparedPreview(target, snapshot, covers);
        }

        private static bool MatchesRecommendationPage(HomeBrowsePage page, PreparedPreview preview)
        {
            return preview.Target.Key.Mode == HomeBrowseMode.Recommendations && page.Target?.Key == preview.Target.Key
                && page.Snapshot is { } current && current.Tracks.Length == preview.Snapshot.Tracks.Length
                && current.Tracks.Zip(preview.Snapshot.Tracks).All(pair => SameMetadataOwner(pair.First, pair.Second)
                    && CoverThumbnailCache.Identity(pair.First) == CoverThumbnailCache.Identity(pair.Second));
        }

        private PreviewSpecification? PreviewFor(HomeBrowseMode mode, bool nextNavigation = false, int? recommendationPage = null)
        {
            if (mode == HomeBrowseMode.Library)
            {
                if (_previewLibrary is not { } library || IsSearching && mode == _homeMode)
                {
                    return null;
                }

                return new PreviewSpecification(new PreviewKey(mode, null, null, OnlineEnabled, 0), library, library, () => library.Values);
            }

            if (mode == HomeBrowseMode.Playlists)
            {
                if (_previewLibrary is not { } library)
                {
                    return null;
                }

                var playlist = nextNavigation ? SelectedPlaylist : Playlists.FirstOrDefault(item => item.Id == _activePlaylistId);
                return new PreviewSpecification(new PreviewKey(mode, playlist?.Id, null, OnlineEnabled, 0), library, playlist,
                    () => playlist?.TrackIds.Where(library.ContainsKey).Select(id => library[id]) ?? []);
            }

            var owner = CurrentRecommendationOwner();
            var pageIndex = recommendationPage ?? _recommendationPageIndex;
            if (owner is null || !_homeSnapshots.TryGetValue(owner, out var snapshot))
            {
                return null;
            }

            var tracks = snapshot.Tracks.Skip(pageIndex * RecommendationPageSize).Take(RecommendationPageSize).ToArray();
            return new PreviewSpecification(new PreviewKey(mode, null, owner, OnlineEnabled, pageIndex), snapshot.Tracks, null, () => tracks);
        }

        private static bool MatchesTarget(PreviewSpecification? first, PreviewSpecification? second)
        {
            return first is not null && second is not null && first.Key == second.Key
                && ReferenceEquals(first.Origin, second.Origin) && ReferenceEquals(first.Selection, second.Selection);
        }

        private bool TryActivateBrowsePage()
        {
            if (!_hasBrowseViewport || IsSearching || PreviewFor(_homeMode) is not { } target)
            {
                return false;
            }

            var page = ActiveBrowsePage;
            if (page.IsReady && MatchesTarget(page.Target, target))
            {
                PublishActiveBrowsePage(false, residentSwitch: true);
                return true;
            }

            if (_browsePreviews.TryGetValue(target.Key, out var prepared) && MatchesTarget(prepared.Target, target))
            {
                _browsePreviews.Remove(target.Key);
                using (prepared)
                {
                    CommitBrowsePage(page, prepared.Snapshot, false, prepared.Covers, target, artworkPrepared: true);
                }
                PublishActiveBrowsePage(false, residentSwitch: true);
                return true;
            }

            return false;
        }

        private void SaveUnfilteredLibraryPage()
        {
            var page = GetBrowsePage(HomeBrowseMode.Library);
            if (page.Target is not { } target || page.Snapshot is not { } snapshot || _cardCovers is null)
            {
                return;
            }

            var covers = _cardCovers.CaptureReady(snapshot.Tracks, ResidentCardLimit);
            StorePreview(new PreparedPreview(target, snapshot, covers));
        }

        private void StorePreview(PreparedPreview preview)
        {
            if (_browsePreviews.Remove(preview.Target.Key, out var previous))
            {
                previous.Dispose();
            }

            _browsePreviews.Add(preview.Target.Key, preview);
            while (_browsePreviews.Count > AlternatePreviewCapacity)
            {
                var victim = _browsePreviews.First();
                victim.Value.Dispose();
                _browsePreviews.Remove(victim.Key);
            }
        }

        private async Task WaitForCurrentBrowseSurfaceAsync(CancellationToken cancellationToken)
        {
            while (_hasBrowseViewport)
            {
                var version = _browseTransitionVersion;
                if (version != _browseCompletionVersion)
                {
                    return;
                }

                await _browseTransitionCompletion.WaitAsync(cancellationToken);
                if (version == _browseTransitionVersion)
                {
                    return;
                }
            }
        }

        private void DisposeBrowsePreviews()
        {
            foreach (var preview in _browsePreviews.Values)
            {
                preview.Dispose();
            }

            _browsePreviews.Clear();
            _previewLibrary = null;
        }

        private void InvalidateBrowsePage(HomeBrowsePage page)
        {
            page.Target = null;
            page.ArtworkPrepared = false;
            page.SetReady(false);
            page.SetRecommendationFooter(false);
            page.Tracks.Clear();
            RefreshCardTracks(page, new BrowseSnapshot([], new()), null);
        }

        internal readonly record struct PreviewKey(HomeBrowseMode Mode, string? Playlist, string? Owner, bool Online, int PageIndex);
        internal sealed record PreviewSpecification(PreviewKey Key, object Origin, object? Selection, Func<IEnumerable<MusicTrack>> GetTracks);

        private sealed record PreparedPreview(PreviewSpecification Target, BrowseSnapshot Snapshot, CoverThumbnailCache.PreloadBatch Covers) : IDisposable
        {
            public void Dispose() => Covers.Dispose();
        }
    }
}
