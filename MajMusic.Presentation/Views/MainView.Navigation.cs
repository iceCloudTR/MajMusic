#nullable enable

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MajMusic.Presentation.ViewModels;
using TKCode.Arc;

namespace MajMusic.Presentation.Views
{
    /// <summary>Connects the desktop navigation controls to the existing music commands.</summary>
    public partial class MainView
    {
        /// <summary>Defers name-scope lookups until the populated desktop navigation has loaded.</summary>
        private bool _desktopNavigationLoaded;
        /// <summary>Prevents a programmatic page synchronization from becoming another navigation request.</summary>
        private bool _restoringDesktopNavigation;
        /// <summary>Coalesces page clicks after the selector's own input and rendering feedback.</summary>
        private long _desktopNavigationRequest;
        /// <summary>Coalesces mode clicks before starting snapshot capture and library preparation.</summary>
        private long _homeNavigationRequest;

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

        /// <summary>Synchronizes the Arc navigation pill with native and programmatic page selection.</summary>
        private void UpdateDesktopNavigation()
        {
            var selectedIndex = this.FindControl<TabControl>("MainTabs")?.SelectedIndex ?? 0;
            var navigation = this.FindControl<SegmentedControl>("DesktopTopNavigation");
            if (navigation is null || navigation.SelectedIndex == selectedIndex)
            {
                return;
            }

            _restoringDesktopNavigation = true;
            try
            {
                navigation.SelectedIndex = selectedIndex;
            }
            finally
            {
                _restoringDesktopNavigation = false;
            }
        }

        /// <summary>Changes the native page once when the person chooses another Arc navigation segment.</summary>
        /// <param name="sender">The desktop navigation selector.</param>
        /// <param name="e">The selected page segment.</param>
        private void OnDesktopNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_desktopNavigationLoaded && !_restoringDesktopNavigation && ReferenceEquals(sender, e.Source)
                && sender is SegmentedControl { SelectedIndex: >= 0 and < 5 } navigation
                && navigation.SelectedIndex != SelectedSectionIndex)
            {
                var section = navigation.SelectedIndex;
                var request = ++_desktopNavigationRequest;
                Dispatcher.UIThread.Post(() =>
                {
                    if (request == _desktopNavigationRequest && IsLoaded && !IsMobileLayout
                        && navigation.SelectedIndex == section && SelectedSectionIndex != section)
                    {
                        NavigateToSection(section);
                    }
                }, DispatcherPriority.Background);
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

            var request = ++_homeNavigationRequest;
            Dispatcher.UIThread.Post(() =>
            {
                if (request != _homeNavigationRequest || !IsLoaded || IsMobileLayout || SelectedSectionIndex != 0
                    || !ReferenceEquals(DataContext, owner)
                    || this.FindControl<SegmentedControl>("HomeModeSegments")?.SelectedValue is not HomeBrowseMode selected || selected != mode)
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
            }, DispatcherPriority.Background);
        }
    }
}
