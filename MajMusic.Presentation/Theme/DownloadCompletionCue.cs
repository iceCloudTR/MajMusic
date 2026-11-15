#nullable enable

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Confirms a newly completed transfer with one small glyph animation in a fixed-size status slot.</summary>
    public sealed class DownloadCompletionCue : Control
    {
        /// <summary>Identifies an active transfer that may produce one completion response.</summary>
        public static readonly StyledProperty<bool> IsDownloadingProperty = AvaloniaProperty.Register<DownloadCompletionCue, bool>(nameof(IsDownloading));
        /// <summary>Identifies a validated completed download.</summary>
        public static readonly StyledProperty<bool> IsCompletedProperty = AvaloniaProperty.Register<DownloadCompletionCue, bool>(nameof(IsCompleted));
        /// <summary>Identifies the current appearance's accent.</summary>
        public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<DownloadCompletionCue, IBrush?>(nameof(Accent));
        /// <summary>Exposes the actually painted confirmation progress.</summary>
        public static readonly DirectProperty<DownloadCompletionCue, double> RevealProperty =
            AvaloniaProperty.RegisterDirect<DownloadCompletionCue, double>(nameof(Reveal), control => control.Reveal);
        /// <summary>Shares the normalized confirmation stroke without rebuilding geometry per frame.</summary>
        private static readonly Geometry Check = Geometry.Parse("M 2.5,7 L 5.5,10 L 11.5,3.5");

        /// <summary>Owns the glyph's single interruptible completion response.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Retains the current drawn progress.</summary>
        private double _reveal;
        /// <summary>Arms a confirmation only after a visible real transfer, rather than a cached completed state.</summary>
        private bool _sawTransfer;
        /// <summary>Suppresses completion responses while a recycled control receives another binding source.</summary>
        private bool _contextPending;
        /// <summary>Rejects initialization posted for an earlier source or attachment.</summary>
        private int _contextVersion;
        /// <summary>Allows scale only while the effective policy permits complete motion.</summary>
        private bool _usesScale;
        /// <summary>Retains the last observed Arc motion policy.</summary>
        private ArcMotionLevel _level;
        /// <summary>Caches the small accent pen outside the frame loop.</summary>
        private Pen? _pen;
        /// <summary>Exposes completion or interruption of the one-shot response.</summary>
        private Task<bool> _motionUpdate = Task.FromResult(true);

        /// <summary>Creates a noninteractive confirmation using the installed Arc frame scheduler.</summary>
        public DownloadCompletionCue()
        {
            IsHitTestVisible = false;
            Focusable = false;
            ArcMotion.SetTracks(this, true);
            Classes.CollectionChanged += OnMotionPolicyChanged;
            DataContextChanged += OnSourceChanged;
        }

        /// <summary>Gets or sets whether this song currently receives audio data.</summary>
        public bool IsDownloading
        {
            get => GetValue(IsDownloadingProperty);
            set => SetValue(IsDownloadingProperty, value);
        }
        /// <summary>Gets or sets whether this song has validated downloaded audio.</summary>
        public bool IsCompleted
        {
            get => GetValue(IsCompletedProperty);
            set => SetValue(IsCompletedProperty, value);
        }
        /// <summary>Gets or sets the current appearance's confirmation accent.</summary>
        public IBrush? Accent
        {
            get => GetValue(AccentProperty);
            set => SetValue(AccentProperty, value);
        }
        /// <summary>Gets the actually painted confirmation progress.</summary>
        public double Reveal => _reveal;
        /// <summary>Gets the local glyph scale; the surrounding card and status slot never scale.</summary>
        public double GlyphScale => _usesScale ? .72 + .28 * _reveal : 1;
        /// <summary>Gets completion or supersession of the current confirmation.</summary>
        public Task<bool> MotionUpdate => _motionUpdate;

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _motion = new MotionValue<double>(this, IsCompleted ? 1 : 0, MotionVectors.Double);
            _motion.Changed += ApplyReveal;
            SnapToState();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_motion is not null)
            {
                _motion.Changed -= ApplyReveal;
                _motion.Dispose();
                _motion = null;
            }

            _sawTransfer = false;
            _contextPending = false;
            _contextVersion++;
            _usesScale = false;
            ApplyReveal(IsCompleted ? 1 : 0);
            _motionUpdate = Task.FromResult(true);
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsDownloadingProperty && IsDownloading)
            {
                _sawTransfer = !_contextPending;
            }
            else if (change.Property == IsCompletedProperty)
            {
                if (IsCompleted && _sawTransfer && !_contextPending && IsLoaded && IsEffectivelyVisible && _motion is not null)
                {
                    _sawTransfer = false;
                    _level = ArcMotion.GetEffectiveLevel(this);
                    _usesScale = _level == ArcMotionLevel.Full;
                    _motion.Jump(0);
                    _motionUpdate = _motion.AnimateTo(1, _level == ArcMotionLevel.Full ? MotionSpec.Spring(ArcSpring.Snappy)
                        : _level == ArcMotionLevel.Reduced ? MotionSpec.Tween(TimeSpan.FromMilliseconds(80), ArcEasings.Standard) : MotionSpec.Instant);
                }
                else
                {
                    if (IsCompleted)
                    {
                        _sawTransfer = false;
                    }

                    _usesScale = false;
                    _motion?.Jump(IsCompleted ? 1 : 0);
                    ApplyReveal(IsCompleted ? 1 : 0);
                }
            }
            else if (change.Property == AccentProperty)
            {
                _pen = Accent is { } brush ? new Pen(brush, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round) : null;
                InvalidateVisual();
            }
        }

        /// <inheritdoc />
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_reveal <= 0 || _pen is null)
            {
                return;
            }

            var scale = GlyphScale;
            var matrix = Matrix.CreateTranslation(-7, -7) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(7, 7);
            using (context.PushOpacity(Math.Clamp(_reveal, 0, 1)))
            using (context.PushTransform(matrix))
            {
                context.DrawGeometry(null, _pen, Check);
            }
        }

        /// <summary>Responds immediately when the person disables or reduces motion during a confirmation.</summary>
        /// <param name="sender">The cue's Arc classification.</param>
        /// <param name="e">The updated policy classes.</param>
        private void OnMotionPolicyChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var level = ArcMotion.GetEffectiveLevel(this);
            if (_level != level)
            {
                _level = level;
                SnapToState();
            }
        }

        /// <summary>Initializes a recycled source after its inherited bindings settle, without borrowing the old transfer.</summary>
        /// <param name="sender">The completion cue receiving a new source.</param>
        /// <param name="e">The source-change notification.</param>
        private void OnSourceChanged(object? sender, EventArgs e)
        {
            var version = ++_contextVersion;
            _contextPending = true;
            SnapToState();
            Dispatcher.UIThread.Post(() =>
            {
                if (version != _contextVersion)
                {
                    return;
                }

                _contextPending = false;
                SnapToState();
            }, DispatcherPriority.Loaded);
        }

        /// <summary>Shows cached or recycled completion immediately without replaying another song's response.</summary>
        private void SnapToState()
        {
            _sawTransfer = !_contextPending && IsDownloading;
            _usesScale = false;
            _level = ArcMotion.GetEffectiveLevel(this);
            _motion?.Jump(IsCompleted ? 1 : 0);
            ApplyReveal(IsCompleted ? 1 : 0);
            _motionUpdate = Task.FromResult(true);
        }

        /// <summary>Publishes confirmation paint without changing layout or another control's transform.</summary>
        /// <param name="value">The current Arc progress, including a restrained spring overshoot.</param>
        private void ApplyReveal(double value)
        {
            SetAndRaise(RevealProperty, ref _reveal, Math.Clamp(value, 0, 1.08));
            InvalidateVisual();
        }
    }
}
