#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Core.Models;
using System.Collections;
using System.ComponentModel;

namespace MajMusic.Presentation.Views
{
    /// <summary>Hosts virtualized artwork cards consistently across recommendations, playlists and searches.</summary>
    public partial class BrowseTracksView : UserControl
    {
        /// <summary>Identifies a touch viewport that keeps complete cover cards in one column.</summary>
        public static readonly StyledProperty<bool> ForceSingleColumnProperty = AvaloniaProperty.Register<BrowseTracksView, bool>(nameof(ForceSingleColumn));
        /// <summary>Identifies the current density shared by all virtualized cards and placeholders.</summary>
        public static readonly StyledProperty<SongCardLayout> CardLayoutProperty = AvaloniaProperty.Register<BrowseTracksView, SongCardLayout>(nameof(CardLayout));
        /// <summary>Identifies the indexed music rows and their optional trailing recommendation pager.</summary>
        public static readonly DirectProperty<BrowseTracksView, IEnumerable?> PresentationRowsProperty =
            AvaloniaProperty.RegisterDirect<BrowseTracksView, IEnumerable?>(nameof(PresentationRows), view => view.PresentationRows);

        /// <summary>Gets or sets whether this browser reserves one complete card per row.</summary>
        public bool ForceSingleColumn
        {
            get => GetValue(ForceSingleColumnProperty);
            set => SetValue(ForceSingleColumnProperty, value);
        }
        /// <summary>Gets the unchanged lightweight music rows followed by an available recommendation footer.</summary>
        public IEnumerable? PresentationRows => _rowsPresentation;
        /// <summary>Gets the actual content lane after the native scrollbar's reserved track.</summary>
        public Size ContentViewport => _browseViewport;
        /// <summary>Gets the active density selected in the music appearance settings.</summary>
        public SongCardLayout CardLayout
        {
            get => GetValue(CardLayoutProperty);
            private set => SetValue(CardLayoutProperty, value);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ForceSingleColumnProperty && Bounds.Width > 0 && _browseOwner is not null)
            {
                RefreshBrowseViewport();
            }
        }

        /// <summary>Observes the current workspace's committed browsing generation.</summary>
        private MainViewModel? _browseOwner;
        /// <summary>Retains the generation whose scroll position is currently displayed.</summary>
        private long _navigationVersion = -1;
        /// <summary>Retains the actual viewport used by both real card rows and bounded loading placeholders.</summary>
        private Size _browseViewport;
        /// <summary>Completes the view handshake when the registered surface is finished or detached.</summary>
        private TaskCompletionSource? _transitionCompletion;
        /// <summary>Invalidates callbacks queued for another owner, navigation generation or attached tree.</summary>
        private long _transitionRegistration;
        /// <summary>Retains observation of the current surface's finite Arc animation.</summary>
        private Task _transitionObservation = Task.CompletedTask;
        /// <summary>Maps native list indices to existing music rows without constructing invisible cards.</summary>
        private BrowseRowsPresentation? _rowsPresentation;
        /// <summary>Rejects deferred footer focus after another navigation gesture.</summary>
        private long _keyboardNavigationVersion;

        /// <summary>Initializes the shared card presentation and its keyboard navigation.</summary>
        public BrowseTracksView()
        {
            InitializeComponent();
            DataContextChanged += OnBrowseContextChanged;
            this.FindControl<ListBox>("CardRowsList")!.AddHandler(KeyDownEvent, OnBrowseKeyDown, RoutingStrategies.Tunnel);
            AddHandler(ScrollViewer.ScrollChangedEvent, OnBrowseScrollChanged, RoutingStrategies.Bubble);
            this.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!.PropertyChanged += OnBrowseSurfaceChanged;
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_browseOwner is { } owner)
            {
                CardLayout = owner.SelectedCardLayout;
                owner.PropertyChanged -= OnBrowseOwnerChanged;
                owner.PropertyChanged += OnBrowseOwnerChanged;
                if (Bounds.Width > 0)
                {
                    RefreshBrowseViewport();
                }

                UpdateSkeletonViewport();
                ResetNavigationOffset();
                RegisterBrowseTransition(owner);
            }
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            CancelPlaceholderWarmup();
            ReleaseBrowseTransition();
            _keyboardNavigationVersion++;
            if (_browseOwner is { } owner)
            {
                owner.PropertyChanged -= OnBrowseOwnerChanged;
                owner.SetBrowseViewportSize(0, 0);
            }

