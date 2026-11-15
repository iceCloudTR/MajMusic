#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Playback
{
    /// <summary>Extends an owned finite feed without issuing a new audio or navigation intent.</summary>
    public sealed partial class PlaybackService
    {
        /// <summary>Adds a matching batch continuation while preserving audio, navigation history and pending preparation.</summary>
        /// <param name="expectedPrefix">The queue's read-only Tracks view captured when its recommendation batch started playing.</param>
        /// <param name="extendedTracks">The same source-scoped prefix followed by additional available recommendations.</param>
        /// <returns>Whether the active nonempty queue still matched the prefix and was extended.</returns>
        /// <exception cref="ArgumentNullException">Either collection is null.</exception>
        /// <exception cref="ArgumentException">Either collection contains a null entry.</exception>
        /// <exception cref="ObjectDisposedException">The playback service has closed.</exception>
        public bool TryExtendQueue(IReadOnlyList<MusicTrack> expectedPrefix, IReadOnlyList<MusicTrack> extendedTracks)
        {
            ArgumentNullException.ThrowIfNull(expectedPrefix);
            ArgumentNullException.ThrowIfNull(extendedTracks);
            var expected = expectedPrefix.ToArray();
            var extended = extendedTracks.ToArray();
            if (Array.Exists(expected, static entry => entry is null) || Array.Exists(extended, static entry => entry is null))
            {
                throw new ArgumentException("Queue entries cannot be null.");
            }

            lock (_transitionGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (!Queue.Owns(expectedPrefix) || !Queue.TryExtend(expected, extended))
                {
                    return false;
                }

                var committed = new PlaybackQueue();
                committed.RestoreState(_committedQueue);
                committed.TryExtend(expected, extended);
                _committedQueue = committed.CaptureState();
                return true;
            }
        }
    }
}
