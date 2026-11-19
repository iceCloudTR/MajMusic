#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace MajMusic.Presentation.Views
{
    /// <summary>Applies the practice transition directly to a platform surface above the app's drawing layer.</summary>
    public interface IPracticeDisc
    {
        /// <summary>Shows the centered fraction of the live chart without resizing its rendering buffer.</summary>
        /// <param name="fraction">Zero closes the disc and one reveals its full diameter.</param>
        void SetAperture(double fraction);
    }

    /// <summary>Identifies the playfield or one of the four native control groups around it.</summary>
    public enum PracticeSlot
    {
        /// <summary>The square hosting the circular chart.</summary>
        Disc,
        /// <summary>The back and practice lock actions.</summary>
        Navigation,
        /// <summary>The fullscreen and settings actions.</summary>
        Options,
        /// <summary>The left group of note counts.</summary>
        NotesLeft,
        /// <summary>The right group of note counts.</summary>
        NotesRight
    }

    /// <summary>Places the playfield and its controls in the inline, portrait or landscape practice view.</summary>
    internal enum PracticeLayout { Inline, Portrait, Landscape }

    /// <summary>Fits a centered disc into the actual free space between the measured corner controls.</summary>
    public sealed class PracticeStage : Panel
    {
        /// <summary>Associates a child with its place around the disc.</summary>
        public static readonly AttachedProperty<PracticeSlot> SlotProperty =
            AvaloniaProperty.RegisterAttached<PracticeStage, Control, PracticeSlot>("Slot");

        /// <summary>Width reserved on each side of the centered landscape playfield.</summary>
        internal const double TransportRail = 82;
        private const double Gap = 4;
        private PracticeLayout _layout;
        private bool _roundHost = true;

        /// <summary>Gets the child's place in the practice stage.</summary>
        /// <param name="child">The stage element to locate.</param>
        /// <returns>The element's assigned slot.</returns>
        public static PracticeSlot GetSlot(Control child) => child.GetValue(SlotProperty);

        /// <summary>Assigns a child to its place in the practice stage.</summary>
        /// <param name="child">The element to position.</param>
        /// <param name="value">The requested place around the playfield.</param>
        public static void SetSlot(Control child, PracticeSlot value) => child.SetValue(SlotProperty, value);

        /// <summary>Changes the stage arrangement without reparenting the native renderer.</summary>
        internal void SetLayout(PracticeLayout layout)
        {
            if (_layout != layout)
            {
                _layout = layout;
                InvalidateMeasure();
            }
        }

        /// <summary>Gets a centered circular aperture in the current native client pixels, including during rotation.</summary>
        /// <param name="width">The measured client width.</param>
        /// <param name="height">The measured client height.</param>
        /// <param name="fraction">The visible fraction of the disc diameter.</param>
        /// <returns>The pixel-aligned enclosing square.</returns>
        public static PixelRect GetDiscClip(int width, int height, double fraction)
        {
            var diameter = (int)Math.Round(Math.Min(width, height) * Math.Clamp(fraction, 0, 1));
            return new PixelRect((width - diameter) / 2, (height - diameter) / 2, diameter, diameter);
        }

        /// <summary>Reports whether this native view clips both painting and input to the circle.</summary>
        /// <param name="host">The mounted platform renderer.</param>
        /// <param name="round">Whether the native corners are available for app controls.</param>
        public static void ReportNativeClip(Control host, bool round) => host.FindAncestorOfType<PracticeStage>()?.SetRoundHost(round);

        /// <summary>Uses square clearance until the native surface can expose its corners to the app.</summary>
        internal void SetRoundHost(bool round)
        {
            if (_roundHost != round)
            {
                _roundHost = round;
                InvalidateMeasure();
            }
        }

        /// <inheritdoc />
        protected override Size MeasureOverride(Size availableSize)
        {
            StackPanel? options = null;
            var rightNotesHeight = 0d;
            foreach (var child in Children)
            {
                child.Measure(availableSize);
                if (GetSlot(child) == PracticeSlot.Options)
                {
                    options = child as StackPanel;
                }
                else if (GetSlot(child) == PracticeSlot.NotesRight)
                {
                    rightNotesHeight = child.DesiredSize.Height;
                }
            }
            if (options is not null)
            {
                // In a short inline pane the same two square buttons can release more space along the right edge.
                var extent = options.DesiredSize;
                var horizontal = options.Orientation == Orientation.Horizontal ? extent : new Size(extent.Height, extent.Width);
                var area = new Rect(availableSize).Deflate(Gap);
                var rotate = _layout == PracticeLayout.Inline && double.IsFinite(area.Width) && double.IsFinite(area.Height)
                    && area.Width > area.Height && area.Height >= horizontal.Width + rightNotesHeight + Gap
                    && Clearance(area.Center, new Rect(area.Right - horizontal.Width, area.Y, horizontal.Width, horizontal.Height)) < area.Height / 2;
                var orientation = rotate ? Orientation.Vertical : Orientation.Horizontal;
                if (options.Orientation != orientation)
                {
                    options.Orientation = orientation;
                    options.Measure(availableSize);
                }
            }
            // The parent's star cell owns the viewport. Native WebView measurements must not enlarge it.
            return default;
        }

        /// <inheritdoc />
        protected override Size ArrangeOverride(Size finalSize)
        {
            var width = finalSize.Width;
            var height = finalSize.Height;
            var inset = Math.Min(Gap, Math.Min(width, height) / 2);
            var area = new Rect(finalSize).Deflate(inset);
            Control? disc = null;
            var navigation = default(Size);
            var options = default(Size);
            foreach (var child in Children)
            {
                switch (GetSlot(child))
                {
                    case PracticeSlot.Disc:
                        disc = child;
                        break;
                    case PracticeSlot.Navigation:
                        navigation = child.DesiredSize;
                        break;
                    case PracticeSlot.Options:
                        options = child.DesiredSize;
                        break;
                }
            }

            var wide = _layout == PracticeLayout.Landscape;
            var portrait = _layout == PracticeLayout.Portrait;
            var rail = wide ? Math.Min(area.Width, Math.Max(TransportRail, Math.Max(navigation.Width, options.Width) + Gap)) : 0;
            var field = area.Deflate(new Thickness(rail, 0, 0, 0));
            var center = field.Center;
            var radius = Math.Min(field.Width, field.Height) / 2;
            var railTop = Math.Max(area.Y, area.Center.Y - (navigation.Height + Gap + options.Height) / 2);
            var notesTop = area.Y + Math.Max(navigation.Height, options.Height) + Gap;
            foreach (var child in Children)
            {
                var slot = GetSlot(child);
                if (slot == PracticeSlot.Disc || !child.IsVisible)
                {
                    continue;
                }
                var size = child.DesiredSize.Constrain(area.Size);
                var position = slot switch
                {
                    PracticeSlot.Navigation => new Point(area.X, wide ? railTop : area.Y),
                    PracticeSlot.Options => wide ? new Point(area.X, railTop + navigation.Height + Gap)
                        : new Point(area.Right - size.Width, area.Y),
                    PracticeSlot.NotesLeft => new Point(area.X, portrait ? notesTop : area.Bottom - size.Height),
                    _ => new Point(area.Right - size.Width, portrait ? notesTop : area.Bottom - size.Height)
                };
                var bounds = new Rect(position, size);
                child.Arrange(bounds);
                radius = Math.Min(radius, Clearance(center, bounds));
            }
            var diameter = Math.Floor(Math.Max(0, 2 * radius));
            disc?.Arrange(new Rect(center.X - diameter / 2, center.Y - diameter / 2, diameter, diameter));
            return finalSize;
        }
        /// <summary>Returns the radius permitted by one measured control, including room for pixel rounding.</summary>
        private double Clearance(Point center, Rect bounds)
        {
            var dx = Math.Max(0, Math.Max(bounds.X - center.X, center.X - bounds.Right));
            var dy = Math.Max(0, Math.Max(bounds.Y - center.Y, center.Y - bounds.Bottom));
            // Circular native surfaces expose their corners; a rectangular fallback needs a free axis.
            return Math.Max(0, (_roundHost ? Math.Sqrt(dx * dx + dy * dy) : Math.Max(dx, dy)) - Gap);
        }
    }
}
