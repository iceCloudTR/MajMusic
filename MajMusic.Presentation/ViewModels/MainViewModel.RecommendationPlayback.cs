#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Keeps a playing recommendation batch continuous when its remaining pages arrive.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Identifies the account whose recommendation queue was explicitly started.</summary>
        private string? _recommendationPlaybackOwner;
        /// <summary>Retains the exact ordered prefix installed in the audio queue.</summary>
        private IReadOnlyList<MusicTrack>? _recommendationPlaybackPrefix;

        /// <summary>Records the successfully started queue and catches up with pages received during audio preparation.</summary>
        /// <param name="owner">The recommendation account, or null for a local or search queue.</param>
        /// <param name="entries">The immutable membership passed to the playback service.</param>
        private void OnPlaybackQueueStarted(string? owner, IReadOnlyList<MusicTrack> entries)
        {
            var queue = _playback.Queue.Tracks;
            _recommendationPlaybackOwner = owner is not null && queue.SequenceEqual(entries) ? owner : null;
            _recommendationPlaybackPrefix = _recommendationPlaybackOwner is null ? null : queue;
            if (_recommendationPlaybackOwner is not null)
            {
                TryExtendRecommendationPlayback(_recommendationPlaybackOwner);
            }
        }

        /// <summary>Extends only the explicitly playing account's matching batch without reopening its audio.</summary>
        /// <param name="owner">The account whose next cached page has just become available.</param>
        private void TryExtendRecommendationPlayback(string owner)
        {
            if (_recommendationPlaybackOwner != owner || _recommendationPlaybackPrefix is not { } prefix
                || !_homeSnapshots.TryGetValue(owner, out var snapshot) || snapshot.Tracks.Count <= prefix.Count
                || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            var extended = snapshot.Tracks.ToArray();
            if (_playback.TryExtendQueue(prefix, extended))
            {
                _recommendationPlaybackPrefix = _playback.Queue.Tracks;
            }
        }
    }
}
