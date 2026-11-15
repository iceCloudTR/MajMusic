#nullable enable

using Avalonia.Controls;
using Avalonia.Input;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Connects object-scoped menu gestures to the desktop playlist list.</summary>
    public partial class MainView
    {
        /// <summary>Captures the playlist underneath a real mouse or keyboard context gesture.</summary>
        /// <param name="sender">The full scrolling playlist list.</param>
        /// <param name="e">The context request.</param>
        private void OnPlaylistContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if (!e.Handled && sender is ListBox list && DataContext is MainViewModel owner
                && MusicContextMenus.OpenPlaylist(list, owner, e.Source as Control, e.TryGetPosition(list, out var point) ? point : null))
            {
                e.Handled = true;
            }
        }
    }
}
