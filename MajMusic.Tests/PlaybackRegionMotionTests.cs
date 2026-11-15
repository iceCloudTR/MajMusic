#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;
using SkiaSharp;
using TKCode.Arc.Motion;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace MajMusic.Tests
{
    /// <summary>Checks fixed artwork, metadata and transport transitions with real frames and disposed external artwork.</summary>
    internal static class PlaybackRegionMotionTests
    {
        /// <summary>Runs intermediate-frame, rapid-cut, appearance, motion-policy and lifecycle regressions.</summary>
        /// <returns>A task representing the complete local animation matrix.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var clock = new ManualFrameClock();
                using var installed = clock.Install();
                foreach (var light in new[] { false, true })
                {
                    MajMusicTheme.ApplyAppearance(light ? AppearancePreference.Light : AppearancePreference.Dark);
                    foreach (var scale in new[] { 1d, 1.25d, 2d })
                    {
                        await VerifyRegionsAsync(clock, light, scale);
                    }
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Checks one real-rendering appearance and display-scale combination.</summary>
        /// <param name="clock">The installed actual Arc frame source.</param>
        /// <param name="light">Whether the light appearance is active.</param>
        /// <param name="scale">The actual host rendering scale.</param>
        /// <returns>A task representing completed frame and ownership assertions.</returns>
        private static async Task VerifyRegionsAsync(ManualFrameClock clock, bool light, double scale)
        {
            var background = light ? new SKColor(238, 238, 241) : new SKColor(39, 39, 44);
            var foreground = light ? Brushes.Black : Brushes.White;
            var cover = new CoverTransition { Width = 120, Height = 120 };
            var coverBorder = new Border
            {
                Width = 120,
                Height = 120,
                CornerRadius = new CornerRadius(12),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromRgb(background.Red, background.Green, background.Blue)),
                Child = cover
            };
            var title = new TextBlock { Text = "First title", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis };
            var artist = new TextBlock { Text = "First artist", FontSize = 11, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis };
            var metadata = new MetadataTransition
            {
                Width = 250,
                Height = 36,
                ChangeKey = "First title",
                SecondaryKey = "First artist",
                Content = new StackPanel { Spacing = 3, Children = { title, artist } }
            };
            var glyph = new PlayPauseTransition { Width = 16, Height = 16 };
            var play = new ShapePath { Data = Geometry.Parse("M 5,2 L 13,8 L 5,14 Z"), Fill = foreground, Width = 16, Height = 16, Stretch = Stretch.None };
            var pause = new ShapePath { Data = Geometry.Parse("M 4,2 L 7,2 L 7,14 L 4,14 Z M 9,2 L 12,2 L 12,14 L 9,14 Z"), Fill = foreground, Width = 16, Height = 16, Stretch = Stretch.None };
            play.Bind(Visual.IsVisibleProperty, new Binding("!IsPlaying") { Source = glyph });
            pause.Bind(Visual.IsVisibleProperty, new Binding(nameof(PlayPauseTransition.IsPlaying)) { Source = glyph });
            glyph.Content = new Canvas { Width = 16, Height = 16, Children = { play, pause } };
            var root = new Canvas { Background = light ? Brushes.White : Brushes.Black, Children = { coverBorder, metadata, glyph } };
            Canvas.SetLeft(coverBorder, 12);
            Canvas.SetTop(coverBorder, 12);
            Canvas.SetLeft(metadata, 150);
            Canvas.SetTop(metadata, 20);
            Canvas.SetLeft(glyph, 158);
            Canvas.SetTop(glyph, 90);
            var window = new Window { Width = 430, Height = 160, Content = root };
            var output = Path.Combine(Environment.CurrentDirectory, ".work");
            Directory.CreateDirectory(output);
            var suffix = $"{(light ? "light" : "dark")}-{scale * 100:0}";
            using var red = Image(SKColors.IndianRed);
            using var green = Image(SKColors.MediumSeaGreen);
            using var blue = Image(SKColors.CornflowerBlue);
            using var orange = Image(SKColors.DarkOrange);
            cover.Source = red;
            try
            {
                window.Show();
                window.SetRenderScaling(scale);
                Pump(window);
                var coverBounds = cover.Bounds;
                var metadataBounds = metadata.Bounds;
                var glyphBounds = glyph.Bounds;
                TestAssert.Equal(SKColors.IndianRed, CoverPixel(window), "The initial artwork must appear without a startup fade.");
                cover.Source = green;
                red.Dispose();
                Pump(window);
                TestAssert.True(cover.HasSnapshot && cover.TransitionProgress == 0, "Changing artwork must capture independently owned outgoing pixels.");
                TestAssert.Equal(SKColors.IndianRed, CoverPixel(window), "Disposing the old view-model bitmap cannot change the outgoing frame.");
                Advance(clock, window, 70);
                var mixed = CoverPixel(window);
                TestAssert.True(mixed != SKColors.IndianRed && mixed != SKColors.MediumSeaGreen,
                    "The actual middle artwork frame must paint both states rather than jumping between static images.");
                Save(window, Path.Combine(output, "ui-playback-cover-middle-" + suffix + ".png"));
                var interruptedCover = cover.TransitionUpdate;
                cover.Source = blue;
                green.Dispose();
                Pump(window);
                TestAssert.True(interruptedCover.IsCompleted && !await interruptedCover && Near(mixed, CoverPixel(window)),
                    "A rapid cover change must start from the last displayed composite after all external old bitmaps are released.");
                Advance(clock, window, 250);
                TestAssert.True(!cover.HasSnapshot && cover.TransitionProgress == 1,
                    "Settled artwork must release its sole outgoing frame.");
                TestAssert.Equal(SKColors.CornflowerBlue, CoverPixel(window), "The newest cover must own the final rendered frame.");
                cover.Source = null;
                blue.Dispose();
                Advance(clock, window, 70);
                TestAssert.True(CoverPixel(window) != background, "Clearing artwork must fade the owned old pixels rather than dereference a released source.");
                Advance(clock, window, 250);
                TestAssert.Equal(background, CoverPixel(window), "A missing cover must settle to the existing placeholder background.");

                var oldText = RegionPixels(window, metadata);
                Save(window, Path.Combine(output, "ui-playback-metadata-before-" + suffix + ".png"));
                metadata.ChangeKey = "Second title";
                title.Text = "Second title";
                metadata.SecondaryKey = "Second artist";
                artist.Text = "Second artist";
                Pump(window);
                TestAssert.True(metadata.HasSnapshot && metadata.GetVisualDescendants().OfType<TextBlock>().Count() == 2,
                    "Text changes in one binding turn must retain a bitmap rather than a second live metadata tree.");
                var firstText = RegionPixels(window, metadata);
                Save(window, Path.Combine(output, "ui-playback-metadata-first-" + suffix + ".png"));
                TestAssert.True(oldText.SequenceEqual(firstText),
                    $"Updating both metadata bindings must begin from the last displayed title and artist ({suffix}, changed={oldText.Zip(firstText).Count(pair => pair.First != pair.Second)}).");
                Advance(clock, window, 65);
                var middleText = RegionPixels(window, metadata);
                TestAssert.True(!oldText.SequenceEqual(middleText) && Live(metadata).RenderTransform!.Value.M31 is > 0 and <= 6,
                    "The metadata middle frame must paint a restrained horizontal transition inside the original text region.");
                Save(window, Path.Combine(output, "ui-playback-metadata-middle-" + suffix + ".png"));
                Advance(clock, window, 250);
                TestAssert.True(!middleText.SequenceEqual(RegionPixels(window, metadata)) && !metadata.HasSnapshot,
                    "The metadata must settle to its newest text and release the old frame.");

                var initialGlyph = RegionPixels(window, glyph);
                glyph.IsPlaying = true;
                Pump(window);
                TestAssert.True(initialGlyph.SequenceEqual(RegionPixels(window, glyph)),
                    "A playback-state change must begin from the previously painted centered icon.");
                Advance(clock, window, 45);
                var middleGlyph = RegionPixels(window, glyph);
                var matrix = Live(glyph).RenderTransform!.Value;
                TestAssert.True(!initialGlyph.SequenceEqual(middleGlyph) && matrix.M11 is > .8 and < 1 && Math.Abs(matrix.M12) > .001,
                    "The actual play/pause middle frame must show a centered local fade, rotation and restrained scale.");
                Save(window, Path.Combine(output, "ui-playback-glyph-middle-" + suffix + ".png"));
                var interruptedGlyph = glyph.TransitionUpdate;
                glyph.IsPlaying = false;
                Pump(window);
                TestAssert.True(interruptedGlyph.IsCompleted && !await interruptedGlyph, "A fast pause/play reversal must release the superseded glyph driver.");
                Advance(clock, window, 250);
                TestAssert.True(play.IsVisible && !pause.IsVisible && !glyph.HasSnapshot,
                    "Paused and unplayed states must settle to the existing play path without changing its canvas.");
                TestAssert.True(cover.Bounds == coverBounds && metadata.Bounds == metadataBounds && glyph.Bounds == glyphBounds,
                    "Artwork, metadata and icon transitions cannot change their original layout rectangles.");

                ArcMotion.SetLevel(window, ArcMotionLevel.Reduced);
                cover.Source = orange;
                metadata.ChangeKey = "Reduced title";
                title.Text = "Reduced title";
                glyph.IsPlaying = true;
                Advance(clock, window, 25);
                foreach (var region in new FixedTransitionHost[] { cover, metadata, glyph })
                {
                    var live = Live(region);
                    TestAssert.True(region.TransitionProgress is > 0 and < 1 && live.RenderTransform!.Value.IsIdentity
                        && live.Opacity is > 0 and < 1, "Reduced motion must animate only opacity, without local scale, rotation or translation.");
                }

                ArcMotion.SetLevel(window, ArcMotionLevel.Off);
                Pump(window);
                TestAssert.True(new FixedTransitionHost[] { cover, metadata, glyph }.All(region => region.TransitionProgress == 1
                    && !region.HasSnapshot && region.TransitionUpdate.IsCompleted), "Motion Off must settle all regions immediately and release outgoing resources.");
                TestAssert.Equal(SKColors.DarkOrange, CoverPixel(window), "Disabled motion must still show the newest valid cover.");
                ArcMotion.SetLevel(window, ArcMotionLevel.Full);
                Pump(window);
                metadata.ChangeKey = "Detach title";
                title.Text = "Detach title";
                Advance(clock, window, 35);
                var detached = metadata.TransitionUpdate;
                TestAssert.True(metadata.HasSnapshot && !detached.IsCompleted,
                    $"The detach fixture must interrupt actual motion (p={metadata.TransitionProgress}, level={ArcMotion.GetEffectiveLevel(metadata)}).");
                root.Children.Remove(metadata);
                TestAssert.True(detached.IsCompleted && !metadata.HasSnapshot,
                    "Detachment must release the in-progress text driver and dispose its outgoing frame.");
                metadata.DataContext = new object();
                metadata.ChangeKey = "Cached title";
                title.Text = "Cached title";
                root.Children.Add(metadata);
                Pump(window);
                TestAssert.True(metadata.TransitionProgress == 1 && !metadata.HasSnapshot,
                    "Recycling and reattaching metadata must show the new source without replaying another track's entrance.");
                root.Children.Remove(coverBorder);
                root.Children.Add(coverBorder);
                Pump(window);
                TestAssert.Equal(SKColors.DarkOrange, CoverPixel(window), "Reattaching artwork must recreate its own pixels from the still-current view-model image.");
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>Creates a generated, independently disposable bitmap without external files or audio.</summary>
        /// <param name="color">The exact opaque fixture color.</param>
        /// <returns>The externally owned source image.</returns>
        private static Bitmap Image(SKColor color)
        {
            using var pixels = new SKBitmap(96, 96);
            pixels.Erase(color);
            using var encoded = pixels.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());
            return new Bitmap(stream);
        }

        /// <summary>Gets the sole live transition presenter independently of its outgoing bitmap.</summary>
        /// <param name="region">The region under inspection.</param>
        /// <returns>The actual content presenter.</returns>
        private static ContentPresenter Live(FixedTransitionHost region)
        {
            return region.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_TransitionLive");
        }

        /// <summary>Samples the center artwork pixel using actual host scaling.</summary>
        /// <param name="window">The real rendering viewport.</param>
        /// <returns>The actual painted cover color.</returns>
        private static SKColor CoverPixel(Window window)
        {
            using var image = Pixels(window);
            return image.GetPixel((int)(72 * window.RenderScaling), (int)(72 * window.RenderScaling));
        }

        /// <summary>Compares continuity allowing only raster rounding when a composite is captured once more.</summary>
        /// <param name="first">The previously visible composite.</param>
        /// <param name="second">The interrupted transition's first frame.</param>
        /// <returns>Whether both frames paint the same visible state.</returns>
        private static bool Near(SKColor first, SKColor second)
        {
            return Math.Abs(first.Red - second.Red) <= 2 && Math.Abs(first.Green - second.Green) <= 2 && Math.Abs(first.Blue - second.Blue) <= 2;
        }

        /// <summary>Reads real pixels from one unchanged logical region.</summary>
        /// <param name="window">The rendering window.</param>
        /// <param name="control">The region to compare across frames.</param>
        /// <returns>The region's packed RGBA pixels.</returns>
        private static uint[] RegionPixels(Window window, Control control)
        {
            using var image = Pixels(window);
            var origin = control.TranslatePoint(default, window)!.Value;
            var scale = window.RenderScaling;
            var width = (int)Math.Floor(control.Bounds.Width * scale);
            var height = (int)Math.Floor(control.Bounds.Height * scale);
            var result = new uint[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    result[y * width + x] = (uint)image.GetPixel((int)(origin.X * scale) + x, (int)(origin.Y * scale) + y);
                }
            }

            return result;
        }

        /// <summary>Decodes the actual composited framebuffer rather than rendering a separate visual-tree view.</summary>
        /// <param name="window">The actual viewport.</param>
        /// <returns>The caller-owned rendered pixels.</returns>
        private static SKBitmap Pixels(Window window)
        {
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The playback region frame was not painted.");
            using var encoded = new MemoryStream();
            frame.Save(encoded, PngBitmapEncoderOptions.Default);
            return SKBitmap.Decode(encoded.ToArray());
        }

        /// <summary>Saves a real intermediate frame for visual inspection.</summary>
        /// <param name="window">The actual rendering viewport.</param>
        /// <param name="path">The ignored image destination.</param>
        private static void Save(Window window, string path)
        {
            using var frame = window.CaptureRenderedFrame()!;
            frame.Save(path, PngBitmapEncoderOptions.Default);
        }

        /// <summary>Advances the real Arc scheduler while preserving normal layout and composition.</summary>
        /// <param name="clock">The installed frame source.</param>
        /// <param name="window">The rendering viewport.</param>
        /// <param name="milliseconds">The elapsed interval.</param>
        private static void Advance(ManualFrameClock clock, Window window, int milliseconds)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMilliseconds(5));
            Pump(window);
        }

        /// <summary>Completes layout and actual frame composition without changing Arc time.</summary>
        /// <param name="window">The rendering viewport.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
