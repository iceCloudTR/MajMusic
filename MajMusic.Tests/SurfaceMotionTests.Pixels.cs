#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MajMusic.Presentation.Theme;
using SkiaSharp;
using TKCode.Arc.Motion;

namespace MajMusic.Tests
{
    /// <summary>Measures painted artwork and glyphs independently of the transition's public diagnostics.</summary>
    internal static partial class SurfaceMotionTests
    {
        /// <summary>Checks actual first-frame identity and horizontal translations at four physical display scales.</summary>
        /// <param name="clock">The installed production Arc frame driver.</param>
        private static void ValidatePixelMatrix(ManualFrameClock clock)
        {
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
            {
                var oldPage = MarkedPage(400, Color.Parse("#501010"), Color.Parse("#E6781E"), Color.Parse("#50F050"));
                var newPage = MarkedPage(40, Color.Parse("#101050"), Color.Parse("#1EBEB4"), Color.Parse("#F53CD2"));
                var host = new MusicSurfaceHost { Content = oldPage, SlideDirection = 1 };
                var top = new Border { Height = 44, Background = Brushes.Gray };
                var bottom = new Border { Height = 44, Background = Brushes.DimGray };
                var grid = new Grid { RowDefinitions = RowDefinitions.Parse("44,*,44"), Children = { top, host, bottom } };
                Grid.SetRow(host, 1);
                Grid.SetRow(bottom, 2);
                var window = new Window { Content = grid, Width = 640, Height = 480 };
                try
                {
                    window.Show();
                    window.SetRenderScaling(scale);
                    Pump(window);
                    var hostBounds = BoundsIn(host, window);
                    var topBounds = top.Bounds;
                    var bottomBounds = bottom.Bounds;
                    using var before = CapturePixels(window, $"ui-surface-slide-{scale * 100:0}-before.png");
                    AssertMarker(before, OldCover, MarkerRect(hostBounds, 400, false), hostBounds, scale, "Initial cover");
                    AssertMarker(before, OldGlyph, MarkerRect(hostBounds, 400, true), hostBounds, scale, "Initial glyph");
                    foreach (var forward in new[] { true, false })
                    {
                        var sign = forward ? 1 : -1;
                        host.SlideDirection = sign;
                        host.Content = forward ? newPage : oldPage;
                        Pump(window);
                        for (var index = 0; index <= 4; index++)
                        {
                            if (index > 0)
                            {
                                clock.Advance(TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(5));
                                Pump(window);
                            }

                            var progress = host.TransitionProgress;
                            var outgoing = sign * -hostBounds.Width * progress;
                            var incoming = sign * hostBounds.Width * (1 - progress);
                            TestAssert.True(BoundsIn(host, window) == hostBounds && top.Bounds == topBounds && bottom.Bounds == bottomBounds,
                                "Snapshot assignment and every slide frame must leave the host and surrounding layout at their original size and vertical position.");
                            AssertOpaqueHorizontalSurfaces(host, incoming, outgoing);
                            using var frame = CapturePixels(window, $"ui-surface-slide-{scale * 100:0}-{(forward ? "forward" : "back")}-{index}.png");
                            var oldOffset = forward ? outgoing : incoming;
                            var newOffset = forward ? incoming : outgoing;
                            AssertMarker(frame, OldCover, MarkerRect(hostBounds, 400, false).Translate(new Vector(oldOffset, 0)), hostBounds, scale,
                                $"Old cover at {scale * 100:0}% / {sign} / {index}");
                            AssertMarker(frame, OldGlyph, MarkerRect(hostBounds, 400, true).Translate(new Vector(oldOffset, 0)), hostBounds, scale,
                                $"Old glyph at {scale * 100:0}% / {sign} / {index}");
                            AssertMarker(frame, NewCover, MarkerRect(hostBounds, 40, false).Translate(new Vector(newOffset, 0)), hostBounds, scale,
                                $"New cover at {scale * 100:0}% / {sign} / {index}");
                            AssertMarker(frame, NewGlyph, MarkerRect(hostBounds, 40, true).Translate(new Vector(newOffset, 0)), hostBounds, scale,
                                $"New glyph at {scale * 100:0}% / {sign} / {index}");
                            if (forward && index == 0)
                            {
                                TestAssert.Equal(FindColor(before, OldCover), FindColor(frame, OldCover),
                                    "The first outgoing snapshot must paint the exact previous cover bounds, including under high display scaling.");
                                TestAssert.Equal(FindColor(before, OldGlyph), FindColor(frame, OldGlyph),
                                    "The first outgoing snapshot must preserve the previous glyph's pixel size and position without a zoom frame.");
                            }
                        }

                        TestAssert.True(host.TransitionProgress == 1 && !host.HasSnapshot,
                            "A completed horizontal slide must retain only its final live page.");
                    }

                    host.SlideDirection = 1;
                    host.Content = newPage;
                    Pump(window);
                    clock.Advance(TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(5));
                    Pump(window);
                    var capturedViewport = host.SnapshotViewport;
                    window.Width = 800;
                    window.Height = 560;
                    Pump(window);
                    var resized = BoundsIn(host, window);
                    var resizedProgress = host.TransitionProgress;
                    TestAssert.Equal(hostBounds.Size, capturedViewport,
                        "The outgoing bitmap must retain its original logical size independently of the resized live viewport.");
                    TestAssert.Equal(capturedViewport, host.SnapshotViewport,
                        "Resizing must not stretch or reinterpret the original snapshot dimensions.");
                    var resizedOutgoing = -Math.Max(resized.Width, capturedViewport.Width) * resizedProgress;
                    var resizedIncoming = resized.Width * (1 - resizedProgress);
                    AssertOpaqueHorizontalSurfaces(host, resizedIncoming, resizedOutgoing);
                    using var resizeFrame = CapturePixels(window, $"ui-surface-slide-{scale * 100:0}-resize.png");
                    AssertMarker(resizeFrame, OldCover, MarkerRect(hostBounds, 400, false).Translate(new Vector(resizedOutgoing, 0)), resized, scale,
                        "Resized outgoing cover");
                    AssertMarker(resizeFrame, OldGlyph, MarkerRect(hostBounds, 400, true).Translate(new Vector(resizedOutgoing, 0)), resized, scale,
                        "Resized outgoing glyph");
                    AssertMarker(resizeFrame, NewCover, MarkerRect(resized, 40, false).Translate(new Vector(resizedIncoming, 0)), resized, scale,
                        "Resized incoming cover");
                    clock.Advance(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(5));
                    Pump(window);
                    TestAssert.True(!host.HasSnapshot && host.SnapshotViewport == default,
                        "The resized transition must release its original bitmap after reaching the newest live viewport.");
                }
                finally
                {
                    window.Close();
                }
            }
        }

