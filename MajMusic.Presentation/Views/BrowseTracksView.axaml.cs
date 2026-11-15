#nullable enable

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Presentation.ViewModels;
using System.ComponentModel;

namespace MajMusic.Presentation.Views
{
    /// <summary>Hosts virtualized artwork cards consistently across recommendations, playlists and searches.</summary>
    public partial class BrowseTracksView : UserControl
    {
        /// <summary>Observes the current workspace's committed browsing generation.</summary>
        private MainViewModel? _browseOwner;
        /// <summary>Retains the generation whose scroll position is currently displayed.</summary>
        private long _navigationVersion = -1;

        /// <summary>Initializes the shared card presentation and its keyboard navigation.</summary>
        public BrowseTracksView()
        {
            InitializeComponent();
            DataContextChanged += OnBrowseContextChanged;
            this.FindControl<ListBox>("CardRowsList")!.AddHandler(KeyDownEvent, OnBrowseKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>Applies the current viewport when an existing browser receives another workspace view.</summary>
        /// <param name="sender">The music browser.</param>
        /// <param name="e">The new data-context notification.</param>
        private void OnBrowseContextChanged(object? sender, EventArgs e)
        {
            if (_browseOwner is not null)
            {
                _browseOwner.PropertyChanged -= OnBrowseOwnerChanged;
            }

            _browseOwner = DataContext as MainViewModel;
            _navigationVersion = -1;
            if (_browseOwner is { } owner)
            {
                owner.PropertyChanged += OnBrowseOwnerChanged;
                if (Bounds.Width > 0)
                {
                    owner.SetBrowseViewportWidth(Bounds.Width - 12);
                }

                ResetNavigationOffset();
            }
        }

        /// <summary>Resets scroll only after a new mode or search snapshot has replaced the previous content.</summary>
        /// <param name="sender">The current workspace presentation.</param>
        /// <param name="e">The changed property notification.</param>
        private void OnBrowseOwnerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.BrowseNavigationVersion))
            {
                ResetNavigationOffset();
            }
        }

        /// <summary>Moves a new query to the beginning while leaving resize, metadata and appended rows untouched.</summary>
        private void ResetNavigationOffset()
        {
            if (_browseOwner is not { } owner || _navigationVersion == owner.BrowseNavigationVersion)
            {
                return;
            }

            _navigationVersion = owner.BrowseNavigationVersion;
            var version = _navigationVersion;
            var list = this.FindControl<ListBox>("CardRowsList")!;
            list.SelectedItem = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_browseOwner, owner) && _navigationVersion == version)
                {
                    var scroll = list.GetVisualDescendants().OfType<ScrollViewer>()
                        .FirstOrDefault(item => ReferenceEquals(item.TemplatedParent, list));
                    if (scroll is not null)
                    {
                        scroll.Offset = default;
                    }
                }
            }, DispatcherPriority.Loaded);
        }

        /// <summary>Fits artwork columns to the actual content viewport.</summary>
        /// <param name="sender">The browsing surface.</param>
        /// <param name="e">The new viewport dimensions.</param>
        private void OnBrowseSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is MainViewModel owner)
            {
                owner.SetBrowseViewportWidth(e.NewSize.Width - 12);
            }
        }

        /// <summary>Moves between individual artwork cards while keeping the selected row and inspector visible.</summary>
        /// <param name="sender">The shared virtualized card list.</param>
        /// <param name="e">The navigation key pressed in the list or one of its cards.</param>
        private async void OnBrowseKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainViewModel owner || owner.CardTracks.Count == 0 || e.KeyModifiers != KeyModifiers.None
                || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))
            {
                return;
            }

            e.Handled = true;
            var selected = owner.SelectedBrowseCard;
            var index = -1;
            for (var candidate = 0; candidate < owner.CardTracks.Count; candidate++)
            {
                if (ReferenceEquals(owner.CardTracks[candidate], selected))
                {
                    index = candidate;
                    break;
                }
            }

            var last = owner.CardTracks.Count - 1;
            var target = e.Key switch
            {
                Key.Home => 0,
                Key.End => last,
                Key.Left => index < 0 ? last : Math.Max(0, index - 1),
                Key.Right => index < 0 ? 0 : Math.Min(last, index + 1),
                Key.Up => index < 0 ? last : Math.Max(0, index - owner.BrowseColumns),
                _ => index < 0 ? 0 : Math.Min(last, index + owner.BrowseColumns)
            };
            owner.SelectedBrowseCard = owner.CardTracks[target];
            var row = owner.BrowseRows[target / owner.BrowseColumns];
            var list = (ListBox)sender!;
            list.SelectedItem = row;
            list.ScrollIntoView(row);
            await owner.LoadCoverAsync();
        }
    }
}
