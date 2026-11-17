#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using System.ComponentModel;

namespace MajMusic.Presentation.Views
{
    /// <summary>Keeps a separately laid-out native card list for each home mode.</summary>
    public partial class BrowseTracksView : UserControl
    {
        /// <summary>Identifies the mobile browser's initial column preference.</summary>
        public static readonly StyledProperty<bool> ForceSingleColumnProperty =
            AvaloniaProperty.Register<BrowseTracksView, bool>(nameof(ForceSingleColumn));
        /// <summary>Identifies the density shared by the three lists and the loading cards.</summary>
        public static readonly StyledProperty<SongCardLayout> CardLayoutProperty =
            AvaloniaProperty.Register<BrowseTracksView, SongCardLayout>(nameof(CardLayout));

        private readonly ResidentBrowseStrip _strip;
        private readonly MusicSurfaceHost _surface;
        private readonly BrowseCardListBox[] _lists;
        private readonly HomeBrowsePage?[] _pages = new HomeBrowsePage?[3];
        private readonly BrowseRowsPresentation?[] _presentations = new BrowseRowsPresentation?[3];
        private readonly Grid _skeletonLayer;
        private readonly TranslateTransform _skeletonMotion = new();
        private MainViewModel? _browseOwner;
        private HomeBrowseMode _residentMode;
        private Size _browseViewport;
        private bool _observing;
        private TaskCompletionSource? _transitionCompletion;
        private long _keyboardNavigationVersion;

        /// <summary>Gets or sets the mobile browser's initial column preference.</summary>
        public bool ForceSingleColumn
        {
            get => GetValue(ForceSingleColumnProperty);
            set => SetValue(ForceSingleColumnProperty, value);
        }
        /// <summary>Gets the density shared by the three lists and the loading cards.</summary>
        public SongCardLayout CardLayout
        {
            get => GetValue(CardLayoutProperty);
            private set => SetValue(CardLayoutProperty, value);
        }
        /// <summary>Gets the actual content lane after the native scrollbar's reserved track.</summary>
        public Size ContentViewport => _browseViewport;
        /// <summary>Gets the native list for the currently chosen mode.</summary>
        public ListBox ActiveList => GetResidentList(_residentMode);
        /// <summary>Gets an existing native list without navigating to it.</summary>
        public ListBox GetResidentList(HomeBrowseMode mode) => _lists[(int)mode];
        /// <summary>Gets the continuous horizontal position in page widths.</summary>
        public double ResidentPosition => _strip.Position;
        /// <summary>Gets completion of the latest motion between home modes.</summary>
        public Task<bool> ResidentTransitionUpdate => _strip.TransitionUpdate;

        /// <summary>Initializes the lists, loading presentation, and keyboard navigation.</summary>
        public BrowseTracksView()
        {
            InitializeComponent();
            _strip = this.FindControl<ResidentBrowseStrip>("BrowseStrip")!;
            _surface = this.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!;
            _skeletonLayer = this.FindControl<Grid>("BrowseSkeletonPresentation")!;
            _strip.PositionChanged += UpdateSkeletonPosition;
            _strip.InitializePages();
            _lists = [
                this.FindControl<BrowseCardListBox>("RecommendationCardRows")!,
                this.FindControl<BrowseCardListBox>("PlaylistCardRows")!,
                this.FindControl<BrowseCardListBox>("LibraryCardRows")!
            ];
            DataContextChanged += OnBrowseContextChanged;
            foreach (var list in _lists)
            {
                list.AddHandler(KeyDownEvent, OnBrowseKeyDown, RoutingStrategies.Tunnel);
            }
            AddHandler(ScrollViewer.ScrollChangedEvent, OnBrowseScrollChanged, RoutingStrategies.Bubble);
            _surface.PropertyChanged += OnBrowseSurfaceChanged;
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ForceSingleColumnProperty && Bounds.Width > 0 && _observing)
            {
                RefreshBrowseViewport();
            }
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ObserveOwner();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            StopObservingOwner();
            base.OnDetachedFromVisualTree(e);
        }

        private void OnBrowseContextChanged(object? sender, EventArgs e)
        {
            StopObservingOwner();
            _browseOwner = DataContext as MainViewModel;
            if (TopLevel.GetTopLevel(this) is not null)
            {
                ObserveOwner();
            }
        }