        /// <summary>Gets the old cover's exclusive diagnostic color.</summary>
        private static SKColor OldCover => new(230, 120, 30);
        /// <summary>Gets the old glyph's exclusive diagnostic color.</summary>
        private static SKColor OldGlyph => new(80, 240, 80);
        /// <summary>Gets the new cover's exclusive diagnostic color.</summary>
        private static SKColor NewCover => new(30, 190, 180);
        /// <summary>Gets the new glyph's exclusive diagnostic color.</summary>
        private static SKColor NewGlyph => new(245, 60, 210);

        /// <summary>Creates a complete page with a fixed two-to-one artwork card and a small geometric glyph.</summary>
        /// <param name="left">The card's untransformed horizontal position.</param>
        /// <param name="background">The page's opaque diagnostic paint.</param>
        /// <param name="cover">The cover's unique opaque paint.</param>
        /// <param name="glyph">The metadata glyph's unique opaque paint.</param>
        /// <returns>The page containing exactly one interactive action and fixed artwork geometry.</returns>
        private static Border MarkedPage(double left, Color background, Color cover, Color glyph)
        {
            var artwork = new Border { Width = 120, Height = 120, Background = new SolidColorBrush(cover) };
            var mark = new Border { Width = 20, Height = 12, Background = new SolidColorBrush(glyph) };
            Canvas.SetLeft(mark, 148);
            Canvas.SetTop(mark, 20);
            var card = new Canvas
            {
                Width = 240,
                Height = 120,
                Background = Brushes.Black,
                Children = { artwork, mark }
            };
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, 80);
            var action = new Button { Content = "Action", Width = 80, Height = 32 };
            Canvas.SetLeft(action, 260);
            Canvas.SetTop(action, 260);
            return new Border
            {
                Background = new SolidColorBrush(background),
                Child = new Canvas { Children = { card, action } }
            };
        }

        /// <summary>Computes the fixture's fixed artwork or glyph bounds independently of its render transform.</summary>
        /// <param name="host">The unchanging host rectangle in window coordinates.</param>
        /// <param name="left">The card's fixed horizontal position.</param>
        /// <param name="glyph">Whether to describe the small glyph instead of the cover.</param>
        /// <returns>The marker's untransformed window rectangle.</returns>
        private static Rect MarkerRect(Rect host, double left, bool glyph)
        {
            return glyph ? new Rect(host.X + left + 148, host.Y + 100, 20, 12)
                : new Rect(host.X + left, host.Y + 80, 120, 120);
        }

