#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using TKCode.Arc.Gestures;
using TKCode.Arc.Mobile;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Uses Arc's song sheet template with one reversible position for opening, dismissal and dragging.</summary>
    /// <remarks>The song details use the content-sized sheet; multiple resting detents are not used.</remarks>
    public sealed class InterruptibleSongSheet : Sheet
    {
        /// <summary>Reserves the measured navigation area below the sheet's own clipped viewport.</summary>
        public static readonly StyledProperty<double> BottomInsetProperty = AvaloniaProperty.Register<InterruptibleSongSheet, double>(
            nameof(BottomInset), coerce: (_, value) => double.IsFinite(value) ? Math.Max(0, value) : 0);
        /// <summary>Keeps the closed panel's outline beyond the viewport.</summary>
        private const double ClosedGap = 24;
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

        /// <summary>Creates an overlay whose moving content cannot paint or catch touches below its viewport.</summary>
        public InterruptibleSongSheet()
        {
            ClipToBounds = true;
        }

        /// <summary>Gets or sets the measured distance from the overlay's bottom to the navigation bar's top.</summary>
        public double BottomInset
        {
            get => GetValue(BottomInsetProperty);
            set => SetValue(BottomInsetProperty, value);
        }

        /// <summary>Gets the displayed opening progress, from zero when closed to one when fully open.</summary>
        public double TransitionProgress => _progress;
        /// <summary>Gets the actual panel offset below its open position, in device-independent pixels.</summary>
        public new double PanelOffset => _translation.Y;
        /// <summary>Gets completion of the current transition; false means that another target interrupted it.</summary>
        public Task<bool> SheetUpdate => _sheetUpdate;
        /// <summary>Gets the distance required to move the complete panel beyond the bottom edge.</summary>
        private double ClosedOffset => Math.Max(0, _panel?.Bounds.Height ?? 0) + ClosedGap;

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
                _progress >= 0.5 || upwardVelocity > 500 && distance >= 6;
            if (IsOpen == open)
            {
                TransitionToTarget();
            }
            else
            {
                SetCurrentValue(IsOpenProperty, open);
            }
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
            if (change.Property == IsOpenProperty)
            {
                // Keep the inherited public property and its bindings, without starting the private Arc spring.
                TransitionToTarget();
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
            _motion = new MotionValue<double>(this, IsOpen ? 1 : 0, MotionVectors.Double);
            _motion.Changed += ApplyProgress;
            _present = IsOpen;
            UpdatePanelSize();
            ApplyProgress(IsOpen ? 1 : 0);
            if (_scrim is not null)
            {
                _scrim.PointerPressed += OnScrimPressed;
            }

            if (_handle is not null)
            {
                _handleDrag = CreateDrag(_handle, false);
            }

            if (_body is not null)
            {
                _bodyDrag = CreateDrag(_body, true);
                _bodyDrag.Starting += OnBodyDragStarting;
            }
        }

        /// <summary>Stops all template work before detach or template replacement.</summary>
        private void DisconnectTemplate()
        {
            _interactiveOpening = false;
            _dragging = false;
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
            var target = IsOpen ? 1d : 0d;
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

            _sheetUpdate = _motion.AnimateTo(target, MotionSpec.Tween(MusicMotion.SurfaceDuration, MusicMotion.InOut));
        }

        /// <summary>Updates both panel translation and scrim directly from the same current progress.</summary>
        /// <param name="progress">The current displayed opening fraction.</param>
        private void ApplyProgress(double progress)
        {
            _progress = Math.Clamp(progress, 0, 1);
            _translation.Y = ClosedOffset * (1 - _progress);
            if (_scrim is not null)
            {
                _scrim.Opacity = _progress;
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

        /// <summary>Keeps the original content-sized panel within the current page height.</summary>
        private void UpdatePanelSize()
        {
            if (_panel is not null)
            {
                _panel.MaxHeight = Math.Max(0, Bounds.Height) * Math.Clamp(MaxHeightFraction, 0.1, 1);
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
            e.Cancel = _body is { } body && body.Extent.Height > body.Viewport.Height + 1 &&
                !(body.Offset.Y <= 0 && e.Offset.Y > 0);
        }

        /// <summary>Catches the displayed position without first moving to the previous animation target.</summary>
        /// <param name="sender">The active recognizer.</param>
        /// <param name="e">The pointer's accumulated displacement.</param>
        private void OnDragStarted(object? sender, DragGestureEventArgs e)
        {
            _motion?.Stop();
            _dragging = true;
            _dragOrigin = PanelOffset - e.Offset.Y;
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
            var projectedOffset = PanelOffset + e.Velocity.Y * 0.2;
            if (IsDismissible && (e.Velocity.Y > 500 || projectedOffset > ClosedOffset / 2))
            {
                SetCurrentValue(IsOpenProperty, false);
            }
            else
            {
                TransitionToTarget();
            }
        }

        /// <summary>Settles a cancelled gesture from its current position without retaining pointer velocity.</summary>
        /// <param name="sender">The cancelled recognizer.</param>
        /// <param name="e">The last pointer state.</param>
        private void OnDragCancelled(object? sender, DragGestureEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                TransitionToTarget();
            }
        }
    }
}
