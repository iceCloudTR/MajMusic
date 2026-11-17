#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Views
{
    /// <summary>Keeps the three native lists laid out and moves their existing pixels along one continuous horizontal axis.</summary>
    public sealed class ResidentBrowseStrip : Grid
    {
        private readonly TranslateTransform[] _transforms = [new(), new(), new()];
        private MotionValue<double>? _motion;
        private double _position;
        private int _target;

        /// <summary>Gets the current position in page widths.</summary>
        public double Position => _position;
        /// <summary>Gets completion of the latest motion; false denotes retargeting.</summary>
        public Task<bool> TransitionUpdate { get; private set; } = Task.FromResult(true);
        /// <summary>Notifies the browser when its loading layer needs to follow a page.</summary>
        internal event Action? PositionChanged;

        internal void InitializePages()
        {
            for (var index = 0; index < Children.Count; index++)
            {
                Children[index].RenderTransform = _transforms[index];
            }
            ApplyPosition(_target);
        }

        internal void Navigate(HomeBrowseMode mode, bool animate)
        {
            var next = (int)mode;
            if (next != _target && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
                && focused.GetVisualAncestors().Contains(Children[_target]))
            {
                TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None);
            }
            _target = next;
            var distance = Math.Abs(_position - next);
            if (animate && _motion is not null && Bounds.Width > 0 && IsEffectivelyVisible
                && distance > 0.001 && ArcMotion.GetEffectiveLevel(this) == ArcMotionLevel.Full)
            {
                UpdateInput();
                var duration = TimeSpan.FromMilliseconds(Math.Clamp(360 * Math.Sqrt(distance), 150, 420));
                TransitionUpdate = _motion.AnimateTo(next, MotionSpec.Tween(duration, MusicMotion.InOut));
            }
            else
            {
                _motion?.Jump(next);
                ApplyPosition(next);
                TransitionUpdate = Task.FromResult(true);
            }
        }

        internal double OffsetFor(HomeBrowseMode mode) => _transforms[(int)mode].X;

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _motion = new MotionValue<double>(this, _target, MotionVectors.Double);
            _motion.Changed += ApplyPosition;
            ApplyPosition(_target);
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_motion is not null)
            {
                _motion.Changed -= ApplyPosition;
                _motion.Dispose();
                _motion = null;
            }
            TransitionUpdate = Task.FromResult(true);
            ApplyPosition(_target);
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = base.ArrangeOverride(finalSize);
            UpdateTransforms(size.Width);
            return size;
        }

        private void ApplyPosition(double position)
        {
            _position = position;
            UpdateTransforms(Bounds.Width);
            UpdateInput();
        }

        private void UpdateTransforms(double width)
        {
            for (var index = 0; index < Children.Count; index++)
            {
                var distance = index - _position;
                var displayed = Math.Abs(distance) < 1;
                Children[index].Opacity = displayed ? 1 : 0;
                _transforms[index].X = displayed ? distance * width : 0;
            }
            PositionChanged?.Invoke();
        }

        private void UpdateInput()
        {
            for (var index = 0; index < Children.Count; index++)
            {
                var interactive = index == _target && Math.Abs(_position - _target) < 0.001;
                Children[index].IsHitTestVisible = interactive;
                KeyboardNavigation.SetTabNavigation(Children[index], interactive ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
            }
        }
    }
}
