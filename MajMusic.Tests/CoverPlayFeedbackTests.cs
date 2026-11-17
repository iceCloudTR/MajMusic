#nullable enable

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using SkiaSharp;
using TKCode.Arc.Motion;

namespace MajMusic.Tests
{
    /// <summary>Checks the cover play affordance's actual display-frame exit and stationary return without audio or network.</summary>
    internal static class CoverPlayFeedbackTests
    {
        /// <summary>Runs real Skia frames and pointer gestures with the production display-frame controller.</summary>
        /// <returns>A task representing rendered continuity and lifecycle checks.</returns>
        public static Task RunAsync() => HeadlessProcessIsolation.RunAsync("cover-play-feedback", RunHeadlessAsync);

        /// <summary>Exercises finite native display callbacks in an isolated rendering application.</summary>
        /// <returns>A task representing the actual rendered gestures.</returns>
        private static async Task RunHeadlessAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await session.Dispatch<bool>(async () =>
            {
                var gesture = new CoverPlayGesture
                {
                    Width = 400,
                    Height = 200,
                    Accent = Brushes.Black,
                    SurfaceBrush = Brushes.Black,
                    GlyphBrush = Brushes.White
                };
                var root = new Border { Background = Brushes.Black, Child = gesture };
                var window = new Window { Width = 400, Height = 200, Background = Brushes.Black, Content = root };
                IPointer? input = null;
                window.AddHandler(InputElement.PointerPressedEvent, (_, args) => input = args.Pointer, RoutingStrategies.Tunnel);
                var plays = 0;
                gesture.PlayRequested += (_, _) => plays++;
                try
                {
                    window.Show();
                    Pump(window);
                    var initial = ReadGlyph(window, gesture);
                    TestAssert.True(initial.Peak > 240 && initial.Bounds.Width > 8 && initial.Bounds.Height > 8,
                        "The fixture must paint the actual resting play glyph before activating it.");
                    var point = gesture.TranslatePoint(initial.Bounds.Center, window)!.Value;
                    Tap(window, point);
                    await UntilAsync(window, () => plays == 1, "The real play gesture must finish its visible exit.");
                    TestAssert.True(gesture.Progress == 0 && ReadGlyph(window, gesture).Peak == 0 && !gesture.HitTest(initial.Bounds.Center),
                        "Successful playback must reset its geometry while completely invisible, without flashing the corner icon.");
                    await NextFrameAsync(window);
                    TestAssert.True(ReadGlyph(window, gesture).Peak == 0,
                        "The first frame after a successful exit must still contain no suddenly restored corner button.");
                    Save(window, "ui-quick-play-return-hidden.png");

                    (int Peak, Rect Bounds) middle = default;
                    await UntilAsync(window, () =>
                    {
                        middle = ReadGlyph(window, gesture);
                        return middle.Peak is > 45 and < 220;
                    }, "The delayed return must expose an actual partially transparent rendered frame.");
                    TestAssert.True(gesture.Progress == 0 && RectDifference(initial.Bounds, middle.Bounds) <= 1 && gesture.HitTest(initial.Bounds.Center),
                        "The returning play glyph must fade at the original painted geometry instead of traveling back into the corner.");
                    Save(window, "ui-quick-play-return-middle.png");
                    Tap(window, point);
                    await UntilAsync(window, () => plays == 2,
                        "A visible returning button must accept a new explicit play while canceling its older restore callback.");
                    TestAssert.True(ReadGlyph(window, gesture).Peak == 0 && !gesture.HitTest(initial.Bounds.Center),
                        "A new play during restoration must complete once and begin its own hidden return without an obsolete corner flash.");
                    await UntilAsync(window, () => ReadGlyph(window, gesture).Peak > 240 && gesture.HitTest(initial.Bounds.Center),
                        "The finite return must settle to the original interactive affordance.");
                    var complete = ReadGlyph(window, gesture);
                    TestAssert.True(RectDifference(initial.Bounds, complete.Bounds) <= 1 && plays == 2,
                        "Restoration must keep the same icon position and must never request another playback.");
                    Save(window, "ui-quick-play-return-complete.png");

                    Tap(window, point);
                    await UntilAsync(window, () => plays == 3, "The recovered affordance must accept another explicit play.");
                    gesture.DataContext = new object();
                    Pump(window);
                    await FramesAsync(window, 320);
                    TestAssert.True(ReadGlyph(window, gesture).Peak > 240 && gesture.Progress == 0 && plays == 3,
                        "Recycling a card must cancel its old delayed return without hiding or moving the new card's resting icon.");

                    Tap(window, point);
                    await UntilAsync(window, () => plays == 4, "The reduced-motion fixture must first complete a real exit.");
                    ArcMotion.SetLevel(gesture, ArcMotionLevel.Reduced);
                    Pump(window);
                    await FramesAsync(window, 320);
                    TestAssert.True(ReadGlyph(window, gesture).Peak > 240 && gesture.Progress == 0 && plays == 4,
                        "Reducing motion must cancel the obsolete delayed restoration and settle the authorized play without replaying it.");

                    ArcMotion.SetLevel(gesture, ArcMotionLevel.Full);
                    Tap(window, point);
                    await UntilAsync(window, () => plays == 5, "The detach fixture must first complete a real exit.");
                    window.Content = null;
                    Pump(window);
                    window.Content = root;
                    Pump(window);
                    await FramesAsync(window, 320);
                    TestAssert.True(ReadGlyph(window, gesture).Peak > 240 && gesture.Progress == 0 && plays == 5,
                        "Detaching and reattaching must discard the previous restore callback and retain one stable resting icon.");

                    var end = new Point(point.X + 225, point.Y);
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                    await UntilAsync(window, () => gesture.Progress > .15, "A capture interruption must start with the icon in flight.");
                    var atCaptureLoss = gesture.Progress;
                    TestAssert.True(ReferenceEquals(input?.Captured, gesture), "The actual pointer must be captured by the moving icon.");
                    input!.Capture(null);
                    TestAssert.Equal(atCaptureLoss, gesture.Progress, "Losing capture must retain the current icon position before rolling back.");
                    window.MouseUp(end, MouseButton.Left);
                    await UntilAsync(window, () => gesture.Progress == 0 && gesture.TransitionUpdate.IsCompleted,
                        "Losing capture must return the icon along its existing path.");
                    TestAssert.Equal(5, plays, "Interrupted pointer capture must not play the song.");

                    window.Content = null;
                    var strip = new ResidentBrowseStrip { Children = { root, new Border(), new Border() } };
                    strip.InitializePages();
                    window.Content = strip;
                    Pump(window);
                    point = gesture.TranslatePoint(ReadGlyph(window, gesture).Bounds.Center, window)!.Value;
                    end = new Point(point.X + 225, point.Y);
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                    await UntilAsync(window, () => gesture.Progress > .15, "A held quick-play drag must paint its intermediate geometry.");
                    var before = gesture.Progress;
                    strip.Navigate(HomeBrowseMode.Playlists, animate: true);
                    TestAssert.Equal(before, gesture.Progress, "Navigation must not snap a held icon to another position.");
                    window.MouseUp(end, MouseButton.Left);
                    strip.Navigate(HomeBrowseMode.Recommendations, animate: true);
                    await UntilAsync(window, () => gesture.Progress == 0 && gesture.TransitionUpdate.IsCompleted,
                        "Taking input away must smoothly roll back the uncommitted drag.");
                    TestAssert.Equal(5, plays, "A release over a different resident page must never start playback.");

                    Tap(window, point);
                    strip.Navigate(HomeBrowseMode.Library, animate: true);
                    await UntilAsync(window, () => plays == 6,
                        "A quick-play click committed before navigation must still play once after its exit.");
                }
                finally
                {
                    window.Close();
                }
                return true;
            }, timeout.Token);
        }

