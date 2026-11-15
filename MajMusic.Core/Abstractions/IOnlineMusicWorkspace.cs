#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Coordinates independently configured Maj account sessions and their online libraries.</summary>
    public interface IOnlineMusicWorkspace
    {
        /// <summary>Reads the current account state without making requests.</summary>
        /// <returns>One account state for each saved endpoint configuration.</returns>
        IReadOnlyList<EndpointSession> GetEndpointSessions();

        /// <summary>Logs an account into one endpoint configuration without persisting its password.</summary>
        /// <param name="endpoint">The saved endpoint configuration.</param>
        /// <param name="username">The account name.</param>
        /// <param name="password">The password used only for this login.</param>
        /// <param name="cancellationToken">Cancels login.</param>
        /// <returns>A task representing the completed account selection.</returns>
        Task LoginAsync(EndpointConfiguration endpoint, string username, string password, CancellationToken cancellationToken = default);

        /// <summary>Returns one endpoint configuration to an independent Guest session.</summary>
        /// <param name="endpoint">The saved endpoint configuration.</param>
        /// <param name="cancellationToken">Cancels the local account change.</param>
        /// <returns>A task representing the completed Guest selection.</returns>
        Task UseGuestAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default);

        /// <summary>Reads cloud lists containing only online references from the selected endpoint.</summary>
        /// <param name="endpoint">The saved endpoint configuration.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The endpoint account's cloud-list snapshots.</returns>
        Task<IReadOnlyList<MusicPlaylist>> GetCloudPlaylistsAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default);

        /// <summary>Reads and indexes one finite recommendation page for the selected account.</summary>
        /// <param name="endpoint">The saved endpoint configuration.</param>
        /// <param name="page">The zero-based page number.</param>
        /// <param name="pageSize">The requested page size, between one and one hundred.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The online music entries in the requested page.</returns>
        Task<IReadOnlyList<MusicTrack>> GetRecommendationsAsync(EndpointConfiguration endpoint, int page = 0, int pageSize = 50, CancellationToken cancellationToken = default);

        /// <summary>Reads optional per-track metadata without downloading its audio.</summary>
        /// <param name="track">The indexed source entry.</param>
        /// <param name="cancellationToken">Cancels the metadata read.</param>
        /// <returns>The updated entry, or its existing metadata when no information is available.</returns>
        Task<MusicTrack> EnrichTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default);

        /// <summary>Persists parsed playback information only when the indexed source revision still matches.</summary>
        /// <param name="track">The entry containing the parsed duration and audio properties.</param>
        /// <param name="cancellationToken">Cancels the update.</param>
        /// <returns>A task representing the metadata merge.</returns>
        Task UpdateTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default);
    }
}
