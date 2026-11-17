#nullable enable

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Reveals expanded desktop details inside the existing content viewport without moving the player.</summary>
    public sealed class SongDetailsOverlay : Decorator
    {
        /// <summary>Identifies the shared expanded-details state.</summary>
        public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<SongDetailsOverlay, bool>(
            nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);
        /// <summary>Moves only the overlay's own painted surface.</summary>
        private readonly TranslateTransform _translation = new();
        /// <summary>Owns the one reversible entry and exit animation.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Retains the current global or inherited motion policy.</summary>
        private ArcMotionLevel _level;
        /// <summary>Exposes completion or interruption without an unobserved continuation.</summary>
        private Task<bool> _overlayUpdate = Task.FromResult(true);

        /// <summary>Creates a clipped overlay which is absent from input until expanded.</summary>
        public SongDetailsOverlay()
        {
            RenderTransform = _translation;
            ClipToBounds = true;
            Focusable = true;
            IsVisible = false;
            IsHitTestVisible = false;
            ArcMotion.SetTracks(this, true);
            Classes.CollectionChanged += OnMotionPolicyChanged;
        }

        /// <summary>Gets or sets whether the existing content region presents expanded song details.</summary>
        public bool IsOpen
        {
            get => GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }
        /// <summary>Gets completion of the active reveal; false indicates a newer request interrupted it.</summary>
        public Task<bool> OverlayUpdate => _overlayUpdate;

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _level = ArcMotion.GetEffectiveLevel(this);
            _motion = new MotionValue<double>(this, IsOpen ? 1 : 0, MotionVectors.Double);
            _motion.Changed += ApplyProgress;
            ApplyProgress(IsOpen ? 1 : 0);
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
            _overlayUpdate = Task.FromResult(true);
            ApplyProgress(IsOpen ? 1 : 0);
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsOpenProperty)
            {
                Retarget();
            }
        }

        /// <inheritdoc />
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (IsOpen && e.Key == Key.Escape)
            {
                SetCurrentValue(IsOpenProperty, false);
                e.Handled = true;
            }
        }

        /// <summary>Retargets from the displayed position while preserving one motion writer.</summary>
        private void Retarget()
        {
            var target = IsOpen ? 1d : 0d;
            if (_motion is null)
            {
                ApplyProgress(target);
                return;
            }
            _motion.Stop();
            if (IsOpen)
            {
                IsVisible = true;
                IsHitTestVisible = true;
                Focus();
            }
            _level = ArcMotion.GetEffectiveLevel(this);
            if (_level != ArcMotionLevel.Full)
            {
                _motion.Jump(target);
                _overlayUpdate = Task.FromResult(true);
            }
            else
            {
                _overlayUpdate = _motion.AnimateTo(target, MotionSpec.Tween(TimeSpan.FromMilliseconds(230), MusicMotion.InOut));
            }
        }

        /// <summary>Applies a bounded reveal without transforming or scaling surrounding layout.</summary>
        /// <param name="value">The current reveal fraction.</param>
        private void ApplyProgress(double value)
        {
            var progress = Math.Clamp(value, 0, 1);
            Opacity = progress;
            _translation.Y = ArcMotion.GetEffectiveLevel(this) == ArcMotionLevel.Full ? (1 - progress) * 16 : 0;
            IsVisible = IsOpen || progress > 0;
            IsHitTestVisible = IsOpen;
        }

        /// <summary>Settles immediately if motion is reduced or disabled during an active reveal.</summary>
        /// <param name="sender">The Arc tracking classes.</param>
        /// <param name="e">The updated policy classification.</param>
        private void OnMotionPolicyChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_level != ArcMotion.GetEffectiveLevel(this))
            {
                Retarget();
            }
        }
    }
}
