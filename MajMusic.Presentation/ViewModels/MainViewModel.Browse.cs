#nullable enable

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Keeps one virtualized row in place while its bounded song slice changes.</summary>
    public sealed class SongCardRow : INotifyPropertyChanged
    {
        /// <summary>Reuses the notification shared by every updated song slice.</summary>
        private static readonly PropertyChangedEventArgs TracksChanged = new(nameof(Tracks));
        /// <summary>Reuses the notification shared by responsive column changes.</summary>
        private static readonly PropertyChangedEventArgs ColumnsChanged = new(nameof(Columns));

        /// <summary>Creates an indexed row without materializing the songs in its lazy slice.</summary>
        /// <param name="tracks">The bounded song slice represented by this row.</param>
        /// <param name="columns">The number of equal-width card columns.</param>
        public SongCardRow(IReadOnlyList<SongCardViewModel> tracks, int columns)
        {
            Tracks = tracks;
            Columns = columns;
        }

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>Gets the current bounded slice of lazily materialized song presentations.</summary>
        public IReadOnlyList<SongCardViewModel> Tracks { get; private set; }
        /// <summary>Gets the current number of equal-width card columns.</summary>
        public int Columns { get; private set; }

        /// <summary>Commits both row values before notifying existing controls to rebind their visible content.</summary>
        /// <param name="tracks">The incoming lazy song slice.</param>
        /// <param name="columns">The incoming responsive column count.</param>
        internal void Update(IReadOnlyList<SongCardViewModel> tracks, int columns)
        {
            var columnsChanged = Columns != columns;
            Tracks = tracks;
            Columns = columns;
            PropertyChanged?.Invoke(this, TracksChanged);
            if (columnsChanged)
            {
                PropertyChanged?.Invoke(this, ColumnsChanged);
            }
        }
    }

    /// <summary>Exposes scrollable artwork browsing independently of the playback queue.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Stores the responsive card-column count.</summary>
        private int _browseColumns = 2;
        /// <summary>Limits concurrent visible-song metadata reads.</summary>
        private readonly SemaphoreSlim _browseMetadataGate = new(4);
        /// <summary>Avoids repeating unavailable metadata reads during this view lifetime.</summary>
        private readonly ConcurrentDictionary<string, byte> _browseMetadataRead = new(StringComparer.Ordinal);
        /// <summary>Lets another visible control retry after the current request owner's viewport interest ends.</summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _browseMetadataPending = new(StringComparer.Ordinal);
        /// <summary>Retains indexed row slots and publishes only changes to the complete row count.</summary>
        private readonly HomeBrowsePage[] _browsePages = [new(), new(), new()];
        private HomeBrowsePage ActiveBrowsePage => GetBrowsePage(_homeMode);

        /// <summary>Gets the page state retained for the workspace lifetime.</summary>
        /// <param name="mode">The home mode owning the page.</param>
        /// <returns>The stable page whose catalog changes only when its content changes.</returns>
        public HomeBrowsePage GetBrowsePage(HomeBrowseMode mode) => _browsePages[(int)mode];

        /// <summary>Gets whether browsing uses the shared artwork-card presentation in every mode.</summary>
        public bool IsCardBrowse => true;
        /// <summary>Gets the rows rendered by the virtualized artwork list.</summary>
        public ObservableCollection<SongCardRow> BrowseRows => ActiveBrowsePage.Rows;
        /// <summary>Gets the responsive number of card columns.</summary>
        public int BrowseColumns => _browseColumns;
        /// <summary>Gets the committed mode or search generation used to reset only a new browsing query.</summary>
        public long BrowseNavigationVersion => _homeVersion;
        /// <summary>Gets the recommendation batch total or the current catalog count independently of a prepared prefix.</summary>
        public string BrowseSummary => IsRecommendationsMode ? $"{RecommendationTotalCount} 首推荐" : $"{Tracks.Count} 首";
        /// <summary>Gets the inspected song when it belongs to the current browsing snapshot.</summary>
        public MusicTrack? SelectedBrowseTrack => SelectedTrack is { } selected
            && _cardIndexes.TryGetValue((selected.Id, selected.SourceId, selected.EndpointId), out var index) ? Tracks[index] : null;
        /// <summary>Gets or selects the artwork-card presentation for the inspected song.</summary>
        public SongCardViewModel? SelectedBrowseCard
        {
            get => SelectedTrack is { } selected && _cardIndexes.TryGetValue((selected.Id, selected.SourceId, selected.EndpointId), out var index)
                ? CardTracks[index] : null;
            set
            {
                if (value is not null)
                {
                    SelectedTrack = value.Track;
                }
            }
        }

        /// <summary>Fits artwork cards within the preferred width range without imposing platform-specific column counts.</summary>
        /// <param name="width">The actual scroll viewport width in logical pixels.</param>
        public void SetBrowseViewportWidth(double width)
        {
            if (!double.IsFinite(width) || width <= 0 || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            const double gap = 12;
            var small = SelectedCardLayout == SongCardLayout.Small;
            var preferredWidth = small ? 250 : 500;
            var minimumWidth = small ? 140 : 280;
            var preferredColumns = (int)Math.Ceiling((width + gap) / (preferredWidth + gap));
            var capacity = Math.Max(1, (int)Math.Floor((width + gap) / (minimumWidth + gap)));
            var columns = Math.Clamp(preferredColumns, 1, Math.Min(16, capacity));
            if (columns != _browseColumns)
            {
                _browseColumns = columns;
                foreach (var page in _browsePages)
                {
                    RefreshBrowseRows(page);
                }

                OnPropertyChanged(nameof(BrowseColumns));
            }
        }

        /// <summary>Updates stable row slots and changes only their tail when the displayed catalog grows or shrinks.</summary>
        private void RefreshBrowseRows(HomeBrowsePage page)
        {
            var cards = page.CardTracks;
            var currentRows = page.MutableRows;
            var count = (cards.Count + _browseColumns - 1) / _browseColumns;
            var rows = new SongCardRow[count];
            for (var index = 0; index < count; index++)
            {
                var start = index * _browseColumns;
                var length = Math.Min(_browseColumns, cards.Count - start);
                if (index < currentRows.Count && currentRows[index].Columns == _browseColumns
                    && currentRows[index].Tracks is CardSlice previous && previous.Matches(cards, start, length))
                {
                    previous.Rebind(cards, start);
                    rows[index] = currentRows[index];
                    continue;
                }

                var slice = new CardSlice(cards, start, length);
                if (index < currentRows.Count)
                {
                    rows[index] = currentRows[index];
                    rows[index].Update(slice, _browseColumns);
                }
                else
                {
                    rows[index] = new SongCardRow(slice, _browseColumns);
                }
            }
            currentRows.ResizeMatchingPrefix(rows);
        }

        /// <summary>Enriches a visible song without opening or changing the playback backend.</summary>
        /// <param name="card">The attached song presentation.</param>
        /// <param name="cancellationToken">Cancels work after this presentation leaves the viewport.</param>
        /// <returns>A task representing the bounded optional metadata read.</returns>
        public Task EnsureBrowseMetadataAsync(SongCardViewModel card, CancellationToken cancellationToken)
        {
            return RunTrackedAsync(async () =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
                while (!cancellation.IsCancellationRequested)
                {
                    var track = card.Track;
                    if (track.Duration is not null && track.AudioInfo is not null)
                    {
                        return;
                    }

                    var key = track.Id + "\n" + track.SourceId + "\n" + track.Revision + "\n" + track.EndpointId + "\n" + OnlineEnabled;
                    if (_browseMetadataRead.ContainsKey(key))
                    {
                        return;
                    }

                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_browseMetadataPending.TryAdd(key, completion))
                    {
                        if (_browseMetadataPending.TryGetValue(key, out var pending))
                        {
                            try
                            {
                                if (await pending.Task.WaitAsync(cancellation.Token))
                                {
                                    return;
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }
                        }

                        continue;
                    }

                    var entered = false;
                    var completed = false;
                    try
                    {
                        await _browseMetadataGate.WaitAsync(cancellation.Token);
                        entered = true;
                        var updated = PreferDownloadedMetadata(await EnrichLocalMetadataAsync(track, cancellation.Token));
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (updated != track)
                        {
                            _dispatch(() => ApplyTrackMetadata(updated));
                            if (_onlineWorkspace is not null)
                            {
                                try
                                {
                                    await _onlineWorkspace.UpdateTrackMetadataAsync(updated, cancellation.Token);
                                }
                                catch (Exception exception) when (exception is not OperationCanceledException)
                                {
                                    // Parsed local properties remain useful when optional persistence is unavailable.
                                }
                            }
                        }

                        if (OnlineEnabled && track.RemoteId is not null && _onlineWorkspace is not null
                            && (updated.Duration is null || updated.AudioInfo is null))
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(8));
                            var remote = await _onlineWorkspace.EnrichTrackMetadataAsync(updated, timeout.Token);
                            var merged = PreferDownloadedMetadata(updated with
                            {
                                Duration = updated.Duration ?? remote.Duration,
                                AudioInfo = updated.AudioInfo ?? remote.AudioInfo
                            });
                            cancellation.Token.ThrowIfCancellationRequested();
                            if (merged != updated)
                            {
                                _dispatch(() => ApplyTrackMetadata(merged));
                                await _onlineWorkspace.UpdateTrackMetadataAsync(merged, cancellation.Token);
                            }
                        }

                        completed = true;
                    }
                    catch (OperationCanceledException)
                    {
                        // A still-visible consumer can acquire the next request after this owner detaches.
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        completed = true;
                        // Retain already published local properties when optional source metadata fails.
                    }
                    finally
                    {
                        if (entered)
                        {
                            _browseMetadataGate.Release();
                        }

                        if (completed)
                        {
                            _browseMetadataRead.TryAdd(key, 0);
                        }

                        _browseMetadataPending.TryRemove(key, out _);
                        completion.TrySetResult(completed);
                    }

                    return;
                }
            });
        }
    }
}
