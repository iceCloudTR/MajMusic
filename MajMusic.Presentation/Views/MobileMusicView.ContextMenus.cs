#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Shares captured playlist actions with the touch presentation.</summary>
    public partial class MobileMusicView
    {
        /// <summary>Enables the platform long-press recognizer on the instantiated playlist menu.</summary>
        /// <param name="sender">The current playlist list.</param>
        /// <param name="e">The completed template loading notification.</param>
        private void OnPlaylistListLoaded(object? sender, RoutedEventArgs e)
        {
            if (sender is ListBox list)
            {
                list.SetValue(InputElement.IsHoldingEnabledProperty, true);
            }
        }

        /// <summary>Opens the captured row's menu for mouse and external-keyboard users.</summary>
        /// <param name="sender">The scrolling playlist list.</param>
        /// <param name="e">The actual context request.</param>
        private void OnPlaylistContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if (!e.Handled && sender is ListBox list && DataContext is MainViewModel owner
                && MusicContextMenus.OpenPlaylist(list, owner, e.Source as Control, e.TryGetPosition(list, out var point) ? point : null))
            {
                e.Handled = true;
            }
        }

        /// <summary>Opens the same row actions when touch users hold a playlist.</summary>
        /// <param name="sender">The active scrolling playlist list.</param>
        /// <param name="e">The platform holding gesture.</param>
        private void OnPlaylistHolding(object? sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started || e.PointerType == PointerType.Mouse
                || sender is not ListBox list || DataContext is not MainViewModel owner || e.Source is not Control source)
            {
                return;
            }

            var item = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
            var position = item?.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), list);
            if (position is not null && MusicContextMenus.OpenPlaylist(list, owner, source, position))
            {
                e.Handled = true;
            }
        }
    }
}
