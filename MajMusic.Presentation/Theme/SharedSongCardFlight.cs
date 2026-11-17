#nullable enable

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Moves one real song card between two reserved slots without cloning its content or affecting surrounding layout.</summary>
    public sealed class SharedSongCardFlight : Panel, IDisposable
    {
        /// <summary>Positions the real card while it belongs to this transparent flight layer.</summary>
        private readonly TranslateTransform _translation = new();
        /// <summary>Owns the current interruptible flight.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Retains the card's ordinary right-hand slot.</summary>
        private ContentControl? _sourceSlot;
        /// <summary>Retains the card's expanded left-hand slot.</summary>
        private ContentControl? _destinationSlot;
        /// <summary>Retains the one card that can occupy a slot or this layer.</summary>
        private Control? _card;
        /// <summary>Remembers the destination requested by the latest navigation action.</summary>
        private bool _expanded;
        /// <summary>Distinguishes temporary overlay ownership from ordinary slot ownership.</summary>
        private bool _isFlying;
        /// <summary>Rejects completion callbacks from superseded flights.</summary>
        private int _version;
        /// <summary>Retains the current segment's original painted position.</summary>
        private Point _start;
        /// <summary>Retains the current segment's target in flight-layer coordinates.</summary>
        private Point _target;
        /// <summary>Freezes the actual arranged card size for the entire flight.</summary>
        private Size _flightSize;
        /// <summary>Retains the current eased segment progress.</summary>
        private double _progress;
        /// <summary>Preserves the card's original width expression during temporary ownership.</summary>
        private double _originalWidth;
        /// <summary>Preserves the card's original height expression during temporary ownership.</summary>
        private double _originalHeight;
        /// <summary>Preserves margins which ordinary slot layout applies outside the card.</summary>
        private Thickness _originalMargin;
        /// <summary>Preserves any resting transform without applying scale or rotation during flight.</summary>
        private ITransform? _originalTransform;
        /// <summary>Tracks subscriptions only while the layer is attached.</summary>
        private bool _slotsSubscribed;
        /// <summary>Prevents use after the owner explicitly releases this helper.</summary>
        private bool _disposed;
        /// <summary>Exposes completion or interruption of the latest move.</summary>
        private Task<bool> _moveUpdate = Task.FromResult(true);

        /// <summary>Creates a transparent overlay which measures neither the moving card nor any surrounding content.</summary>
        public SharedSongCardFlight()
        {
            ClipToBounds = false;
            ArcMotion.SetTracks(this, true);
            Classes.CollectionChanged += OnMotionPolicyChanged;
            SizeChanged += OnGeometryChanged;
        }

        /// <summary>Gets completion of the latest move; false indicates supersession, resizing, or detachment.</summary>
        public Task<bool> MoveUpdate => _moveUpdate;

        /// <summary>Assigns the real card and its two reserved, equally sized, padding-free slots on the UI thread.</summary>
        /// <param name="sourceSlot">The arranged ordinary-details slot, which keeps its size while empty.</param>
        /// <param name="destinationSlot">The arranged expanded-details slot, which keeps its size while empty.</param>
        /// <param name="card">The existing card, initially in either slot or unattached.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The two slots are the same control.</exception>
        /// <exception cref="InvalidOperationException">A slot contains unrelated content or the card has another owner.</exception>
        /// <exception cref="ObjectDisposedException">The layer has been disposed.</exception>
        public void Configure(ContentControl sourceSlot, ContentControl destinationSlot, Control card)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(sourceSlot);
            ArgumentNullException.ThrowIfNull(destinationSlot);
            ArgumentNullException.ThrowIfNull(card);
            if (ReferenceEquals(sourceSlot, destinationSlot))
            {
                throw new ArgumentException("The source and destination slots must be different controls.", nameof(destinationSlot));
            }
            if (ReferenceEquals(_sourceSlot, sourceSlot) && ReferenceEquals(_destinationSlot, destinationSlot) && ReferenceEquals(_card, card))
            {
                return;
            }
            if ((sourceSlot.Content is not null && !ReferenceEquals(sourceSlot.Content, card))
                || (destinationSlot.Content is not null && !ReferenceEquals(destinationSlot.Content, card))
                || (card.Parent is not null && !ReferenceEquals(card.Parent, sourceSlot) && !ReferenceEquals(card.Parent, destinationSlot)))
            {
                throw new InvalidOperationException("The flight must exclusively own its card and both content slots.");
            }

            StopAndPlace();
            UnsubscribeSlots();
            _sourceSlot = sourceSlot;
            _destinationSlot = destinationSlot;
            _card = card;
            _expanded = ReferenceEquals(destinationSlot.Content, card);
            PlaceInRequestedSlot();
            if (TopLevel.GetTopLevel(this) is not null)
            {
                SubscribeSlots();
            }
            _moveUpdate = Task.FromResult(true);
        }

        /// <summary>Moves the existing card from its currently painted position to the requested slot on the UI thread.</summary>
        /// <param name="expanded">True selects the expanded destination; false restores the original source slot.</param>
        /// <exception cref="InvalidOperationException">The layer has not been configured.</exception>
        /// <exception cref="ObjectDisposedException">The layer has been disposed.</exception>
        public void MoveTo(bool expanded)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_card is null || _sourceSlot is null || _destinationSlot is null)
            {
                throw new InvalidOperationException("Configure the card and both slots before moving them.");
            }
            if (_expanded == expanded && (_isFlying || ReferenceEquals(RequestedSlot?.Content, _card)))
            {
                return;
            }

            _expanded = expanded;
            var version = ++_version;
            _motion?.Stop();
            var slot = RequestedSlot!;
            var start = _isFlying ? CurrentPosition : _card.TranslatePoint(default, this);
            var margin = _isFlying ? _originalMargin : _card.Margin;
            var target = slot.TranslatePoint(new Point(slot.Padding.Left + margin.Left, slot.Padding.Top + margin.Top), this);
            var size = _isFlying ? _flightSize : _card.Bounds.Size;
            if (_motion is null || !IsEffectivelyVisible || ArcMotion.GetEffectiveLevel(this) != ArcMotionLevel.Full
                || start is null || target is null || !IsUsable(size) || !IsUsable(slot.Bounds.Size))
            {
                PlaceInRequestedSlot();
                _moveUpdate = Task.FromResult(true);
                return;
            }

            _start = start.Value;
            _target = target.Value;
            _flightSize = size;
            _progress = 0;
            if (!_isFlying)
            {
                TakeCardForFlight();
            }
            ApplyProgress(0);
            InvalidateMeasure();
            InvalidateArrange();
            _motion.Jump(0);
            _moveUpdate = CompleteMoveAsync(_motion.AnimateTo(1,
                MotionSpec.Tween(TimeSpan.FromMilliseconds(360), MusicMotion.InOut)), version);
        }

        /// <summary>Returns the real card to its requested slot and releases all motion and slot subscriptions.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            StopAndPlace();
            ReleaseMotion();
            UnsubscribeSlots();
            Classes.CollectionChanged -= OnMotionPolicyChanged;
            SizeChanged -= OnGeometryChanged;
            _card = null;
            _sourceSlot = null;
            _destinationSlot = null;
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_disposed)
            {
                return;
            }
            _motion = new MotionValue<double>(this, 0, MotionVectors.Double);
            _motion.Changed += ApplyProgress;
            SubscribeSlots();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            StopAndPlace();
            ReleaseMotion();
            UnsubscribeSlots();
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override Size MeasureOverride(Size availableSize)
        {
            if (_isFlying)
            {
                _card?.Measure(_flightSize);
            }
            return default;
        }

        /// <inheritdoc />
        protected override Size ArrangeOverride(Size finalSize)
        {
            if (_isFlying)
            {
                _card?.Arrange(new Rect(default(Point), _flightSize));
            }
            return finalSize;
        }

        /// <summary>Gets the requested ordinary or expanded owner.</summary>
        private ContentControl? RequestedSlot => _expanded ? _destinationSlot : _sourceSlot;
        /// <summary>Gets the current painted origin independently of the next layout pass.</summary>
        private Point CurrentPosition => new(_start.X + (_target.X - _start.X) * _progress,
            _start.Y + (_target.Y - _start.Y) * _progress);

        /// <summary>Temporarily gives the one real card a frozen rectangle in the overlay.</summary>
        private void TakeCardForFlight()
        {
            var card = _card!;
            _originalWidth = card.Width;
            _originalHeight = card.Height;
            _originalMargin = card.Margin;
            _originalTransform = card.RenderTransform;
            RemoveFromSlots();
            card.SetCurrentValue(WidthProperty, _flightSize.Width);
            card.SetCurrentValue(HeightProperty, _flightSize.Height);
            card.SetCurrentValue(MarginProperty, default(Thickness));
            card.SetCurrentValue(RenderTransformProperty, _translation);
            _isFlying = true;
            Children.Add(card);
        }

        /// <summary>Interpolates only translation, without changing the card's layout or its scale.</summary>
        /// <param name="value">The eased progress of the current uninterrupted segment.</param>
        private void ApplyProgress(double value)
        {
            _progress = Math.Clamp(value, 0, 1);
            if (_isFlying)
            {
                var position = CurrentPosition;
                _translation.X = position.X;
                _translation.Y = position.Y;
            }
        }

        /// <summary>Commits a completed segment only if no newer navigation or lifecycle event replaced it.</summary>
        /// <param name="animation">The sole Arc animation task for this segment.</param>
        /// <param name="version">The navigation generation that started the segment.</param>
        /// <returns>True after committing the requested slot, or false after interruption.</returns>
        private async Task<bool> CompleteMoveAsync(Task<bool> animation, int version)
        {
            var completed = await animation;
            if (!completed || version != _version || _disposed)
            {
                return false;
            }
            PlaceInRequestedSlot();
            return true;
        }

        /// <summary>Stops obsolete completion work before committing the currently requested owner.</summary>
        private void StopAndPlace()
        {
            _version++;
            _motion?.Stop();
            PlaceInRequestedSlot();
        }

        /// <summary>Restores the original card expressions and puts it in exactly one live content slot.</summary>
        private void PlaceInRequestedSlot()
        {
            if (_card is not { } card || RequestedSlot is not { } slot)
            {
                return;
            }
            if (!_isFlying && ReferenceEquals(slot.Content, card))
            {
                return;
            }
            if (_isFlying)
            {
                _isFlying = false;
                Children.Remove(card);
                card.SetCurrentValue(WidthProperty, _originalWidth);
                card.SetCurrentValue(HeightProperty, _originalHeight);
                card.SetCurrentValue(MarginProperty, _originalMargin);
                card.SetCurrentValue(RenderTransformProperty, _originalTransform);
                _originalTransform = null;
            }
            RemoveFromSlots();
            slot.SetCurrentValue(ContentControl.ContentProperty, card);
        }

        /// <summary>Removes only this card, leaving ownership of unrelated controls outside this helper.</summary>
        private void RemoveFromSlots()
        {
            if (_sourceSlot is not null && ReferenceEquals(_sourceSlot.Content, _card))
            {
                _sourceSlot.SetCurrentValue(ContentControl.ContentProperty, null);
            }
            if (_destinationSlot is not null && ReferenceEquals(_destinationSlot.Content, _card))
            {
                _destinationSlot.SetCurrentValue(ContentControl.ContentProperty, null);
            }
        }

        /// <summary>Watches the reserved slots only while they share this layer's visual lifetime.</summary>
        private void SubscribeSlots()
        {
            if (_slotsSubscribed || _sourceSlot is null || _destinationSlot is null)
            {
                return;
            }
            _sourceSlot.SizeChanged += OnGeometryChanged;
            _destinationSlot.SizeChanged += OnGeometryChanged;
            _slotsSubscribed = true;
        }

        /// <summary>Removes external event references when the layer is replaced or detached.</summary>
        private void UnsubscribeSlots()
        {
            if (_slotsSubscribed)
            {
                _sourceSlot!.SizeChanged -= OnGeometryChanged;
                _destinationSlot!.SizeChanged -= OnGeometryChanged;
                _slotsSubscribed = false;
            }
        }

        /// <summary>Settles on resize rather than moving toward coordinates captured in a previous viewport.</summary>
        /// <param name="sender">The flight layer or a reserved slot.</param>
        /// <param name="e">The changed layout geometry.</param>
        private void OnGeometryChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_isFlying)
            {
                StopAndPlace();
            }
        }

        /// <summary>Finishes immediately if reduced or disabled motion is requested during a flight.</summary>
        /// <param name="sender">The inherited Arc motion classifications.</param>
        /// <param name="e">The changed motion policy.</param>
        private void OnMotionPolicyChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isFlying && ArcMotion.GetEffectiveLevel(this) != ArcMotionLevel.Full)
            {
                StopAndPlace();
            }
        }

        /// <summary>Disposes the only frame driver after unsubscribing its control callback.</summary>
        private void ReleaseMotion()
        {
            if (_motion is not null)
            {
                _motion.Changed -= ApplyProgress;
                _motion.Dispose();
                _motion = null;
            }
        }

        /// <summary>Checks whether a laid-out rectangle can support a real-card flight.</summary>
        /// <param name="size">The actual slot or card size.</param>
        /// <returns>True for a finite, nonempty rectangle.</returns>
        private static bool IsUsable(Size size)
        {
            return double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;
        }
    }
}
