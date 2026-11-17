#nullable enable

using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using TKCode.Arc.Mobile;
using ArcSlider = TKCode.Arc.Slider;

namespace MajMusic.Presentation.Views
{
    /// <summary>Hosts touch-first music pages over the existing workspace and playback commands.</summary>
    public partial class MobileMusicView : UserControl
    {
        /// <summary>Identifies the active shared section without creating hidden duplicate account editors.</summary>
        public static readonly StyledProperty<int> SelectedSectionIndexProperty = AvaloniaProperty.Register<MobileMusicView, int>(
            nameof(SelectedSectionIndex), 0, defaultBindingMode: BindingMode.TwoWay, coerce: (_, value) => Math.Clamp(value, 0, 4));
        /// <summary>Names the lazily instantiated section templates in navigation order.</summary>
        private static readonly string[] SectionTemplates = ["MobileHomeTemplate", "MobilePlaylistsTemplate", "MobileDirectoriesTemplate", "MobileAccountsTemplate", "MobileSettingsTemplate"];
        /// <summary>Names the section currently presented in the navigation bar.</summary>
        private static readonly string[] SectionTitles = ["MajMusic", "歌单管理", "本地目录", "账号", "设置"];
        /// <summary>Observes browsing changes while this view is attached to its responsive host.</summary>
        private MainViewModel? _owner;
        /// <summary>Prevents restoring a segment selection from issuing another browsing command.</summary>
        private bool _restoringMode;
        /// <summary>Indicates whether this view is the active responsive presentation.</summary>
        private bool _attached;
        /// <summary>Owns only a drag that started on the noninteractive part of the bottom player.</summary>
        private IPointer? _miniPlayerPointer;
        /// <summary>Retains the player gesture origin in stable mobile-root coordinates.</summary>
        private Point _miniPlayerGestureStart;
        /// <summary>Retains the exact workspace that owned the player at pointer contact.</summary>
        private MainViewModel? _miniPlayerOwner;
        /// <summary>Retains the actual playing song rather than an unrelated inspector selection.</summary>
        private MusicTrack? _miniPlayerTrack;
        /// <summary>Indicates that the sheet is following this player's upward pull.</summary>
        private bool _miniPlayerDragging;
        /// <summary>Retains the latest actual pointer movement for release velocity.</summary>
        private Point _miniPlayerLastPosition;
        /// <summary>Times the latest actual movement independently of rendering or media I/O.</summary>
        private long _miniPlayerLastMovement;
        /// <summary>Retains vertical pointer velocity in logical pixels per second.</summary>
        private double _miniPlayerVelocityY;

        /// <summary>Initializes the mobile shell and its existing seek gesture boundary.</summary>
        public MobileMusicView()
        {
            InitializeComponent();
            DataContextChanged += OnOwnerChanged;
            SizeChanged += OnMobileSizeChanged;
            var timeline = this.FindControl<ArcSlider>("MobilePlaybackTimeline")!;
            timeline.AddHandler(PointerPressedEvent, OnSeekPressed, RoutingStrategies.Tunnel, true);
            timeline.AddHandler(PointerReleasedEvent, OnSeekReleased, RoutingStrategies.Bubble, true);
            timeline.AddHandler(KeyDownEvent, OnSeekKeyPressed, RoutingStrategies.Tunnel, true);
            timeline.AddHandler(KeyUpEvent, OnSeekKeyReleased, RoutingStrategies.Bubble, true);
            timeline.LostFocus += OnSeekLostFocus;
            var player = this.FindControl<Border>("MobileMiniPlayer")!;
            player.AddHandler(PointerPressedEvent, OnMiniPlayerPressed, RoutingStrategies.Tunnel, true);
            player.AddHandler(PointerMovedEvent, OnMiniPlayerMoved, RoutingStrategies.Bubble, true);
            player.AddHandler(PointerReleasedEvent, OnMiniPlayerReleased, RoutingStrategies.Bubble, true);
            player.PointerCaptureLost += OnMiniPlayerCaptureLost;
        }

        /// <summary>Gets or sets the visible home, playlists, directories, accounts or settings section.</summary>
        public int SelectedSectionIndex
        {
            get => GetValue(SelectedSectionIndexProperty);
            set => SetValue(SelectedSectionIndexProperty, value);
        }

