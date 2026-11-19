#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Executes context-menu actions against captured songs and playlists independently of later selection changes.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Gets the opened playlist independently of the sidebar insertion target.</summary>
        public MusicPlaylist? BrowsedPlaylistForMenu => BrowsedPlaylist;

        /// <summary>Checks favorite membership for the captured song without changing the inspector.</summary>
        /// <param name="track">The song whose context menu is open.</param>
        /// <returns>Whether this song belongs to the effective favorites collection.</returns>
        public bool IsTrackFavoriteForMenu(MusicTrack track)
        {
            ArgumentNullException.ThrowIfNull(track);
            return IsTrackFavorite(track);
        }

        /// <summary>Checks whether the captured playlist accepts local membership edits.</summary>
        /// <param name="playlist">The playlist shown by the menu.</param>
        /// <returns>Whether an idle workspace can edit this current local list.</returns>
        public bool CanEditPlaylistForMenu(MusicPlaylist playlist)
        {
            return IsIdle && FindEditableContextPlaylist(playlist) is not null;
        }

        /// <summary>Checks whether a captured user-created local playlist can be renamed.</summary>
        /// <param name="playlist">The playlist shown by the menu.</param>
        /// <returns>Whether this is a current local list rather than built-in favorites.</returns>
        public bool CanRenamePlaylistForMenu(MusicPlaylist playlist)
        {
            return IsIdle && FindEditableContextPlaylist(playlist) is { IsFavorite: false } target && target.Id != FavoritesId;
        }

        /// <summary>Checks whether a captured user-created local playlist can be deleted.</summary>
        /// <param name="playlist">The playlist shown by the menu.</param>
        /// <returns>Whether the local deletion service can remove this current list.</returns>
        public bool CanDeletePlaylistForMenu(MusicPlaylist playlist)
        {
            return _workspace is IPlaylistManagementWorkspace && CanRenamePlaylistForMenu(playlist);
        }

        /// <summary>Checks membership removal for the captured song and actual opened playlist.</summary>
        /// <param name="track">The song whose context menu is open.</param>
        /// <param name="playlist">The playlist captured from the browsing view.</param>
        /// <returns>Whether the current editable list contains this song or a matching favorite alias.</returns>
        public bool CanRemoveTrackForMenu(MusicTrack track, MusicPlaylist? playlist)
        {
            return IsIdle && _workspace is IPlaylistManagementWorkspace && playlist is not null
                && FindEditableContextPlaylist(playlist) is { } current
                && (current.TrackIds.Contains(track.Id, StringComparer.Ordinal)
                    || (current.IsFavorite || current.Id == FavoritesId) && TrackMajHash(track) is { } hash && _favoriteHashes.Contains(hash));
        }

        /// <summary>Plays the captured song using the queue associated with its visible catalog at invocation.</summary>
        /// <param name="track">The exact song selected by the menu action.</param>
        /// <returns>A task representing the tracked audio command.</returns>
        public Task PlayTrackFromMenuAsync(MusicTrack track)
        {
            ArgumentNullException.ThrowIfNull(track);
            var entries = IsRecommendationsMode && _recommendationTracks is { } batch ? batch.ToArray() : Tracks.ToArray();
            var index = Array.FindIndex(entries, item => SameContextTrack(item, track));
            var owner = index >= 0 && IsRecommendationsMode ? CurrentRecommendationOwner() : null;
            if (index < 0)
            {
                entries = [track];
                index = 0;
            }

            CancelSeek();
            return RunTransportAsync(async () =>
            {
                SelectedTrack = track;
                await _playback.SetQueueAsync(entries, index, _lifetime.Token);
                OnPlaybackQueueStarted(owner, entries);
            });
        }

        /// <summary>Changes favorite membership for the captured song without moving the browsing selection.</summary>
        /// <param name="track">The exact song selected by the menu action.</param>
        /// <returns>A task representing local persistence and any configured collection synchronization.</returns>
        public Task ToggleTrackFavoriteFromMenuAsync(MusicTrack track)
        {
            ArgumentNullException.ThrowIfNull(track);
            return RunBusyAsync(() => ToggleFavoriteTrackCoreAsync(track));
        }

        /// <summary>Checks whether the captured online song can start a new download.</summary>
        /// <param name="track">The exact song and revision captured by the menu.</param>
        /// <returns>Whether the missing audio can be downloaded now.</returns>
        internal bool CanDownloadTrackForMenu(MusicTrack track)
        {
            return IsIdle && OnlineEnabled && track.RemoteId is not null
                && !HasDownloadedTrack(track) && !IsTrackDownloadInProgress(track);
        }

        /// <summary>Downloads the captured online song independently of later inspector selection changes.</summary>
        /// <param name="track">The exact song selected by the menu action.</param>
        /// <returns>A task representing the pinned download and refreshed visible availability.</returns>
        public Task DownloadTrackFromMenuAsync(MusicTrack track)
        {
            ArgumentNullException.ThrowIfNull(track);
            if (!CanDownloadTrackForMenu(track))
            {
                return Task.CompletedTask;
            }

            return RunBusyAsync(async () =>
            {
                await _workspace.ResolveAudioAsync(track, true, _lifetime.Token);
                await ReloadLibraryCoreAsync(_lifetime.Token, preservePage: true);
                Status = $"已在本地：{track.Title}";
            });
        }

        /// <summary>Adds the captured song to the captured local playlist using its newest membership.</summary>
        /// <param name="track">The exact song selected by the menu action.</param>
        /// <param name="playlist">The local destination captured from the submenu.</param>
        /// <returns>A task representing the membership update and presentation refresh.</returns>
        public Task AddTrackToPlaylistFromMenuAsync(MusicTrack track, MusicPlaylist playlist)
        {
            ArgumentNullException.ThrowIfNull(track);
            ArgumentNullException.ThrowIfNull(playlist);
            return RunBusyAsync(async () =>
            {
                if (FindEditableContextPlaylist(playlist) is not { } target)
                {
                    return;
                }

                if (!target.TrackIds.Contains(track.Id, StringComparer.Ordinal))
                {
                    await _workspace.SavePlaylistAsync(target with
                    {
                        TrackIds = target.TrackIds.Append(track.Id).ToArray(), UpdatedAt = DateTimeOffset.UtcNow
                    }, _lifetime.Token);
                    RestorePlaylistMirrorMember(target.Id, track);
                    await ReloadPlaylistsAsync();
                    if (IsContextPlaylistOpen(target))
                    {
                        await ReloadLibraryCoreAsync(_lifetime.Token, preservePage: true);
                    }
                }

                Status = $"已加入：{target.Name}";
            });
        }

        /// <summary>Removes the captured song from the captured opened list without using the insertion target.</summary>
        /// <param name="track">The exact song selected by the menu action.</param>
        /// <param name="playlist">The playlist actually browsed when the menu was opened.</param>
        /// <returns>A task representing the local membership update.</returns>
        public Task RemoveTrackFromPlaylistFromMenuAsync(MusicTrack track, MusicPlaylist playlist)
        {
            ArgumentNullException.ThrowIfNull(track);
            ArgumentNullException.ThrowIfNull(playlist);
            return RunBusyAsync(() => RemoveTrackFromPlaylistCoreAsync(playlist, track));
        }

        /// <summary>Opens the captured playlist while retaining the currently playing audio queue.</summary>
        /// <param name="playlist">The list selected by the context menu.</param>
        /// <returns>A task representing the playlist's content read.</returns>
        public Task OpenPlaylistFromMenuAsync(MusicPlaylist playlist)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            return RunBusyAsync(async () =>
            {
                if (FindContextPlaylist(playlist) is not { } target)
                {
                    return;
                }

                SelectedPlaylist = target;
                UseHomeMode(HomeBrowseMode.Playlists);
                await ReloadLibraryAsync();
            });
        }

        /// <summary>Starts the captured playlist in its saved order without borrowing another selected list.</summary>
        /// <param name="playlist">The list selected by the context menu.</param>
        /// <returns>A task representing the finite queue's first audio resource.</returns>
        public Task PlayPlaylistFromMenuAsync(MusicPlaylist playlist)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            var target = FindContextPlaylist(playlist);
            if (target is null)
            {
                return Task.CompletedTask;
            }

            var ids = target.TrackIds.ToArray();
            CancelSeek();
            return RunTransportAsync(async () =>
            {
                var library = (await _workspace.SearchAsync(null, _lifetime.Token)).ToDictionary(item => item.Id, StringComparer.Ordinal);
                var entries = ids.Where(library.ContainsKey).Select(id => library[id]).ToArray();
                if (entries.Length == 0)
                {
                    return;
                }

                await _playback.SetQueueAsync(entries, 0, _lifetime.Token);
                OnPlaybackQueueStarted(null, entries);
            });
        }

        /// <summary>Renames the captured user-created local list without modifying a source index file.</summary>
        /// <param name="playlist">The local list whose menu opened the name editor.</param>
        /// <param name="name">The new nonblank display name.</param>
        /// <returns>A task representing local persistence and projected list refresh.</returns>
        public Task RenamePlaylistFromMenuAsync(MusicPlaylist playlist, string name)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var renamed = name.Trim();
            return RunBusyAsync(async () =>
            {
                if (FindEditableContextPlaylist(playlist) is not { IsFavorite: false } target || target.Id == FavoritesId)
                {
                    return;
                }

                await _workspace.SavePlaylistAsync(target with { Name = renamed, UpdatedAt = DateTimeOffset.UtcNow }, _lifetime.Token);
                await ReloadPlaylistsAsync();
                Status = $"已重命名：{renamed}";
            });
        }

        /// <summary>Deletes the captured user-created local playlist without deleting music files.</summary>
        /// <param name="playlist">The exact local list selected by the context menu.</param>
        /// <returns>A task representing deletion and active-view repair.</returns>
        public Task DeletePlaylistFromMenuAsync(MusicPlaylist playlist)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            return RunBusyAsync(() => DeletePlaylistCoreAsync(playlist));
        }

        /// <summary>Exports the captured list to the explicitly chosen new document.</summary>
        /// <param name="playlist">The list selected when the file picker was opened.</param>
        /// <param name="filePath">The new destination chosen by the person.</param>
        /// <returns>A task representing the compatible document export.</returns>
        public Task ExportPlaylistFromMenuAsync(MusicPlaylist playlist, string filePath)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            return RunBusyAsync(async () =>
            {
                if (_collections is not null && FindContextPlaylist(playlist) is { } current)
                {
                    await _collections.ExportAsync(filePath, current, _lifetime.Token);
                    Status = "列表已导出";
                }
            });
        }

        /// <summary>Locates a current source-scoped playlist rather than trusting a menu's older metadata snapshot.</summary>
        /// <param name="playlist">The captured menu target.</param>
        /// <returns>The current projection, or null when it no longer belongs to the visible catalog.</returns>
        private MusicPlaylist? FindContextPlaylist(MusicPlaylist playlist)
        {
            var current = Playlists.FirstOrDefault(item => item.Id == playlist.Id && item.EndpointId == playlist.EndpointId && item.RemoteId == playlist.RemoteId);
            return current ?? (!playlist.IsCloud && ResolveLocalPlaylist(playlist) is { } local ? VisiblePlaylistForLocal(local) : null);
        }

        /// <summary>Resolves a current locally owned list for a membership or management action.</summary>
        /// <param name="playlist">The captured visible playlist.</param>
        /// <returns>The current editable local list, or null for a removed or remote-only collection.</returns>
        private MusicPlaylist? FindEditableContextPlaylist(MusicPlaylist playlist)
        {
            return FindContextPlaylist(playlist) is { } current ? ResolveLocalPlaylist(current) : null;
        }

        /// <summary>Checks the logical identity of the opened list, including a synchronized source projection.</summary>
        /// <param name="local">The editable local list affected by the operation.</param>
        /// <returns>Whether its local or MajNet presentation is currently being browsed.</returns>
        private bool IsContextPlaylistOpen(MusicPlaylist local)
        {
            return IsPlaylistsMode && BrowsedPlaylist is { } current && ResolveLocalPlaylist(current)?.Id == local.Id;
        }

        /// <summary>Matches a song independently of metadata refreshes and another account's similarly named entry.</summary>
        /// <param name="first">The first source-scoped song.</param>
        /// <param name="second">The second source-scoped song.</param>
        /// <returns>Whether both descriptors identify the same catalog entry.</returns>
        private static bool SameContextTrack(MusicTrack first, MusicTrack second)
        {
            return first.Id == second.Id && first.SourceId == second.SourceId && first.EndpointId == second.EndpointId;
        }
    }
}
