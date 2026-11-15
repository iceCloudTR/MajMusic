#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Edits local playlists while keeping the browsed list separate from the sidebar insertion target.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Gets the command deleting the local playlist selected on the management page.</summary>
        public AsyncCommand DeletePlaylistCommand { get; private set; } = null!;
        /// <summary>Gets the command removing the inspected song from the playlist currently being browsed.</summary>
        public AsyncCommand RemoveFromPlaylistCommand { get; private set; } = null!;
        /// <summary>Gets whether the selected management-page playlist can be deleted.</summary>
        public bool CanDeleteSelectedPlaylist => IsIdle && _workspace is IPlaylistManagementWorkspace
            && SelectedPlaylist is { } selected && ResolveLocalPlaylist(selected) is { IsFavorite: false } playlist && playlist.Id != FavoritesId;
        /// <summary>Gets whether the current home mode browses an editable local playlist.</summary>
        public bool IsLocalPlaylistView => IsPlaylistsMode && BrowsedPlaylist is { } visible && ResolveLocalPlaylist(visible) is not null;
        /// <summary>Gets whether the inspected song belongs to an editable playlist currently being browsed.</summary>
        public bool CanRemoveSelectedPlaylistTrack => IsIdle && _workspace is IPlaylistManagementWorkspace && IsPlaylistsMode
            && BrowsedPlaylist is { } visible && ResolveLocalPlaylist(visible) is { } playlist && SelectedTrack is { } track
            && (playlist.TrackIds.Contains(track.Id, StringComparer.Ordinal)
                || (playlist.IsFavorite || playlist.Id == FavoritesId) && TrackMajHash(track) is { } hash && _favoriteHashes.Contains(hash));

        /// <summary>Gets the actual browsed playlist without falling back to the sidebar's insertion target.</summary>
        private MusicPlaylist? BrowsedPlaylist => Playlists.FirstOrDefault(item => item.Id == _activePlaylistId);

        /// <summary>Creates lifetime-bound local playlist editing commands.</summary>
        private void InitializePlaylistManagementCommands()
        {
            DeletePlaylistCommand = CreateCommand(DeleteSelectedPlaylistAsync, () => CanDeleteSelectedPlaylist);
            RemoveFromPlaylistCommand = CreateCommand(RemoveSelectedPlaylistTrackAsync, () => CanRemoveSelectedPlaylistTrack);
        }

        /// <summary>Deletes the selected local list and repairs an active home view when that list was open.</summary>
        /// <returns>A task representing deletion and presentation refresh.</returns>
        private Task DeleteSelectedPlaylistAsync()
        {
            return SelectedPlaylist is { } selected ? DeletePlaylistCoreAsync(selected) : Task.CompletedTask;
        }

        /// <summary>Deletes one captured editable logical list without deleting unrelated source collections.</summary>
        /// <param name="visible">The current local or verified source presentation targeted by the user.</param>
        /// <returns>A task representing local deletion and active-view repair.</returns>
        private async Task DeletePlaylistCoreAsync(MusicPlaylist visible)
        {
            if (_workspace is not IPlaylistManagementWorkspace management
                || ResolveLocalPlaylist(visible) is not { IsFavorite: false } playlist || playlist.Id == FavoritesId)
            {
                return;
            }

            var wasOpen = IsLocalPlaylistOpen(playlist.Id);
            var version = _homeVersion;
            await management.DeletePlaylistAsync(playlist, _lifetime.Token);
            _pendingMirrorRemovals.Remove(playlist.Id);
            foreach (var endpointId in _cloudPlaylists.Keys.ToArray())
            {
                _cloudPlaylists[endpointId] = _cloudPlaylists[endpointId].Where(item => item.LocalMirrorId != playlist.Id).ToArray();
            }

            await ReloadPlaylistsAsync();
            if (wasOpen && IsPlaylistsMode && version == _homeVersion)
            {
                var insertionTarget = SelectedPlaylist;
                SelectedPlaylist = _favorites is { } favorites ? VisiblePlaylistForLocal(favorites) : null;
                UseHomeMode(HomeBrowseMode.Playlists);
                if (insertionTarget is not null && Playlists.Any(item => item.Id == insertionTarget.Id))
                {
                    SelectedPlaylist = insertionTarget;
                }

                await ReloadLibraryAsync();
            }

            Status = $"已删除歌单：{playlist.Name}";
        }

        /// <summary>Removes a song from the active home playlist without touching the unrelated insertion target.</summary>
        /// <returns>A task representing the local membership edit and refreshed contents.</returns>
        private Task RemoveSelectedPlaylistTrackAsync()
        {
            return IsPlaylistsMode && BrowsedPlaylist is { } visible && SelectedTrack is { } track
                ? RemoveTrackFromPlaylistCoreAsync(visible, track) : Task.CompletedTask;
        }

        /// <summary>Removes a captured song from its captured logical list without moving another inspector selection.</summary>
        /// <param name="visible">The local or verified source presentation actually targeted by the user.</param>
        /// <param name="track">The captured song or portable favorite alias.</param>
        /// <returns>A task representing preserved membership, local removal and current-view refresh.</returns>
        private async Task RemoveTrackFromPlaylistCoreAsync(MusicPlaylist visible, MusicTrack track)
        {
            if (_workspace is not IPlaylistManagementWorkspace management || ResolveLocalPlaylist(visible) is not { } playlist)
            {
                return;
            }

            await EnsureLogicalPlaylistMembersPersistedAsync(playlist);
            await management.RemoveFromPlaylistAsync(playlist.Id, track, _lifetime.Token);
            RememberPlaylistMirrorRemoval(playlist, track);
            await ReloadPlaylistsAsync();
            if (IsLocalPlaylistOpen(playlist.Id))
            {
                await ReloadLibraryAsync();
            }

            Status = $"已从{playlist.Name}移除";
        }

        /// <summary>Refreshes bindings and command availability after selection, mode, busy-state or membership changes.</summary>
        private void RefreshPlaylistManagementCommands()
        {
            OnPropertyChanged(nameof(CanDeleteSelectedPlaylist));
            OnPropertyChanged(nameof(IsLocalPlaylistView));
            OnPropertyChanged(nameof(CanRemoveSelectedPlaylistTrack));
            DeletePlaylistCommand?.NotifyCanExecuteChanged();
            RemoveFromPlaylistCommand?.NotifyCanExecuteChanged();
        }
    }
}