        /// <summary>Activates the actual icon through native headless pointer routing.</summary>
        /// <param name="window">The rendered viewport.</param>
        /// <param name="point">An already painted glyph point in window coordinates.</param>
        private static void Tap(Window window, Point point)
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Pump(window);
        }

        /// <summary>Allows one real display timestamp to advance before committing a rendered frame.</summary>
        /// <param name="window">The rendered viewport.</param>
        /// <returns>A task representing one finite compositor opportunity.</returns>
        private static async Task NextFrameAsync(Window window)
        {
            await Task.Delay(12);
            Pump(window);
        }

        /// <summary>Waits for a rendered state without replacing the production display clock or asserting machine performance.</summary>
        /// <param name="window">The rendered viewport.</param>
        /// <param name="condition">The actual geometry or pixel condition to observe.</param>
        /// <param name="message">The failure context if the finite gesture never completes.</param>
        /// <returns>A task representing the bounded observation.</returns>
        private static async Task UntilAsync(Window window, Func<bool> condition, string message)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(4))
            {
                await NextFrameAsync(window);
            }
            TestAssert.True(condition(), message);
        }

        /// <summary>Lets canceled native callbacks pass their original finite lifetime.</summary>
        /// <param name="window">The rendered viewport.</param>
        /// <param name="milliseconds">The display-clock interval to observe.</param>
        /// <returns>A task representing those real rendered frames.</returns>
        private static async Task FramesAsync(Window window, int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < milliseconds)
            {
                await NextFrameAsync(window);
            }
        }

        /// <summary>Reads the bright play ink separately from the low-opacity glass edge to compare its actual stationary geometry.</summary>
        /// <param name="window">The real rendered viewport.</param>
        /// <param name="gesture">The transparent cover overlay.</param>
        /// <returns>The peak ink brightness and its pixel-normalized local bounds.</returns>
        private static (int Peak, Rect Bounds) ReadGlyph(Window window, CoverPlayGesture gesture)
        {
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The play fixture has no rendered frame.");
            using var encoded = new MemoryStream();
            frame.Save(encoded, PngBitmapEncoderOptions.Default);
            using var pixels = SKBitmap.Decode(encoded.ToArray());
            var origin = gesture.TranslatePoint(default, window)!.Value;
            var scale = window.RenderScaling;
            var left = (int)Math.Round(origin.X * scale);
            var top = (int)Math.Round(origin.Y * scale);
            var width = (int)Math.Round(gesture.Bounds.Width * scale);
            var height = (int)Math.Round(gesture.Bounds.Height * scale);
            var peak = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    peak = Math.Max(peak, pixels.GetPixel(left + x, top + y).Red);
                }
            }
            if (peak == 0)
            {
                return (0, default);
            }
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (pixels.GetPixel(left + x, top + y).Red >= peak * .75)
                    {
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }
            return (peak, new Rect(minX / scale, minY / scale, (maxX - minX + 1) / scale, (maxY - minY + 1) / scale));
        }

        /// <summary>Measures independent visible geometry differences while ignoring changing opacity.</summary>
        /// <param name="first">The originally painted glyph bounds.</param>
        /// <param name="second">The current rendered glyph bounds.</param>
        /// <returns>The largest edge or size difference in logical pixels.</returns>
        private static double RectDifference(Rect first, Rect second)
        {
            return Math.Max(Math.Max(Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y)),
                Math.Max(Math.Abs(first.Width - second.Width), Math.Abs(first.Height - second.Height)));
        }

        /// <summary>Commits bindings, layout and a real native headless frame.</summary>
        /// <param name="window">The current rendered viewport.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        /// <summary>Stores generated synthetic frames in the ignored preview directory.</summary>
        /// <param name="window">The current rendered viewport.</param>
        /// <param name="name">The generated PNG filename.</param>
        private static void Save(Window window, string name)
        {
            Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".work"));
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The play fixture has no rendered frame.");
            frame.Save(Path.Combine(Environment.CurrentDirectory, ".work", name), PngBitmapEncoderOptions.Default);
        }
    }
}
