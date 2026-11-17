#nullable enable

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc.Gestures;
using TKCode.Arc.Mobile;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Uses Arc's song sheet template with one reversible position for opening, dismissal and dragging.</summary>
    /// <remarks>One position drives the closed, half-height and expanded resting points.</remarks>
    public sealed class InterruptibleSongSheet : Sheet
    {
        /// <summary>Reserves the measured navigation area below the sheet's own clipped viewport.</summary>
        public static readonly StyledProperty<double> BottomInsetProperty = AvaloniaProperty.Register<InterruptibleSongSheet, double>(
            nameof(BottomInset), coerce: (_, value) => double.IsFinite(value) ? Math.Max(0, value) : 0);
        /// <summary>Identifies the expanded resting point while retaining the inherited open state.</summary>
        public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<InterruptibleSongSheet, bool>(
            nameof(IsExpanded), defaultBindingMode: BindingMode.TwoWay);
        /// <summary>Identifies expanded content during an upward preview as well as at its committed resting point.</summary>
        public static readonly DirectProperty<InterruptibleSongSheet, bool> ShowsExpandedContentProperty =
            AvaloniaProperty.RegisterDirect<InterruptibleSongSheet, bool>(nameof(ShowsExpandedContent), sheet => sheet.ShowsExpandedContent);
        /// <summary>Keeps the closed panel's outline beyond the viewport.</summary>
        private const double ClosedGap = 24;
        /// <summary>Finishes a tapped or released sheet promptly without changing direct pointer tracking.</summary>
        private static readonly TimeSpan SettleDuration = TimeSpan.FromMilliseconds(230);
        /// <summary>Starts each released transition quickly and eases smoothly into its resting position.</summary>
        private static readonly Easing SettleEaseOut = new SplineEasing(0.16, 1, 0.3, 1);
        /// <summary>Applies the shared position to the official panel without changing its layout.</summary>
        private readonly TranslateTransform _translation = new();
        /// <summary>Retains the template's dismissible background.</summary>
        private Border? _scrim;
        /// <summary>Retains the content-sized panel from the Arc template.</summary>
        private Border? _panel;
        /// <summary>Retains the title and grabber gesture surface.</summary>
        private Control? _handle;
        /// <summary>Retains the original scrollable content surface.</summary>
        private ScrollViewer? _body;
        /// <summary>Retains the original title so expanded content can provide its own fixed header.</summary>
        private TextBlock? _title;
        /// <summary>Retains the content control under the initial contact for nested scrolling arbitration.</summary>
        private Control? _bodyGestureSource;
        /// <summary>Owns the sole animated progress for the attached template.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Owns a drag beginning on the title or grabber.</summary>
        private DragTracker? _handleDrag;
        /// <summary>Owns a downward pull beginning at the top of the content.</summary>
        private DragTracker? _bodyDrag;
        /// <summary>Retains the current operation instead of an unobserved completion continuation.</summary>
        private Task<bool> _sheetUpdate = Task.FromResult(true);
        /// <summary>Retains the actual visible progress independently of its open target.</summary>
        private double _progress;
        /// <summary>Retains the panel position at the start of a pointer drag.</summary>
        private double _dragOrigin;
        /// <summary>Indicates that the sheet must remain present through an unfinished exit.</summary>
        private bool _present;
        /// <summary>Indicates that the finger currently owns the position.</summary>
        private bool _dragging;
        /// <summary>Distinguishes a player-originated opening pull from a drag inside the sheet.</summary>
        private bool _interactiveOpening;
        /// <summary>Retains the displayed offset caught by an external opening pull.</summary>
        private double _interactiveOriginOffset;
        /// <summary>Retains the previous resting target for an interrupted external pull.</summary>
        private bool _interactiveOriginOpen;
        /// <summary>Retains the external gesture's expanded resting point for cancellation.</summary>
        private bool _interactiveOriginExpanded;
        /// <summary>Retains the sheet gesture's original expanded resting point.</summary>
        private bool _dragOriginExpanded;
        /// <summary>Suppresses intermediate retargets while committing two related public properties.</summary>
        private bool _committingDetent;
        /// <summary>Retains whether the actual translated pane has entered its expanded-content region.</summary>
        private bool _showsExpandedContent;
        /// <summary>Retains the installed motion policy for immediate settling when that policy changes.</summary>
        private ArcMotionLevel _level;

        /// <summary>Creates an overlay whose moving content cannot paint or catch touches below its viewport.</summary>
        public InterruptibleSongSheet()
        {
            ClipToBounds = true;
            ArcMotion.SetTracks(this, true);
            Classes.CollectionChanged += OnMotionPolicyChanged;
        }

        /// <summary>Gets or sets the measured distance from the overlay's bottom to the navigation bar's top.</summary>
        public double BottomInset
        {
            get => GetValue(BottomInsetProperty);
            set => SetValue(BottomInsetProperty, value);
        }

        /// <summary>Gets or sets whether the open pane rests at full height instead of half height.</summary>
        public bool IsExpanded
        {
            get => GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }
        /// <summary>Gets whether a full-content preview is visible while dragging or expanded.</summary>
        public bool ShowsExpandedContent => _showsExpandedContent;

        /// <summary>Gets opening progress from zero when closed to one at either open resting point.</summary>
        public double TransitionProgress => Math.Clamp(_progress / HalfProgress, 0, 1);
        /// <summary>Gets the actual panel offset below its expanded resting point, in device-independent pixels.</summary>
        public new double PanelOffset => _translation.Y;
        /// <summary>Gets completion of the current transition; false means that another target interrupted it.</summary>
        public Task<bool> SheetUpdate => _sheetUpdate;
        /// <summary>Gets the distance required to move the complete panel beyond the bottom edge.</summary>
        private double ClosedOffset => FullPanelHeight + ClosedGap;
        /// <summary>Gets the stable full-height pane size used for all three resting points.</summary>
        private double FullPanelHeight => Math.Max(0, Bounds.Height) * Math.Clamp(MaxHeightFraction, 0.1, 1);
        /// <summary>Gets the half-height pane's distance below the expanded resting point.</summary>
        private double HalfOffset => FullPanelHeight * 0.5;
        /// <summary>Gets the raw position at which exactly half of the pane remains visible.</summary>
        private double HalfProgress => 1 - HalfOffset / ClosedOffset;
        /// <summary>Gets the committed normalized position without carrying pointer velocity into settling.</summary>
        private double TargetProgress => IsOpen ? IsExpanded ? 1 : HalfProgress : 0;

        /// <summary>Catches the current position for a direct upward pull beginning on the mini player.</summary>
        /// <returns>Whether the attached sheet is ready to follow the pointer.</returns>
        public bool BeginInteractiveOpen()
        {
            ApplyTemplate();
            if (_motion is null || _panel is null)
            {
                return false;
            }

            _motion.Stop();
            _interactiveOriginOpen = IsOpen;
            _interactiveOriginExpanded = IsExpanded;
            _interactiveOpening = true;
            _dragging = true;
            _present = true;
            _panel.IsVisible = true;
            IsHitTestVisible = true;
            UpdatePanelSize();
            _panel.UpdateLayout();
            ApplyProgress(_motion.Value);
            _interactiveOriginOffset = Math.Max(0, PanelOffset - (_progress == 0 ? ClosedGap : 0));
            return true;
        }

        /// <summary>Moves the panel by the finger's actual travel, without easing or a speed limit.</summary>
        /// <param name="upwardDistance">Upward travel from the original player contact, in logical pixels.</param>
        public void UpdateInteractiveOpen(double upwardDistance)
        {
            if (_interactiveOpening && _motion is not null && double.IsFinite(upwardDistance))
            {
                var offset = _interactiveOriginOffset - upwardDistance;
                _motion.Jump(1 - Math.Clamp(offset / ClosedOffset, 0, 1));
            }
        }

        /// <summary>Settles an external pull from its current position after release or cancellation.</summary>
        /// <param name="upwardVelocity">The final upward pointer velocity, in logical pixels per second.</param>
        /// <param name="cancelled">Whether capture, viewport or playback ownership invalidated the gesture.</param>
        public void EndInteractiveOpen(double upwardVelocity, bool cancelled = false)
        {
            if (!_interactiveOpening)
            {
                return;
            }

            _interactiveOpening = false;
            _dragging = false;
            var distance = _interactiveOriginOffset - PanelOffset;
            var open = cancelled ? _interactiveOriginOpen : distance >= 8 ||
                _progress >= HalfProgress * 0.5 || upwardVelocity > 500 && distance >= 6;
            var expanded = cancelled ? _interactiveOriginExpanded : open && _progress > (HalfProgress + 1) * 0.5;
            CommitDetent(open, expanded);
        }

        /// <inheritdoc />
        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            // Sheet's template hook installs its private spring and drag writers. ContentControl has no
            // template hook of its own; its presenter registers independently, so only that private engine is replaced.
            DisconnectTemplate();
            _scrim = e.NameScope.Find<Border>("PART_Scrim");
            _panel = e.NameScope.Find<Border>("PART_Panel");
            _handle = e.NameScope.Find<Control>("PART_Handle");
            _body = e.NameScope.Find<ScrollViewer>("PART_Body");
            _title = e.NameScope.Find<TextBlock>("PART_Title");
            ConnectTemplate();
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ConnectTemplate();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            DisconnectTemplate();
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property == IsOpenProperty || change.Property == IsExpandedProperty)
            {
                // Keep the inherited public property and its bindings, without starting the private Arc spring.
                if (!_committingDetent)
                {
                    if (change.Property == IsOpenProperty && !IsOpen && IsExpanded)
                    {
                        CommitDetent(false, false);
                    }
                    else if (change.Property == IsExpandedProperty && IsExpanded && !IsOpen)
                    {
                        CommitDetent(true, true);
                    }
                    else
                    {
                        UpdatePanelSize();
                        TransitionToTarget();
                    }
                }
                return;
            }

            base.OnPropertyChanged(change);
            if (change.Property == BottomInsetProperty)
            {
                SetCurrentValue(MarginProperty, new Thickness(Margin.Left, Margin.Top, Margin.Right, BottomInset));
                if (BottomInset > 0)
                {
                    SetCurrentValue(SafeAreaBottomProperty, 0d);
                }
                else
                {
                    ClearValue(SafeAreaBottomProperty);
                }
            }

            if (change.Property == BoundsProperty || change.Property == MaxHeightFractionProperty)
            {
                UpdatePanelSize();
                if (!_dragging)
                {
                    TransitionToTarget();
                }
            }
            else if (change.Property == TitleProperty)
            {
                UpdateBodySize();
            }
        }

        /// <summary>Attaches one motion driver and the original template's gesture surfaces.</summary>
        private void ConnectTemplate()
        {
            if (_panel is null || _motion is not null)
            {
                return;
            }

            _panel.RenderTransform = _translation;
            _panel.SizeChanged += OnPanelSizeChanged;
            _level = ArcMotion.GetEffectiveLevel(this);
            _motion = new MotionValue<double>(this, TargetProgress, MotionVectors.Double);
            _motion.Changed += ApplyProgress;
            _present = IsOpen;
            UpdatePanelSize();
            ApplyProgress(TargetProgress);
            if (_scrim is not null)
            {
                _scrim.PointerPressed += OnScrimPressed;
            }

            if (_handle is not null)
            {
                _handle.SizeChanged += OnHandleSizeChanged;
                _handleDrag = CreateDrag(_handle, false);
            }

            if (_body is not null)
            {
                _bodyDrag = CreateDrag(_body, true);
                _bodyDrag.Starting += OnBodyDragStarting;
                _body.AddHandler(PointerPressedEvent, OnBodyPointerPressed, RoutingStrategies.Tunnel, true);
            }
        }

        /// <summary>Stops all template work before detach or template replacement.</summary>
        private void DisconnectTemplate()
        {
            _interactiveOpening = false;
            _dragging = false;
            _bodyGestureSource = null;
            if (_handle is not null)
            {
                _handle.SizeChanged -= OnHandleSizeChanged;
            }
            _body?.RemoveHandler(PointerPressedEvent, OnBodyPointerPressed);
            _handleDrag?.Dispose();
            _handleDrag = null;
            _bodyDrag?.Dispose();
            _bodyDrag = null;
            if (_motion is not null)
            {
                _motion.Changed -= ApplyProgress;
                _motion.Dispose();
                _motion = null;
            }

            if (_panel is not null)
            {
                _panel.SizeChanged -= OnPanelSizeChanged;
            }

            if (_scrim is not null)
            {
                _scrim.PointerPressed -= OnScrimPressed;
            }

            _sheetUpdate = Task.FromResult(true);
        }

        /// <summary>Retargets from the currently displayed position without carrying the old direction's velocity.</summary>
        private void TransitionToTarget()
        {
            var target = TargetProgress;
            if (_motion is null || _panel is null)
            {
                ApplyProgress(target);
                return;
            }

            _interactiveOpening = false;
            _dragging = false;
            _motion.Stop();
            if (IsOpen)
            {
                _present = true;
                _panel.IsVisible = true;
                IsHitTestVisible = true;
                UpdatePanelSize();
                _panel.UpdateLayout();
                ApplyProgress(_motion.Value);
                Focus();
            }

            if (Math.Abs(_motion.Value - target) < 0.000001 || !IsEffectivelyVisible ||
                ArcMotion.GetEffectiveLevel(this) != ArcMotionLevel.Full)
            {
                _motion.Jump(target);
                _sheetUpdate = Task.FromResult(true);
                return;
            }

            _sheetUpdate = _motion.AnimateTo(target, MotionSpec.Tween(SettleDuration, SettleEaseOut));
        }

        /// <summary>Updates both panel translation and scrim directly from the same current progress.</summary>
        /// <param name="progress">The current displayed opening fraction.</param>
        private void ApplyProgress(double progress)
        {
            _progress = Math.Clamp(progress, 0, 1);
            _translation.Y = ClosedOffset * (1 - _progress);
            SetAndRaise(ShowsExpandedContentProperty, ref _showsExpandedContent, IsExpanded || _progress > (HalfProgress + 1) * 0.5);
            UpdateBodySize();
            if (_scrim is not null)
            {
                _scrim.Opacity = TransitionProgress;
            }

            if (!IsOpen && !_dragging && _progress == 0)
            {
                _present = false;
            }

            IsHitTestVisible = IsOpen || _present;
            if (_panel is not null)
            {
                _panel.IsVisible = IsOpen || _present;
            }
        }

        /// <summary>Keeps one full-size pane within the current page so translation alone defines its resting point.</summary>
        private void UpdatePanelSize()
        {
            if (_panel is not null)
            {
                _panel.MaxHeight = FullPanelHeight;
                _panel.Height = FullPanelHeight;
                UpdateBodySize();
            }
        }

        /// <summary>Fits the live body to the visible detent while keeping its fixed actions above the lower viewport edge.</summary>
        private void UpdateBodySize()
        {
            if (_body is null)
            {
                return;
            }
            if (_title is not null)
            {
                _title.IsVisible = !ShowsExpandedContent && !string.IsNullOrEmpty(Title);
            }
            var handle = _handle is null ? 44 : _handle.Bounds.Height + _handle.Margin.Top + _handle.Margin.Bottom;
            var visible = ShowsExpandedContent ? Math.Clamp(FullPanelHeight - PanelOffset, HalfOffset, FullPanelHeight) : HalfOffset;
            _body.Height = Math.Max(0, visible - handle - (_panel?.BorderThickness.Top ?? 0) - (_panel?.BorderThickness.Bottom ?? 0));
            _body.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            _body.VerticalScrollBarVisibility = ShowsExpandedContent ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        }

        /// <summary>Preserves the same visible bottom edge when the grabber title changes height.</summary>
        /// <param name="sender">The template handle.</param>
        /// <param name="e">Its updated measured size.</param>
        private void OnHandleSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            UpdateBodySize();
        }

        /// <summary>Commits a resting point atomically before starting one reversible settle.</summary>
        /// <param name="open">Whether at least the half-height pane remains open.</param>
        /// <param name="expanded">Whether the pane rests at full height.</param>
        private void CommitDetent(bool open, bool expanded)
        {
            _committingDetent = true;
            try
            {
                SetCurrentValue(IsExpandedProperty, open && expanded);
                SetCurrentValue(IsOpenProperty, open);
            }
            finally
            {
                _committingDetent = false;
            }
            UpdatePanelSize();
            TransitionToTarget();
        }

        /// <summary>Settles to the committed resting point when inherited motion is reduced or disabled.</summary>
        /// <param name="sender">The Arc policy classes.</param>
        /// <param name="e">The updated motion classification.</param>
        private void OnMotionPolicyChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var level = ArcMotion.GetEffectiveLevel(this);
            if (_level != level)
            {
                _level = level;
                if (!_dragging)
                {
                    TransitionToTarget();
                }
            }
        }

        /// <summary>Recomputes only the pixel offset when content or the viewport changes size.</summary>
        /// <param name="sender">The Arc panel.</param>
        /// <param name="e">Its new measured size.</param>
        private void OnPanelSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            ApplyProgress(_progress);
        }

        /// <summary>Dismisses a sheet immediately toward its closed target when the background is pressed.</summary>
        /// <param name="sender">The template scrim.</param>
        /// <param name="e">The background press.</param>
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
        {
            if (IsOpen && IsDismissible)
            {
                e.Handled = true;
                SetCurrentValue(IsOpenProperty, false);
            }
        }

        /// <summary>Creates the same axis-locked Arc recognizer for a handle or touch-only content pull.</summary>
        /// <param name="surface">The existing template gesture surface.</param>
        /// <param name="content">Whether touches must defer to content scrolling.</param>
        /// <returns>The owned gesture recognizer.</returns>
        private DragTracker CreateDrag(Control surface, bool content)
        {
            var tracker = new DragTracker(surface, new DragOptions
            {
                Axis = DragAxis.Vertical,
                Threshold = content ? 4 : 3,
                RejectCrossAxis = content,
                HandledEventsToo = content,
            })
            {
                CanBegin = args => IsOpen && (!content || args.Pointer.Type == PointerType.Touch),
            };
            tracker.Started += OnDragStarted;
            tracker.Moved += OnDragMoved;
            tracker.Completed += OnDragCompleted;
            tracker.Cancelled += OnDragCancelled;
            return tracker;
        }

        /// <summary>Leaves ordinary scrolling with the content until a downward pull reaches its top.</summary>
        /// <param name="sender">The content recognizer.</param>
        /// <param name="e">The proposed content drag.</param>
        private void OnBodyDragStarting(object? sender, DragGestureEventArgs e)
        {
            var ancestors = _bodyGestureSource?.GetVisualAncestors().OfType<Control>().ToArray() ?? [];
            if (_bodyGestureSource is TextBox || ancestors.Any(control => control is TextBox))
            {
                e.Cancel = true;
                return;
            }
            var body = _bodyGestureSource as ScrollViewer ?? ancestors.OfType<ScrollViewer>().FirstOrDefault() ?? _body;
            e.Cancel = body is not null && body.Extent.Height > body.Viewport.Height + 1 &&
                !(body.Offset.Y <= 0 && e.Offset.Y > 0);
        }

        /// <summary>Remembers nested comment scrolling before Arc claims a vertical gesture.</summary>
        /// <param name="sender">The outer sheet body.</param>
        /// <param name="e">The original contact.</param>
        private void OnBodyPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _bodyGestureSource = e.Source as Control;
        }

        /// <summary>Catches the displayed position without first moving to the previous animation target.</summary>
        /// <param name="sender">The active recognizer.</param>
        /// <param name="e">The pointer's accumulated displacement.</param>
        private void OnDragStarted(object? sender, DragGestureEventArgs e)
        {
            _motion?.Stop();
            _dragging = true;
            _dragOrigin = PanelOffset - e.Offset.Y;
            _dragOriginExpanded = IsExpanded;
        }

        /// <summary>Maps pointer motion to the same normalized opening fraction.</summary>
        /// <param name="sender">The active recognizer.</param>
        /// <param name="e">The pointer's accumulated displacement.</param>
        private void OnDragMoved(object? sender, DragGestureEventArgs e)
        {
            if (_dragging && _motion is not null)
            {
                var offset = IsDismissible ? _dragOrigin + e.Offset.Y : 0;
                _motion.Jump(1 - Math.Clamp(offset / ClosedOffset, 0, 1));
            }
        }

        /// <summary>Closes a downward flick or settles the caught panel from its actual release position.</summary>
        /// <param name="sender">The completing recognizer.</param>
        /// <param name="e">The pointer's release velocity.</param>
        private void OnDragCompleted(object? sender, DragGestureEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            var projectedOffset = PanelOffset + e.Velocity.Y * 0.16;
            var open = !IsDismissible || projectedOffset < (HalfOffset + ClosedOffset) * 0.5;
            var expanded = open && projectedOffset < HalfOffset * 0.5;
            if (!_dragOriginExpanded && (e.Offset.Y < -36 || e.Velocity.Y < -500))
            {
                open = true;
                expanded = true;
            }
            else if (_dragOriginExpanded && e.Offset.Y > 36 && open)
            {
                expanded = false;
            }
            else if (!_dragOriginExpanded && IsDismissible && (e.Offset.Y > 48 || e.Velocity.Y > 500))
            {
                open = false;
                expanded = false;
            }
            CommitDetent(open, expanded);
        }

        /// <summary>Settles a cancelled gesture from its current position without retaining pointer velocity.</summary>
        /// <param name="sender">The cancelled recognizer.</param>
        /// <param name="e">The last pointer state.</param>
        private void OnDragCancelled(object? sender, DragGestureEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                CommitDetent(true, _dragOriginExpanded);
            }
        }
    }
}
