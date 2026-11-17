#nullable enable

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Moves the existing desktop inspector between its sidebar and expanded discussion slot.</summary>
    public partial class MainView
    {
        /// <summary>Observes the workspace currently driving desktop detail navigation.</summary>
        private MainViewModel? _desktopDetailsOwner;
        /// <summary>Retains the one real inspector rather than creating a second artwork and action tree.</summary>
        private Border? _desktopSongCard;
        /// <summary>Coalesces pane requests until inherited visibility and layout bindings have reached the destination slot.</summary>
        private long _desktopDetailsRequest;

        /// <summary>Installs one reusable inspector flight after both XAML name scopes have been created.</summary>
        private void InitializeDesktopSongDetails()
        {
            _desktopSongCard = this.FindControl<Border>("LibraryDetails")!;
            var source = this.FindControl<ContentControl>("DesktopSidebarDetailsSlot")!;
            var destination = this.FindControl<DesktopSongDetailsView>("DesktopSongDetails")!.FindControl<ContentControl>("DesktopSongSummarySlot")!;
            this.FindControl<SharedSongCardFlight>("DesktopSongCardFlight")!.Configure(source, destination, _desktopSongCard);
            DataContextChanged += OnDesktopDetailsContextChanged;
            AttachedToVisualTree += OnDesktopDetailsAttached;
            DetachedFromVisualTree += OnDesktopDetailsDetached;
            _desktopSongCard.SizeChanged += OnDesktopSongCardSizeChanged;
        }

        /// <summary>Rebinds explicit detail navigation when the root receives another workspace.</summary>
        /// <param name="sender">The music view.</param>
        /// <param name="e">The replaced data context.</param>
        private void OnDesktopDetailsContextChanged(object? sender, EventArgs e)
        {
            ObserveDesktopDetails(DataContext as MainViewModel);
        }

        /// <summary>Restores state observation when the root returns to its presentation tree.</summary>
        /// <param name="sender">The music view.</param>
        /// <param name="e">The attachment notification.</param>
        private void OnDesktopDetailsAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            ObserveDesktopDetails(DataContext as MainViewModel);
            if (_desktopDetailsOwner?.IsSongDetailsExpanded == true)
            {
                QueueDesktopDetailsMove();
            }
        }

        /// <summary>Releases workspace subscriptions when the whole root leaves its host.</summary>
        /// <param name="sender">The music view.</param>
        /// <param name="e">The detachment notification.</param>
        private void OnDesktopDetailsDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            ObserveDesktopDetails(null);
        }

        /// <summary>Observes one owner without retaining retired windows or replaying normal library navigation.</summary>
        /// <param name="owner">The current root workspace, or null on detach.</param>
        private void ObserveDesktopDetails(MainViewModel? owner)
        {
            if (ReferenceEquals(_desktopDetailsOwner, owner))
            {
                return;
            }
            _desktopDetailsRequest++;
            if (_desktopDetailsOwner is not null)
            {
                _desktopDetailsOwner.PropertyChanged -= OnDesktopDetailsStateChanged;
            }
            _desktopDetailsOwner = owner;
            if (owner is not null)
            {
                owner.PropertyChanged += OnDesktopDetailsStateChanged;
            }
        }

        /// <summary>Relocates the same card only when desktop expanded state actually changes.</summary>
        /// <param name="sender">The current workspace.</param>
        /// <param name="e">The changed detail state.</param>
        private void OnDesktopDetailsStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsSongDetailsExpanded) && !IsMobileLayout)
            {
                QueueDesktopDetailsMove();
            }
        }

        /// <summary>Starts the real-card flight after the overlay binding has made its destination measurable.</summary>
        private void QueueDesktopDetailsMove()
        {
            var owner = _desktopDetailsOwner;
            var expanded = owner?.IsSongDetailsExpanded == true;
            var request = ++_desktopDetailsRequest;
            Dispatcher.UIThread.Post(() =>
            {
                if (request != _desktopDetailsRequest || IsMobileLayout || !ReferenceEquals(owner, _desktopDetailsOwner)
                    || owner?.IsSongDetailsExpanded != expanded || TopLevel.GetTopLevel(this) is null)
                {
                    return;
                }
                UpdateLayout();
                this.FindControl<SharedSongCardFlight>("DesktopSongCardFlight")!.MoveTo(expanded);
            }, DispatcherPriority.Loaded);
        }

        /// <summary>Collapses secondary metadata and playlist controls only when the card has insufficient vertical room.</summary>
        /// <param name="sender">The one shared song card.</param>
        /// <param name="e">Its measured size.</param>
        private void OnDesktopSongCardSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            var compact = e.NewSize.Height < 300;
            _desktopSongCard?.Classes.Set("short", compact);
            if (e.NewSize.Height > 0 && this.FindControl<Viewbox>("DesktopDetailsCover") is { } artwork)
            {
                // Reserve the measured card chrome's maximum two-line title and fixed action rows.
                artwork.MaxHeight = Math.Clamp(e.NewSize.Height - (compact ? 169 : 217), 40, 320);
            }
        }
    }
}
