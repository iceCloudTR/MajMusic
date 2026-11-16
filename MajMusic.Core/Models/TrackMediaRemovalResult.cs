#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Reports local-file removal without removing the song from its library or playlists.</summary>
    /// <param name="UpdatedTrack">The current descriptor with removed managed resources detached.</param>
    /// <param name="AudioRemoved">Whether the song's managed audio reference was removed.</param>
    /// <param name="CoverRemoved">Whether the song's managed artwork reference was removed.</param>
    /// <param name="DeletedFileCount">The number of physical objects removed after shared references were protected.</param>
    public sealed record TrackMediaRemovalResult(MusicTrack UpdatedTrack, bool AudioRemoved, bool CoverRemoved, int DeletedFileCount);
}
