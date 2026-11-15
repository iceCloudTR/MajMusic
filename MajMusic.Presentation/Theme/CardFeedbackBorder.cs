#nullable enable

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Paints restrained hover and selection emphasis without changing song-card layout or artwork.</summary>
    public sealed class CardFeedbackBorder : Control
    {
        /// <summary>Identifies whether the card is under the pointer.</summary>
        public static readonly StyledProperty<bool> IsHoveredProperty = AvaloniaProperty.Register<CardFeedbackBorder, bool>(nameof(IsHovered));
        /// <summary>Identifies the independently selected song.</summary>
        public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<CardFeedbackBorder, bool>(nameof(IsSelected));
        /// <summary>Suppresses the information wash while download progress supplies its own background.</summary>
        public static readonly StyledProperty<bool> HasDownloadStatusProperty = AvaloniaProperty.Register<CardFeedbackBorder, bool>(nameof(HasDownloadStatus));
        /// <summary>Identifies the current appearance's single accent brush.</summary>
        public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<CardFeedbackBorder, IBrush?>(nameof(Accent));
        /// <summary>Exposes the actual rendered emphasis for frame-based validation.</summary>
        public static readonly DirectProperty<CardFeedbackBorder, double> EmphasisProperty =
            AvaloniaProperty.RegisterDirect<CardFeedbackBorder, double>(nameof(Emphasis), control => control.Emphasis);
        /// <summary>Exposes the subtle background wash drawn below information text and transfer fill.</summary>
        public static readonly DirectProperty<CardFeedbackBorder, double> InformationOpacityProperty =
            AvaloniaProperty.RegisterDirect<CardFeedbackBorder, double>(nameof(InformationOpacity), control => control.InformationOpacity);

        /// <summary>Owns the interrupted color-strength transition while this overlay is attached.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Retains the rendered opacity strength independently of layout.</summary>
        private double _emphasis;
        /// <summary>Retains the underlay opacity independently of foreground text and transfer colors.</summary>
        private double _informationOpacity;
        /// <summary>Caches the accent outline without allocating a pen per frame.</summary>
        private Pen? _pen;
        /// <summary>Retains the last observed effective motion policy.</summary>
        private ArcMotionLevel _level;
        /// <summary>Shows recycled binding values directly instead of animating another song's selection.</summary>
        private bool _contextPending;
        /// <summary>Rejects initialization belonging to an earlier source or attachment.</summary>
        private int _contextVersion;
        /// <summary>Exposes completion of the current input-driven visual transition.</summary>
        private Task<bool> _motionUpdate = Task.FromResult(true);

        /// <summary>Creates an input-transparent overlay driven by the installed Arc motion policy.</summary>
        public CardFeedbackBorder()
        {
            IsHitTestVisible = false;
            Focusable = false;
            ArcMotion.SetTracks(this, true);
            Classes.CollectionChanged += OnMotionPolicyChanged;
            DataContextChanged += OnSourceChanged;
        }

        /// <summary>Gets or sets whether the pointer currently rests over the song.</summary>
        public bool IsHovered
        {
            get => GetValue(IsHoveredProperty);
            set => SetValue(IsHoveredProperty, value);
        }
        /// <summary>Gets or sets whether this is the inspected song.</summary>
        public bool IsSelected
        {
            get => GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }
        /// <summary>Gets or sets whether transfer fill must retain its original color.</summary>
        public bool HasDownloadStatus
        {
            get => GetValue(HasDownloadStatusProperty);
            set => SetValue(HasDownloadStatusProperty, value);
        }
        /// <summary>Gets or sets the current appearance's accent.</summary>
        public IBrush? Accent
        {
            get => GetValue(AccentProperty);
            set => SetValue(AccentProperty, value);
        }
        /// <summary>Gets the actual paint strength, with selection stronger than hover.</summary>
        public double Emphasis => _emphasis;
        /// <summary>Gets the information underlay opacity, or zero while a transfer owns the background.</summary>
        public double InformationOpacity => _informationOpacity;
        /// <summary>Gets completion or supersession of the latest visual input.</summary>
        public Task<bool> MotionUpdate => _motionUpdate;
        /// <summary>Gets the requested steady emphasis without affecting geometry.</summary>
        private double Target => IsSelected ? 1 : IsHovered ? .38 : 0;

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _level = ArcMotion.GetEffectiveLevel(this);
            _motion = new MotionValue<double>(this, Target, MotionVectors.Double);
            _motion.Changed += ApplyEmphasis;
            SnapToState();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_motion is not null)
            {
                _motion.Changed -= ApplyEmphasis;
                _motion.Dispose();
                _motion = null;
            }

            ApplyEmphasis(Target);
            _contextPending = false;
            _contextVersion++;
            _motionUpdate = Task.FromResult(true);
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsHoveredProperty || change.Property == IsSelectedProperty)
            {
                Retarget();
            }
            else if (change.Property == AccentProperty || change.Property == HasDownloadStatusProperty)
            {
                _pen = Accent is { } brush ? new Pen(brush, 2) : null;
                ApplyEmphasis(_emphasis);
                InvalidateVisual();
            }
        }

        /// <inheritdoc />
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_emphasis <= 0 || _pen is null || Bounds.Width <= 3 || Bounds.Height <= 3)
            {
                return;
            }

            var outline = new RoundedRect(new Rect(Bounds.Size).Deflate(1.5), 11);
            using (context.PushOpacity(_emphasis * .22))
            {
                context.DrawRectangle(null, _pen, outline);
            }

        }

        /// <summary>Responds to Arc's public tracking classes when theme or inherited motion changes.</summary>
        /// <param name="sender">The overlay's class collection.</param>
        /// <param name="e">The updated motion classification.</param>
        private void OnMotionPolicyChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var level = ArcMotion.GetEffectiveLevel(this);
            if (_level != level)
            {
                _level = level;
                Retarget();
            }
        }

        /// <summary>Lets a recycled card's new bindings settle without carrying the previous song's emphasis.</summary>
        /// <param name="sender">The overlay receiving another source.</param>
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

        /// <summary>Shows recycled and initially attached states directly rather than animating from another song.</summary>
        private void SnapToState()
        {
            _motion?.Jump(Target);
            ApplyEmphasis(Target);
            _motionUpdate = Task.FromResult(true);
        }

        /// <summary>Retargets the opacity-only emphasis while honoring full, reduced and disabled motion.</summary>
        private void Retarget()
        {
            if (_motion is null || !IsLoaded || _contextPending)
            {
                SnapToState();
                return;
            }

            _level = ArcMotion.GetEffectiveLevel(this);
            _motionUpdate = _motion.AnimateTo(Target, _level == ArcMotionLevel.Off ? MotionSpec.Instant
                : MotionSpec.Tween(TimeSpan.FromMilliseconds(_level == ArcMotionLevel.Full ? 150 : 70), MusicMotion.InOut));
        }

        /// <summary>Publishes paint strength without invalidating measurement.</summary>
        /// <param name="value">The current Arc transition value.</param>
        private void ApplyEmphasis(double value)
        {
            SetAndRaise(EmphasisProperty, ref _emphasis, Math.Clamp(value, 0, 1));
            SetAndRaise(InformationOpacityProperty, ref _informationOpacity, HasDownloadStatus ? 0 : _emphasis * .04);
            InvalidateVisual();
        }
    }
}
