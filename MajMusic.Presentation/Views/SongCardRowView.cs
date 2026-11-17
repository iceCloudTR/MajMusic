#nullable enable

using Avalonia;
using Avalonia.Controls.Primitives;
using System.ComponentModel;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Rebinds one bounded row of existing song controls when its native virtualized container is recycled.</summary>
    public sealed class SongCardRowView : UniformGrid
    {
        /// <summary>Identifies the shared song-card proportions without replacing the row's child controls.</summary>
        public static readonly StyledProperty<SongCardLayout> CardLayoutProperty =
            AvaloniaProperty.Register<SongCardRowView, SongCardLayout>(nameof(CardLayout));
        /// <summary>Identifies whether the original owning list currently displays real song content.</summary>
        public static readonly StyledProperty<bool> IsBrowsingActiveProperty =
            AvaloniaProperty.Register<SongCardRowView, bool>(nameof(IsBrowsingActive), true);
        /// <summary>Bounds retained static child trees by the maximum responsive column count.</summary>
        private const int MaximumCards = 16;
        /// <summary>Retains only this row's reusable controls, including hidden spare columns.</summary>
        private readonly List<SongCard> _cards = new();
        /// <summary>Retains only the latest lightweight source slot, independently of its changing lazy catalog slice.</summary>
        private SongCardRow? _row;
        /// <summary>Records whether the current attached slot can notify this view.</summary>
        private bool _observingRow;
        /// <summary>Prevents the slot's column synchronization from recursively rebinding the same data.</summary>
        private bool _synchronizingColumns;
        /// <summary>Prevents detached virtualized rows from starting media reads.</summary>
        private bool _attached;

        /// <summary>Keeps a null recycled row finite and prevents hidden spare controls from adding another grid row.</summary>
        static SongCardRowView()
        {
            ColumnsProperty.OverrideDefaultValue<SongCardRowView>(1);
            RowsProperty.OverrideDefaultValue<SongCardRowView>(1);
        }

        /// <summary>Creates a single-row panel whose song controls remain owned by their original virtualized container.</summary>
        public SongCardRowView()
        {
            Focusable = false;
            DataContextChanged += OnRowContextChanged;
        }

        /// <summary>Gets or sets the proportions applied to every retained song control.</summary>
        public SongCardLayout CardLayout
        {
            get => GetValue(CardLayoutProperty);
            set => SetValue(CardLayoutProperty, value);
        }
        /// <summary>Gets or sets whether attached current-row cards may activate their artwork and metadata.</summary>
        public bool IsBrowsingActive
        {
            get => GetValue(IsBrowsingActiveProperty);
            set => SetValue(IsBrowsingActiveProperty, value);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ColumnsProperty && !_synchronizingColumns)
            {
                BindCurrentRow();
            }
            else if (change.Property == CardLayoutProperty)
            {
                foreach (var card in _cards)
                {
                    card.CardLayout = CardLayout;
                }
            }
            else if (change.Property == IsBrowsingActiveProperty)
            {
                BindCurrentRow();
            }
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            ObserveCurrentRow();
            SynchronizeColumns();
            BindCurrentRow();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = false;
            StopObservingRow();
            BindCurrentRow();
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override Size MeasureOverride(Size availableSize)
        {
            if ((!_attached || !IsBrowsingActive) && _row is { Tracks.Count: > 0 })
            {
                // Native virtualization may measure before attaching a recycled row. Preserve its extent without touching lazy card indices.
                var width = SongCardGeometry.GetMeasureWidth(availableSize.Width, Bounds.Width, CardLayout);
                var columns = Math.Clamp(Columns, 1, MaximumCards);
                var height = SongCardGeometry.GetHeight(Math.Max(0, width / columns - 12), CardLayout) + 12;
                return new Size(width, height);
            }
            return base.MeasureOverride(availableSize);
        }

        /// <summary>Transfers only the row's visible data while keeping its previously constructed static control trees.</summary>
        /// <param name="sender">The recycled row panel.</param>
        /// <param name="args">The replaced row data.</param>
        private void OnRowContextChanged(object? sender, EventArgs args)
        {
            ObserveCurrentRow();
            SynchronizeColumns();
            BindCurrentRow();
        }

        /// <summary>Subscribes only while attached, so a stable view-model slot cannot retain a retired control tree.</summary>
        private void ObserveCurrentRow()
        {
            var row = DataContext as SongCardRow;
            if (!ReferenceEquals(row, _row))
            {
                StopObservingRow();
                _row = row;
            }
            if (_attached && !_observingRow && _row is not null)
            {
                _row.PropertyChanged += OnRowPropertyChanged;
                _observingRow = true;
            }
        }

        /// <summary>Releases the slot's reverse event reference while preserving a detached view's current data context.</summary>
        private void StopObservingRow()
        {
            if (_observingRow)
            {
                _row!.PropertyChanged -= OnRowPropertyChanged;
                _observingRow = false;
            }
        }

        /// <summary>Reads the newest slice from an existing slot without replacing its native list container.</summary>
        /// <param name="sender">The stable current source slot.</param>
        /// <param name="args">The changed source or grouping property.</param>
        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (ReferenceEquals(sender, _row) && args.PropertyName is null or "" or nameof(SongCardRow.Tracks) or nameof(SongCardRow.Columns))
            {
                SynchronizeColumns();
                BindCurrentRow();
            }
        }

        /// <summary>Applies current slot grouping without reentering the column-change binding path.</summary>
        private void SynchronizeColumns()
        {
            if (_row is null)
            {
                return;
            }
            _synchronizingColumns = true;
            try
            {
                SetCurrentValue(ColumnsProperty, Math.Clamp(_row.Columns, 1, MaximumCards));
            }
            finally
            {
                _synchronizingColumns = false;
            }
        }

        /// <summary>Materializes only this row's bounded visible prefix and clears every spare control before reuse.</summary>
        private void BindCurrentRow()
        {
            var row = _row;
            var count = !_attached || !IsBrowsingActive || row is null ? 0 : Math.Min(row.Tracks.Count, Math.Clamp(Columns, 1, MaximumCards));
            while (_cards.Count < count)
            {
                var card = new SongCard
                {
                    Margin = new Thickness(6),
                    IsBrowsingActive = false,
                    IsVisible = false,
                    DataContext = null,
                    CardLayout = CardLayout
                };
                _cards.Add(card);
                Children.Add(card);
            }

            for (var index = 0; index < _cards.Count; index++)
            {
                var card = _cards[index];
                if (index >= count)
                {
                    card.IsBrowsingActive = false;
                    card.IsVisible = false;
                    card.DataContext = null;
                    continue;
                }

                var next = row!.Tracks[index];
                if (!ReferenceEquals(card.DataContext, next))
                {
                    // Stop the old resource owner and quick-play gesture before a new song can inherit this control.
                    card.IsBrowsingActive = false;
                    card.IsVisible = false;
                    card.DataContext = next;
                }
                card.CardLayout = CardLayout;
                card.IsVisible = true;
                card.IsBrowsingActive = _attached && IsBrowsingActive;
            }
            InvalidateMeasure();
        }

    }
}