        private void ObserveOwner()
        {
            if (_observing || _browseOwner is not { } owner)
            {
                return;
            }

            _observing = true;
            CardLayout = owner.SelectedCardLayout;
            _residentMode = owner.HomeMode;
            _strip.Navigate(_residentMode, false);
            owner.PropertyChanged += OnBrowseOwnerChanged;
            for (var index = 0; index < _lists.Length; index++)
            {
                var page = owner.GetBrowsePage((HomeBrowseMode)index);
                _pages[index] = page;
                page.PropertyChanged += OnResidentPageChanged;
                _presentations[index] = new BrowseRowsPresentation(owner, page.Rows);
                _lists[index].CardLayout = CardLayout;
                _lists[index].ItemsSource = _presentations[index];
            }
            UpdatePageVisibility();
            RefreshBrowseViewport();
            UpdateSkeletonViewport();
            UpdateRecommendationFooter();
        }

        private void StopObservingOwner()
        {
            if (!_observing)
            {
                return;
            }

            _observing = false;
            CancelPlaceholderWarmup();
            ReleaseBrowseTransition();
            _keyboardNavigationVersion++;
            _browseOwner!.PropertyChanged -= OnBrowseOwnerChanged;
            _browseOwner.SetBrowseViewportSize(0, 0);
            for (var index = 0; index < _lists.Length; index++)
            {
                _pages[index]!.PropertyChanged -= OnResidentPageChanged;
                _pages[index] = null;
                _lists[index].ItemsSource = null;
                _presentations[index]!.Dispose();
                _presentations[index] = null;
            }
            this.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!.ClearPlaceholders();
        }

