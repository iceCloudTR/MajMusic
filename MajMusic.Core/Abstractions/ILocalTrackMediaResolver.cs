#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Discovers completed local media from Music-owned media storage without making network requests.</summary>
    public interface ILocalTrackMediaResolver
    {
        /// <summary>Refreshes local media paths for one known source revision while retaining its online identity.</summary>
        /// <param name="track">The captured music entry and source revision.</param>
        /// <param name="cancellationToken">Cancels local discovery and validation.</param>
        /// <returns>The same source entry with verified completed local resources, or the original entry on a miss.</returns>
        Task<MusicTrack> ResolveLocalTrackAsync(MusicTrack track, CancellationToken cancellationToken = default);
    }
}
