#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Expands a right-anchored search field while its source selector gives up the same layout space.</summary>
    /// <remarks>Children are the selectors, search border, opening button and closing button, in that order.</remarks>
    public sealed class SearchRevealPanel : Panel
    {
        /// <summary>Identifies whether search currently owns the toolbar.</summary>
        public static readonly StyledProperty<bool> IsExpandedProperty =
            AvaloniaProperty.Register<SearchRevealPanel, bool>(nameof(IsExpanded));

        /// <summary>Identifies the continuous visual progress from the closed icon to the expanded field.</summary>
        public static readonly DirectProperty<SearchRevealPanel, double> RevealProgressProperty =
            AvaloniaProperty.RegisterDirect<SearchRevealPanel, double>(nameof(RevealProgress), panel => panel.RevealProgress);

        /// <summary>Identifies the width currently occupied by the right-anchored search border.</summary>
        public static readonly DirectProperty<SearchRevealPanel, double> DisplayedSearchWidthProperty =
            AvaloniaProperty.RegisterDirect<SearchRevealPanel, double>(nameof(DisplayedSearchWidth), panel => panel.DisplayedSearchWidth);

        /// <summary>Defines the shared control height and closed search diameter.</summary>
        private const double ControlHeight = 44;
        /// <summary>Separates the source selector from the closed search icon.</summary>
        private const double SourceSpacing = 10;
        /// <summary>Retains the current layout progress independently of its target.</summary>
        private double _revealProgress;
        /// <summary>Retains the actual search width last arranged in the toolbar.</summary>
        private double _displayedSearchWidth = ControlHeight;
        /// <summary>Owns one interruptible Arc value for the attached visual lifetime.</summary>
        private MotionValue<double>? _motion;
        /// <summary>Retains the current animation completion, including interrupted transitions.</summary>
        private Task<bool> _revealUpdate = Task.FromResult(true);
        /// <summary>Invalidates deferred focus changes when search reverses or leaves the visual tree.</summary>
        private int _focusReturnVersion;
        /// <summary>Prevents duplicate focus work while the first close frame is awaiting layout.</summary>
        private bool _focusReturnQueued;

        /// <summary>Gets or sets whether the search input should be expanded.</summary>
        public bool IsExpanded
        {
            get => GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        /// <summary>Gets the current visual progress, from zero for the icon to one for the complete field.</summary>
        public double RevealProgress => _revealProgress;
        /// <summary>Gets the actual right-anchored search width, including its closed icon slot.</summary>
        public double DisplayedSearchWidth => _displayedSearchWidth;
        /// <summary>Gets completion of the current transition; false indicates that another target superseded it.</summary>
        public Task<bool> RevealUpdate => _revealUpdate;

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _motion = new MotionValue<double>(this, IsExpanded ? 1 : 0, MotionVectors.Double);
            _motion.Changed += ApplyProgress;
            _motion.Jump(IsExpanded ? 1 : 0);
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _focusReturnVersion++;
            _focusReturnQueued = false;
            if (_motion is not null)
            {
                _motion.Changed -= ApplyProgress;
                _motion.Dispose();
                _motion = null;
            }

            _revealUpdate = Task.FromResult(true);
            ApplyProgress(IsExpanded ? 1 : 0);
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsExpandedProperty)
            {
                _focusReturnVersion++;
                _focusReturnQueued = false;
                var target = IsExpanded ? 1d : 0d;
                if (_motion is null || !IsEffectivelyVisible || ArcMotion.GetEffectiveLevel(this) != ArcMotionLevel.Full)
                {
                    if (_motion is null)
                    {
                        ApplyProgress(target);
                    }
                    else
                    {
                        _motion.Jump(target);
                    }

                    _revealUpdate = Task.FromResult(true);
                    return;
                }

                SynchronizeControls();
                var specification = MotionSpec.Tween(TimeSpan.FromMilliseconds(260), MusicMotion.InOut);
                _revealUpdate = _motion.AnimateTo(target, specification);
            }
        }

        /// <inheritdoc />
        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 440;
            var searchWidth = SearchWidth(width);
            var sourceWidth = Math.Max(0, width - ControlHeight - SourceSpacing) * (1 - RevealProgress);
            SynchronizeControls();
            if (Children.Count > 0)
            {
                Children[0].Measure(new Size(sourceWidth, ControlHeight));
            }

            if (Children.Count > 1)
            {
                Children[1].Measure(new Size(searchWidth, ControlHeight));
            }

            for (var index = 2; index < Children.Count; index++)
            {
                Children[index].Measure(new Size(Math.Min(width, ControlHeight), ControlHeight));
            }

            return new Size(width, ControlHeight);
        }

        /// <inheritdoc />
        protected override Size ArrangeOverride(Size finalSize)
        {
            var width = Math.Max(0, finalSize.Width);
            var searchWidth = SearchWidth(width);
            var sourceWidth = Math.Max(0, width - ControlHeight - SourceSpacing) * (1 - RevealProgress);
            var buttonWidth = Math.Min(width, ControlHeight);
            if (Children.Count > 0)
            {
                Children[0].Arrange(new Rect(0, 0, sourceWidth, ControlHeight));
            }

            if (Children.Count > 1)
            {
                Children[1].Arrange(new Rect(width - searchWidth, 0, searchWidth, ControlHeight));
            }

            for (var index = 2; index < Children.Count; index++)
            {
                Children[index].Arrange(new Rect(width - buttonWidth, 0, buttonWidth, ControlHeight));
            }

            SetAndRaise(DisplayedSearchWidthProperty, ref _displayedSearchWidth, searchWidth);
            return finalSize;
        }

        /// <summary>Calculates the current field width without changing its right-hand anchor.</summary>
        /// <param name="width">The toolbar space beside the fixed home modes.</param>
        /// <returns>The field width between its icon diameter and the complete toolbar lane.</returns>
        private double SearchWidth(double width)
        {
            var collapsedWidth = Math.Min(width, ControlHeight);
            return collapsedWidth + (width - collapsedWidth) * RevealProgress;
        }

        /// <summary>Applies one continuous frame to both sides of the toolbar.</summary>
        /// <param name="progress">The Arc value's current progress.</param>
        private void ApplyProgress(double progress)
        {
            SetAndRaise(RevealProgressProperty, ref _revealProgress, Math.Clamp(progress, 0, 1));
            SynchronizeControls();
            InvalidateMeasure();
        }

        /// <summary>Keeps fading and hit targets consistent while the input and selector exchange their layout space.</summary>
        private void SynchronizeControls()
        {
            var progress = RevealProgress;
            var returnFocus = !IsExpanded && Children.Count > 2 &&
                (Children.Count > 1 && Children[1] is Border { Child: { } previousField } && previousField.IsKeyboardFocusWithin
                    || Children.Count > 3 && Children[3].IsKeyboardFocusWithin);
            if (Children.Count > 2)
            {
                Children[2].IsVisible = !IsExpanded || progress < 1;
                Children[2].Opacity = 1 - progress;
                Children[2].IsHitTestVisible = !IsExpanded;
                Children[2].Focusable = !IsExpanded;
            }

            if (Children.Count > 3)
            {
                Children[3].IsVisible = IsExpanded || progress > 0;
                Children[3].Opacity = progress;
                Children[3].IsHitTestVisible = IsExpanded;
                Children[3].Focusable = IsExpanded;
            }

            if (Children.Count > 0)
            {
                Children[0].IsVisible = !IsExpanded || progress < 1;
                Children[0].Opacity = 1 - progress;
                Children[0].IsHitTestVisible = !IsExpanded;
            }

            if (Children.Count > 1 && Children[1] is Border searchHost)
            {
                searchHost.IsHitTestVisible = IsExpanded;
                searchHost.CornerRadius = new CornerRadius(22 - 10 * progress);
                if (searchHost.Child is { } field)
                {
                    if (returnFocus)
                    {
                        ReturnFocusAfterLayout();
                    }

                    field.IsVisible = IsExpanded || progress > 0;
                    field.Opacity = progress;
                    field.IsHitTestVisible = IsExpanded;
                    field.Focusable = IsExpanded;
                }
            }
        }

        /// <summary>Returns focus after the restored icon has been arranged, unless another search intent superseded closing.</summary>
        private void ReturnFocusAfterLayout()
        {
            if (_focusReturnQueued)
            {
                return;
            }

            _focusReturnQueued = true;
            var version = _focusReturnVersion;
            Dispatcher.UIThread.Post(() =>
            {
                if (version != _focusReturnVersion)
                {
                    return;
                }

                _focusReturnQueued = false;
                if (!IsExpanded && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null
                    && Children.Count > 2 && Children[2].IsVisible && Children[2].IsEffectivelyEnabled)
                {
                    Children[2].Focus();
                }
            }, DispatcherPriority.Loaded);
        }
    }
}
