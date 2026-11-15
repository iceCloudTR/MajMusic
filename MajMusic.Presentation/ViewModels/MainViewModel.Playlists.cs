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
            && SelectedPlaylist is { IsCloud: false, IsFavorite: false } playlist && playlist.Id != FavoritesId;
        /// <summary>Gets whether the current home mode browses an editable local playlist.</summary>
        public bool IsLocalPlaylistView => IsPlaylistsMode && BrowsedPlaylist is { IsCloud: false };
        /// <summary>Gets whether the inspected song belongs to an editable playlist currently being browsed.</summary>
        public bool CanRemoveSelectedPlaylistTrack => IsIdle && _workspace is IPlaylistManagementWorkspace && IsPlaylistsMode
            && BrowsedPlaylist is { IsCloud: false } playlist && SelectedTrack is { } track
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
        private async Task DeleteSelectedPlaylistAsync()
        {
            if (_workspace is not IPlaylistManagementWorkspace management
                || SelectedPlaylist is not { IsCloud: false, IsFavorite: false } playlist || playlist.Id == FavoritesId)
            {
                return;
            }

            var wasOpen = _activePlaylistId == playlist.Id;
            var version = _homeVersion;
            await management.DeletePlaylistAsync(playlist, _lifetime.Token);
            await ReloadPlaylistsAsync();
            if (wasOpen && IsPlaylistsMode && version == _homeVersion)
            {
                SelectedPlaylist = _favorites;
                UseHomeMode(HomeBrowseMode.Playlists);
                await ReloadLibraryAsync();
            }

            Status = $"已删除歌单：{playlist.Name}";
        }

        /// <summary>Removes a song from the active home playlist without touching the unrelated insertion target.</summary>
        /// <returns>A task representing the local membership edit and refreshed contents.</returns>
        private async Task RemoveSelectedPlaylistTrackAsync()
        {
            if (_workspace is not IPlaylistManagementWorkspace management || !IsPlaylistsMode
                || BrowsedPlaylist is not { IsCloud: false } playlist || SelectedTrack is not { } track)
            {
                return;
            }

            await management.RemoveFromPlaylistAsync(playlist.Id, track, _lifetime.Token);
            await ReloadPlaylistsAsync();
            if (_activePlaylistId == playlist.Id && IsPlaylistsMode)
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
