#nullable enable

using Avalonia;
using Avalonia.Controls;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;

namespace MajMusic.Presentation.Views
{
    /// <summary>Creates only the artwork placeholders covering the current viewport and one nearby row.</summary>
    public sealed class BrowseSkeletonView : Panel
    {
        /// <summary>Retains the number of rows represented by the current placeholder children.</summary>
        private int _placeholderRows;

        /// <summary>Gets the target number of equal-width artwork columns.</summary>
        public int Columns { get; private set; } = 1;
        /// <summary>Gets the proportions shared with the incoming real music cards.</summary>
        public SongCardLayout CardLayout { get; private set; }

        /// <summary>Gets the finite number of instantiated artwork placeholders.</summary>
        public int PlaceholderCount => Children.Count;

        /// <summary>Fits placeholder rows to the same available width and margins used by real artwork cards.</summary>
        /// <param name="viewport">The current browsing viewport in logical pixels.</param>
        /// <param name="columns">The target responsive artwork-column count.</param>
        /// <param name="layout">The selected shared song-card density.</param>
        public void SetViewport(Size viewport, int columns, SongCardLayout layout = SongCardLayout.Loose)
        {
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
            Children.Clear();
            for (var index = 0; index < columns * rows; index++)
            {
                Children.Add(new SongCardSkeleton { Margin = new Thickness(6), CardLayout = layout });
            }

            InvalidateMeasure();
        }

        /// <summary>Releases completed placeholder controls and their Arc pulse subscriptions.</summary>
        public void ClearPlaceholders()
        {
            Children.Clear();
            _placeholderRows = 0;
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
