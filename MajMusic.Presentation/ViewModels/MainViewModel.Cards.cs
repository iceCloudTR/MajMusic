#nullable enable

using MajMusic.Core.Models;
using System.Collections;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Maintains stable music-card instances independently of selected-song cover loading.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Shares bounded thumbnail requests among all card controls in this view.</summary>
        private CoverThumbnailCache? _cardCovers;
        /// <summary>Preserves one card for each currently browsed source-scoped music identity.</summary>
        private Dictionary<(string Id, string Source, string? Endpoint), SongCardViewModel> _cards = new();
        /// <summary>Locates entries without constructing presentations for unvisited songs.</summary>
        private Dictionary<(string Id, string Source, string? Endpoint), int> _cardIndexes = new();
        /// <summary>Retains canceled checks belonging to cards removed from the query until they finish.</summary>
        private readonly List<Task> _retiredCardResources = new();

        /// <summary>Gets current music cards in the same order as the browsed music collection.</summary>
        public IReadOnlyList<SongCardViewModel> CardTracks { get; private set; } = Array.Empty<SongCardViewModel>();
        /// <summary>Gets the number of materialized song presentations retained by the current catalog.</summary>
        public int MaterializedCardCount => _cards.Count;

        /// <summary>Reconciles the browsed entries in one binding update while reusing unchanged card instances.</summary>
        /// <param name="appendRows">Whether a recommendation continuation should preserve existing row containers.</param>
        /// <param name="snapshot">The prepared catalog and its background-built identity index.</param>
        private void RefreshCardTracks(bool appendRows = false, BrowseSnapshot? snapshot = null)
        {
            _retiredCardResources.RemoveAll(task => task.IsCompletedSuccessfully);
            var tracks = snapshot?.Tracks ?? Tracks.ToArray();
            _cardIndexes = snapshot?.Indexes ?? tracks.Select((track, index) => (track, index))
                .ToDictionary(item => (item.track.Id, item.track.SourceId, item.track.EndpointId), item => item.index);
            var next = new Dictionary<(string, string, string?), SongCardViewModel>();
            var retained = new List<(SongCardViewModel Card, MusicTrack Track)>();
            var retired = new List<SongCardViewModel>();
            foreach (var (key, card) in _cards)
            {
                if (_cardIndexes.TryGetValue(key, out var index))
                {
                    next[key] = card;
                    retained.Add((card, tracks[index]));
                }
                else
                {
                    retired.Add(card);
                }
            }

            _cards = next;
            CardTracks = new LazyCardList(tracks, MaterializeCard);
            foreach (var (card, track) in retained)
            {
                card.UpdateTrack(track);
                RestoreCardDownloadProgress(card);
            }

            foreach (var card in retired)
            {
                card.Dispose();
                if (!card.ResourceCompletion.IsCompletedSuccessfully)
                {
                    _retiredCardResources.Add(card.ResourceCompletion);
                }
            }

            RefreshCardSelection();
            OnPropertyChanged(nameof(CardTracks));
            RefreshBrowseRows(appendRows);
        }

        /// <summary>Finds the current row index without walking or materializing the entire catalog.</summary>
        /// <param name="card">The selected card, or null for no selection.</param>
        /// <returns>The zero-based catalog index, or minus one when the card is outside this query.</returns>
        public int GetBrowseCardIndex(SongCardViewModel? card)
        {
            return card is not null && _cardIndexes.TryGetValue((card.Track.Id, card.Track.SourceId, card.Track.EndpointId), out var index)
                ? index : -1;
        }

        /// <summary>Creates a presentation only when a visible row or explicit user action requests its song.</summary>
        /// <param name="track">The descriptor captured by the requesting catalog.</param>
        /// <returns>The current stable presentation, or an inert presentation for a retired catalog.</returns>
        private SongCardViewModel MaterializeCard(MusicTrack track)
        {
            var key = (track.Id, track.SourceId, track.EndpointId);
            if (_cards.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var current = _cardIndexes.TryGetValue(key, out var index);
            if (current)
            {
                track = Tracks[index];
            }

            _cardCovers ??= new CoverThumbnailCache(_workspace, _lifetime.Token);
            var card = new SongCardViewModel(track, _cardCovers);
            if (!current || Volatile.Read(ref _isDisposing))
            {
                card.Dispose();
                return card;
            }

            RestoreCardDownloadProgress(card);
            card.IsSelected = SelectedTrack is { } selected && selected.Id == track.Id
                && selected.SourceId == track.SourceId && selected.EndpointId == track.EndpointId;
            _cards.Add(key, card);
            return card;
        }

        /// <summary>Updates audio properties on an existing card without replacing its cover or resource ownership.</summary>
        /// <param name="updated">The metadata parsed for the matching source revision.</param>
        private void RefreshCardTrackMetadata(MusicTrack updated)
        {
            if (_cards.TryGetValue((updated.Id, updated.SourceId, updated.EndpointId), out var card)
                && card.Track.Revision == updated.Revision)
            {
                card.UpdateTrack(card.Track with
                {
                    Duration = updated.Duration,
                    AudioInfo = updated.AudioInfo,
                    AudioMetadataLength = updated.AudioMetadataLength ?? card.Track.AudioMetadataLength,
                    AudioMetadataLastWriteUtcTicks = updated.AudioMetadataLastWriteUtcTicks ?? card.Track.AudioMetadataLastWriteUtcTicks
                });
            }
        }

        /// <summary>Synchronizes card highlights without loading or clearing any cover.</summary>
        private void RefreshCardSelection()
        {
            foreach (var card in _cards.Values.ToArray())
            {
                card.IsSelected = SelectedTrack is { } selected && card.Track.Id == selected.Id
                    && card.Track.SourceId == selected.SourceId && card.Track.EndpointId == selected.EndpointId;
            }
        }

        /// <summary>Cancels thumbnail requests and waits for their media access before releasing decoded resources.</summary>
        /// <returns>A task representing completed thumbnail cleanup.</returns>
        private async Task DisposeCardsAsync()
        {
            var resources = new List<Task>(_retiredCardResources);
            foreach (var card in _cards.Values)
            {
                card.Dispose();
                resources.Add(card.ResourceCompletion);
            }

            _cards.Clear();
            _cardIndexes.Clear();
            CardTracks = Array.Empty<SongCardViewModel>();
            await Task.WhenAll(resources);
            _retiredCardResources.Clear();
            if (_cardCovers is not null)
            {
                await _cardCovers.DisposeAsync();
            }
        }

        /// <summary>Retains the prepared immutable catalog and its source-scoped identity lookup.</summary>
        /// <param name="Tracks">The next complete catalog in display order.</param>
        /// <param name="Indexes">The index prepared away from the presentation thread.</param>
        private sealed record BrowseSnapshot(MusicTrack[] Tracks, Dictionary<(string, string, string?), int> Indexes);

        /// <summary>Defers song presentations until a visible row requests a specific catalog entry.</summary>
        private sealed class LazyCardList : IReadOnlyList<SongCardViewModel>
        {
            /// <summary>Retains the catalog descriptors for this query.</summary>
            private readonly MusicTrack[] _tracks;
            /// <summary>Creates or reuses a presentation on first indexed access.</summary>
            private readonly Func<MusicTrack, SongCardViewModel> _factory;
            /// <summary>Retains presentations already requested from this query.</summary>
            private readonly SongCardViewModel?[] _materialized;

            /// <summary>Creates a catalog without constructing its song presentations.</summary>
            /// <param name="tracks">The immutable ordered descriptors.</param>
            /// <param name="factory">The owner that supplies stable song presentations.</param>
            public LazyCardList(MusicTrack[] tracks, Func<MusicTrack, SongCardViewModel> factory)
            {
                _tracks = tracks;
                _factory = factory;
                _materialized = new SongCardViewModel?[tracks.Length];
            }

            /// <summary>Gets the number of descriptors without materializing them.</summary>
            public int Count => _tracks.Length;
            /// <summary>Gets one descriptor without constructing a song presentation.</summary>
            /// <param name="index">The zero-based catalog position.</param>
            /// <returns>The captured descriptor at that position.</returns>
            public MusicTrack DescriptorAt(int index)
            {
                return _tracks[index];
            }
            /// <summary>Gets the requested presentation, materializing only this song when needed.</summary>
            /// <param name="index">The zero-based catalog position.</param>
            /// <returns>The stable presentation at that position.</returns>
            public SongCardViewModel this[int index] => _materialized[index] ??= _factory(_tracks[index]);

            /// <summary>Enumerates explicitly requested presentations in catalog order.</summary>
            /// <returns>The lazy presentation enumerator.</returns>
            public IEnumerator<SongCardViewModel> GetEnumerator()
            {
                for (var index = 0; index < Count; index++)
                {
                    yield return this[index];
                }
            }

            /// <inheritdoc />
            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        /// <summary>Exposes one lightweight virtual row without enumerating its song presentations during grouping.</summary>
        private sealed class CardSlice : IReadOnlyList<SongCardViewModel>
        {
            /// <summary>Retains the catalog behind this row.</summary>
            private readonly IReadOnlyList<SongCardViewModel> _source;
            /// <summary>Identifies the first descriptor in the catalog.</summary>
            private readonly int _start;

            /// <summary>Creates an indexed row segment without touching its presentations.</summary>
            /// <param name="source">The lazy catalog.</param>
            /// <param name="start">The first catalog position.</param>
            /// <param name="count">The number of entries in this row.</param>
            public CardSlice(IReadOnlyList<SongCardViewModel> source, int start, int count)
            {
                _source = source;
                _start = start;
                Count = count;
            }

            /// <summary>Gets the number of entries in the row.</summary>
            public int Count { get; }
            /// <summary>Gets a presentation only when the row is realized or explicitly inspected.</summary>
            /// <param name="index">The position within this row.</param>
            /// <returns>The requested song presentation.</returns>
            public SongCardViewModel this[int index] => index >= 0 && index < Count
                ? _source[_start + index] : throw new ArgumentOutOfRangeException(nameof(index));

            /// <summary>Compares immutable descriptors while retaining already realized continuation rows.</summary>
            /// <param name="source">The new lazy catalog.</param>
            /// <param name="start">The corresponding new catalog position.</param>
            /// <param name="count">The corresponding new row length.</param>
            /// <returns>Whether the descriptors match without materializing either row.</returns>
            public bool Matches(IReadOnlyList<SongCardViewModel> source, int start, int count)
            {
                if (count != Count || _source is not LazyCardList previous || source is not LazyCardList next)
                {
                    return false;
                }

                for (var index = 0; index < Count; index++)
                {
                    if (previous.DescriptorAt(_start + index) != next.DescriptorAt(start + index))
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>Enumerates the presentations requested by this realized row.</summary>
            /// <returns>The row's lazy enumerator.</returns>
            public IEnumerator<SongCardViewModel> GetEnumerator()
            {
                for (var index = 0; index < Count; index++)
                {
                    yield return this[index];
                }
            }

            /// <inheritdoc />
            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