            this.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!.ClearPlaceholders();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>Applies the current viewport when an existing browser receives another workspace view.</summary>
        /// <param name="sender">The music browser.</param>
        /// <param name="e">The new data-context notification.</param>
        private void OnBrowseContextChanged(object? sender, EventArgs e)
        {
            CancelPlaceholderWarmup();
            ReleaseBrowseTransition();
            if (_browseOwner is not null)
            {
                _browseOwner.PropertyChanged -= OnBrowseOwnerChanged;
                if (TopLevel.GetTopLevel(this) is not null)
                {
                    _browseOwner.SetBrowseViewportSize(0, 0);
                }
            }

            _browseOwner = DataContext as MainViewModel;
            CardLayout = _browseOwner?.SelectedCardLayout ?? SongCardLayout.Loose;
            var previousRows = _rowsPresentation;
            previousRows?.Dispose();
            _rowsPresentation = _browseOwner is { } current ? new BrowseRowsPresentation(current) : null;
            RaisePropertyChanged(PresentationRowsProperty, previousRows, _rowsPresentation);
            _navigationVersion = -1;
            if (_browseOwner is { } owner)
            {
                if (TopLevel.GetTopLevel(this) is not null)
                {
                    owner.PropertyChanged += OnBrowseOwnerChanged;
                    if (Bounds.Width > 0)
                    {
                        RefreshBrowseViewport();
                    }
                }

                ResetNavigationOffset();
                UpdateSkeletonViewport();
                UpdateRecommendationFooter();
                if (TopLevel.GetTopLevel(this) is not null)
                {
                    RegisterBrowseTransition(owner);
                }
            }
        }

