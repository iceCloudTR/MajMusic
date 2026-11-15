#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Edits locally owned playlist membership independently of music-file ownership.</summary>
    public interface IPlaylistManagementWorkspace
    {
        /// <summary>Deletes a local playlist and remembers the deletion during subsequent index discovery.</summary>
        /// <param name="playlist">The selected local playlist snapshot.</param>
        /// <param name="cancellationToken">Cancels the local write.</param>
        /// <returns>A task representing the completed deletion.</returns>
        /// <exception cref="ArgumentNullException">The playlist is null.</exception>
        /// <exception cref="InvalidOperationException">The playlist is cloud-owned or is the built-in favorites collection.</exception>
        Task DeletePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default);

        /// <summary>Removes a song from a local playlist, including its matching portable references.</summary>
        /// <param name="playlistId">The local playlist identity currently being browsed.</param>
        /// <param name="track">The selected song; favorites also remove aliases with the same portable identity.</param>
        /// <param name="cancellationToken">Cancels the local read or write.</param>
        /// <returns>A task representing the completed membership update.</returns>
        /// <exception cref="ArgumentException">The playlist identity is blank.</exception>
        /// <exception cref="ArgumentNullException">The song is null.</exception>
        /// <exception cref="InvalidOperationException">The playlist is missing or cloud-owned.</exception>
        Task RemoveFromPlaylistAsync(string playlistId, MusicTrack track, CancellationToken cancellationToken = default);

        /// <summary>Checks whether automatic index discovery should retain a previous local deletion.</summary>
        /// <param name="playlistId">The playlist identity derived from its source document.</param>
        /// <param name="cancellationToken">Cancels the local read.</param>
        /// <returns>Whether the playlist was deleted locally and has not been explicitly restored.</returns>
        /// <exception cref="ArgumentException">The playlist identity is blank.</exception>
        Task<bool> IsPlaylistDeletedAsync(string playlistId, CancellationToken cancellationToken = default);
    }
}
