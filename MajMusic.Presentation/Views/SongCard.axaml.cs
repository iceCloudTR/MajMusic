#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Displays one song and activates artwork only while the presentation is attached.</summary>
    public partial class SongCard : UserControl
    {
        /// <summary>Identifies the compact searchable-row layout.</summary>
        public static readonly StyledProperty<bool> IsCompactProperty = AvaloniaProperty.Register<SongCard, bool>(nameof(IsCompact));
        /// <summary>Identifies whether the owning browse mode currently displays this presentation.</summary>
        public static readonly StyledProperty<bool> IsBrowsingActiveProperty = AvaloniaProperty.Register<SongCard, bool>(nameof(IsBrowsingActive), true);
        /// <summary>Constrains Arc's animated label slot to the actual card content width.</summary>
        public static readonly DirectProperty<SongCard, double> CardContentWidthProperty = AvaloniaProperty.RegisterDirect<SongCard, double>(
            nameof(CardContentWidth), card => card.CardContentWidth);
        /// <summary>Stores the finite width shared by the card's cover and information columns.</summary>
        private double _cardContentWidth;
        /// <summary>Cancels optional metadata when the item leaves the viewport.</summary>
        private CancellationTokenSource? _activationCancellation;
        /// <summary>Retains the exact presentation whose artwork lease is active.</summary>
        private SongCardViewModel? _attachedCard;
        /// <summary>Tracks whether this control currently belongs to a rendered viewport.</summary>
        private bool _attached;
        /// <summary>Prevents a completed long press from also opening the normal song inspector.</summary>
        private bool _suppressContextTap;

        /// <summary>Registers layout invalidation for compact-mode changes.</summary>
        static SongCard()
        {
            AffectsMeasure<SongCard>(IsCompactProperty);
        }

        /// <summary>Initializes the artwork and song-information template.</summary>
        public SongCard()
        {
            InitializeComponent();
            DataContextChanged += OnCardDataContextChanged;
            Loaded += OnCardLoaded;
            EffectiveViewportChanged += OnCardViewportChanged;
            SetValue(InputElement.IsHoldingEnabledProperty, true);
            AddHandler(ContextRequestedEvent, OnCardContextRequested, RoutingStrategies.Bubble);
            AddHandler(HoldingEvent, OnCardHolding, RoutingStrategies.Bubble);
        }

        /// <summary>Gets or sets whether this song uses a compact searchable row.</summary>
        public bool IsCompact
        {
            get => GetValue(IsCompactProperty);
            set => SetValue(IsCompactProperty, value);
        }

        /// <summary>Gets or sets whether the owning list is currently visible.</summary>
        public bool IsBrowsingActive
        {
            get => GetValue(IsBrowsingActiveProperty);
            set => SetValue(IsBrowsingActiveProperty, value);
        }

        /// <summary>Gets the finite content width after the card's one-pixel border.</summary>
        public double CardContentWidth => _cardContentWidth;

        /// <inheritdoc />
        protected override async void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if ((change.Property == IsBrowsingActiveProperty || change.Property == IsVisibleProperty) && _attached)
            {
                await ActivateCardAsync();
            }
        }

        /// <inheritdoc />
        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 320;
            var height = IsCompact ? 60 : width / 2;
            SetAndRaise(CardContentWidthProperty, ref _cardContentWidth, Math.Max(0, width - 2));
            base.MeasureOverride(new Size(width, height));
            return new Size(width, height);
        }

        /// <inheritdoc />
        protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            await ActivateCardAsync();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = false;
            MusicContextMenus.Close(this);
            DeactivateCard();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>Transfers the artwork lease when a virtualized control is recycled.</summary>
        /// <param name="sender">The recycled song control.</param>
        /// <param name="e">The data-context change notification.</param>
        private async void OnCardDataContextChanged(object? sender, EventArgs e)
        {
            MusicContextMenus.Close(this);
            if (_attached)
            {
                await ActivateCardAsync();
            }
        }

        /// <summary>Starts visible artwork after inherited visibility and initial layout have settled.</summary>
        /// <param name="sender">The loaded song presentation.</param>
        /// <param name="e">The load notification.</param>
        private async void OnCardLoaded(object? sender, RoutedEventArgs e)
        {
            await ActivateCardAsync();
        }

        /// <summary>Retries a presentation that enters the scroll viewport after being hidden or recycled.</summary>
        /// <param name="sender">The visible song presentation.</param>
        /// <param name="e">The effective scroll viewport.</param>
        private async void OnCardViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
        {
            if (_attached && e.EffectiveViewport.Intersects(new Rect(Bounds.Size)))
            {
                await ActivateCardAsync();
            }
        }

        /// <summary>Starts observed, cancellable artwork and metadata reads for one visible song.</summary>
        /// <returns>A task representing the current visible-item reads.</returns>
        private async Task ActivateCardAsync()
        {
            if (_attached && IsBrowsingActive && IsEffectivelyVisible && ReferenceEquals(DataContext, _attachedCard))
            {
                return;
            }

            DeactivateCard();
            if (!_attached || !IsBrowsingActive || !IsEffectivelyVisible || DataContext is not SongCardViewModel card)
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _activationCancellation = cancellation;
            _attachedCard = card;
            try
            {
                var metadata = this.FindAncestorOfType<MainView>()?.DataContext is MainViewModel owner
                    ? owner.EnsureBrowseMetadataAsync(card, cancellation.Token) : Task.CompletedTask;
                await Task.WhenAll(card.ActivateAsync(), metadata);
            }
            catch (OperationCanceledException)
            {
                // A recycled presentation has transferred its work to the new viewport owner.
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning("Song artwork could not be loaded: {0}", exception.GetType().Name);
            }
        }

        /// <summary>Releases the current artwork lease and cancels its optional metadata read.</summary>
        private void DeactivateCard()
        {
            _activationCancellation?.Cancel();
            _activationCancellation?.Dispose();
            _activationCancellation = null;
            _attachedCard?.Deactivate();
            _attachedCard = null;
        }

        /// <summary>Selects the song without starting or replacing playback.</summary>
        /// <param name="sender">The song card button.</param>
        /// <param name="e">The click notification.</param>
        private async void OnSelectClicked(object? sender, RoutedEventArgs e)
        {
            if (_suppressContextTap)
            {
                _suppressContextTap = false;
                e.Handled = true;
                return;
            }

            var host = this.FindAncestorOfType<MainView>();
            if (DataContext is SongCardViewModel card && host?.DataContext is MainViewModel owner)
            {
                owner.SelectedTrack = card.Track;
                host.OpenTrackDetails();
                await owner.LoadCoverAsync();
            }
        }

        /// <summary>Opens the current card's captured actions without using the inspector's older selected song.</summary>
        /// <param name="sender">The artwork card.</param>
        /// <param name="e">The actual pointer or keyboard context request.</param>
        private void OnCardContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if (!e.Handled && OpenCardMenu(e.TryGetPosition(this, out var point) ? point : null))
            {
                e.Handled = true;
            }
        }

        /// <summary>Provides the same captured actions to a touch or pen long press.</summary>
        /// <param name="sender">The touched card.</param>
        /// <param name="e">The platform holding gesture.</param>
        private void OnCardHolding(object? sender, HoldingRoutedEventArgs e)
        {
            if (e.PointerType == PointerType.Mouse)
            {
                return;
            }

            if (e.HoldingState == HoldingState.Started && OpenCardMenu(new Point(Bounds.Width / 2, Bounds.Height / 2)))
            {
                _suppressContextTap = true;
                e.Handled = true;
            }
            else if (e.HoldingState == HoldingState.Canceled)
            {
                _suppressContextTap = false;
            }
            else if (e.HoldingState == HoldingState.Completed)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _suppressContextTap = false,
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>Captures the card's exact descriptor for a mouse, keyboard or touch menu.</summary>
        /// <param name="position">The optional location relative to this card.</param>
        /// <returns>Whether an attached music target was available.</returns>
        private bool OpenCardMenu(Point? position)
        {
            var owner = this.FindAncestorOfType<MainView>()?.DataContext as MainViewModel
                ?? this.FindAncestorOfType<MobileMusicView>()?.DataContext as MainViewModel;
            if (!_attached || DataContext is not SongCardViewModel card || owner is null)
            {
                return false;
            }

            MusicContextMenus.OpenTrack(this, owner, card.Track, position);
            return true;
        }

        /// <summary>Starts the chosen song using the existing playback queue action.</summary>
        /// <param name="sender">The double-tapped song button.</param>
        /// <param name="e">The pointer gesture.</param>
        private async void OnPlayDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is SongCardViewModel card && this.FindAncestorOfType<MainView>()?.DataContext is MainViewModel owner)
            {
                owner.SelectedTrack = card.Track;
                await owner.PlaySelectedAsync();
                e.Handled = true;
            }
        }
    }
}
