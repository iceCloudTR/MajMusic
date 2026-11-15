#nullable enable

namespace MajMusic.Core.Playback
{
    /// <summary>Specifies what automatic playback does at a track or queue boundary.</summary>
    public enum RepeatMode
    {
        /// <summary>Stops when the queue reaches its end.</summary>
        None,

        /// <summary>Returns to the beginning after the final queue entry.</summary>
        All,

        /// <summary>Repeats the current track when it finishes automatically.</summary>
        One
    }
}
