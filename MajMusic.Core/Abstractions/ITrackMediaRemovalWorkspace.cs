#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Removes application-owned local media without deleting imported music or collection membership.</summary>
    public interface ITrackMediaRemovalWorkspace
    {
        /// <summary>Removes only managed local resources belonging to the captured song revision.</summary>
        /// <param name="track">The source-scoped music entry selected by the user.</param>
        /// <param name="cancellationToken">Cancels waiting and local publication before its commit.</param>
        /// <returns>The retained music metadata and local-resource changes.</returns>
        Task<TrackMediaRemovalResult> RemoveTrackMediaAsync(MusicTrack track, CancellationToken cancellationToken = default);
    }
}
