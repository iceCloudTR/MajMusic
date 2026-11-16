#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Chooses the persistent amount of space used by music-list cards.</summary>
    public enum SongCardLayout
    {
        /// <summary>Uses spacious artwork and complete music information.</summary>
        Loose = 0,
        /// <summary>Uses a shorter card while retaining the usual information.</summary>
        Compact = 1,
        /// <summary>Uses smaller cards to display more songs in the same viewport.</summary>
        Small = 2
    }
}
