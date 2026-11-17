#nullable enable

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;

namespace MajMusic.Presentation.Views
{
    /// <summary>Creates only the artwork placeholders covering the current viewport and one nearby row.</summary>
    public sealed class BrowseSkeletonView : Panel
    {
        /// <summary>Bounds retained detached cards independently of the largest requested viewport.</summary>
        private const int MaximumPooledPlaceholders = 32;
        /// <summary>Retains only detached static cards whose Arc loops cannot request frames.</summary>
        private readonly Stack<SongCardSkeleton> _placeholderPool = new(MaximumPooledPlaceholders);
        /// <summary>Retains the number of rows represented by the current placeholder children.</summary>
        private int _placeholderRows;
        /// <summary>Counts actual card construction independently of attachment and layout.</summary>
        private int _createdPlaceholderCount;
        /// <summary>Counts cards returned from this view's bounded detached pool.</summary>
        private int _reusedPlaceholderCount;
        /// <summary>Retains the latest effective viewport update cost without observable notifications.</summary>
        private TimeSpan _lastSetViewportElapsed;
        /// <summary>Identifies the viewport whose detached templates were last fully prepared.</summary>
        private Size _preparedViewport;
        /// <summary>Retains the column count used by the last complete detached preparation.</summary>
        private int _preparedColumns;
        /// <summary>Retains the layout used by the last complete detached preparation.</summary>
        private SongCardLayout _preparedLayout;
        /// <summary>Counts completed idle preparation passes independently of navigation.</summary>
        private int _prewarmCount;
        /// <summary>Accumulates idle preparation cost separately from synchronous navigation setup.</summary>
        private TimeSpan _totalPrewarmElapsed;

        /// <summary>Gets the target number of equal-width artwork columns.</summary>
        public int Columns { get; private set; } = 1;
        /// <summary>Gets the proportions shared with the incoming real music cards.</summary>
        public SongCardLayout CardLayout { get; private set; }

        /// <summary>Gets the finite number of instantiated artwork placeholders.</summary>
        public int PlaceholderCount => Children.Count;
        /// <summary>Gets the bounded number of detached retained cards, none of which receives pulse frames.</summary>
        public int PooledPlaceholderCount => _placeholderPool.Count;
        /// <summary>Gets the cumulative number of actual card constructions.</summary>
        public int CreatedPlaceholderCount => _createdPlaceholderCount;
        /// <summary>Gets the cumulative number of detached cards reused by later viewport updates.</summary>
        public int ReusedPlaceholderCount => _reusedPlaceholderCount;
        /// <summary>Gets the synchronous cost of the last effective update, excluding unchanged viewport calls.</summary>
        public TimeSpan LastSetViewportElapsed => _lastSetViewportElapsed;
        /// <summary>Gets the number of completed idle template and layout preparation passes.</summary>
        public int PrewarmCount => _prewarmCount;
        /// <summary>Gets accumulated idle preparation cost, which is not part of a navigation callback.</summary>
        public TimeSpan TotalPrewarmElapsed => _totalPrewarmElapsed;

        /// <summary>Fits placeholder rows to the same available width and margins used by real artwork cards.</summary>
        /// <param name="viewport">The current browsing viewport in logical pixels.</param>
        /// <param name="columns">The target responsive artwork-column count.</param>
        /// <param name="layout">The selected shared song-card density.</param>
        public void SetViewport(Size viewport, int columns, SongCardLayout layout = SongCardLayout.Loose)
        {
            var started = Stopwatch.GetTimestamp();
            columns = Math.Clamp(columns, 1, 16);
            var width = double.IsFinite(viewport.Width) && viewport.Width > 0 ? viewport.Width : 640;
            var height = double.IsFinite(viewport.Height) && viewport.Height > 0 ? viewport.Height : 380;
            var cardHeight = Math.Max(1, SongCardGeometry.GetHeight(width / columns - 12, layout));
            var rows = Math.Clamp((int)Math.Ceiling(Math.Min(height, 8192) / (cardHeight + 12)) + 1, 1, 64);
            if (Columns == columns && CardLayout == layout && _placeholderRows == rows && Children.Count != 0)
            {
                return;
            }

            Columns = columns;
            CardLayout = layout;
            _placeholderRows = rows;
            var count = columns * rows;
            RetireExcessPlaceholders(count);
            foreach (var child in Children)
            {
                ((SongCardSkeleton)child).CardLayout = layout;
            }
            while (Children.Count < count)
            {
                Children.Add(RentPlaceholder(layout));
            }

            InvalidateMeasure();
            _lastSetViewportElapsed = Stopwatch.GetElapsedTime(started);
        }

        /// <summary>Detaches completed placeholders to stop their Arc loops, retaining only a bounded reusable prefix.</summary>
        public void ClearPlaceholders()
        {
            RetireExcessPlaceholders(0);
            _placeholderRows = 0;
        }