        /// <summary>Resets scroll only after a new mode or search snapshot has replaced the previous content.</summary>
        /// <param name="sender">The current workspace presentation.</param>
        /// <param name="e">The changed property notification.</param>
        private void OnBrowseOwnerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedCardLayout) && _browseOwner is { } layoutOwner)
            {
                CardLayout = layoutOwner.SelectedCardLayout;
                RefreshBrowseViewport();
                UpdateSkeletonViewport();
            }
            else if (e.PropertyName == nameof(MainViewModel.BrowseTransitionVersion) && _browseOwner is { } owner)
            {
                RegisterBrowseTransition(owner);
            }
            else if (e.PropertyName == nameof(MainViewModel.BrowseNavigationVersion))
            {
                ResetNavigationOffset();
            }
            else if (e.PropertyName is nameof(MainViewModel.IsBrowsePlaceholderVisible) or nameof(MainViewModel.BrowseColumns))
            {
                RefreshBrowseViewport();
                UpdateSkeletonViewport();
            }

            if (e.PropertyName is nameof(MainViewModel.IsBrowsePlaceholderVisible) or nameof(MainViewModel.CardTracks)
                or nameof(MainViewModel.HasRecommendationPages) or nameof(MainViewModel.RecommendationCurrentPageTrackCount)
                or nameof(MainViewModel.IsRecommendationsMode))
            {
                UpdateRecommendationFooter();
            }
        }

        /// <summary>Appends pagination only after the complete target page has finished its existing slide.</summary>
        private void UpdateRecommendationFooter()
        {
            var owner = _browseOwner;
            _rowsPresentation?.SetFooterVisible(owner is { HasRecommendationPages: true, IsBrowsePlaceholderVisible: false }
                && owner.CardTracks.Count == owner.RecommendationCurrentPageTrackCount
                && this.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!.TransitionProgress >= 1);
        }

        /// <summary>Updates only the trailing footer when the incoming target finishes moving.</summary>
        /// <param name="sender">The existing horizontal browsing presenter.</param>
        /// <param name="e">The changed actual Arc progress.</param>
        private void OnBrowseSurfaceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == MusicSurfaceHost.TransitionProgressProperty
                && sender is MusicSurfaceHost { TransitionProgress: 0 or 1 })
            {
                UpdateRecommendationFooter();
            }
        }

        /// <summary>Registers this generation before querying and observes its Arc task after bindings have applied the new version.</summary>
        /// <param name="owner">The currently attached browsing presentation.</param>
        private void RegisterBrowseTransition(MainViewModel owner)
        {
            ReleaseBrowseTransition();
            var registration = _transitionRegistration;
            var version = owner.BrowseTransitionVersion;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _transitionCompletion = completed;
            owner.SetBrowseTransitionCompletion(version, completed.Task);
            Dispatcher.UIThread.Post(() =>
            {
                if (_transitionRegistration != registration || !ReferenceEquals(_browseOwner, owner)
                    || owner.BrowseTransitionVersion != version || TopLevel.GetTopLevel(this) is null)
                {
                    completed.TrySetResult();
                    return;
                }

                var transition = this.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!.TransitionUpdate;
                _transitionObservation = transition.ContinueWith(static (task, state) =>
                {
                    _ = task.Exception;
                    ((TaskCompletionSource)state!).TrySetResult();
                }, completed, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }, DispatcherPriority.Loaded);
        }

        /// <summary>Releases a replaced or detached surface's waiter without holding a newer target behind obsolete motion.</summary>
        private void ReleaseBrowseTransition()
        {
            _transitionRegistration++;
            _transitionCompletion?.TrySetResult();
            _transitionCompletion = null;
        }

        /// <summary>Creates placeholders only for an actively loading viewport.</summary>
        private void UpdateSkeletonViewport()
        {
            if (TopLevel.GetTopLevel(this) is not null && _browseOwner is { IsBrowsePlaceholderVisible: true } owner)
            {
                CancelPlaceholderWarmup();
                this.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!.SetViewport(_browseViewport, owner.BrowseColumns, CardLayout);
            }
            else
            {
                this.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!.ClearPlaceholders();
                QueuePlaceholderWarmup();
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
            RefreshBrowseViewport();
            UpdateSkeletonViewport();
        }

        /// <summary>Reports actual lane changes after native layout, ignoring offset or footer-only extent changes.</summary>
        /// <param name="sender">The browser owning the two native presenters.</param>
        /// <param name="e">The native viewport, extent or offset change.</param>
        private void OnBrowseScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (e.ViewportDelta != default)
            {
                RefreshBrowseViewport();
                UpdateSkeletonViewport();
            }
        }

        /// <summary>Uses one measured native content lane for placeholders, card columns and cover preparation.</summary>
        private void RefreshBrowseViewport()
        {
            if (_browseOwner is not { } owner || TopLevel.GetTopLevel(this) is null)
            {
                return;
            }

            var list = this.FindControl<ListBox>("CardRowsList")!;
            var viewer = owner.IsBrowsePlaceholderVisible
                ? this.FindControl<ScrollViewer>("BrowseSkeletonScroll")
                : list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(control => ReferenceEquals(control.TemplatedParent, list));
            var presenter = viewer?.GetVisualDescendants().OfType<ScrollContentPresenter>()
                .FirstOrDefault(control => ReferenceEquals(control.TemplatedParent, viewer));
            if (presenter is not { Bounds.Width: > 0, Bounds.Height: > 0 })
            {
                return;
            }

            _browseViewport = presenter.Bounds.Size;
            owner.SetBrowseViewportWidth(_browseViewport.Width);
            owner.SetBrowseViewportSize(_browseViewport.Width, _browseViewport.Height);
        }

        /// <summary>Moves between individual artwork cards while keeping the selected row and inspector visible.</summary>
        /// <param name="sender">The shared virtualized card list.</param>
        /// <param name="e">The navigation key pressed in the list or one of its cards.</param>
        private async void OnBrowseKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainViewModel owner || owner.IsBrowsePlaceholderVisible || owner.CardTracks.Count == 0 || e.KeyModifiers != KeyModifiers.None
                || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))
            {
                return;
            }

            e.Handled = true;
            var keyboardVersion = ++_keyboardNavigationVersion;
            var selected = owner.SelectedBrowseCard;
            var index = owner.GetBrowseCardIndex(selected);

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
            if (e.Key == Key.End && _rowsPresentation?.Footer is { } footer)
            {
                list.ScrollIntoView(footer);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_keyboardNavigationVersion == keyboardVersion && ReferenceEquals(_browseOwner, owner)
                        && ReferenceEquals(_rowsPresentation?.Footer, footer) && TopLevel.GetTopLevel(this) is not null)
                    {
                        var container = list.ContainerFromIndex(_rowsPresentation.Count - 1);
                        container?.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                            .FirstOrDefault(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled)?.Focus();
                    }
                }, DispatcherPriority.Loaded);
            }
            else
            {
                list.Focus();
            }

            await owner.LoadCoverAsync();
        }
    }
}
