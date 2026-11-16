#nullable enable

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Plays a captured song after a reversible cover-corner gesture without moving its card or intercepting the rest of its artwork.</summary>
    public sealed class CoverPlayGesture : Control, ICustomHitTest
    {
        /// <summary>Identifies the square cover's fraction of the surrounding card width.</summary>
        public static readonly StyledProperty<double> CoverFractionProperty = AvaloniaProperty.Register<CoverPlayGesture, double>(nameof(CoverFraction), .5);
        /// <summary>Identifies the current theme's restrained interaction accent.</summary>
        public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<CoverPlayGesture, IBrush?>(nameof(Accent), Brushes.MediumPurple);
        /// <summary>Identifies the theme-aware background underneath the translucent play glyph.</summary>
        public static readonly StyledProperty<IBrush?> SurfaceBrushProperty = AvaloniaProperty.Register<CoverPlayGesture, IBrush?>(nameof(SurfaceBrush), Brushes.Black);
        /// <summary>Identifies the theme-aware play-glyph and focus-ring color.</summary>
        public static readonly StyledProperty<IBrush?> GlyphBrushProperty = AvaloniaProperty.Register<CoverPlayGesture, IBrush?>(nameof(GlyphBrush), Brushes.White);
        /// <summary>Exposes the real reversible paint progress without changing layout.</summary>
        public static readonly DirectProperty<CoverPlayGesture, double> ProgressProperty = AvaloniaProperty.RegisterDirect<CoverPlayGesture, double>(nameof(Progress), control => control.Progress);
        /// <summary>Retains a centered play shape on this control's dispatcher without allocating during an animation.</summary>
        private readonly Geometry _playGlyph = Geometry.Parse("M -3,-5 L 5,0 L -3,5 Z");
        /// <summary>Defines the deliberate horizontal travel required before a release commits playback.</summary>
        private const double CommitProgress = .62;
        /// <summary>Defines the travel before a pending press becomes a horizontal or vertical gesture.</summary>
        private const double DirectionSlop = 8;
        /// <summary>Owns the interruptible Arc paint value while this card is attached.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Retains the actual reversible progress along the complete trajectory.</summary>
        private double _progress;
        /// <summary>Tracks the pointer whose initial press hit the cover-corner circle.</summary>
        private IPointer? _pointer;
        /// <summary>Stores the pointer's initial position independently of the animated circle.</summary>
        private Point _pressPosition;
        /// <summary>Distinguishes a deliberate horizontal drag from an ordinary click.</summary>
        private bool _dragging;
        /// <summary>Rejects another activation while a release or click is settling.</summary>
        private bool _settling;
        /// <summary>Retains a committed activation across a change of the effective motion policy.</summary>
        private bool _commitPending;
        /// <summary>Suppresses repeated keyboard activations until the original key is released.</summary>
        private bool _keyHeld;
        /// <summary>Rejects completion belonging to a recycled, canceled or detached card.</summary>
        private long _version;
        /// <summary>Identifies a currently rendered card that can request playback.</summary>
        private bool _attached;
        /// <summary>Retains the effective Arc motion preference used for the current paint.</summary>
        private ArcMotionLevel _level;
        /// <summary>Caches the accent outline independently of frame-by-frame geometry.</summary>
        private Pen? _accentPen;
        /// <summary>Caches the quiet focus outline independently of frame-by-frame geometry.</summary>
        private Pen? _focusPen;
        /// <summary>Exposes the currently observed completion or rollback.</summary>
        private Task _transitionUpdate = Task.CompletedTask;

        /// <summary>Creates a theme-aware, keyboard-accessible overlay that reserves no layout space of its own.</summary>
        public CoverPlayGesture()
        {
            Focusable = true;
            ClipToBounds = true;
            ArcMotion.SetTracks(this, true);
            SetValue(IsHoldingEnabledProperty, false);
            AutomationProperties.SetName(this, "播放这首");
            Classes.CollectionChanged += OnMotionPolicyChanged;
            DataContextChanged += OnSourceChanged;
            AddHandler(TappedEvent, OnTapHandled, RoutingStrategies.Tunnel);
            AddHandler(DoubleTappedEvent, OnTapHandled, RoutingStrategies.Tunnel);
            AddHandler(ContextRequestedEvent, OnContextHandled, RoutingStrategies.Tunnel);
            RefreshPens();
        }

        /// <summary>Occurs once after an explicitly completed click or drag has finished its outgoing animation.</summary>
        public event EventHandler? PlayRequested;
        /// <summary>Gets or sets the cover-width fraction; loose and small cards use one half, compact cards one third.</summary>
        public double CoverFraction
        {
            get => GetValue(CoverFractionProperty);
            set => SetValue(CoverFractionProperty, value);
        }
        /// <summary>Gets or sets the current theme's interaction accent.</summary>
        public IBrush? Accent
        {
            get => GetValue(AccentProperty);
            set => SetValue(AccentProperty, value);
        }
        /// <summary>Gets or sets the current theme's play-circle surface.</summary>
        public IBrush? SurfaceBrush
        {
            get => GetValue(SurfaceBrushProperty);
            set => SetValue(SurfaceBrushProperty, value);
        }
        /// <summary>Gets or sets the current theme's play-glyph color.</summary>
        public IBrush? GlyphBrush
        {
            get => GetValue(GlyphBrushProperty);
            set => SetValue(GlyphBrushProperty, value);
        }
        /// <summary>Gets the actual position along the reversible growth and exit trajectory.</summary>
        public double Progress => _progress;
        /// <summary>Gets completion of the current click, committed drag or rollback.</summary>
        public Task TransitionUpdate => _transitionUpdate;

        /// <summary>Limits hit testing to the original corner circle, including a modest minimum touch target.</summary>
        /// <param name="point">The point relative to this full-card overlay.</param>
        /// <returns>Whether this point belongs to the initial play affordance.</returns>
        public bool HitTest(Point point)
        {
            if (!IsEffectivelyEnabled || Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return false;
            }

            var (center, radius) = InitialCircle();
            var delta = point - center;
            var hitRadius = Math.Max(18, radius);
            return new Rect(Bounds.Size).Contains(point) && delta.X * delta.X + delta.Y * delta.Y <= hitRadius * hitRadius;
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            _level = ArcMotion.GetEffectiveLevel(this);
            _motion = new MotionValue<double>(this, 0, MotionVectors.Double);
            _motion.Changed += ApplyProgress;
            ResetGesture();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = false;
            ResetGesture();
            if (_motion is { } motion)
            {
                motion.Changed -= ApplyProgress;
                motion.Dispose();
                _motion = null;
            }

            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == AccentProperty || change.Property == GlyphBrushProperty)
            {
                RefreshPens();
            }

            if (change.Property == CoverFractionProperty || change.Property == BoundsProperty
                || change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled
                || change.Property == IsVisibleProperty && !IsVisible)
            {
                ResetGesture();
            }

            if (change.Property == AccentProperty || change.Property == SurfaceBrushProperty || change.Property == GlyphBrushProperty
                || change.Property == CoverFractionProperty || change.Property == IsFocusedProperty)
            {
                InvalidateVisual();
            }
        }

        /// <inheritdoc />
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!HitTest(e.GetPosition(this)) || e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;
            if (!_attached || _settling || _pointer is not null)
            {
                return;
            }

            _version++;
            _pressPosition = e.GetPosition(this);
            _pointer = e.Pointer;
            _dragging = false;
            Focus(NavigationMethod.Pointer);
            e.Pointer.Capture(this);
        }

        /// <inheritdoc />
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!ReferenceEquals(_pointer, e.Pointer))
            {
                return;
            }

            var distance = e.GetPosition(this) - _pressPosition;
            if (!_dragging)
            {
                if (Math.Abs(distance.Y) >= DirectionSlop && Math.Abs(distance.Y) > Math.Abs(distance.X))
                {
                    ResetGesture();
                    return;
                }

                if (Math.Abs(distance.X) < DirectionSlop || Math.Abs(distance.X) < Math.Abs(distance.Y) * 1.25)
                {
                    return;
                }

                _dragging = true;
            }

            e.Handled = true;
            e.PreventGestureRecognition();
            var (center, _) = InitialCircle();
            var travel = Math.Max(72, Bounds.Width - center.X);
            _motion?.Jump(Math.Clamp(distance.X / travel, 0, 1));
        }

        /// <inheritdoc />
        protected override async void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!ReferenceEquals(_pointer, e.Pointer))
            {
                return;
            }

            e.Handled = true;
            e.PreventGestureRecognition();
            var delta = e.GetPosition(this) - _pressPosition;
            var click = !_dragging && Math.Abs(delta.X) < DirectionSlop && Math.Abs(delta.Y) < DirectionSlop;
            var commit = click || _dragging && _progress >= CommitProgress;
            ReleasePointer();
            _transitionUpdate = SettleAsync(commit);
            await _transitionUpdate;
        }

        /// <inheritdoc />
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (ReferenceEquals(_pointer, e.Pointer))
            {
                ResetGesture();
            }
        }

        /// <inheritdoc />
        protected override async void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape && (_pointer is not null || _settling))
            {
                e.Handled = true;
                ResetGesture();
            }
            else if (e.Key is Key.Enter or Key.Space && IsEffectivelyEnabled && _attached)
            {
                e.Handled = true;
                if (!_keyHeld && !_settling && _pointer is null)
                {
                    _keyHeld = true;
                    _transitionUpdate = SettleAsync(true);
                    await _transitionUpdate;
                }
            }
        }

        /// <inheritdoc />
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.Key is Key.Enter or Key.Space)
            {
                _keyHeld = false;
                e.Handled = true;
            }
        }

        /// <inheritdoc />
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return;
            }

            var (start, initialRadius) = InitialCircle();
            var center = start;
            var radius = initialRadius;
            var opacity = IsEffectivelyEnabled ? 1 : .4;
            if (_level == ArcMotionLevel.Full)
            {
                var growth = MusicMotion.InOut.Ease(Math.Clamp(_progress / .55, 0, 1));
                var exit = MusicMotion.InOut.Ease(Math.Clamp((_progress - .55) / .45, 0, 1));
                var enlarged = Math.Max(initialRadius, Bounds.Height * .42);
                radius = initialRadius + (enlarged - initialRadius) * growth;
                center = new Point(start.X + (Bounds.Width / 2 - start.X) * growth,
                    start.Y + (Bounds.Height / 2 - start.Y) * growth);
                center = new Point(center.X + (Bounds.Width + enlarged + 4 - Bounds.Width / 2) * exit, center.Y);
            }
            else if (_level == ArcMotionLevel.Reduced)
            {
                opacity *= 1 - _progress;
            }

            using (context.PushClip(new RoundedRect(new Rect(Bounds.Size), 12)))
            using (context.PushOpacity(opacity))
            {
                using (context.PushOpacity(.76))
                {
                    context.DrawEllipse(SurfaceBrush, null, center, radius, radius);
                }

                using (context.PushOpacity(.12 + _progress * .12))
                {
                    context.DrawEllipse(Accent, null, center, radius, radius);
                }

                using (context.PushOpacity(.45 + _progress * .35))
                {
                    context.DrawEllipse(null, _accentPen, center, radius, radius);
                }

                if (IsFocused && _progress == 0)
                {
                    context.DrawEllipse(null, _focusPen, center, radius + 2, radius + 2);
                }

                var scale = radius / 9;
                using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(center.X + scale * .3, center.Y)))
                {
                    context.DrawGeometry(GlyphBrush, null, _playGlyph);
                }
            }
        }

        /// <summary>Calculates the original affordance inside the cover's lower-right corner.</summary>
        /// <returns>The circle center and radius in card-local logical pixels.</returns>
        private (Point Center, double Radius) InitialCircle()
        {
            var fraction = double.IsFinite(CoverFraction) ? Math.Clamp(CoverFraction, .1, 1) : .5;
            var cover = Math.Max(1, Math.Min(Bounds.Height, Bounds.Width * fraction) - MusicGeometry.OutlineWidth * 2);
            var radius = Math.Max(7, cover / 6);
            var inset = Math.Clamp(cover * .045, 4, 8) + MusicGeometry.OutlineWidth;
            return (new Point(Math.Max(radius, cover + MusicGeometry.OutlineWidth - inset - radius),
                Math.Max(radius, Bounds.Height - inset - radius)), radius);
        }

        /// <summary>Completes or reverses the current path and emits playback only for the same committed card.</summary>
        /// <param name="commit">Whether this explicit gesture has crossed the playback threshold.</param>
        /// <returns>A task representing the finite animation and its safe return to the corner.</returns>
        private async Task SettleAsync(bool commit)
        {
            var version = ++_version;
            var motion = _motion;
            if (!_attached || !IsEffectivelyEnabled || motion is null)
            {
                ResetGesture();
                return;
            }

            _settling = true;
            _commitPending = commit;
            _level = ArcMotion.GetEffectiveLevel(this);
            var target = commit ? 1d : 0;
            var duration = TimeSpan.FromMilliseconds(_level == ArcMotionLevel.Reduced ? 90 : Math.Max(90, 480 * Math.Abs(target - _progress)));
            try
            {
                if (await motion.AnimateTo(target, _level == ArcMotionLevel.Off ? MotionSpec.Instant : MotionSpec.Tween(duration, MusicMotion.InOut))
                    && version == _version && _attached && IsEffectivelyEnabled && commit)
                {
                    _commitPending = false;
                    PlayRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            finally
            {
                if (version == _version)
                {
                    _settling = false;
                    _commitPending = false;
                    _dragging = false;
                    motion.Jump(0);
                }
            }
        }

        /// <summary>Releases pointer ownership before the resulting capture notification can cancel a valid release.</summary>
        private void ReleasePointer()
        {
            var pointer = _pointer;
            _pointer = null;
            if (pointer is not null && ReferenceEquals(pointer.Captured, this))
            {
                pointer.Capture(null);
            }
        }

        /// <summary>Cancels a recycled or interrupted card without ever treating cancellation as a completed gesture.</summary>
        private void ResetGesture()
        {
            _version++;
            ReleasePointer();
            _dragging = false;
            _settling = false;
            _commitPending = false;
            _keyHeld = false;
            _motion?.Jump(0);
            ApplyProgress(0);
        }

        /// <summary>Publishes paint-only progress while preserving the surrounding card's measurement.</summary>
        /// <param name="value">The normalized position along the shared reversible trajectory.</param>
        private void ApplyProgress(double value)
        {
            SetAndRaise(ProgressProperty, ref _progress, Math.Clamp(value, 0, 1));
            InvalidateVisual();
        }

        /// <summary>Rebuilds theme-dependent pens only when their source brushes change.</summary>
        private void RefreshPens()
        {
            _accentPen = Accent is { } accent ? new Pen(accent, 1.25) : null;
            _focusPen = GlyphBrush is { } glyph ? new Pen(glyph, 1) : null;
        }

        /// <summary>Cancels any pending action when a virtualized overlay is assigned another song.</summary>
        /// <param name="sender">The recycled overlay.</param>
        /// <param name="e">The changed source notification.</param>
        private void OnSourceChanged(object? sender, EventArgs e)
        {
            ResetGesture();
        }

        /// <summary>Retargets an authorized release when Arc's effective motion policy changes mid-animation.</summary>
        /// <param name="sender">The updated Arc tracking classes.</param>
        /// <param name="e">The changed class collection.</param>
        private async void OnMotionPolicyChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var level = ArcMotion.GetEffectiveLevel(this);
            if (_level == level)
            {
                return;
            }

            _level = level;
            if (_settling)
            {
                var commit = _commitPending;
                _version++;
                _motion?.Jump(_progress);
                _transitionUpdate = SettleAsync(commit);
                await _transitionUpdate;
            }
            else
            {
                InvalidateVisual();
            }
        }

        /// <summary>Stops gesture-generated taps from opening the song inspector underneath this independent affordance.</summary>
        /// <param name="sender">The play overlay.</param>
        /// <param name="e">The completed tap or double-tap.</param>
        private void OnTapHandled(object? sender, TappedEventArgs e)
        {
            e.Handled = true;
        }

        /// <summary>Keeps a long press on the dedicated play affordance out of the song-context menu.</summary>
        /// <param name="sender">The play overlay.</param>
        /// <param name="e">The local context request.</param>
        private void OnContextHandled(object? sender, ContextRequestedEventArgs e)
        {
            e.Handled = true;
        }
    }
}