        /// <summary>Requests the responsive host's existing platform folder picker.</summary>
        public event EventHandler? BrowseDirectoryRequested;
        /// <summary>Requests the responsive host's existing Maj collection import picker.</summary>
        public event EventHandler? ImportCollectionRequested;
        /// <summary>Requests the responsive host's existing Maj collection export picker.</summary>
        public event EventHandler? ExportCollectionRequested;

        /// <summary>Opens the inspected song's cover, metadata and existing music actions.</summary>
        public void OpenTrackDetails()
        {
            if (DataContext is MainViewModel { SelectedTrack: not null })
            {
                UpdateSheetViewport();
                var sheet = this.FindControl<InterruptibleSongSheet>("MobileTrackSheet")!;
                sheet.SetCurrentValue(InterruptibleSongSheet.IsExpandedProperty, false);
                sheet.IsOpen = true;
            }
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SelectedSectionIndexProperty && _attached)
            {
                ReleaseMiniPlayerGesture();
                this.FindControl<InterruptibleSongSheet>("MobileTrackSheet")!.IsOpen = false;
                PresentSection();
            }
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            LayoutUpdated += OnMobileLayoutUpdated;
            ObserveOwner();
            PresentSection();
            UpdateSheetViewport();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = false;
            LayoutUpdated -= OnMobileLayoutUpdated;
            ReleaseMiniPlayerGesture();
            ReleaseSection();
            if (_owner is not null)
            {
                _owner.PropertyChanged -= OnOwnerPropertyChanged;
                _owner.CancelSeek();
                _owner = null;
            }

            this.FindControl<InterruptibleSongSheet>("MobileTrackSheet")!.IsOpen = false;
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>Transfers the shell's property observer when its responsive host changes workspace.</summary>
        /// <param name="sender">The mobile shell.</param>
        /// <param name="e">The data-context change.</param>
        private void OnOwnerChanged(object? sender, EventArgs e)
        {
            ReleaseMiniPlayerGesture();
            ObserveOwner();
            if (_attached)
            {
                PresentSection();
            }
        }

        /// <summary>Observes only the currently attached workspace.</summary>
        private void ObserveOwner()
        {
            if (_owner is not null)
            {
                _owner.PropertyChanged -= OnOwnerPropertyChanged;
            }

            _owner = _attached ? DataContext as MainViewModel : null;
            if (_owner is not null)
            {
                _owner.PropertyChanged += OnOwnerPropertyChanged;
            }
        }

        /// <summary>Creates just the active page and restores its browsing segment without navigating again.</summary>
        private void PresentSection()
        {
            this.FindControl<Navbar>("MobileNavbar")!.Title = SectionTitles[SelectedSectionIndex];
            if (DataContext is not MainViewModel owner)
            {
                ReleaseSection();
                return;
            }

            _restoringMode = true;
            try
            {
                var template = (IDataTemplate)Resources[SectionTemplates[SelectedSectionIndex]]!;
                var section = template.Build(owner)!;
                section.DataContext = owner;
                var host = this.FindControl<ContentControl>("MobileSectionHost")!;
                var previous = host.Content as Control;
                host.Content = section;
                if (previous is not null)
                {
                    previous.DataContext = null;
                }
                RestoreBrowsingMode();
                UpdateBrowseViewport();
            }
            finally
            {
                _restoringMode = false;
            }
        }

        /// <summary>Detaches the inactive page's inherited bindings and browse subscriptions.</summary>
        private void ReleaseSection()
        {
            var host = this.FindControl<ContentControl>("MobileSectionHost");
            if (host?.Content is Control previous)
            {
                previous.DataContext = null;
                host.Content = null;
            }
        }

        /// <summary>Keeps phone artwork in one column while wider touch hosts use the shared tablet grouping.</summary>
        /// <param name="sender">The touch shell.</param>
        /// <param name="e">The updated touch viewport size.</param>
        private void OnMobileSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            ReleaseMiniPlayerGesture();
            UpdateSheetViewport();
            UpdateBrowseViewport();
        }

        /// <summary>Keeps the sheet's clipped bottom on the actual navigation bar after safe-area or footer layout changes.</summary>
        /// <param name="sender">The laid-out mobile shell.</param>
        /// <param name="e">The completed layout notification.</param>
        private void OnMobileLayoutUpdated(object? sender, EventArgs e)
        {
            UpdateSheetViewport();
        }

