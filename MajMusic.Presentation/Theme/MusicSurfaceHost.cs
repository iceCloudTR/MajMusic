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
        /// <summary>Identifies the current eased transition progress.</summary>
        public static readonly DirectProperty<MusicSurfaceHost, double> TransitionProgressProperty =
            AvaloniaProperty.RegisterDirect<MusicSurfaceHost, double>(nameof(TransitionProgress), host => host.TransitionProgress);
        /// <summary>Identifies whether an outgoing snapshot is retained.</summary>
        public static readonly DirectProperty<MusicSurfaceHost, bool> HasSnapshotProperty =
            AvaloniaProperty.RegisterDirect<MusicSurfaceHost, bool>(nameof(HasSnapshot), host => host.HasSnapshot);

        /// <summary>Limits the longest side of a transient snapshot independently of display scaling.</summary>
        private const int MaximumSnapshotEdge = 1920;
        /// <summary>Contains the two paint surfaces without changing their layout size.</summary>
        private Grid? _surface;
        /// <summary>Contains only the latest live page and its interactive controls.</summary>
        private ContentPresenter? _live;
        /// <summary>Paints one outgoing page without retaining its old interactive tree.</summary>
        private Image? _outgoing;
        /// <summary>Positions the incoming surface independently of its layout.</summary>
        private readonly TranslateTransform _incomingTransform = new();
        /// <summary>Positions the outgoing snapshot independently of its layout.</summary>
        private readonly TranslateTransform _outgoingTransform = new();
        /// <summary>Owns the only retained outgoing bitmap.</summary>
        private RenderTargetBitmap? _snapshot;
        /// <summary>Owns the attached surface's interruptible Arc animation.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Retains the current eased progress.</summary>
        private double _transitionProgress = 1;
        /// <summary>Retains the observable snapshot lifetime.</summary>
        private bool _hasSnapshot;
        /// <summary>Exposes completion or interruption of the latest surface transition.</summary>
        private Task<bool> _transitionUpdate = Task.FromResult(true);

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
                _outgoing = new Image
                {
                    Name = "PART_OutgoingSurface",
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    Focusable = false,
                    RenderTransform = _outgoingTransform
                };
                _surface = new Grid { ClipToBounds = true, Children = { _outgoing, _live } };
                return _surface;
            });
        }

        /// <summary>Gets or sets the generation whose changes replay this surface's transition.</summary>
        public long ChangeVersion
        {
            get => GetValue(ChangeVersionProperty);
            set => SetValue(ChangeVersionProperty, value);
        }
        /// <summary>Gets the actual eased progress from zero to one.</summary>
        public double TransitionProgress => _transitionProgress;
        /// <summary>Gets whether a noninteractive outgoing bitmap is currently retained.</summary>
        public bool HasSnapshot => _hasSnapshot;
        /// <summary>Gets the latest transition's completion; false indicates that a newer transition superseded it.</summary>
        public Task<bool> TransitionUpdate => _transitionUpdate;
        /// <summary>Gets the current incoming translation in logical pixels.</summary>
        public double IncomingOffset => _incomingTransform.Y;
        /// <summary>Gets the current outgoing translation in logical pixels.</summary>
        public double OutgoingOffset => _outgoingTransform.Y;

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
                && IsEffectivelyVisible && ArcMotion.GetEffectiveLevel(this) == ArcMotionLevel.Full;
            var captured = canAnimate ? CaptureSurface() : null;
            base.OnPropertyChanged(change);
            if (!transitionChanged || _live is null)
            {
                return;
            }

            if (contentChanged)
            {
                PresentLatestContent();
            }

            if (captured is null || _motion is null)
            {
                _motion?.Jump(1);
                ApplyProgress(1);
                _transitionUpdate = Task.FromResult(true);
                return;
            }

            _motion.Stop();
            ReleaseSnapshot();
            _snapshot = captured;
            _outgoing!.Source = captured;
            SetAndRaise(HasSnapshotProperty, ref _hasSnapshot, true);
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
        private RenderTargetBitmap? CaptureSurface()
        {
            if (_surface is not { Bounds.Width: > 0, Bounds.Height: > 0 } surface)
            {
                return null;
            }

            var size = surface.Bounds.Size;
            var scale = Math.Min(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1,
                MaximumSnapshotEdge / Math.Max(size.Width, size.Height));
            var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(size.Height * scale))), new Vector(96 * scale, 96 * scale));
            try
            {
                bitmap.Render(surface);
                return bitmap;
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
            _incomingTransform.Y = 12 * (1 - value);
            _outgoingTransform.Y = -10 * value;
            if (_live is not null)
            {
                _live.Opacity = value;
            }

            if (_outgoing is not null)
            {
                _outgoing.Opacity = 1 - value;
            }

            if (value >= 1)
            {
                ReleaseSnapshot();
            }
        }

        /// <summary>Detaches the snapshot from painting before releasing its native bitmap.</summary>
        private void ReleaseSnapshot()
        {
            if (_outgoing is not null)
            {
                _outgoing.Source = null;
            }

            _snapshot?.Dispose();
            _snapshot = null;
            SetAndRaise(HasSnapshotProperty, ref _hasSnapshot, false);
        }
    }
}
