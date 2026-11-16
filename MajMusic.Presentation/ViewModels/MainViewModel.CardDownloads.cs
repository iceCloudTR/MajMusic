#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Retains each song revision's download fill independently of the current query and selection.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Preserves completed and active operations while their songs leave the visible query.</summary>
        private readonly Dictionary<CardDownloadOwner, SongDownloadState> _cardDownloadStates = new();

        /// <summary>Applies one operation only to cards belonging to the same source revision.</summary>
        /// <param name="progress">The audio-only transfer notification marshaled to the presentation thread.</param>
        private void ApplyCardDownloadProgress(AudioDownloadProgress progress)
        {
            var matching = _cards.Values.Where(card => MatchesCardDownload(card.Track, progress)).ToArray();
            var owner = progress.SourceId is { } source
                ? new CardDownloadOwner(progress.TrackId, source, progress.EndpointId, progress.Revision)
                : Tracks.FirstOrDefault(track => MatchesCardDownload(track, progress)) is { } first ? DownloadOwner(first) : null;
            if (owner is null)
            {
                return;
            }

            if (!_cardDownloadStates.TryGetValue(owner, out var state))
            {
                state = new SongDownloadState();
                _cardDownloadStates.Add(owner, state);
            }

            if (state.Progress is { } previous && previous.OperationId != progress.OperationId
                && progress.State != AudioDownloadState.Preparing)
            {
                return;
            }

            state.Progress = progress;
            state.Version++;
            if (progress.State == AudioDownloadState.Downloading)
            {
                state.IsAvailable = false;
            }
            else if (progress.State == AudioDownloadState.Completed)
            {
                state.IsAvailable = true;
                state.LocalPath = progress.LocalAudioPath ?? state.LocalPath;
            }

            foreach (var card in matching)
            {
                card.RestoreDownloadState(state);
                card.NotifyDownloadState();
            }

            ObserveDownloadedMetadata(owner, progress);
        }

        /// <summary>Restores a card's retained operation without performing any filesystem work for an invisible song.</summary>
        /// <param name="card">The stable card instance for the current query.</param>
        private void RestoreCardDownloadProgress(SongCardViewModel card)
        {
            var owner = DownloadOwner(card.Track);
            if (!_cardDownloadStates.TryGetValue(owner, out var state))
            {
                state = new SongDownloadState { LocalPath = card.Track.Audio.LocalPath };
                _cardDownloadStates.Add(owner, state);
            }

            card.RestoreDownloadState(state);
        }

        /// <summary>Invalidates cached completion after cleanup and checks only currently displayed audio paths.</summary>
        /// <returns>A task representing the visible cards' canceled-or-completed rechecks.</returns>
        private async Task ResetCardDownloadProgress()
        {
            foreach (var state in _cardDownloadStates.Values)
            {
                state.Version++;
            }

            var checks = new List<Task>();
            foreach (var card in _cards.Values.ToArray())
            {
                card.NotifyDownloadState();
                checks.Add(card.RecheckDownloadAvailabilityAsync());
            }

            await Task.WhenAll(checks);
        }

        /// <summary>Checks requested track identity and, when available, its complete source ownership.</summary>
        /// <param name="track">The current browsed music revision.</param>
        /// <param name="progress">The source-scoped operation notification.</param>
        /// <returns>Whether this operation belongs to the current card.</returns>
        private static bool MatchesCardDownload(MusicTrack track, AudioDownloadProgress progress)
        {
            return track.Id == progress.TrackId && (progress.SourceId is null || track.SourceId == progress.SourceId
                && track.EndpointId == progress.EndpointId && track.Revision == progress.Revision);
        }

        /// <summary>Gets the full ownership identity used by a stable music card.</summary>
        /// <param name="track">The current immutable music descriptor.</param>
        /// <returns>The track's source and revision ownership.</returns>
        private static CardDownloadOwner DownloadOwner(MusicTrack track)
        {
            return new CardDownloadOwner(track.Id, track.SourceId, track.EndpointId, track.Revision);
        }

        /// <summary>Separates download state belonging to different servers, accounts routes or revisions.</summary>
        /// <param name="TrackId">The requested music identity.</param>
        /// <param name="SourceId">The indexed source identity.</param>
        /// <param name="EndpointId">The independently configured endpoint.</param>
        /// <param name="Revision">The exact source revision.</param>
        private sealed record CardDownloadOwner(string TrackId, string SourceId, string? EndpointId, string? Revision);
    }
}