        /// <summary>Measures the navigation bar in the overlay parent's coordinates without using a fixed bar height.</summary>
        private void UpdateSheetViewport()
        {
            var sheet = this.FindControl<InterruptibleSongSheet>("MobileTrackSheet");
            var navigation = this.FindControl<Tabbar>("MobileNavigationBar");
            if (!_attached || sheet?.GetVisualParent() is not Visual overlay || navigation is null ||
                overlay.Bounds.Height <= 0 || navigation.Bounds.Height <= 0 ||
                navigation.TranslatePoint(default, overlay) is not { } top || !double.IsFinite(top.Y))
            {
                return;
            }

            var inset = Math.Clamp(overlay.Bounds.Height - top.Y, 0, overlay.Bounds.Height);
            var safeTop = this.FindControl<Navbar>("MobileNavbar")?.TranslatePoint(default, overlay) is { } navbarTop
                ? Math.Clamp(navbarTop.Y, 0, overlay.Bounds.Height) : 0;
            if (Math.Abs(sheet.Margin.Top - safeTop) > 0.1)
            {
                sheet.Margin = new Thickness(sheet.Margin.Left, safeTop, sheet.Margin.Right, sheet.Margin.Bottom);
            }
            if (Math.Abs(sheet.BottomInset - inset) > 0.1)
            {
                sheet.BottomInset = inset;
            }
        }

