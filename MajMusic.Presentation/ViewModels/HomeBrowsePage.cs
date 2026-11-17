#nullable enable

using System.Collections.ObjectModel;
using System.ComponentModel;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Holds the catalog and stable rows for one resident home surface.</summary>
    public sealed class HomeBrowsePage : INotifyPropertyChanged
    {
        private static readonly PropertyChangedEventArgs ReadyChanged = new(nameof(IsReady));
        private static readonly PropertyChangedEventArgs NavigationChanged = new(nameof(NavigationVersion));
        private static readonly PropertyChangedEventArgs FooterChanged = new(nameof(HasRecommendationPages));

        internal MainViewModel.SnapshotCollection<SongCardRow> MutableRows { get; } = new();
        internal MainViewModel.SnapshotCollection<MusicTrack> Tracks { get; } = new();
        internal MainViewModel.BrowseSnapshot? Snapshot { get; set; }
        internal MainViewModel.PreviewSpecification? Target { get; set; }
        internal bool ArtworkPrepared { get; set; }

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>Gets the persistent row collection rendered by this mode's browser.</summary>
        public ObservableCollection<SongCardRow> Rows => MutableRows;
        /// <summary>Gets the ordered cards, materializing long-list continuations as needed.</summary>
        public IReadOnlyList<SongCardViewModel> CardTracks { get; internal set; } = [];
        /// <summary>Gets whether the page already has content for its committed target.</summary>
        public bool IsReady { get; private set; }
        /// <summary>Gets the content generation used to reset scroll after a new query.</summary>
        public long NavigationVersion { get; private set; }
        /// <summary>Gets whether the page needs its recommendation paging footer, even while offscreen.</summary>
        public bool HasRecommendationPages { get; private set; }

        internal void SetReady(bool ready)
        {
            if (IsReady != ready)
            {
                IsReady = ready;
                PropertyChanged?.Invoke(this, ReadyChanged);
            }
        }

        internal void ResetNavigation()
        {
            NavigationVersion++;
            PropertyChanged?.Invoke(this, NavigationChanged);
        }

        internal void SetRecommendationFooter(bool visible)
        {
            if (HasRecommendationPages != visible)
            {
                HasRecommendationPages = visible;
                PropertyChanged?.Invoke(this, FooterChanged);
            }
        }
    }
}
