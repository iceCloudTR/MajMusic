#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Coordinates local music management and explicitly enabled sources.</summary>
    public interface IMusicWorkspace
    {
        /// <summary>Gets whether network requests are currently permitted.</summary>
        bool IsOnline { get; }

        /// <summary>Gets the user data directory shown in settings.</summary>
        string DataDirectory { get; }

        /// <summary>Reads preferences without contacting a source.</summary>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The saved preferences or offline defaults.</returns>
        Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);

        /// <summary>Saves preferences and applies network permission.</summary>
        /// <param name="settings">The non-secret preferences.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task representing the completed write.</returns>
        Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

        /// <summary>Changes network permission without saving preferences.</summary>
        /// <param name="enabled">Whether explicit network operations are allowed.</param>
        void SetOnlineEnabled(bool enabled);

        /// <summary>Searches the persisted local metadata index.</summary>
        /// <param name="query">The optional literal keyword.</param>
        /// <param name="cancellationToken">Cancels the query.</param>
        /// <returns>The matching entries.</returns>
        Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default);

        /// <summary>Indexes the original music and artwork without copying or changing imported files.</summary>
        /// <param name="directory">The selected source directory.</param>
        /// <param name="cancellationToken">Cancels the import.</param>
        /// <returns>The imported track.</returns>
        Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default);

        /// <summary>Recursively indexes a selected directory while referencing its original audio and covers read-only.</summary>
        /// <param name="directory">The root directory selected by the user.</param>
        /// <param name="cancellationToken">Cancels scanning before committing the index transaction.</param>
        /// <returns>The completed scan with discovered tracks and skipped-path explanations.</returns>
        Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default);

        /// <summary>Reads local playlists.</summary>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The saved playlists.</returns>
        Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);

        /// <summary>Persists a local playlist.</summary>
        /// <param name="playlist">The playlist to save.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task representing the completed write.</returns>
        Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default);

        /// <summary>Explicitly synchronizes metadata from a configured source.</summary>
        /// <param name="endpoint">The user-configured endpoint.</param>
        /// <param name="cancellationToken">Cancels synchronization.</param>
        /// <returns>The number of received entries.</returns>
        Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default);

        /// <summary>Resolves a cover to a local file.</summary>
        /// <param name="track">The selected track.</param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>The cover path, or null if unavailable offline.</returns>
        Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default);

        /// <summary>Resolves audio to a complete local file.</summary>
        /// <param name="track">The track to resolve.</param>
        /// <param name="keepOffline">Whether to protect the download from cache cleanup.</param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>The complete local audio path.</returns>
        Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default);

        /// <summary>Explicitly removes managed local online media while preserving imported source files and library entries.</summary>
        /// <param name="cancellationToken">Cancels cleanup.</param>
        /// <returns>A task representing the completed cleanup.</returns>
        Task ClearCacheAsync(CancellationToken cancellationToken = default);
    }
}
