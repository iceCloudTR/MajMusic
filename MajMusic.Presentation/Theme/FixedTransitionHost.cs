#nullable enable

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Transitions one fixed content region using an owned outgoing frame that never contributes to measurement.</summary>
    public class FixedTransitionHost : ContentControl
    {
        /// <summary>Exposes the current rendered transition progress.</summary>
        public static readonly DirectProperty<FixedTransitionHost, double> TransitionProgressProperty =
            AvaloniaProperty.RegisterDirect<FixedTransitionHost, double>(nameof(TransitionProgress), host => host.TransitionProgress);
        /// <summary>Exposes whether an independent outgoing frame is retained.</summary>
        public static readonly DirectProperty<FixedTransitionHost, bool> HasSnapshotProperty =
            AvaloniaProperty.RegisterDirect<FixedTransitionHost, bool>(nameof(HasSnapshot), host => host.HasSnapshot);

        /// <summary>Bounds one transient frame without depending on an external image's original resolution.</summary>
        private const int MaximumSnapshotEdge = 1024;
        /// <summary>Contains the current interactive content.</summary>
        private ContentPresenter? _live;
        /// <summary>Paints the independently owned outgoing pixels.</summary>
        private SnapshotVisual? _ghost;
        /// <summary>Arranges both surfaces while measuring only live content.</summary>
        private RegionPanel? _panel;
        /// <summary>Owns the sole retained outgoing frame.</summary>
        private RenderTargetBitmap? _snapshot;
        /// <summary>Retains the outgoing frame's original logical viewport.</summary>
        private Size _snapshotSize;
        /// <summary>Positions live text or the current glyph around its fixed center.</summary>
        private readonly TransformGroup _liveTransform = new();
        /// <summary>Positions the outgoing text or glyph without changing its layout rectangle.</summary>
        private readonly TransformGroup _ghostTransform = new();
        /// <summary>Retains the live glyph's local scale.</summary>
        private readonly ScaleTransform _liveScale = new();
        /// <summary>Retains the live glyph's local rotation.</summary>
        private readonly RotateTransform _liveRotation = new();
        /// <summary>Retains the live text's small horizontal offset.</summary>
        private readonly TranslateTransform _liveOffset = new();
        /// <summary>Retains the outgoing glyph's local scale.</summary>
        private readonly ScaleTransform _ghostScale = new();
        /// <summary>Retains the outgoing glyph's local rotation.</summary>
        private readonly RotateTransform _ghostRotation = new();
        /// <summary>Retains the outgoing text's small horizontal offset.</summary>
        private readonly TranslateTransform _ghostOffset = new();
        /// <summary>Owns an interruptible transition while the region remains attached.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Stores the actual painted progress.</summary>
        private double _transitionProgress = 1;
        /// <summary>Stores snapshot lifetime independently of a public resource reference.</summary>
        private bool _hasSnapshot;
        /// <summary>Distinguishes a previously displayed region from first binding and layout.</summary>
        private bool _hasRendered;
        /// <summary>Prevents recycled source bindings from replaying another song's transition.</summary>
        private bool _contextPending;
        /// <summary>Rejects initialization posted for a previous context or attachment.</summary>
        private int _contextVersion;
        /// <summary>Tracks policy changes while a transition is in progress.</summary>
        private ArcMotionLevel _level;
        /// <summary>Exposes completion or supersession of the latest transition.</summary>
        private Task<bool> _transitionUpdate = Task.FromResult(true);

        /// <summary>Creates a snapshot overlay around one live content presenter.</summary>
        public FixedTransitionHost()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            ClipToBounds = true;
            // Transparent snapshots cannot retain LCD subpixel coverage; use the same grayscale coverage for both surfaces.
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
            _liveTransform.Children.Add(_liveScale);
            _liveTransform.Children.Add(_liveRotation);
            _liveTransform.Children.Add(_liveOffset);
            _ghostTransform.Children.Add(_ghostScale);
            _ghostTransform.Children.Add(_ghostRotation);
            _ghostTransform.Children.Add(_ghostOffset);
            ArcMotion.SetTracks(this, true);
            Classes.CollectionChanged += OnMotionPolicyChanged;
            DataContextChanged += OnSourceContextChanged;
            ActualThemeVariantChanged += (_, _) => FinishTransition();
            Template = new FuncControlTemplate<FixedTransitionHost>((_, _) =>
            {
                _live = new ContentPresenter
                {
                    Name = "PART_TransitionLive",
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    RenderTransform = _liveTransform,
                    RenderTransformOrigin = new RelativePoint(.5, .5, RelativeUnit.Relative)
                };
                _ghost = new SnapshotVisual
                {
                    Name = "PART_TransitionGhost",
                    IsHitTestVisible = false,
                    Focusable = false,
                    UseLayoutRounding = false,
                    RenderTransform = _ghostTransform,
                    RenderTransformOrigin = new RelativePoint(.5, .5, RelativeUnit.Relative)
                };
                _panel = new RegionPanel(this, _live, _ghost) { ClipToBounds = true };
                // RenderTargetBitmap starts at this panel, so it also needs the rendering policy explicitly.
                TextOptions.SetTextRenderingMode(_panel, TextRenderingMode.Antialias);
                return _panel;
            });
        }

        /// <summary>Gets the actual currently painted transition progress.</summary>
        public double TransitionProgress => _transitionProgress;
        /// <summary>Gets whether independently owned outgoing pixels remain alive.</summary>
        public bool HasSnapshot => _hasSnapshot;
        /// <summary>Gets completion or interruption of the latest region transition.</summary>
        public Task<bool> TransitionUpdate => _transitionUpdate;
        /// <summary>Gets the full-motion duration for this fixed region.</summary>
        protected virtual TimeSpan FullDuration => TimeSpan.FromMilliseconds(180);
        /// <summary>Gets the maximum horizontal text entrance; artwork and glyphs keep a fixed center.</summary>
        protected virtual double TextTravel => 0;
        /// <summary>Gets whether full motion includes a restrained local glyph rotation and scale.</summary>
        protected virtual bool IsGlyph => false;

        /// <inheritdoc />
        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            PresentContent();
            FinishTransition();
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _hasRendered = false;
            _level = ArcMotion.GetEffectiveLevel(this);
            _motion = new MotionValue<double>(this, 1, MotionVectors.Double);
            _motion.Changed += ApplyProgress;
            FinishTransition();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _contextVersion++;
            _contextPending = false;
            _hasRendered = false;
            if (_motion is not null)
            {
                _motion.Changed -= ApplyProgress;
                _motion.Dispose();
                _motion = null;
            }

            FinishTransition();
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property == ContentProperty || change.Property == ContentTemplateProperty)
            {
                BeginTransition();
            }

            base.OnPropertyChanged(change);
            if (change.Property == ContentProperty || change.Property == ContentTemplateProperty)
            {
                PresentContent();
            }
        }

        /// <inheritdoc />
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            _hasRendered = true;
        }

        /// <summary>Captures the current composed pixels before a derived control updates its live state.</summary>
        protected void BeginTransition()
        {
            if (_panel is null || _motion is null || !IsLoaded || !IsEffectivelyVisible || !_hasRendered || _contextPending
                || (_level = ArcMotion.GetEffectiveLevel(this)) == ArcMotionLevel.Off)
            {
                FinishTransition();
                return;
            }

            var reuse = HasSnapshot && TransitionProgress == 0;
            var captured = reuse ? null : CaptureFrame();
            if (!reuse && captured is null)
            {
                FinishTransition();
                return;
            }

            _motion.Stop();
            if (captured is { } frame)
            {
                ReleaseSnapshot();
                _snapshot = frame.Bitmap;
                _snapshotSize = frame.Size;
                _ghost!.SetSnapshot(_snapshot, _snapshotSize);
                SetAndRaise(HasSnapshotProperty, ref _hasSnapshot, true);
                _panel.InvalidateArrange();
            }

            _motion.Jump(0);
            _transitionUpdate = _motion.AnimateTo(1,
                MotionSpec.Tween(_level == ArcMotionLevel.Reduced ? TimeSpan.FromMilliseconds(80) : FullDuration, ArcEasings.Standard));
        }

        /// <summary>Shows source initialization and disabled motion directly without retaining outgoing pixels.</summary>
        protected void FinishTransition()
        {
            _motion?.Jump(1);
            ApplyProgress(1);
            _transitionUpdate = Task.FromResult(true);
        }

        /// <summary>Copies only the current viewport, including an interrupted outgoing frame and incoming live state.</summary>
        /// <returns>An independent bounded frame, or null before a drawable viewport exists.</returns>
        private (RenderTargetBitmap Bitmap, Size Size)? CaptureFrame()
        {
            if (_panel is not { Bounds.Width: > 0, Bounds.Height: > 0 } panel)
            {
                return null;
            }

            var size = panel.Bounds.Size;
            var scale = Math.Min(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1, MaximumSnapshotEdge / Math.Max(size.Width, size.Height));
            var frame = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(size.Height * scale))), new Vector(96 * scale, 96 * scale));
            try
            {
                frame.Render(panel);
                return (frame, size);
            }
            catch (InvalidOperationException)
            {
                frame.Dispose();
                return null;
            }
        }

        /// <summary>Updates the one live tree without making the outgoing snapshot a second interactive control.</summary>
        private void PresentContent()
        {
            if (_live is not null)
            {
                _live.ContentTemplate = null;
                _live.Content = Content;
                _live.ContentTemplate = Content is null or Control ? null : ContentTemplate;
            }
        }

        /// <summary>Applies an opacity crossfade and only the small geometry feedback allowed for this region.</summary>
        /// <param name="progress">The current Arc value.</param>
        private void ApplyProgress(double progress)
        {
            var value = Math.Clamp(progress, 0, 1);
            SetAndRaise(TransitionProgressProperty, ref _transitionProgress, value);
            var full = _level == ArcMotionLevel.Full;
            _liveOffset.X = full ? TextTravel * (1 - value) : 0;
            _ghostOffset.X = full ? -TextTravel * value : 0;
            _liveScale.ScaleX = _liveScale.ScaleY = full && IsGlyph ? .9 + .1 * value : 1;
            _ghostScale.ScaleX = _ghostScale.ScaleY = full && IsGlyph ? 1 - .08 * value : 1;
            _liveRotation.Angle = full && IsGlyph ? -8 * (1 - value) : 0;
            _ghostRotation.Angle = full && IsGlyph ? 8 * value : 0;
            if (_live is not null)
            {
                _live.Opacity = value;
            }

            if (_ghost is not null)
            {
                _ghost.Opacity = 1 - value;
            }

            if (value >= 1)
            {
                ReleaseSnapshot();
            }
        }

        /// <summary>Releases the only owned outgoing bitmap after removing it from the painter.</summary>
        private void ReleaseSnapshot()
        {
            _ghost?.SetSnapshot(null, default);
            _snapshot?.Dispose();
            _snapshot = null;
            _snapshotSize = default;
            SetAndRaise(HasSnapshotProperty, ref _hasSnapshot, false);
        }

        /// <summary>Settles immediately if the person changes the effective motion policy.</summary>
        /// <param name="sender">The Arc classification collection.</param>
        /// <param name="e">The classification change.</param>
        private void OnMotionPolicyChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var level = ArcMotion.GetEffectiveLevel(this);
            if (level != _level)
            {
                _level = level;
                FinishTransition();
            }
        }

        /// <summary>Suppresses another song's transition while inherited bindings receive a recycled context.</summary>
        /// <param name="sender">The region receiving a new source.</param>
        /// <param name="e">The source-change notification.</param>
        private void OnSourceContextChanged(object? sender, EventArgs e)
        {
            var version = ++_contextVersion;
            _contextPending = true;
            FinishTransition();
            Dispatcher.UIThread.Post(() =>
            {
                if (version == _contextVersion)
                {
                    _contextPending = false;
                    FinishTransition();
                }
            }, DispatcherPriority.Loaded);
        }

        /// <summary>Uses live content alone for desired size and arranges a fixed outgoing overlay.</summary>
        private sealed class RegionPanel : Panel
        {
            /// <summary>Owns snapshot dimensions.</summary>
            private readonly FixedTransitionHost _owner;
            /// <summary>Contributes the only desired content size.</summary>
            private readonly Control _live;
            /// <summary>Paints outgoing pixels without affecting measurement.</summary>
            private readonly Control _ghost;

            /// <summary>Creates one live tree and one nonmeasuring overlay.</summary>
            /// <param name="owner">The region's transition owner.</param>
            /// <param name="live">The only live content presenter.</param>
            /// <param name="ghost">The outgoing pixel painter.</param>
            public RegionPanel(FixedTransitionHost owner, Control live, Control ghost)
            {
                _owner = owner;
                _live = live;
                _ghost = ghost;
                Children.Add(ghost);
                Children.Add(live);
            }

            /// <inheritdoc />
            protected override Size MeasureOverride(Size availableSize)
            {
                _live.Measure(availableSize);
                _ghost.Measure(default);
                return _live.DesiredSize;
            }

            /// <inheritdoc />
            protected override Size ArrangeOverride(Size finalSize)
            {
                _live.Arrange(new Rect(finalSize));
                _ghost.Arrange(new Rect(_owner._snapshotSize));
                return finalSize;
            }
        }

        /// <summary>Paints one independently owned frame at its original logical viewport.</summary>
        private sealed class SnapshotVisual : Control
        {
            /// <summary>References pixels owned by the surrounding region.</summary>
            private Bitmap? _bitmap;
            /// <summary>Retains the captured logical viewport.</summary>
            private Size _size;

            /// <summary>Replaces the paint reference without taking external bitmap ownership.</summary>
            /// <param name="bitmap">The independently owned outgoing frame.</param>
            /// <param name="size">The original logical viewport.</param>
            public void SetSnapshot(Bitmap? bitmap, Size size)
            {
                _bitmap = bitmap;
                _size = size;
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
                if (_bitmap is { } bitmap)
                {
                    using (context.PushClip(new Rect(_size)))
                    {
                        context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), new Rect(bitmap.Size));
                    }
                }
            }
        }
    }
}
