#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Prepares bounded first-screen artwork and keeps each incoming surface stable throughout navigation.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Bounds preparation to a small first-screen prefix rather than the complete library.</summary>
        private const int PreviewTrackLimit = 12;
        /// <summary>Bounds independently pinned recommendation, library and playlist previews.</summary>
        private const int PreviewCapacity = 6;
        /// <summary>Bounds one foreground or background cover preparation without waiting for unresponsive sources.</summary>
        private static readonly TimeSpan CoverPreparationDeadline = TimeSpan.FromMilliseconds(400);
        /// <summary>Retains only the library's first-screen descriptors already read for playlist classification.</summary>
        private MusicTrack[] _previewLibrary = [];
        /// <summary>Locates each saved playlist's bounded first screen without retaining the complete catalog.</summary>
        private Dictionary<string, MusicTrack> _previewLibraryById = new(StringComparer.Ordinal);
        /// <summary>Retains first-screen data and its independently pinned ready covers.</summary>
        private readonly Dictionary<PreviewKey, PreparedPreview> _browsePreviews = new();
        /// <summary>Orders bounded prepared targets for eviction.</summary>
        private long _previewStamp;
        /// <summary>Identifies the home generation already displaying a prepared real prefix.</summary>
        private long _publishedPreviewVersion = -1;
        /// <summary>Indicates whether an active browsing viewport can consume speculative artwork.</summary>
        private bool _hasBrowseViewport;
        /// <summary>Owns speculative work separately from the current navigation request.</summary>
        private CancellationTokenSource? _previewWarmCancellation;
        /// <summary>Exposes the observed bounded warm-up batch.</summary>
        private Task _browsePreloadUpdate = Task.CompletedTask;
        /// <summary>Identifies the transition whose actual view completion is registered.</summary>
        private long _browseCompletionVersion = -1;
        /// <summary>Completes after the active view has finished painting its incoming surface.</summary>
        private Task _browseTransitionCompletion = Task.CompletedTask;

        /// <summary>Gets completion of the most recently scheduled first-screen preparation.</summary>
        public Task BrowsePreloadUpdate => _browsePreloadUpdate;

        /// <summary>Registers a real viewport so background preparation stays bounded to an active presentation.</summary>
        /// <param name="width">The width used by the responsive browser, or zero on detachment.</param>
        /// <param name="height">The visible browser height, or zero on detachment.</param>
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

        /// <summary>Registers the actual surface animation without coupling the workspace to a view or animation toolkit.</summary>
        /// <param name="version">The navigation generation owning the view transition.</param>
        /// <param name="completion">Completes when the view has settled, detached or been superseded.</param>
        public void SetBrowseTransitionCompletion(long version, Task completion)
        {
            ArgumentNullException.ThrowIfNull(completion);
            if (version == _browseTransitionVersion)
            {
                _browseCompletionVersion = version;
                _browseTransitionCompletion = completion;
            }
        }

        /// <summary>Replaces preview metadata after a real catalog or playlist mutation.</summary>
        /// <param name="library">The ordered library already read for playlist classification.</param>
        private void UpdatePreviewLibrary(IReadOnlyDictionary<string, MusicTrack> library)
        {
            _previewLibrary = library.Values.Take(PreviewTrackLimit).ToArray();
            var playlistPreviews = new Dictionary<string, MusicTrack>(StringComparer.Ordinal);
            foreach (var playlist in Playlists)
            {
                var count = 0;
                foreach (var id in playlist.TrackIds)
                {
                    if (!library.TryGetValue(id, out var track))
                    {
                        continue;
                    }

                    playlistPreviews[id] = track;
                    if (++count == PreviewTrackLimit)
                    {
                        break;
                    }
                }
            }

            _previewLibraryById = playlistPreviews;
            foreach (var key in _browsePreviews.Keys.Where(key => key.Mode != HomeBrowseMode.Recommendations).ToArray())
            {
                _browsePreviews[key].Dispose();
                _browsePreviews.Remove(key);
            }

            QueueWarmBrowsePreviews();
        }

        /// <summary>Prepares the few songs likely to be shown by the next home navigation.</summary>
        private void QueueWarmBrowsePreviews()
        {
            if (!_hasBrowseViewport || !_homeInitialized || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            _previewWarmCancellation?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _previewWarmCancellation = cancellation;
            var candidates = new List<PreviewSpecification?>
            {
                PreviewFor(HomeBrowseMode.Library), PreviewFor(HomeBrowseMode.Playlists, true)
            };
            for (var pageIndex = 0; pageIndex < RecommendationPageCount; pageIndex++)
            {
                candidates.Add(PreviewFor(HomeBrowseMode.Recommendations, recommendationPage: pageIndex));
            }

            var targets = candidates.OfType<PreviewSpecification>().ToArray();
            _browsePreloadUpdate = RunTrackedAsync(async () =>
            {
                try
                {
                    await Task.Yield();
                    foreach (var target in targets)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        _browsePreviews.TryGetValue(target.Key, out var existing);
                        if (existing is not null && ReferenceEquals(existing.Origin, target.Origin))
                        {
                            continue;
                        }

                        _cardCovers ??= new CoverThumbnailCache(_workspace, _lifetime.Token);
                        var covers = await _cardCovers.PreloadAsync(target.Tracks, CoverPreparationDeadline, cancellation.Token);
                        if (cancellation.IsCancellationRequested)
                        {
                            covers.Dispose();
                            cancellation.Token.ThrowIfCancellationRequested();
                        }

                        _browsePreviews.TryGetValue(target.Key, out var current);
                        var nextTarget = PreviewFor(target.Key.Mode, true, target.Key.PageIndex);
                        if (!ReferenceEquals(existing, current) || nextTarget is null || nextTarget.Key != target.Key
                            || !ReferenceEquals(nextTarget.Origin, target.Origin))
                        {
                            covers.Dispose();
                            continue;
                        }

                        StorePreview(target, covers);
                    }
                }
                catch (OperationCanceledException)
                {
                    // A changed source, viewport or shutdown superseded this speculative prefix.
                }
                catch (Exception exception)
                {
                    ReportError(exception);
                }
                finally
                {
                    if (ReferenceEquals(_previewWarmCancellation, cancellation))
                    {
                        _previewWarmCancellation = null;
                    }

                    cancellation.Dispose();
                }
            });
        }

        /// <summary>Gets one current source-scoped first-screen target without creating card controls or making requests.</summary>
        /// <param name="mode">The requested home mode.</param>
        /// <param name="nextNavigation">Whether a playlist preview follows the next insertion target rather than the currently opened playlist.</param>
        /// <param name="recommendationPage">The cached recommendation page to prepare, otherwise the currently selected page.</param>
        /// <returns>The current preparation target, or null for an unavailable recommendation source.</returns>
        private PreviewSpecification? PreviewFor(HomeBrowseMode mode, bool nextNavigation = false, int? recommendationPage = null)
        {
            if (mode == HomeBrowseMode.Library)
            {
                return new PreviewSpecification(new PreviewKey(mode, null, null, OnlineEnabled, 0), _previewLibrary, _previewLibrary.Take(PreviewTrackLimit).ToArray());
            }

            if (mode == HomeBrowseMode.Playlists)
            {
                var playlist = nextNavigation ? SelectedPlaylist : Playlists.FirstOrDefault(item => item.Id == _activePlaylistId) ?? SelectedPlaylist;
                return playlist is null ? null : new PreviewSpecification(new PreviewKey(mode, playlist.Id, null, OnlineEnabled, 0), playlist,
                    playlist.TrackIds.Where(_previewLibraryById.ContainsKey).Take(PreviewTrackLimit).Select(id => _previewLibraryById[id]).ToArray());
            }

            var owner = CurrentRecommendationOwner();
            var pageIndex = recommendationPage ?? _recommendationPageIndex;
            return owner is not null && _homeSnapshots.TryGetValue(owner, out var snapshot)
                ? new PreviewSpecification(new PreviewKey(mode, null, owner, OnlineEnabled, pageIndex), snapshot.Tracks,
                    snapshot.Tracks.Skip(pageIndex * RecommendationPageSize).Take(PreviewTrackLimit).ToArray()) : null;
        }

        /// <summary>Installs a warm real first screen in the same input turn after the old surface has been captured.</summary>
        /// <param name="loadingOwner">The loading presentation started for this navigation.</param>
        /// <returns>Whether the current target's prepared real cards were published immediately.</returns>
        private bool TryPublishPreparedPreview(long loadingOwner)
        {
            if (!_hasBrowseViewport || IsSearching || PreviewFor(_homeMode) is not { } target
                || !_browsePreviews.TryGetValue(target.Key, out var prepared) || !ReferenceEquals(target.Origin, prepared.Origin))
            {
                return false;
            }

            var tracks = prepared.Tracks.Select(track => track.EndpointId is { } endpoint
                && _settings.Endpoints.FirstOrDefault(item => item.Id == endpoint) is { } source
                && source.Name != track.SourceName ? track with { SourceName = source.Name } : track).ToArray();
            var indexes = new Dictionary<(string, string, string?), int>(tracks.Length);
            for (var index = 0; index < tracks.Length; index++)
            {
                var track = tracks[index];
                indexes[(track.Id, track.SourceId, track.EndpointId)] = index;
            }
            var covers = _cardCovers!.CaptureReady(prepared.Tracks);
            StorePreview(target with { Tracks = prepared.Tracks }, covers);
            _publishedPreviewVersion = _homeVersion;
            PublishBrowseSnapshot(new BrowseSnapshot(tracks, indexes), false, covers);
            FinishBrowseLoad(loadingOwner);
            return true;
        }

        /// <summary>Pins one bounded ready prefix and evicts the least recently used independent target.</summary>
        /// <param name="target">The immutable target identity and metadata prefix.</param>
        /// <param name="covers">The ready cover pins transferred to this cache.</param>
        private void StorePreview(PreviewSpecification target, CoverThumbnailCache.PreloadBatch covers)
        {
            if (_browsePreviews.Remove(target.Key, out var previous))
            {
                previous.Dispose();
            }

            _browsePreviews.Add(target.Key, new PreparedPreview(target.Origin, target.Tracks, covers, ++_previewStamp));
            while (_browsePreviews.Count > PreviewCapacity)
            {
                var victim = _browsePreviews.MinBy(item => item.Value.Stamp);
                victim.Value.Dispose();
                _browsePreviews.Remove(victim.Key);
            }
        }

        /// <summary>Waits for this target's actual view transition before replacing its stable incoming surface.</summary>
        /// <param name="version">The surface generation captured by the query.</param>
        /// <param name="cancellationToken">Cancels an obsolete navigation.</param>
        /// <returns>A task representing completion of the registered visible movement.</returns>
        private Task WaitForBrowseSurfaceAsync(long version, CancellationToken cancellationToken)
        {
            return version == _browseCompletionVersion ? _browseTransitionCompletion.WaitAsync(cancellationToken) : Task.CompletedTask;
        }

        /// <summary>Releases speculative target pins before the shared bitmap cache is disposed.</summary>
        private void DisposeBrowsePreviews()
        {
            foreach (var preview in _browsePreviews.Values)
            {
                preview.Dispose();
            }

            _browsePreviews.Clear();
        }

        /// <summary>Scopes previews to their mode, playlist, account and network permission.</summary>
        /// <param name="Mode">The browsing surface to enter.</param>
        /// <param name="Playlist">The opened playlist identity, when applicable.</param>
        /// <param name="Owner">The selected recommendation account identity, when applicable.</param>
        /// <param name="Online">The network permission associated with this preparation.</param>
        /// <param name="PageIndex">The cached recommendation page, otherwise zero.</param>
        private readonly record struct PreviewKey(HomeBrowseMode Mode, string? Playlist, string? Owner, bool Online, int PageIndex);
        /// <summary>Retains a current source object and its bounded ordered metadata prefix.</summary>
        /// <param name="Key">The destination and account-scoped cache identity.</param>
        /// <param name="Origin">The immutable source object used to reject obsolete preparations.</param>
        /// <param name="Tracks">The bounded first-screen metadata in display order.</param>
        private sealed record PreviewSpecification(PreviewKey Key, object Origin, MusicTrack[] Tracks);

        /// <summary>Owns one immutable first-screen prefix and its ready cover pins.</summary>
        private sealed class PreparedPreview : IDisposable
        {
            /// <summary>Constructs a cached prefix by taking ownership of its prepared pins.</summary>
            /// <param name="origin">The catalog, playlist or account snapshot that produced the prefix.</param>
            /// <param name="tracks">The prefix in display order.</param>
            /// <param name="covers">The ready covers and stable unavailable placeholders.</param>
            /// <param name="stamp">The cache access order.</param>
            public PreparedPreview(object origin, MusicTrack[] tracks, CoverThumbnailCache.PreloadBatch covers, long stamp)
            {
                Origin = origin;
                Tracks = tracks;
                Covers = covers;
                Stamp = stamp;
            }

            /// <summary>Gets the immutable source object that invalidates a stale prefix.</summary>
            public object Origin { get; }
            /// <summary>Gets the bounded display descriptors.</summary>
            public MusicTrack[] Tracks { get; }
            /// <summary>Gets the independently pinned cover preparation.</summary>
            public CoverThumbnailCache.PreloadBatch Covers { get; }
            /// <summary>Gets or sets the most recent cache access order.</summary>
            public long Stamp { get; set; }

            /// <summary>Releases only this target's pins.</summary>
            public void Dispose()
            {
                Covers.Dispose();
            }
        }
    }
}
