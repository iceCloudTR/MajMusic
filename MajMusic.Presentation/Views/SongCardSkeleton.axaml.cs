#nullable enable

using Avalonia;
using Avalonia.Controls;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;

namespace MajMusic.Presentation.Views
{
    /// <summary>Reserves the same two-to-one artwork and metadata surface as a real music card.</summary>
    public sealed partial class SongCardSkeleton : UserControl
    {
        /// <summary>Identifies the proportions shared with the actual incoming song card.</summary>
        public static readonly StyledProperty<SongCardLayout> CardLayoutProperty = AvaloniaProperty.Register<SongCardSkeleton, SongCardLayout>(nameof(CardLayout));
        /// <summary>Retains the width used by the latest proportional placeholder measurement.</summary>
        private double _measuredCardWidth;

        /// <summary>Registers geometry invalidation when the preferred card density changes.</summary>
        static SongCardSkeleton()
        {
            AffectsMeasure<SongCardSkeleton>(CardLayoutProperty);
        }

        /// <summary>Creates Arc's accessible pulsing text placeholders beside a square artwork placeholder.</summary>
        public SongCardSkeleton()
        {
            InitializeComponent();
            SizeChanged += OnSkeletonSizeChanged;
        }

        /// <summary>Gets or sets the density used by the corresponding real song cards.</summary>
        public SongCardLayout CardLayout
        {
            get => GetValue(CardLayoutProperty);
            set => SetValue(CardLayoutProperty, value);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == CardLayoutProperty)
            {
                Classes.Set("small", CardLayout == SongCardLayout.Small);
                if (this.FindControl<Grid>("SkeletonArtworkGrid") is { } grid)
                {
                    grid.ColumnDefinitions = new ColumnDefinitions(CardLayout == SongCardLayout.Compact ? "*,2*" : "*,*");
                }

                if (this.FindControl<TKCode.Arc.Skeleton>("SkeletonTitle") is { } title)
                {
                    title.Lines = CardLayout == SongCardLayout.Small ? 1 : 2;
                }
            }
        }

        /// <inheritdoc />
        protected override Size MeasureOverride(Size availableSize)
        {
            var width = SongCardGeometry.GetMeasureWidth(availableSize.Width, Bounds.Width, CardLayout);
            _measuredCardWidth = width;
            var size = new Size(Math.Max(0, width), SongCardGeometry.GetHeight(width, CardLayout));
            base.MeasureOverride(size);
            return size;
        }

        /// <summary>Recalculates placeholder height when an initially unmeasured viewport assigns its real width.</summary>
        /// <param name="sender">The arranged placeholder card.</param>
        /// <param name="e">The actual assigned dimensions.</param>
        private void OnSkeletonSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (double.IsFinite(e.NewSize.Width) && e.NewSize.Width > MusicGeometry.OutlineWidth * 2
                && Math.Abs(e.NewSize.Width - _measuredCardWidth) > 0.5)
            {
                InvalidateMeasure();
            }
        }
    }
}
