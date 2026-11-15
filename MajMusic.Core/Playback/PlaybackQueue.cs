#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Playback
{
    /// <summary>Maintains a stable play order and bounded navigation history independently of audio decoding.</summary>
    /// <remarks>A playback service serializes mutations; this type is not independently thread-safe.</remarks>
    public sealed class PlaybackQueue
    {
        /// <summary>Limits retained navigation history during long-running playback.</summary>
        private const int MaximumHistoryLength = 256;

        /// <summary>Supplies randomness only when a new shuffle order is requested.</summary>
        private readonly Random _random;

        /// <summary>Retains entries in their original playlist order.</summary>
        private MusicTrack[] _tracks = Array.Empty<MusicTrack>();

        /// <summary>Exposes the original entries without exposing a mutable array.</summary>
        private IReadOnlyList<MusicTrack> _trackView = Array.Empty<MusicTrack>();

        /// <summary>Maps the stable playback order to original entry indexes.</summary>
        private int[] _order = Array.Empty<int>();

        /// <summary>Records visited original entry indexes, including repeated entries.</summary>
        private readonly List<int> _history = new();

        /// <summary>Identifies the current entry in the stable play order.</summary>
        private int _position = -1;

        /// <summary>Identifies the currently selected history entry.</summary>
        private int _historyPosition = -1;

        /// <summary>Indicates whether entries follow a shuffled stable order.</summary>
        private bool _shuffle;

        /// <summary>Creates an empty queue.</summary>
        /// <param name="random">An optional deterministic source for shuffling.</param>
        public PlaybackQueue(Random? random = null)
        {
            _random = random ?? Random.Shared;
        }

        /// <summary>Gets the current entry, or null when the queue is empty.</summary>
        public MusicTrack? Current => _position < 0 ? null : _tracks[_order[_position]];

        /// <summary>Gets the original immutable view of the queue entries.</summary>
        public IReadOnlyList<MusicTrack> Tracks => _trackView;

        /// <summary>Gets or sets the automatic repeat behavior.</summary>
        public RepeatMode RepeatMode { get; set; }

        /// <summary>Gets or sets shuffle while preserving the current entry and past navigation.</summary>
        public bool Shuffle
        {
            get => _shuffle;
            set
            {
                if (_shuffle == value)
                {
                    return;
                }

                var currentIndex = GetCurrentIndex();
                _shuffle = value;
                RebuildOrder(currentIndex);
                RemoveForwardHistory();
            }
        }

        /// <summary>Replaces the queue and selects its starting entry.</summary>
        /// <param name="tracks">The entries to copy into the queue.</param>
        /// <param name="startIndex">The original playlist index to select.</param>
        /// <exception cref="ArgumentNullException"><paramref name="tracks"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The starting index is outside a nonempty queue, or is not zero for an empty queue.</exception>
        /// <exception cref="ArgumentException">An entry is null.</exception>
        public void SetTracks(IEnumerable<MusicTrack> tracks, int startIndex = 0)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            var entries = tracks.ToArray();
            if (startIndex < 0 || (entries.Length == 0 ? startIndex != 0 : startIndex >= entries.Length))
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex));
            }

            if (Array.Exists(entries, static entry => entry is null))
            {
                throw new ArgumentException("Queue entries cannot be null.", nameof(tracks));
            }

            _tracks = entries;
            _trackView = Array.AsReadOnly(entries);
            _history.Clear();
            _historyPosition = -1;
            RebuildOrder(entries.Length == 0 ? -1 : startIndex);
            if (entries.Length > 0)
            {
                RecordVisit(startIndex);
            }
        }

        /// <summary>Moves forward using the existing order or forward navigation history.</summary>
        /// <param name="automatic">Whether completion of the current track triggered navigation.</param>
        /// <returns>The selected entry, or null when playback should stop.</returns>
        /// <remarks>Manual navigation always advances even when single-track repeat is enabled.</remarks>
        public MusicTrack? MoveNext(bool automatic = false)
        {
            if (_position < 0)
            {
                return null;
            }

            if (automatic && RepeatMode == RepeatMode.One)
            {
                return Current;
            }

            if (_historyPosition + 1 < _history.Count)
            {
                _historyPosition++;
                _position = Array.IndexOf(_order, _history[_historyPosition]);
                return Current;
            }

            var nextPosition = _position + 1;
            if (nextPosition >= _order.Length)
            {
                if (RepeatMode != RepeatMode.All)
                {
                    return null;
                }

                nextPosition = 0;
            }

            _position = nextPosition;
            RecordVisit(_order[_position]);
            return Current;
        }

        /// <summary>Returns to an earlier visited entry, or wraps at the beginning when repeating the queue.</summary>
        /// <returns>The selected entry, or null when no previous entry exists.</returns>
        public MusicTrack? MovePrevious()
        {
            if (_position < 0)
            {
                return null;
            }

            if (_historyPosition > 0)
            {
                _historyPosition--;
                _position = Array.IndexOf(_order, _history[_historyPosition]);
                return Current;
            }

            var previousPosition = _position - 1;
            if (previousPosition < 0)
            {
                if (RepeatMode != RepeatMode.All)
                {
                    return null;
                }

                previousPosition = _order.Length - 1;
            }

            _position = previousPosition;
            _history.Insert(0, _order[_position]);
            if (_history.Count > MaximumHistoryLength)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            _historyPosition = 0;
            return Current;
        }

        /// <summary>Copies navigation state so an unsuccessful audio operation can roll it back.</summary>
        /// <returns>An isolated navigation snapshot.</returns>
        internal QueueState CaptureState()
        {
            return new QueueState(_tracks, _order.ToArray(), _history.ToArray(), _position, _historyPosition, _shuffle, RepeatMode);
        }

        /// <summary>Restores navigation after a failed or canceled audio operation.</summary>
        /// <param name="state">The previously captured state.</param>
        internal void RestoreState(QueueState state)
        {
            _tracks = state.Tracks;
            _trackView = Array.AsReadOnly(_tracks);
            _order = state.Order;
            _history.Clear();
            _history.AddRange(state.History);
            _position = state.Position;
            _historyPosition = state.HistoryPosition;
            _shuffle = state.Shuffle;
            RepeatMode = state.RepeatMode;
        }

        /// <summary>Finds the selected original index.</summary>
        /// <returns>The selected original index, or minus one for an empty queue.</returns>
        private int GetCurrentIndex()
        {
            return _position < 0 ? -1 : _order[_position];
        }

        /// <summary>Creates one stable order and preserves the selected original entry.</summary>
        /// <param name="currentIndex">The original index to preserve.</param>
        private void RebuildOrder(int currentIndex)
        {
            _order = Enumerable.Range(0, _tracks.Length).ToArray();
            if (_shuffle && _order.Length > 0)
            {
                (_order[0], _order[currentIndex]) = (_order[currentIndex], _order[0]);
                for (var index = _order.Length - 1; index > 1; index--)
                {
                    var other = _random.Next(1, index + 1);
                    (_order[index], _order[other]) = (_order[other], _order[index]);
                }
            }

            _position = currentIndex < 0 ? -1 : Array.IndexOf(_order, currentIndex);
        }

        /// <summary>Appends a visit and bounds retained history.</summary>
        /// <param name="trackIndex">The visited original entry index.</param>
        private void RecordVisit(int trackIndex)
        {
            RemoveForwardHistory();
            _history.Add(trackIndex);
            if (_history.Count > MaximumHistoryLength)
            {
                _history.RemoveAt(0);
            }

            _historyPosition = _history.Count - 1;
        }

        /// <summary>Removes navigation entries invalidated by a new route.</summary>
        private void RemoveForwardHistory()
        {
            var firstForwardEntry = _historyPosition + 1;
            if (firstForwardEntry < _history.Count)
            {
                _history.RemoveRange(firstForwardEntry, _history.Count - firstForwardEntry);
            }
        }

        /// <summary>Contains an isolated snapshot used by the owning playback service.</summary>
        /// <param name="Tracks">The immutable original entry array.</param>
        /// <param name="Order">The copied order.</param>
        /// <param name="History">The copied history.</param>
        /// <param name="Position">The current order position.</param>
        /// <param name="HistoryPosition">The current history position.</param>
        /// <param name="Shuffle">Whether shuffle is enabled.</param>
        /// <param name="RepeatMode">The repeat behavior.</param>
        internal sealed record QueueState(MusicTrack[] Tracks, int[] Order, int[] History, int Position, int HistoryPosition, bool Shuffle, RepeatMode RepeatMode);
    }
}
