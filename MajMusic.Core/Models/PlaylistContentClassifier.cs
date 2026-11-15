#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Classifies playlist contents without restricting what a local list may contain.</summary>
    public static class PlaylistContentClassifier
    {
        /// <summary>Classifies known origins while retaining a distinct state for wholly unavailable references.</summary>
        /// <param name="playlist">The local or cloud playlist to inspect.</param>
        /// <param name="tracks">Current library metadata indexed by stable entry identity.</param>
        /// <returns>The origin classification of available entries.</returns>
        /// <exception cref="ArgumentNullException">The playlist or track lookup is null.</exception>
        public static PlaylistContentKind Classify(MusicPlaylist playlist, IReadOnlyDictionary<string, MusicTrack> tracks)
        {
            return GetSummary(playlist, tracks).Kind;
        }

        /// <summary>Counts resolved origins and unavailable references without inspecting files or performing network access.</summary>
        /// <param name="playlist">The local or cloud playlist to inspect.</param>
        /// <param name="tracks">Current library metadata indexed by stable entry identity.</param>
        /// <returns>Resolved counts and unresolved references for an accurate presentation label.</returns>
        /// <exception cref="ArgumentNullException">The playlist or track lookup is null.</exception>
        public static PlaylistContentSummary GetSummary(MusicPlaylist playlist, IReadOnlyDictionary<string, MusicTrack> tracks)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            ArgumentNullException.ThrowIfNull(tracks);
            var local = 0;
            var online = 0;
            var unresolved = 0;
            foreach (var id in playlist.TrackIds)
            {
                if (!tracks.TryGetValue(id, out var track))
                {
                    unresolved++;
                }
                else if (track.RemoteId is not null)
                {
                    online++;
                }
                else
                {
                    local++;
                }
            }

            foreach (var entry in playlist.MajCollection?.Entries ?? Array.Empty<MajCollectionEntry>())
            {
                if (entry.TrackId is null)
                {
                    unresolved++;
                }
            }

            var kind = local > 0 && online > 0 ? PlaylistContentKind.Mixed
                : local > 0 ? PlaylistContentKind.Local
                : online > 0 ? PlaylistContentKind.Online
                : unresolved > 0 ? PlaylistContentKind.Unresolved : PlaylistContentKind.Empty;
            return new PlaylistContentSummary
            {
                Kind = kind,
                LocalCount = local,
                OnlineCount = online,
                UnresolvedCount = unresolved
            };
        }
    }
}