        /// <summary>Requires only one translation on each complete opaque surface.</summary>
        /// <param name="host">The production slide presenter.</param>
        /// <param name="incoming">The independently calculated incoming horizontal offset.</param>
        /// <param name="outgoing">The independently calculated outgoing horizontal offset.</param>
        private static void AssertOpaqueHorizontalSurfaces(MusicSurfaceHost host, double incoming, double outgoing)
        {
            foreach (var (name, expected) in new[] { ("PART_LiveSurface", incoming), ("PART_OutgoingSurface", outgoing) })
            {
                var control = host.GetVisualDescendants().OfType<Control>().Single(item => item.Name == name);
                var matrix = control.RenderTransform?.Value ?? Matrix.Identity;
                TestAssert.True(control.Opacity == 1 && Math.Abs(matrix.M11 - 1) < .0001 && Math.Abs(matrix.M22 - 1) < .0001
                    && Math.Abs(matrix.M12) < .0001 && Math.Abs(matrix.M21) < .0001 && Math.Abs(matrix.M32) < .0001
                    && Math.Abs(matrix.M31 - expected) < .01,
                    $"{name} must apply exactly one viewport-width horizontal slide with no opacity, scale or vertical animation: {matrix}, opacity {control.Opacity}.");
            }

            var live = host.GetVisualDescendants().OfType<ContentPresenter>().Single(item => item.Name == "PART_LiveSurface");
            TestAssert.True(live.Child is { Opacity: 1 } child && (child.RenderTransform is null || child.RenderTransform.Value == Matrix.Identity),
                "The incoming page must retain its original geometry and opacity without a second nested entrance animation.");
        }

        /// <summary>Compares actual painted marker edges with a translated, clipped, otherwise unchanged rectangle.</summary>
        /// <param name="pixels">The actual physical display frame.</param>
        /// <param name="color">The unique unmodified marker color.</param>
        /// <param name="expected">The expected translated marker in logical pixels.</param>
        /// <param name="viewport">The fixed clipping viewport.</param>
        /// <param name="scale">The actual display scale.</param>
        /// <param name="purpose">The marker and frame described by a failure.</param>
        private static void AssertMarker(SKBitmap pixels, SKColor color, Rect expected, Rect viewport, double scale, string purpose)
        {
            var clipped = expected.Intersect(viewport);
            var actual = FindColor(pixels, color);
            if (clipped.Width < 1 || clipped.Height < 1)
            {
                TestAssert.True(actual is null, purpose + " must remain outside the clipped viewport.");
                return;
            }

            TestAssert.True(actual is { } found && Math.Abs(found.X - clipped.X * scale) <= 2
                && Math.Abs(found.Y - clipped.Y * scale) <= 2 && Math.Abs(found.Width - clipped.Width * scale) <= 2
                && Math.Abs(found.Height - clipped.Height * scale) <= 2,
                $"{purpose} must preserve cover/glyph pixel dimensions and Y while moving only in X: actual {actual}, expected {clipped}, scale {scale}.");
        }

        /// <summary>Finds opaque diagnostic pixels while allowing two channel values of raster rounding.</summary>
        /// <param name="pixels">The real renderer's bitmap.</param>
        /// <param name="color">The marker's unmodified color.</param>
        /// <returns>The physical marker rectangle, or null when no opaque marker is painted.</returns>
        private static Rect? FindColor(SKBitmap pixels, SKColor color)
        {
            var left = pixels.Width;
            var top = pixels.Height;
            var right = -1;
            var bottom = -1;
            for (var y = 0; y < pixels.Height; y++)
            {
                for (var x = 0; x < pixels.Width; x++)
                {
                    var pixel = pixels.GetPixel(x, y);
                    if (pixel.Alpha >= 253 && Math.Abs(pixel.Red - color.Red) <= 2
                        && Math.Abs(pixel.Green - color.Green) <= 2 && Math.Abs(pixel.Blue - color.Blue) <= 2)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            return right < 0 ? null : new Rect(left, top, right - left + 1, bottom - top + 1);
        }

        /// <summary>Captures the real display-scaled compositor output rather than rerendering a diagnostic property.</summary>
        /// <param name="window">The actual rendering viewport.</param>
        /// <param name="name">The ignored frame artifact name.</param>
        /// <returns>The independently decoded physical bitmap.</returns>
        private static SKBitmap CapturePixels(Window window, string name)
        {
            Pump(window);
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The slide did not produce a frame.");
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            var output = Path.Combine(Environment.CurrentDirectory, ".work");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, name), stream.ToArray());
            stream.Position = 0;
            return SKBitmap.Decode(stream) ?? throw new InvalidOperationException("The slide frame could not be decoded.");
        }

        /// <summary>Reads a control's actual rectangle in its rendering window.</summary>
        /// <param name="control">The layout surface to inspect.</param>
        /// <param name="window">The containing viewport.</param>
        /// <returns>The translated control rectangle.</returns>
        private static Rect BoundsIn(Control control, Window window)
        {
            return new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
        }
    }
}
