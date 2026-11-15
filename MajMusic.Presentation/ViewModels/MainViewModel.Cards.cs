#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Maintains stable music-card instances independently of selected-song cover loading.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Shares bounded thumbnail requests among all card controls in this view.</summary>
        private CoverThumbnailCache? _cardCovers;
        /// <summary>Preserves one card for each currently browsed source-scoped music identity.</summary>
        private Dictionary<(string Id, string Source, string? Endpoint), SongCardViewModel> _cards = new();

        /// <summary>Gets current music cards in the same order as the browsed music collection.</summary>
        public IReadOnlyList<SongCardViewModel> CardTracks { get; private set; } = Array.Empty<SongCardViewModel>();

        /// <summary>Reconciles the browsed entries in one binding update while reusing unchanged card instances.</summary>
        private void RefreshCardTracks()
        {
            var next = new Dictionary<(string, string, string?), SongCardViewModel>();
            var ordered = new List<SongCardViewModel>(Tracks.Count);
            foreach (var track in Tracks)
            {
                var key = (track.Id, track.SourceId, track.EndpointId);
                if (_cards.TryGetValue(key, out var card))
                {
                    card.UpdateTrack(track);
                }
                else
                {
                    _cardCovers ??= new CoverThumbnailCache(_workspace, _lifetime.Token);
                    card = new SongCardViewModel(track, _cardCovers);
                }

                next[key] = card;
                ordered.Add(card);
            }

            foreach (var (key, card) in _cards)
            {
                if (!next.ContainsKey(key))
                {
                    card.Dispose();
                }
            }

            _cards = next;
            CardTracks = ordered;
            RefreshCardSelection();
            OnPropertyChanged(nameof(CardTracks));
            RefreshBrowseRows();
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
            foreach (var card in CardTracks)
            {
                card.IsSelected = SelectedTrack is { } selected && card.Track.Id == selected.Id
                    && card.Track.SourceId == selected.SourceId && card.Track.EndpointId == selected.EndpointId;
            }
        }

        /// <summary>Cancels thumbnail requests and waits for their media access before releasing decoded resources.</summary>
        /// <returns>A task representing completed thumbnail cleanup.</returns>
        private async Task DisposeCardsAsync()
        {
            foreach (var card in CardTracks)
            {
                card.Dispose();
            }

            _cards.Clear();
            CardTracks = Array.Empty<SongCardViewModel>();
            if (_cardCovers is not null)
            {
                await _cardCovers.DisposeAsync();
            }
        }
    }
}