        /// <summary>Tracks a tap or drag only when it begins on the bottom player's noninteractive information area.</summary>
        /// <param name="sender">The persistent bottom player.</param>
        /// <param name="e">The initial pointer contact.</param>
        private void OnMiniPlayerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border player || !_attached || _miniPlayerPointer is not null
                || DataContext is not MainViewModel { PlaybackTrack: { } playing } owner
                || e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(player).Properties.IsLeftButtonPressed)
            {
                return;
            }

            for (var source = e.Source as Visual; source is not null && !ReferenceEquals(source, player); source = source.GetVisualParent())
            {
                if (source is Avalonia.Controls.Button or TextBox or ArcSlider or Thumb)
                {
                    return;
                }
            }

            _miniPlayerGestureStart = e.GetPosition(this);
            _miniPlayerLastPosition = _miniPlayerGestureStart;
            _miniPlayerLastMovement = Stopwatch.GetTimestamp();
            _miniPlayerVelocityY = 0;
            _miniPlayerOwner = owner;
            _miniPlayerTrack = playing;
            _miniPlayerPointer = e.Pointer;
            e.Pointer.Capture(player);
        }

        /// <summary>Lets an upward player pull reveal the current song at the finger's actual position.</summary>
        /// <param name="sender">The persistent bottom player.</param>
        /// <param name="e">The captured pointer movement.</param>
        private async void OnMiniPlayerMoved(object? sender, PointerEventArgs e)
        {
            if (!ReferenceEquals(_miniPlayerPointer, e.Pointer))
            {
                return;
            }

            if (!HasCurrentPlayerGestureOwner())
            {
                ReleaseMiniPlayerGesture();
                return;
            }

            var position = e.GetPosition(this);
            var movement = position - _miniPlayerGestureStart;
            SampleMiniPlayerVelocity(position);
            var started = false;
            var owner = _miniPlayerOwner!;
            var sheet = this.FindControl<InterruptibleSongSheet>("MobileTrackSheet")!;
            if (!_miniPlayerDragging)
            {
                if (movement.Y > 8 || Math.Abs(movement.X) > 8 && Math.Abs(movement.X) > -movement.Y / 1.5)
                {
                    ReleaseMiniPlayerGesture();
                    return;
                }

                if (movement.Y > -6 || -movement.Y < Math.Abs(movement.X) * 1.5)
                {
                    return;
                }

                owner.SelectedTrack = _miniPlayerTrack;
                UpdateSheetViewport();
                if (!sheet.BeginInteractiveOpen())
                {
                    ReleaseMiniPlayerGesture();
                    return;
                }

                _miniPlayerDragging = true;
                started = true;
            }

            e.Handled = true;
            e.PreventGestureRecognition();
            sheet.UpdateInteractiveOpen(-movement.Y);
            if (started)
            {
                await LoadPlayerDetailCoverAsync(owner);
            }
        }

        /// <summary>Settles a following sheet or opens the current song after a short information-area tap.</summary>
        /// <param name="sender">The persistent bottom player.</param>
        /// <param name="e">The pointer release completing the local player gesture.</param>
        private async void OnMiniPlayerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!ReferenceEquals(_miniPlayerPointer, e.Pointer))
            {
                return;
            }

            var movement = e.GetPosition(this) - _miniPlayerGestureStart;
            if (!HasCurrentPlayerGestureOwner())
            {
                ReleaseMiniPlayerGesture();
                return;
            }

            var owner = _miniPlayerOwner!;
            var playing = _miniPlayerTrack!;
            if (_miniPlayerDragging)
            {
                SampleMiniPlayerVelocity(e.GetPosition(this));
                var velocity = Stopwatch.GetElapsedTime(_miniPlayerLastMovement).TotalMilliseconds <= 120
                    ? -_miniPlayerVelocityY : 0;
                var sheet = this.FindControl<InterruptibleSongSheet>("MobileTrackSheet")!;
                sheet.UpdateInteractiveOpen(-movement.Y);
                sheet.EndInteractiveOpen(velocity);
                _miniPlayerDragging = false;
                ReleaseMiniPlayerGesture();
                e.Handled = true;
                e.PreventGestureRecognition();
                return;
            }

            var isTap = Math.Abs(movement.X) <= 8 && Math.Abs(movement.Y) <= 8;
            var isUpwardSwipe = movement.Y <= -8 && -movement.Y >= Math.Abs(movement.X) * 1.5;
            ReleaseMiniPlayerGesture();
            if (!isTap && !isUpwardSwipe)
            {
                return;
            }

            e.Handled = true;
            owner.SelectedTrack = playing;
            OpenTrackDetails();
            await LoadPlayerDetailCoverAsync(owner);
        }

        /// <summary>Loads artwork once when a tap or recognized pull begins, without delaying pointer movement.</summary>
        /// <param name="owner">The workspace whose playing song is being inspected.</param>
        /// <returns>The observed cover request.</returns>
        private static async Task LoadPlayerDetailCoverAsync(MainViewModel owner)
        {
            try
            {
                await owner.LoadCoverAsync();
            }
            catch (OperationCanceledException)
            {
                // A changed selection or application shutdown superseded the inspector artwork.
            }
            catch (Exception exception)
            {
                owner.ReportError(exception);
            }
        }

        /// <summary>Forgets an interrupted player-only pointer gesture.</summary>
        /// <param name="sender">The bottom player losing pointer capture.</param>
        /// <param name="e">The capture-loss notification.</param>
        private void OnMiniPlayerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (ReferenceEquals(_miniPlayerPointer, e.Pointer))
            {
                ReleaseMiniPlayerGesture();
            }
        }

        /// <summary>Rejects a pull whose playing song or workspace changed before release.</summary>
        /// <returns>Whether the captured player still belongs to the same actual playback owner.</returns>
        private bool HasCurrentPlayerGestureOwner()
        {
            return ReferenceEquals(DataContext, _miniPlayerOwner) && _miniPlayerTrack is { } captured &&
                _miniPlayerOwner?.PlaybackTrack is { } current && current.Id == captured.Id &&
                current.EndpointId == captured.EndpointId && current.SourceId == captured.SourceId &&
                current.Revision == captured.Revision;
        }

        /// <summary>Samples only actual vertical movement so a stationary release does not erase a recent flick.</summary>
        /// <param name="position">The pointer position in stable mobile-root coordinates.</param>
        private void SampleMiniPlayerVelocity(Point position)
        {
            var distance = position.Y - _miniPlayerLastPosition.Y;
            if (Math.Abs(distance) > 0.001)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(_miniPlayerLastMovement, now).TotalSeconds;
                if (elapsed > 0)
                {
                    _miniPlayerVelocityY = distance / elapsed;
                }

                _miniPlayerLastPosition = position;
                _miniPlayerLastMovement = now;
            }
        }

        /// <summary>Releases the local player gesture without stealing a browser or slider pointer.</summary>
        private void ReleaseMiniPlayerGesture()
        {
            var pointer = _miniPlayerPointer;
            _miniPlayerPointer = null;
            var dragging = _miniPlayerDragging;
            _miniPlayerDragging = false;
            _miniPlayerOwner = null;
            _miniPlayerTrack = null;
            if (dragging)
            {
                this.FindControl<InterruptibleSongSheet>("MobileTrackSheet")?.EndInteractiveOpen(0, true);
            }

            if (pointer is not null && pointer.Captured == this.FindControl<Border>("MobileMiniPlayer"))
            {
                pointer.Capture(null);
            }
        }

        /// <summary>Applies only a viewport grouping change without recreating the active page or its song presentations.</summary>
        private void UpdateBrowseViewport()
        {
            if (FindSectionControl<BrowseTracksView>("MobileSongBrowser") is { } browser && DataContext is MainViewModel owner)
            {
                browser.ForceSingleColumn = false;
                if (browser.Bounds.Width > 0)
                {
                    owner.SetBrowseViewportWidth(browser.Bounds.Width);
                }
            }
        }

        /// <summary>Finds a named control within the one instantiated section template.</summary>
        /// <typeparam name="T">The expected section control.</typeparam>
        /// <param name="name">The template-local name.</param>
        /// <returns>The current page's matching control, or null on another page.</returns>
        private T? FindSectionControl<T>(string name) where T : Control
        {
            return this.FindControl<ContentControl>("MobileSectionHost")?.Content is Control section
                ? section.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name) : null;
        }

        /// <summary>Updates the selected browsing mode after existing commands finish without rebuilding the page.</summary>
        /// <param name="sender">The shared view model.</param>
        /// <param name="e">The changed property.</param>
        private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_miniPlayerPointer is not null && e.PropertyName == nameof(MainViewModel.PlaybackTitle) &&
                !HasCurrentPlayerGestureOwner())
            {
                ReleaseMiniPlayerGesture();
            }

            if (e.PropertyName is nameof(MainViewModel.IsRecommendationsMode) or nameof(MainViewModel.IsPlaylistsMode)
                or nameof(MainViewModel.IsLibraryMode) or nameof(MainViewModel.IsSearchExpanded))
            {
                RestoreBrowsingMode();
            }
        }

        /// <summary>Projects the shared browsing mode into the mobile segments and search action.</summary>
        private void RestoreBrowsingMode()
        {
            if (DataContext is not MainViewModel owner)
            {
                return;
            }

            var restoring = _restoringMode;
            _restoringMode = true;
            try
            {
                if (FindSectionControl<Segmented>("MobileHomeModes") is { } modes)
                {
                    modes.SelectedIndex = owner.IsLibraryMode ? 2 : owner.IsPlaylistsMode ? 1 : 0;
                }

            }
            finally
            {
                _restoringMode = restoring;
            }
        }

        /// <summary>Changes browsing through the same observed commands as the desktop segments.</summary>
        /// <param name="sender">The mobile mode selector.</param>
        /// <param name="e">The selected mode.</param>
        private void OnHomeModeChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!_restoringMode && e.AddedItems.Count > 0 && DataContext is MainViewModel owner && sender is Segmented modes)
            {
                var command = modes.SelectedIndex switch
                {
                    1 => owner.ShowPlaylistsModeCommand,
                    2 => owner.ShowLibraryModeCommand,
                    _ => owner.ShowRecommendationsModeCommand
                };
                if (command.CanExecute(null))
                {
                    command.Execute(null);
                }
            }
        }

        /// <summary>Opens or exits progressive search while preserving the shared search behavior.</summary>
        /// <param name="sender">The navbar search action.</param>
        /// <param name="e">The click notification.</param>
        private void OnSearchClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel owner)
            {
                var command = owner.IsSearchExpanded ? owner.CancelSearchCommand : owner.BeginSearchCommand;
                if (command.CanExecute(null))
                {
                    command.Execute(null);
                    Dispatcher.UIThread.Post(() => FindSectionControl<Searchbar>("MobileSearchBox")?.Focus(), DispatcherPriority.Input);
                }
            }
        }

        /// <summary>Submits the existing search command when the soft keyboard's Search key is pressed.</summary>
        /// <param name="sender">The mobile search field.</param>
        /// <param name="e">The submit notification.</param>
        private void OnSearchSubmitted(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel owner && owner.SearchCommand.CanExecute(null))
            {
                owner.SearchCommand.Execute(null);
            }
        }

        /// <summary>Returns to the home browser after opening the selected playlist.</summary>
        /// <param name="sender">The playlist open action.</param>
        /// <param name="e">The click notification.</param>
        private void OnOpenPlaylistClicked(object? sender, RoutedEventArgs e)
        {
            SelectedSectionIndex = 0;
        }

        /// <summary>Explicitly plays the inspected song even when a different queue owner is paused.</summary>
        /// <param name="sender">The inspected song's play action.</param>
        /// <param name="e">The click notification.</param>
        private async void OnPlaySelectedClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel owner)
            {
                this.FindControl<InterruptibleSongSheet>("MobileTrackSheet")!.IsOpen = false;
                await owner.PlaySelectedAsync();
            }
        }

        /// <summary>Persists only actual saved-account choices in the currently instantiated card.</summary>
        /// <param name="sender">The selected source's account picker.</param>
        /// <param name="e">The added and removed account choices.</param>
        private void OnSavedAccountSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is Control { DataContext: EndpointCardViewModel owner } && e.AddedItems.OfType<SavedAccountOption>().Any()
                && owner.SelectAccountCommand.CanExecute(null))
            {
                owner.SelectAccountCommand.Execute(null);
            }
        }

        /// <summary>Requests the host's existing folder picker without duplicating import logic.</summary>
        /// <param name="sender">The folder browse action.</param>
        /// <param name="e">The click notification.</param>
        private void OnDirectoryBrowseClicked(object? sender, RoutedEventArgs e)
        {
            BrowseDirectoryRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Requests the host's existing collection import picker.</summary>
        /// <param name="sender">The import action.</param>
        /// <param name="e">The click notification.</param>
        private void OnCollectionImportClicked(object? sender, RoutedEventArgs e)
        {
            ImportCollectionRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Requests the host's existing collection export picker.</summary>
        /// <param name="sender">The export action.</param>
        /// <param name="e">The click notification.</param>
        private void OnCollectionExportClicked(object? sender, RoutedEventArgs e)
        {
            ExportCollectionRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Begins the existing seek preview before the slider changes its value.</summary>
        /// <param name="sender">The seek slider.</param>
        /// <param name="e">The pointer press.</param>
        private void OnSeekPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is MainViewModel owner && (e.Pointer.Type is PointerType.Touch or PointerType.Pen
                || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed))
            {
                owner.BeginSeek();
            }
        }

        /// <summary>Commits the existing seek after the slider processes the pointer release.</summary>
        /// <param name="sender">The seek slider.</param>
        /// <param name="e">The pointer release.</param>
        private async void OnSeekReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (DataContext is MainViewModel owner && (e.InitialPressMouseButton == MouseButton.Left || e.Pointer.Type is PointerType.Touch or PointerType.Pen))
            {
                await owner.CommitSeekAsync();
            }
        }

        /// <summary>Preserves the existing keyboard preview and cancellation behavior.</summary>
        /// <param name="sender">The seek slider.</param>
        /// <param name="e">The keyboard press.</param>
        private void OnSeekKeyPressed(object? sender, KeyEventArgs e)
        {
            if (DataContext is MainViewModel owner)
            {
                if (e.Key == Key.Escape)
                {
                    owner.CancelSeek();
                    e.Handled = true;
                }
                else if (IsSeekKey(e.Key) && e.KeyModifiers == KeyModifiers.None)
                {
                    if (owner.BeginSeek())
                    {
                        var target = e.Key switch
                        {
                            Key.Home => 0,
                            Key.End => owner.DurationSeconds,
                            Key.Left or Key.Down => owner.PositionSeconds - 5,
                            Key.Right or Key.Up => owner.PositionSeconds + 5,
                            Key.PageDown => owner.PositionSeconds - 15,
                            _ => owner.PositionSeconds + 15
                        };
                        owner.PositionSeconds = Math.Clamp(target, 0, owner.DurationSeconds);
                        if (sender is RangeBase slider)
                        {
                            slider.SetCurrentValue(RangeBase.ValueProperty, owner.PositionSeconds);
                        }
                    }

                    e.Handled = true;
                }
            }
        }

        /// <summary>Commits the completed seek navigation key.</summary>
        /// <param name="sender">The seek slider.</param>
        /// <param name="e">The keyboard release.</param>
        private async void OnSeekKeyReleased(object? sender, KeyEventArgs e)
        {
            if (IsSeekKey(e.Key) && DataContext is MainViewModel owner)
            {
                await owner.CommitSeekAsync();
            }
        }

        /// <summary>Discards an unfinished seek when the input focus leaves the player.</summary>
        /// <param name="sender">The seek slider.</param>
        /// <param name="e">The focus notification.</param>
        private void OnSeekLostFocus(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel { IsSeeking: true } owner)
            {
                owner.CancelSeek();
            }
        }

        /// <summary>Identifies the keyboard keys participating in a seek gesture.</summary>
        /// <param name="key">The input key.</param>
        /// <returns>Whether the key may change the timeline value.</returns>
        private static bool IsSeekKey(Key key)
        {
            return key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;
        }
    }
}