        /// <summary>Checks whether the current detached pool has already prepared this exact viewport.</summary>
        /// <param name="viewport">The actual content lane.</param>
        /// <param name="columns">The responsive column count.</param>
        /// <param name="layout">The preferred card density.</param>
        /// <returns>Whether another idle preparation pass is unnecessary.</returns>
        internal bool IsPreparedFor(Size viewport, int columns, SongCardLayout layout)
        {
            return _preparedViewport == viewport && _preparedColumns == columns && _preparedLayout == layout
                && _placeholderPool.Count > 0;
        }

        /// <summary>Prepares bounded static templates while their presentation is hidden, then detaches every card.</summary>
        /// <param name="viewport">The stable real-content lane.</param>
        /// <param name="columns">The current responsive column count.</param>
        /// <param name="layout">The preferred card density.</param>
        internal void PrepareViewport(Size viewport, int columns, SongCardLayout layout)
        {
            if (Children.Count != 0 || IsPreparedFor(viewport, columns, layout) || viewport.Width <= 0 || viewport.Height <= 0)
            {
                return;
            }

            var started = Stopwatch.GetTimestamp();
            columns = Math.Clamp(columns, 1, 16);
            var rowHeight = SongCardGeometry.GetHeight(viewport.Width / columns - 12, layout) + 12;
            var rows = Math.Clamp((int)Math.Ceiling(Math.Min(viewport.Height, 8192) / Math.Max(1, rowHeight)) + 1, 1, 64);
            Columns = columns;
            CardLayout = layout;
            _placeholderRows = rows;
            try
            {
                var count = Math.Min(MaximumPooledPlaceholders, columns * rows);
                while (Children.Count < count)
                {
                    Children.Add(RentPlaceholder(layout));
                }
                ApplyPlaceholderTemplates(this);
                Measure(viewport);
                Arrange(new Rect(viewport));
                ApplyPlaceholderTemplates(this);
                Measure(viewport);
                Arrange(new Rect(viewport));
                _preparedViewport = viewport;
                _preparedColumns = columns;
                _preparedLayout = layout;
                _prewarmCount++;
            }
            finally
            {
                ClearPlaceholders();
                _totalPrewarmElapsed += Stopwatch.GetElapsedTime(started);
            }
        }

        /// <summary>Builds detached control templates without attaching another view or starting visual pulse loops.</summary>
        /// <param name="control">The static placeholder or one of its existing descendants.</param>
        private static void ApplyPlaceholderTemplates(Control control)
        {
            if (control is TemplatedControl templated)
            {
                templated.ApplyTemplate();
            }
            foreach (var child in control.GetVisualChildren().OfType<Control>())
            {
                ApplyPlaceholderTemplates(child);
            }
        }

        /// <summary>Reuses a detached fully static card before constructing another control tree.</summary>
        /// <param name="layout">The incoming shared card density.</param>
        /// <returns>A detached card without an inherited workspace or network context.</returns>
        private SongCardSkeleton RentPlaceholder(SongCardLayout layout)
        {
            if (_placeholderPool.TryPop(out var card))
            {
                _reusedPlaceholderCount++;
                card.CardLayout = layout;
                return card;
            }

            card = new SongCardSkeleton { DataContext = null, Margin = new Thickness(6), CardLayout = layout };
            _createdPlaceholderCount++;
            return card;
        }

        /// <summary>Detaches surplus controls before adding them to the bounded pool, preserving prefix identity.</summary>
        /// <param name="retainedCount">The number of existing visible cards still required.</param>
        private void RetireExcessPlaceholders(int retainedCount)
        {
            for (var index = Children.Count - 1; index >= retainedCount; index--)
            {
                var card = (SongCardSkeleton)Children[index];
                Children.RemoveAt(index);
                if (_placeholderPool.Count < MaximumPooledPlaceholders)
                {
                    _placeholderPool.Push(card);
                }
            }
        }

        /// <inheritdoc />
        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 640;
            var cellWidth = width / Columns;
            var cellHeight = SongCardGeometry.GetHeight(cellWidth - 12, CardLayout) + 12;
            foreach (var child in Children)
            {
                child.Measure(new Size(cellWidth, cellHeight));
            }

            var height = double.IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height) : cellHeight * _placeholderRows;
            return new Size(width, height);
        }

        /// <inheritdoc />
        protected override Size ArrangeOverride(Size finalSize)
        {
            var cellWidth = finalSize.Width / Columns;
            var cellHeight = SongCardGeometry.GetHeight(cellWidth - 12, CardLayout) + 12;
            for (var index = 0; index < Children.Count; index++)
            {
                Children[index].Arrange(new Rect(index % Columns * cellWidth, index / Columns * cellHeight, cellWidth, cellHeight));
            }

            return finalSize;
        }
    }
}