        private void OnBrowseOwnerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_browseOwner is not { } owner)
            {
                return;
            }
            if (e.PropertyName == nameof(MainViewModel.SelectedCardLayout))
            {
                CardLayout = owner.SelectedCardLayout;
                foreach (var list in _lists)
                {
                    list.CardLayout = CardLayout;
                }
                RefreshBrowseViewport();
                UpdateSkeletonViewport();
            }
            else if (e.PropertyName == nameof(MainViewModel.BrowseTransitionVersion))
            {
                RegisterBrowseTransition(owner);
            }
            else if (e.PropertyName is nameof(MainViewModel.IsBrowsePlaceholderVisible) or nameof(MainViewModel.BrowseColumns))
            {
                UpdatePageVisibility();
                RefreshBrowseViewport();
                UpdateSkeletonViewport();
            }
        }

        private void OnResidentPageChanged(object? sender, PropertyChangedEventArgs e)
        {
            var index = Array.FindIndex(_pages, page => ReferenceEquals(page, sender));
            if (index < 0)
            {
                return;
            }
            if (e.PropertyName == nameof(HomeBrowsePage.NavigationVersion))
            {
                ResetNavigationOffset(_lists[index]);
            }
            if (e.PropertyName == nameof(HomeBrowsePage.IsReady))
            {
                UpdatePageVisibility();
                UpdateSkeletonViewport();
            }
            UpdateRecommendationFooter();
        }

        private void UpdatePageVisibility()
        {
            if (!_observing || _browseOwner is not { } owner)
            {
                return;
            }
            var index = (int)_residentMode;
            var placeholder = _pages[index]?.IsReady != true
                || (owner.HomeMode == _residentMode && owner.IsBrowsePlaceholderVisible);
            for (var listIndex = 0; listIndex < _lists.Length; listIndex++)
            {
                _lists[listIndex].IsVisible = _pages[listIndex]?.IsReady == true
                    && (listIndex != index || !placeholder);
            }
            _skeletonLayer.IsVisible = placeholder;
            UpdateSkeletonPosition();
        }

        private void UpdateSkeletonPosition()
        {
            if (_skeletonLayer.IsVisible)
            {
                _skeletonMotion.X = _strip.OffsetFor(_residentMode);
                _skeletonLayer.RenderTransform = _skeletonMotion.X == 0 ? null : _skeletonMotion;
                _skeletonLayer.Opacity = _strip.Children[(int)_residentMode].Opacity;
            }
        }

        private void UpdateRecommendationFooter()
        {
            _presentations[(int)HomeBrowseMode.Recommendations]?.SetFooterVisible(
                _pages[(int)HomeBrowseMode.Recommendations] is { HasRecommendationPages: true, IsReady: true }
                && (_residentMode != HomeBrowseMode.Recommendations || _surface.TransitionProgress >= 1));
        }

        private void OnBrowseSurfaceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == MusicSurfaceHost.TransitionProgressProperty && _surface.TransitionProgress is 0 or 1)
            {
                UpdateRecommendationFooter();
            }
        }

        private void RegisterBrowseTransition(MainViewModel owner)
        {
            ReleaseBrowseTransition();
            _keyboardNavigationVersion++;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _transitionCompletion = completed;
            owner.SetBrowseTransitionCompletion(owner.BrowseTransitionVersion, completed.Task);
            var nextMode = owner.HomeMode;
            Task transition;
            if (nextMode != _residentMode)
            {
                var useLivePages = _pages[(int)nextMode]?.IsReady == true && _surface.TransitionUpdate.IsCompleted
                    || !_strip.TransitionUpdate.IsCompleted;
                if (!useLivePages)
                {
                    StartSameSurfaceTransition(owner);
                }
                _residentMode = nextMode;
                _strip.Navigate(nextMode, useLivePages);
                UpdatePageVisibility();
                UpdateSkeletonViewport();
                transition = useLivePages ? _strip.TransitionUpdate : _surface.TransitionUpdate;
            }
            else
            {
                if (_strip.TransitionUpdate.IsCompleted)
                {
                    StartSameSurfaceTransition(owner);
                }
                transition = _strip.TransitionUpdate.IsCompleted ? _surface.TransitionUpdate : _strip.TransitionUpdate;
            }
            _ = transition.ContinueWith(static (task, state) =>
            {
                _ = task.Exception;
                ((TaskCompletionSource)state!).TrySetResult();
            }, completed, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void StartSameSurfaceTransition(MainViewModel owner)
        {
            _surface.SlideDirection = owner.BrowseTransitionDirection;
            _surface.ChangeVersion = owner.BrowseTransitionVersion;
        }

        private void ReleaseBrowseTransition()
        {
            _transitionCompletion?.TrySetResult();
            _transitionCompletion = null;
        }

        private void UpdateSkeletonViewport()
        {
            var skeleton = this.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!;
            if (TopLevel.GetTopLevel(this) is not null && _skeletonLayer.IsVisible && _browseOwner is { } owner)
            {
                CancelPlaceholderWarmup();
                skeleton.SetViewport(_browseViewport, owner.BrowseColumns, CardLayout);
            }
            else
            {
                skeleton.ClearPlaceholders();
                QueuePlaceholderWarmup();
            }
        }

        private static void ResetNavigationOffset(ListBox list)
        {
            list.SelectedItem = null;
            if (FindScrollViewer(list) is { } scroll)
            {
                scroll.Offset = default;
            }
        }

        private static ScrollViewer? FindScrollViewer(ListBox list) => list.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(item => ReferenceEquals(item.TemplatedParent, list));

        private void OnBrowseSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            RefreshBrowseViewport();
            UpdateSkeletonViewport();
        }

        private void OnBrowseScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (e.ViewportDelta != default)
            {
                RefreshBrowseViewport();
                UpdateSkeletonViewport();
            }
        }

        private void RefreshBrowseViewport()
        {
            if (!_observing || _browseOwner is not { } owner || TopLevel.GetTopLevel(this) is null)
            {
                return;
            }
            var viewer = _skeletonLayer.IsVisible
                ? this.FindControl<ScrollViewer>("BrowseSkeletonScroll")
                : FindScrollViewer(ActiveList);
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

        private async void OnBrowseKeyDown(object? sender, KeyEventArgs e)
        {
            if (!ReferenceEquals(sender, ActiveList) || !_strip.TransitionUpdate.IsCompleted
                || _browseOwner is not { } owner || owner.IsBrowsePlaceholderVisible || owner.CardTracks.Count == 0 || e.KeyModifiers != KeyModifiers.None
                || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))
            {
                return;
            }
            e.Handled = true;
            var keyboardVersion = ++_keyboardNavigationVersion;
            var index = owner.GetBrowseCardIndex(owner.SelectedBrowseCard);
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
            var list = ActiveList;
            var presentation = _presentations[(int)_residentMode];
            list.SelectedItem = row;
            list.ScrollIntoView(row);
            if (e.Key == Key.End && presentation?.Footer is { } footer)
            {
                list.ScrollIntoView(footer);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_keyboardNavigationVersion == keyboardVersion && ReferenceEquals(_browseOwner, owner)
                        && ReferenceEquals(presentation.Footer, footer) && TopLevel.GetTopLevel(this) is not null)
                    {
                        var container = list.ContainerFromIndex(presentation.Count - 1);
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
