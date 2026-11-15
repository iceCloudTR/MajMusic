#nullable enable

using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Fits local-source and playlist navigation into fixed page regions.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Matches the configured directory and playlist row height.</summary>
        private const double PanelRowHeight = 52;
        /// <summary>Stores the active playlist page.</summary>
        private int _playlistPageIndex;
        /// <summary>Stores the playlist capacity of the current viewport.</summary>
        private int _playlistPageSize = 6;
        /// <summary>Stores the active directory page.</summary>
        private int _directoryPageIndex;
        /// <summary>Stores the directory capacity of the current viewport.</summary>
        private int _directoryPageSize = 5;

        /// <summary>Gets playlist rows that fit the current page.</summary>
        public IReadOnlyList<MusicPlaylist> VisiblePlaylists { get; private set; } = Array.Empty<MusicPlaylist>();
        /// <summary>Gets directory rows that fit the current page.</summary>
        public IReadOnlyList<LocalDirectorySource> VisibleDirectorySources { get; private set; } = Array.Empty<LocalDirectorySource>();
        /// <summary>Gets the playlist page indicator.</summary>
        public string PlaylistPageText => PageText(_playlistPageIndex, _playlistPageSize, Playlists.Count);
        /// <summary>Gets the directory page indicator.</summary>
        public string DirectoryPageText => PageText(_directoryPageIndex, _directoryPageSize, DirectorySources.Count);
        /// <summary>Gets the preceding-playlist-page command.</summary>
        public AsyncCommand PreviousPlaylistPageCommand { get; private set; } = null!;
        /// <summary>Gets the following-playlist-page command.</summary>
        public AsyncCommand NextPlaylistPageCommand { get; private set; } = null!;
        /// <summary>Gets the preceding-directory-page command.</summary>
        public AsyncCommand PreviousDirectoryPageCommand { get; private set; } = null!;
        /// <summary>Gets the following-directory-page command.</summary>
        public AsyncCommand NextDirectoryPageCommand { get; private set; } = null!;

        /// <summary>Creates bounded navigation actions without accessing the filesystem or network.</summary>
        private void InitializePanelPagingCommands()
        {
            PreviousPlaylistPageCommand = CreateCommand(() => ChangePlaylistPageAsync(-1), () => _playlistPageIndex > 0);
            NextPlaylistPageCommand = CreateCommand(() => ChangePlaylistPageAsync(1), () => (_playlistPageIndex + 1) * _playlistPageSize < Playlists.Count);
            PreviousDirectoryPageCommand = CreateCommand(() => ChangeDirectoryPageAsync(-1), () => _directoryPageIndex > 0);
            NextDirectoryPageCommand = CreateCommand(() => ChangeDirectoryPageAsync(1), () => (_directoryPageIndex + 1) * _directoryPageSize < DirectorySources.Count);
        }

        /// <summary>Sets playlist capacity using the allocated list height.</summary>
        /// <param name="height">The list viewport height in device-independent units.</param>
        public void SetPlaylistViewportHeight(double height)
        {
            var size = PanelPageSize(height);
            if (size != _playlistPageSize)
            {
                var anchor = _playlistPageIndex * _playlistPageSize;
                _playlistPageSize = size;
                _playlistPageIndex = anchor / size;
                RefreshPlaylistPages();
            }
        }

        /// <summary>Sets directory capacity using the allocated list height.</summary>
        /// <param name="height">The list viewport height in device-independent units.</param>
        public void SetDirectoryViewportHeight(double height)
        {
            var size = PanelPageSize(height);
            if (size != _directoryPageSize)
            {
                var anchor = _directoryPageIndex * _directoryPageSize;
                _directoryPageSize = size;
                _directoryPageIndex = anchor / size;
                RefreshDirectoryPages();
            }
        }

        /// <summary>Publishes the current playlist page in one binding update.</summary>
        private void RefreshPlaylistPages()
        {
            _playlistPageIndex = Math.Clamp(_playlistPageIndex, 0, Math.Max(0, (Playlists.Count - 1) / _playlistPageSize));
            VisiblePlaylists = Playlists.Skip(_playlistPageIndex * _playlistPageSize).Take(_playlistPageSize).ToArray();
            OnPropertyChanged(nameof(VisiblePlaylists));
            OnPropertyChanged(nameof(PlaylistPageText));
            PreviousPlaylistPageCommand?.NotifyCanExecuteChanged();
            NextPlaylistPageCommand?.NotifyCanExecuteChanged();
        }

        /// <summary>Publishes the current directory page in one binding update.</summary>
        private void RefreshDirectoryPages()
        {
            _directoryPageIndex = Math.Clamp(_directoryPageIndex, 0, Math.Max(0, (DirectorySources.Count - 1) / _directoryPageSize));
            VisibleDirectorySources = DirectorySources.Skip(_directoryPageIndex * _directoryPageSize).Take(_directoryPageSize).ToArray();
            OnPropertyChanged(nameof(VisibleDirectorySources));
            OnPropertyChanged(nameof(DirectoryPageText));
            PreviousDirectoryPageCommand?.NotifyCanExecuteChanged();
            NextDirectoryPageCommand?.NotifyCanExecuteChanged();
        }

        /// <summary>Moves to a playlist page without changing the open music queue.</summary>
        /// <param name="offset">The signed page offset.</param>
        /// <returns>A completed navigation task.</returns>
        private Task ChangePlaylistPageAsync(int offset)
        {
            _playlistPageIndex += offset;
            RefreshPlaylistPages();
            SelectedPlaylist = VisiblePlaylists.FirstOrDefault();
            return Task.CompletedTask;
        }

        /// <summary>Moves to a directory page and selects its first configuration.</summary>
        /// <param name="offset">The signed page offset.</param>
        /// <returns>A completed navigation task.</returns>
        private Task ChangeDirectoryPageAsync(int offset)
        {
            _directoryPageIndex += offset;
            RefreshDirectoryPages();
            SelectedDirectory = VisibleDirectorySources.FirstOrDefault();
            return Task.CompletedTask;
        }

        /// <summary>Computes a row capacity with a small border allowance.</summary>
        /// <param name="height">The allocated list height.</param>
        /// <returns>The positive number of complete visible rows.</returns>
        private static int PanelPageSize(double height)
        {
            return double.IsFinite(height) && height > 0 ? Math.Clamp((int)Math.Floor(Math.Max(0, height - 2) / PanelRowHeight), 1, 100) : 1;
        }

        /// <summary>Formats a compact page label.</summary>
        /// <param name="index">The active zero-based page.</param>
        /// <param name="size">The current capacity.</param>
        /// <param name="count">The total row count.</param>
        /// <returns>The current and total page numbers.</returns>
        private static string PageText(int index, int size, int count)
        {
            return $"{index + 1} / {Math.Max(1, (count + size - 1) / size)}";
        }
    }
}
