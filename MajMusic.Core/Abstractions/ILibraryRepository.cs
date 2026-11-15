#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Persists searchable metadata and local playlists independently of a user interface.</summary>
    public interface ILibraryRepository
    {
        /// <summary>Finds locally indexed tracks matching a literal keyword.</summary>
        /// <param name="query">The keyword, or null to return all tracks.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The matching tracks in stable title order.</returns>
        Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default);

        /// <summary>Atomically inserts or updates metadata without removing other sources.</summary>
        /// <param name="tracks">The tracks to persist.</param>
        /// <param name="cancellationToken">Cancels the transaction.</param>
        /// <returns>A task representing the completed write.</returns>
        Task UpsertTracksAsync(IReadOnlyList<MusicTrack> tracks, CancellationToken cancellationToken = default);

        /// <summary>Loads all local playlists.</summary>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The saved playlists.</returns>
        Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);

        /// <summary>Creates or updates a local playlist.</summary>
        /// <param name="playlist">The playlist to persist.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task representing the completed write.</returns>
        Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default);

        /// <summary>Removes a playlist without deleting its tracks or media.</summary>
        /// <param name="id">The playlist identity.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task representing the completed write.</returns>
        Task DeletePlaylistAsync(string id, CancellationToken cancellationToken = default);
    }
}
