#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Shares one protected library snapshot across an explicitly requested local-media cleanup.</summary>
    public interface ITrackMediaRemovalBatchWorkspace
    {
        /// <summary>Removes only managed files belonging to the captured source revisions without repeatedly reading the complete library.</summary>
        /// <param name="tracks">The songs selected by the user's explicit cleanup range.</param>
        /// <param name="cancellationToken">Cancels waiting and subsequent removals.</param>
        /// <returns>The retained source metadata and completed local-resource changes.</returns>
        Task<IReadOnlyList<TrackMediaRemovalResult>> RemoveTrackMediaBatchAsync(IReadOnlyList<MusicTrack> tracks,
            CancellationToken cancellationToken = default);
    }
}
