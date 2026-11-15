#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Collections;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Owns local playlist deletion and portable membership updates without changing source files.</summary>
    public sealed partial class MusicWorkspace : IPlaylistManagementWorkspace
    {
        /// <inheritdoc />
        public async Task DeletePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            EnsureDeletablePlaylist(playlist);
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = (await _library.GetPlaylistsAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(item => item.Id == playlist.Id);
                if (current is not null)
                {
                    EnsureDeletablePlaylist(current);
                    await _library.DeletePlaylistAsync(current.Id, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task RemoveFromPlaylistAsync(string playlistId, MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
            ArgumentNullException.ThrowIfNull(track);
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var playlist = (await _library.GetPlaylistsAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(item => item.Id == playlistId)
                    ?? throw new InvalidOperationException("播放列表已不存在。");
                if (playlist.IsCloud)
                {
                    throw new InvalidOperationException("云端歌单由接入点管理。");
                }

                var library = await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false);
                var current = library.FirstOrDefault(item => item.Id == track.Id) ?? track;
                var hash = GetPlaylistTrackHash(current)
                    ?? playlist.MajCollection?.Entries.FirstOrDefault(entry => entry.TrackId == track.Id)?.Hash;
                var favorites = playlist.IsFavorite || playlist.Id == MajCollectionService.FavoritesPlaylistId;
                var removedIds = new HashSet<string>(StringComparer.Ordinal) { track.Id };
                if (favorites && hash is not null)
                {
                    foreach (var alias in library.Where(item => GetPlaylistTrackHash(item) == hash))
                    {
                        removedIds.Add(alias.Id);
                    }
                }

                var updated = playlist with
                {
                    TrackIds = playlist.TrackIds.Where(id => !removedIds.Contains(id)).ToArray(),
                    MajCollection = playlist.MajCollection is { } definition ? definition with
                    {
                        Entries = definition.Entries.Where(entry => (entry.TrackId is null || !removedIds.Contains(entry.TrackId))
                            && !(hash is not null && entry.Hash == hash && (favorites || entry.TrackId is null))).ToArray()
                    } : null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await _library.SavePlaylistAsync(updated, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        /// <inheritdoc />
        public Task<bool> IsPlaylistDeletedAsync(string playlistId, CancellationToken cancellationToken = default)
        {
            return _library.IsPlaylistDeletedAsync(playlistId, cancellationToken);
        }

        /// <summary>Rejects whole-list deletion of remote collections and the fixed favorites collection.</summary>
        /// <param name="playlist">The list whose ownership is checked.</param>
        /// <exception cref="InvalidOperationException">The list is cloud-owned or built-in.</exception>
        private static void EnsureDeletablePlaylist(MusicPlaylist playlist)
        {
            if (playlist.IsCloud || playlist.IsFavorite || playlist.Id == MajCollectionService.FavoritesPlaylistId)
            {
                throw new InvalidOperationException(playlist.IsCloud ? "云端歌单由接入点管理。" : "我喜欢保留为内置歌单。");
            }
        }

        /// <summary>Reads the portable identity used to keep favorite aliases and retained Maj entries consistent.</summary>
        /// <param name="track">The current library entry.</param>
        /// <returns>The portable chart identity, or null for independent audio files.</returns>
        private static string? GetPlaylistTrackHash(MusicTrack track)
        {
            return track.MajChartHash ?? (track.RemoteId is not null ? track.Revision : null);
        }
    }
}
