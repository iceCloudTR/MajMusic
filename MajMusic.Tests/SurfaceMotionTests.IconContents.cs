#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc.Motion;
using ArcButton = TKCode.Arc.Button;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace MajMusic.Tests
{
    /// <summary>Checks original icon Paths inside Arc's real label and size templates rather than relying on button borders.</summary>
    internal static partial class SurfaceMotionTests
    {
        /// <summary>Requires static first attachment, source-icon replacement and pointer presses to keep icon geometry fixed.</summary>
        /// <param name="clock">The installed deterministic production Arc clock.</param>
        private static void ValidateStaticIconContents(ManualFrameClock clock)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Margin = new Thickness(20) };
            var buttons = new[] { "icon", "iconSmall", "transport", "transportPrimary" }
                .Select(name => new ArcButton { Classes = { name }, Content = StaticIconPath() }).ToArray();
            foreach (var button in buttons)
            {
                content.Children.Add(button);
            }
            var window = new Window { Width = 300, Height = 110, Content = content };
            window.Styles.Add(new StyleInclude(new Uri("avares://MajMusic.Presentation/Styles/MusicStyles.axaml"))
            {
                Source = new Uri("avares://MajMusic.Presentation/Styles/MusicStyles.axaml")
            });
            try
            {
                window.Show();
                Pump(window);
                var first = buttons.Select(button => PaintedIconBounds((ShapePath)button.Content!, window)).ToArray();
                clock.Advance(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5));
                Pump(window);
                for (var index = 0; index < buttons.Length; index++)
                {
                    var button = buttons[index];
                    var path = (ShapePath)button.Content!;
                    TestAssert.True(SharedCardRectDifference(first[index], PaintedIconBounds(path, window)) < .1
                        && IconTransformDescription(path, button).Length == 0,
                        "A static icon must not acquire a Y+2 label entrance on first attachment: " + button.Classes + "; " + IconTransformDescription(path, button));
                }

                foreach (var button in buttons)
                {
                    var original = PaintedIconBounds((ShapePath)button.Content!, window);
                    var replacement = StaticIconPath();
                    button.Content = replacement;
                    Pump(window);
                    var entering = PaintedIconBounds(replacement, window);
                    var enteringTransforms = IconTransformDescription(replacement, button);
                    clock.Advance(TimeSpan.FromMilliseconds(45), TimeSpan.FromMilliseconds(5));
                    Pump(window);
                    var middle = PaintedIconBounds(replacement, window);
                    var transforms = IconTransformDescription(replacement, button);
                    TestAssert.True(SharedCardRectDifference(original, entering) < .1 && enteringTransforms.Length == 0
                        && SharedCardRectDifference(original, middle) < .1 && transforms.Length == 0,
                        $"Replacing an icon source must keep the real Path fixed rather than introduce an Arc label slide: class={string.Join(',', button.Classes)}, before={original}, entering={entering}, middle={middle}, enterAncestors={enteringTransforms}, middleAncestors={transforms}.");

                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(point, MouseButton.Left);
                    clock.Advance(TimeSpan.FromMilliseconds(45), TimeSpan.FromMilliseconds(5));
                    Pump(window);
                    transforms = IconTransformDescription(replacement, button);
                    TestAssert.True(SharedCardRectDifference(original, PaintedIconBounds(replacement, window)) < .1 && transforms.Length == 0,
                        "An icon press must not add a nested scale around its already animated or shared card: " + transforms);
                    window.MouseUp(point, MouseButton.Left);
                    Pump(window);
                }
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>Creates an original vector control with fixed design coordinates, independently of template-generated content.</summary>
        /// <returns>The source Path to inspect after Arc has hosted it.</returns>
        private static ShapePath StaticIconPath()
        {
            return new ShapePath
            {
                Data = Geometry.Parse("M 3,6 L 3,3 L 6,3 M 10,3 L 13,3 L 13,6 M 13,10 L 13,13 L 10,13 M 6,13 L 3,13 L 3,10"),
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
                Stroke = Brushes.White,
                StrokeThickness = 1.5
            };
        }

        /// <summary>Measures the original Path's design-space ink through every actual visual ancestor transform.</summary>
        /// <param name="path">The original Button.Content Path, excluding any outgoing template layer.</param>
        /// <param name="window">The actual rendered viewport.</param>
        /// <returns>The design ink's transformed window rectangle.</returns>
        private static Rect PaintedIconBounds(ShapePath path, Window window)
        {
            var ink = path.Data!.Bounds;
            var start = path.TranslatePoint(ink.TopLeft, window)!.Value;
            var end = path.TranslatePoint(ink.BottomRight, window)!.Value;
            return new Rect(start, end);
        }

        /// <summary>Reports any source-icon or template transform, including the owning button's own press transform.</summary>
        /// <param name="path">The original source vector control.</param>
        /// <param name="button">The fixed button boundary at which traversal stops.</param>
        /// <returns>A nonempty diagnostic if nested geometry feedback is present.</returns>
        private static string IconTransformDescription(Control path, Control button)
        {
            var transforms = new List<string>();
            for (Visual? current = path; current is not null; current = current.GetVisualParent())
            {
                if (current.RenderTransform is { } transform && !transform.Value.IsIdentity)
                {
                    transforms.Add(current.GetType().Name + "/" + (current as Control)?.Name + "=" + transform.Value);
                }
                if (ReferenceEquals(current, button))
                {
                    break;
                }
            }
            return string.Join("; ", transforms);
        }
    }
}
