#nullable enable

using Avalonia.Controls;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Hosts virtualized card rows and a compact scrolling music index.</summary>
    public partial class BrowseTracksView : UserControl
    {
        /// <summary>Initializes both stable browsing presentations.</summary>
        public BrowseTracksView()
        {
            InitializeComponent();
            DataContextChanged += OnBrowseContextChanged;
        }

        /// <summary>Applies the current viewport when an existing browser receives another workspace view.</summary>
        /// <param name="sender">The music browser.</param>
        /// <param name="e">The new data-context notification.</param>
        private void OnBrowseContextChanged(object? sender, EventArgs e)
        {
            if (Bounds.Width > 0 && DataContext is MainViewModel owner)
            {
                owner.SetBrowseViewportWidth(Bounds.Width - 12);
            }
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

        /// <summary>Updates the inspector cover for keyboard and pointer selection of compact rows.</summary>
        /// <param name="sender">The compact scrolling list.</param>
        /// <param name="e">The newly selected song presentations.</param>
        private async void OnDenseSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (DataContext is MainViewModel owner && e.AddedItems.OfType<SongCardViewModel>().LastOrDefault() is { } card)
            {
                owner.SelectedTrack = card.Track;
                await owner.LoadCoverAsync();
            }
        }
    }
}
