#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Transitions a complete music surface using Arc's motion engine and one noninteractive outgoing snapshot.</summary>
    public sealed class MusicSurfaceHost : ContentControl
    {
        /// <summary>Replays a surface transition when the same live view changes its browsing generation.</summary>
        public static readonly StyledProperty<long> ChangeVersionProperty =
            AvaloniaProperty.Register<MusicSurfaceHost, long>(nameof(ChangeVersion));
        /// <summary>Chooses forward motion with a positive value, backward motion with a negative value, or automatic version ordering with zero.</summary>
        public static readonly StyledProperty<int> SlideDirectionProperty =
            AvaloniaProperty.Register<MusicSurfaceHost, int>(nameof(SlideDirection));
        /// <summary>Identifies the current eased transition progress.</summary>
        public static readonly DirectProperty<MusicSurfaceHost, double> TransitionProgressProperty =
            AvaloniaProperty.RegisterDirect<MusicSurfaceHost, double>(nameof(TransitionProgress), host => host.TransitionProgress);
        /// <summary>Identifies whether an outgoing snapshot is retained.</summary>
        public static readonly DirectProperty<MusicSurfaceHost, bool> HasSnapshotProperty =
            AvaloniaProperty.RegisterDirect<MusicSurfaceHost, bool>(nameof(HasSnapshot), host => host.HasSnapshot);

        /// <summary>Limits the longest side of a transient snapshot independently of display scaling.</summary>
        private const int MaximumSnapshotEdge = 1920;
        /// <summary>Bounds transient rasterization and allocation by area as well as its longest edge.</summary>
        private const int MaximumSnapshotPixels = 2 * 1024 * 1024;
        /// <summary>Contains the two paint surfaces without changing their layout size.</summary>
        private SurfacePanel? _surface;
        /// <summary>Contains only the latest live page and its interactive controls.</summary>
        private ContentPresenter? _live;
        /// <summary>Paints one outgoing page without retaining its old interactive tree.</summary>
        private SnapshotSurface? _outgoing;
        /// <summary>Positions the incoming surface independently of its layout.</summary>
        private readonly TranslateTransform _incomingTransform = new();
        /// <summary>Positions the outgoing snapshot independently of its layout.</summary>
        private readonly TranslateTransform _outgoingTransform = new();
        /// <summary>Owns the only retained outgoing bitmap.</summary>
        private RenderTargetBitmap? _snapshot;
        /// <summary>Retains the original logical viewport painted into the snapshot.</summary>
        private Size _snapshotViewport;
        /// <summary>Retains the current layout viewport independently of the outgoing bitmap's dimensions.</summary>
        private Size _viewport;
        /// <summary>Retains the direction of the active transition even when the next direction binding changes.</summary>
        private int _activeDirection = 1;
        /// <summary>Owns the attached surface's interruptible Arc animation.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Retains the current eased progress.</summary>
        private double _transitionProgress = 1;
        /// <summary>Retains the observable snapshot lifetime.</summary>
        private bool _hasSnapshot;
        /// <summary>Exposes completion or interruption of the latest surface transition.</summary>
        private Task<bool> _transitionUpdate = Task.FromResult(true);
        /// <summary>Allows a version's delayed content publication to join its already running transition.</summary>
        private bool _awaitingVersionContent;

        /// <summary>Creates a fixed-size two-surface presenter whose navigation does not move surrounding chrome.</summary>
        public MusicSurfaceHost()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            ClipToBounds = true;
            Template = new FuncControlTemplate<MusicSurfaceHost>((_, _) =>
            {
                _live = new ContentPresenter
                {
                    Name = "PART_LiveSurface",
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    RenderTransform = _incomingTransform
                };
                _outgoing = new SnapshotSurface
                {
                    Name = "PART_OutgoingSurface",
                    IsHitTestVisible = false,
                    Focusable = false,
                    UseLayoutRounding = false,
                    RenderTransform = _outgoingTransform
                };
                _surface = new SurfacePanel(this, _live, _outgoing) { ClipToBounds = true };
                return _surface;
            });
        }

        /// <summary>Gets or sets the generation whose changes replay this surface's transition.</summary>
        public long ChangeVersion
        {
            get => GetValue(ChangeVersionProperty);
            set => SetValue(ChangeVersionProperty, value);
        }
        /// <summary>Gets or sets the slide direction; zero infers it from a changed version's ordering.</summary>
        public int SlideDirection
        {
            get => GetValue(SlideDirectionProperty);
            set => SetValue(SlideDirectionProperty, value);
        }
        /// <summary>Gets the actual eased progress from zero to one.</summary>
        public double TransitionProgress => _transitionProgress;
        /// <summary>Gets whether a noninteractive outgoing bitmap is currently retained.</summary>
        public bool HasSnapshot => _hasSnapshot;
        /// <summary>Gets the latest transition's completion; false indicates that a newer transition superseded it.</summary>
        public Task<bool> TransitionUpdate => _transitionUpdate;
        /// <summary>Gets the current incoming translation in logical pixels.</summary>
        public double IncomingOffset => _incomingTransform.X;
        /// <summary>Gets the current outgoing translation in logical pixels.</summary>
        public double OutgoingOffset => _outgoingTransform.X;
        /// <summary>Gets the original logical snapshot viewport, whose size is independent of subsequent layout.</summary>
        public Size SnapshotViewport => _snapshotViewport;
        /// <summary>Gets the bounded raster dimensions currently retained for the outgoing surface.</summary>
        public PixelSize SnapshotPixelSize => _snapshot?.PixelSize ?? default;

        /// <inheritdoc />
        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            PresentLatestContent();
            ApplyProgress(1);
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _motion = new MotionValue<double>(this, 1, MotionVectors.Double);
            _motion.Changed += ApplyProgress;
            _motion.Jump(1);
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_motion is not null)
            {
                _motion.Changed -= ApplyProgress;
                _motion.Dispose();
                _motion = null;
            }

            _transitionUpdate = Task.FromResult(true);
            ApplyProgress(1);
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            var contentChanged = change.Property == ContentProperty || change.Property == ContentTemplateProperty;
            var transitionChanged = contentChanged || change.Property == ChangeVersionProperty;
            var canAnimate = transitionChanged && _live is not null && _motion is not null && IsLoaded
                && IsEffectivelyVisible && ArcMotion.GetEffectiveLevel(this) == ArcMotionLevel.Full
                && !(change.Property == ContentProperty && change.NewValue is null);
            var joinVersionContent = canAnimate && HasSnapshot && contentChanged && _awaitingVersionContent;
            var reuseSnapshot = canAnimate && HasSnapshot && (TransitionProgress == 0 || joinVersionContent);
            var captured = canAnimate && !reuseSnapshot ? CaptureSurface() : null;
            base.OnPropertyChanged(change);
            if (!transitionChanged || _live is null)
            {
                return;
            }

            if (contentChanged)
            {
                PresentLatestContent();
            }

            if ((!reuseSnapshot && captured is null) || _motion is null)
            {
                _motion?.Jump(1);
                ApplyProgress(1);
                _transitionUpdate = Task.FromResult(true);
                return;
            }

            if (SlideDirection != 0)
            {
                _activeDirection = Math.Sign(SlideDirection);
            }
            else if (change.Property == ChangeVersionProperty && change.OldValue is long previous && change.NewValue is long current
                && previous != current)
            {
                _activeDirection = current > previous ? 1 : -1;
            }

            if (joinVersionContent)
            {
                _awaitingVersionContent = false;
                ApplyProgress(TransitionProgress);
                return;
            }

            _motion.Stop();
            if (!reuseSnapshot && captured is { } frame)
            {
                ReleaseSnapshot();
                _snapshot = frame.Bitmap;
                _snapshotViewport = frame.Viewport;
                _outgoing!.SetSnapshot(frame.Bitmap, frame.Viewport);
                SetAndRaise(HasSnapshotProperty, ref _hasSnapshot, true);
                _surface?.InvalidateArrange();
            }

            _awaitingVersionContent = change.Property == ChangeVersionProperty;
            _motion.Jump(0);
            _transitionUpdate = _motion.AnimateTo(1, MotionSpec.Tween(MusicMotion.SurfaceDuration, MusicMotion.InOut));
        }

        /// <summary>Presents only the newest live content without introducing another animated presenter.</summary>
        private void PresentLatestContent()
        {
            if (_live is null)
            {
                return;
            }

            _live.ContentTemplate = null;
            _live.Content = Content;
            _live.ContentTemplate = Content is null or Control ? null : ContentTemplate;
        }

        /// <summary>Captures the currently painted composite before its live page or generation changes.</summary>
        /// <returns>A bounded snapshot, or null when the host has not yet acquired a drawable viewport.</returns>
        private (RenderTargetBitmap Bitmap, Size Viewport)? CaptureSurface()
        {
            if (_surface is not { Bounds.Width: > 0, Bounds.Height: > 0 } surface)
            {
                return null;
            }

            var size = surface.Bounds.Size;
            var scale = Math.Min(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1,
                MaximumSnapshotEdge / Math.Max(size.Width, size.Height));
            // Leave room for the final ceil in both dimensions without exceeding the area budget.
            scale = Math.Min(scale, Math.Sqrt((MaximumSnapshotPixels - MaximumSnapshotEdge * 2 - 1d)
                / (size.Width * size.Height)));
            var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(size.Height * scale))), new Vector(96 * scale, 96 * scale));
            try
            {
                bitmap.Render(surface);
                return (bitmap, size);
            }
            catch (InvalidOperationException)
            {
                bitmap.Dispose();
                return null;
            }
        }

        /// <summary>Applies both surfaces from one Arc value and releases the outgoing bitmap at the final frame.</summary>
        /// <param name="progress">The current eased progress.</param>
        private void ApplyProgress(double progress)
        {
            var value = Math.Clamp(progress, 0, 1);
            SetAndRaise(TransitionProgressProperty, ref _transitionProgress, value);
            _incomingTransform.X = _activeDirection * _viewport.Width * (1 - value);
            _incomingTransform.Y = 0;
            _outgoingTransform.X = -_activeDirection * Math.Max(_viewport.Width, _snapshotViewport.Width) * value;
            _outgoingTransform.Y = 0;
            if (_live is not null)
            {
                _live.Opacity = 1;
            }

            if (_outgoing is not null)
            {
                _outgoing.Opacity = 1;
            }

            if (value >= 1)
            {
                ReleaseSnapshot();
            }
        }

        /// <summary>Detaches the snapshot from painting before releasing its native bitmap.</summary>
        private void ReleaseSnapshot()
        {
            _awaitingVersionContent = false;
            if (_snapshot is null && !HasSnapshot)
            {
                return;
            }

            if (_outgoing is not null)
            {
                _outgoing.SetSnapshot(null, default);
            }

            _snapshot?.Dispose();
            _snapshot = null;
            _snapshotViewport = default;
            SetAndRaise(HasSnapshotProperty, ref _hasSnapshot, false);
        }

        /// <summary>Arranges the live viewport while keeping the outgoing snapshot entirely out of desired-size measurement.</summary>
        private sealed class SurfacePanel : Panel
        {
            /// <summary>Receives the actual viewport used by horizontal motion.</summary>
            private readonly MusicSurfaceHost _owner;
            /// <summary>Is the sole child contributing to this panel's desired layout.</summary>
            private readonly Control _content;
            /// <summary>Paints the outgoing viewport without constraining its parent.</summary>
            private readonly Control _ghost;

            /// <summary>Creates a layout containing one live child and one nonmeasuring overlay.</summary>
            /// <param name="owner">The transition owner.</param>
            /// <param name="content">The current live presenter.</param>
            /// <param name="ghost">The outgoing bitmap painter.</param>
            public SurfacePanel(MusicSurfaceHost owner, Control content, Control ghost)
            {
                _owner = owner;
                _content = content;
                _ghost = ghost;
                Children.Add(ghost);
                Children.Add(content);
            }

            /// <inheritdoc />
            protected override Size MeasureOverride(Size availableSize)
            {
                _content.Measure(availableSize);
                _ghost.Measure(default);
                return _content.DesiredSize;
            }

            /// <inheritdoc />
            protected override Size ArrangeOverride(Size finalSize)
            {
                if (_owner._viewport != finalSize)
                {
                    _owner._viewport = finalSize;
                    _owner.ApplyProgress(_owner.TransitionProgress);
                }
                _content.Arrange(new Rect(finalSize));
                _ghost.Arrange(new Rect(_owner._snapshotViewport));
                return finalSize;
            }
        }

        /// <summary>Paints captured pixels at their original logical size without participating in layout or applying image stretching.</summary>
        private sealed class SnapshotSurface : Control
        {
            /// <summary>References the bitmap owned by the transition host.</summary>
            private Bitmap? _bitmap;
            /// <summary>Clips fractional pixel rounding to the original viewport.</summary>
            private Size _capturedViewport;

            /// <summary>Replaces the noninteractive captured frame.</summary>
            /// <param name="bitmap">The captured bitmap, or null to stop painting.</param>
            /// <param name="viewport">The original logical viewport.</param>
            public void SetSnapshot(Bitmap? bitmap, Size viewport)
            {
                if (ReferenceEquals(_bitmap, bitmap) && _capturedViewport == viewport)
                {
                    return;
                }

                _bitmap = bitmap;
                _capturedViewport = viewport;
                InvalidateVisual();
            }

            /// <inheritdoc />
            protected override Size MeasureOverride(Size availableSize)
            {
                return default;
            }

            /// <inheritdoc />
            public override void Render(DrawingContext context)
            {
                base.Render(context);
                if (_bitmap is { } bitmap)
                {
                    // Source coordinates are physical pixels; destination coordinates are logical units.
                    var imageBounds = new Rect(bitmap.Size);
                    var pixelBounds = new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    using (context.PushClip(new Rect(_capturedViewport)))
                    {
                        context.DrawImage(bitmap, pixelBounds, imageBounds);
                    }
                }
            }
        }
    }
}
