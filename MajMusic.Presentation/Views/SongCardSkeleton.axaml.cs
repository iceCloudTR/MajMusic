#nullable enable

using Avalonia;
using Avalonia.Controls;

namespace MajMusic.Presentation.Views
{
    /// <summary>Reserves the same two-to-one artwork and metadata surface as a real music card.</summary>
    public sealed partial class SongCardSkeleton : UserControl
    {
        /// <summary>Creates Arc's accessible pulsing text placeholders beside a square artwork placeholder.</summary>
        public SongCardSkeleton()
        {
            InitializeComponent();
        }

        /// <inheritdoc />
        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 320;
            var size = new Size(Math.Max(0, width), Math.Max(0, width / 2));
            base.MeasureOverride(size);
            return size;
        }
    }
}
