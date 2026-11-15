#nullable enable

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MajMusic.Presentation.ViewModels;
using TKCode.Arc;

namespace MajMusic.Presentation.Views
{
    /// <summary>Connects the desktop navigation controls to the existing music commands.</summary>
    public partial class MainView
    {
        /// <summary>Defers name-scope lookups until the populated desktop navigation has loaded.</summary>
        private bool _desktopNavigationLoaded;

        /// <summary>Highlights the page restored by the native page host.</summary>
        /// <param name="sender">The desktop navigation bar.</param>
        /// <param name="e">The completed loading notification.</param>
        private void OnDesktopNavigationLoaded(object? sender, RoutedEventArgs e)
        {
            _desktopNavigationLoaded = true;
            UpdateDesktopNavigation();
        }

        /// <summary>Keeps navigation highlighting in sync with explicit and programmatic page changes.</summary>
        /// <param name="sender">The native page host.</param>
        /// <param name="e">The changed page selection.</param>
        private void OnDesktopSectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_desktopNavigationLoaded && ReferenceEquals(sender, e.Source))
            {
                UpdateDesktopNavigation();
            }
        }

        /// <summary>Applies the selected page treatment without replacing any page content.</summary>
        private void UpdateDesktopNavigation()
        {
            var selectedIndex = this.FindControl<TabControl>("MainTabs")?.SelectedIndex ?? 0;
            var navigationNames = new[]
            {
                "NavigateHome", "NavigatePlaylists", "NavigateDirectories", "NavigateAccounts", "NavigateSettings"
            };
            for (var index = 0; index < navigationNames.Length; index++)
            {
                if (this.FindControl<TKCode.Arc.Button>(navigationNames[index]) is { } button)
                {
                    button.Classes.Set("current", index == selectedIndex);
                }
            }
        }

        /// <summary>Runs the original browsing command for the newly selected home segment.</summary>
        /// <param name="sender">The home mode selector.</param>
        /// <param name="e">The changed segment selection.</param>
        private void OnHomeModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ReferenceEquals(sender, e.Source) && sender is SegmentedControl selector
                && selector.SelectedValue is HomeBrowseMode mode)
            {
                ExecuteHomeMode(mode);
            }
        }

        /// <summary>Allows the current segment to leave an expanded search without needing another mode first.</summary>
        /// <param name="sender">The home mode selector.</param>
        /// <param name="e">The completed pointer gesture.</param>
        private void OnHomeModePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (DataContext is MainViewModel { IsSearchExpanded: true }
                && sender is SegmentedControl { SelectedValue: HomeBrowseMode mode }
                && (e.InitialPressMouseButton == MouseButton.Left || e.Pointer.Type is PointerType.Touch or PointerType.Pen))
            {
                ExecuteHomeMode(mode);
            }
        }

        /// <summary>Allows keyboard activation of the current segment to leave an expanded search.</summary>
        /// <param name="sender">The home mode selector.</param>
        /// <param name="e">The segment activation key.</param>
        private void OnHomeModeKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None
                && DataContext is MainViewModel { IsSearchExpanded: true }
                && sender is SegmentedControl { SelectedValue: HomeBrowseMode mode })
            {
                ExecuteHomeMode(mode);
                e.Handled = true;
            }
        }

        /// <summary>Invokes one existing, tracked command instead of duplicating browsing behavior in the view.</summary>
        /// <param name="mode">The requested home browsing mode.</param>
        private void ExecuteHomeMode(HomeBrowseMode mode)
        {
            if (DataContext is not MainViewModel owner || owner.HomeMode == mode && !owner.IsSearchExpanded)
            {
                return;
            }

            var command = mode switch
            {
                HomeBrowseMode.Playlists => owner.ShowPlaylistsModeCommand,
                HomeBrowseMode.Library => owner.ShowLibraryModeCommand,
                _ => owner.ShowRecommendationsModeCommand
            };
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        }
    }
}
