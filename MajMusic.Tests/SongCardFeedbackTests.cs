#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using SkiaSharp;
using TKCode.Arc.Motion;
using ArcButton = TKCode.Arc.Button;

namespace MajMusic.Tests
{
    /// <summary>Checks real song-card hover, selection and one-shot transfer confirmation under Arc's motion policies.</summary>
    internal static class SongCardFeedbackTests
    {
        /// <summary>Exercises actual pointer input, rendered intermediate frames and recycled completion state.</summary>
        /// <returns>A task representing the completed visual feedback checks.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var clock = new ManualFrameClock();
                using var installed = clock.Install();
                var output = Path.Combine(Environment.CurrentDirectory, ".work");
                Directory.CreateDirectory(output);
                var coverPath = Path.Combine(output, "ui-card-feedback-cover.png");
                using (var cover = new SKBitmap(128, 128))
                {
                    cover.Erase(new SKColor(180, 123, 115));
                    using var encoded = cover.Encode(SKEncodedImageFormat.Png, 100);
                    await File.WriteAllBytesAsync(coverPath, encoded.ToArray(), timeout.Token);
                }

                var workspace = new CardDownloadPresentationTests.DownloadWorkspace();
                await using var cache = new CoverThumbnailCache(workspace, timeout.Token);
                var track = new MusicTrack
                {
                    Id = "feedback",
                    Title = "A small confirmation",
                    Artist = "Generated fixture",
                    SourceId = "feedback-source",
                    RemoteId = "feedback-remote",
                    Revision = "one",
                    Audio = new MediaResource(),
                    Duration = TimeSpan.FromSeconds(180),
                    Cover = new MediaResource { LocalPath = coverPath }
                };
                using var model = new SongCardViewModel(track, cache);
                var state = new SongDownloadState();
                model.RestoreDownloadState(state);
                var card = new SongCard { DataContext = model, Width = 400, Height = 200, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
                var root = new Grid { Margin = new Thickness(10), Children = { card } };
                var window = new Window { Width = 420, Height = 240, Content = root };
                window.Styles.Add(new StyleInclude(new Uri("avares://MajMusic.Presentation/"))
                {
                    Source = new Uri("avares://MajMusic.Presentation/Styles/MusicStyles.axaml")
                });
                try
                {
                    window.Show();
                    Pump(window);
                    await model.ResourceCompletion;
                    var feedback = card.FindControl<CardFeedbackBorder>("CardFeedback")!;
                    var cue = card.FindControl<DownloadCompletionCue>("CardCompletionCue")!;
                    var button = card.GetVisualDescendants().OfType<ArcButton>().Single();
                    var bounds = card.Bounds;
                    var paneBounds = card.FindControl<Grid>("CardInformationPane")!.Bounds;
                    var baseline = Capture(window, Path.Combine(output, "ui-card-feedback-idle.png"));
                    var point = button.TranslatePoint(new Point(100, 100), window)!.Value;
                    window.MouseMove(point);
                    Pump(window);
                    Advance(clock, window, 75);
                    TestAssert.True(feedback.Emphasis is > 0 and < .38,
                        "Hovering the real card must paint intermediate emphasis instead of snapping or scaling its artwork.");
                    var hovering = Capture(window, Path.Combine(output, "ui-card-feedback-hover-middle.png"));
                    TestAssert.True(hovering != baseline, "The actual hovered information background must differ from the idle frame.");
                    Advance(clock, window, 150);
                    var hover = feedback.Emphasis;
                    model.IsSelected = true;
                    Pump(window);
                    Advance(clock, window, 75);
                    TestAssert.True(feedback.Emphasis > hover && feedback.Emphasis < 1,
                        "Selecting a card must animate a stronger emphasis than hover while preserving its dimensions.");
                    Capture(window, Path.Combine(output, "ui-card-feedback-selected-middle.png"));
                    Advance(clock, window, 200);
                    AssertGeometry(card, button, bounds, paneBounds);
                    window.MouseMove(new Point(415, 235));
                    model.IsSelected = false;
                    Pump(window);
                    Advance(clock, window, 50);
                    var interrupted = feedback.MotionUpdate;
                    var before = feedback.Emphasis;
                    window.MouseMove(point);
                    Pump(window);
                    TestAssert.True(Math.Abs(feedback.Emphasis - before) < .001 && interrupted.IsCompleted && !await interrupted,
                        "A rapid hover reversal must continue from the visible state and supersede its previous transition.");
                    Advance(clock, window, 250);

                    Emit(model, state, track, AudioDownloadState.Downloading);
                    Pump(window);
                    Emit(model, state, track, AudioDownloadState.Completed);
                    Pump(window);
                    TestAssert.True(cue.Reveal == 0 && cue.GlyphScale < 1, "Only a new visible transfer completion may begin a small glyph response.");
                    Advance(clock, window, 65);
                    TestAssert.True(cue.Reveal is > 0 and < 1 && cue.GlyphScale is > .72 and < 1,
                        "The actual completed check must have a partially drawn, locally scaled middle frame under full motion.");
                    Capture(window, Path.Combine(output, "ui-card-feedback-completion-middle.png"));
                    AssertCuePainted(window, card, cue);
                    AssertGeometry(card, button, bounds, paneBounds);
                    Advance(clock, window, 800);
                    TestAssert.True(Math.Abs(cue.Reveal - 1) < .001 && Math.Abs(cue.GlyphScale - 1) < .001,
                        "The completion response must settle to a persistent check.");
                    model.NotifyDownloadState();
                    card.InvalidateVisual();
                    Advance(clock, window, 65);
                    TestAssert.True(cue.Reveal == 1 && cue.GlyphScale == 1,
                        "Repeated progress notifications and repaint cannot replay a completed download's response.");
                    model.IsSelected = true;
                    Advance(clock, window, 250);
                    AssertDownloadColor(window, card);

                    ArcMotion.SetLevel(window, ArcMotionLevel.Reduced);
                    Pump(window);
                    Emit(model, state, track, AudioDownloadState.Downloading);
                    Pump(window);
                    Emit(model, state, track, AudioDownloadState.Completed);
                    Pump(window);
                    Advance(clock, window, 25);
                    TestAssert.True(cue.Reveal is > 0 and < 1 && cue.GlyphScale == 1,
                        "Reduced motion must retain a short confirmation fade while omitting all glyph scaling.");
                    Capture(window, Path.Combine(output, "ui-card-feedback-reduced-middle.png"));
                    var reduced = cue.MotionUpdate;
                    ArcMotion.SetLevel(window, ArcMotionLevel.Off);
                    Pump(window);
                    TestAssert.True(cue.Reveal == 1 && cue.GlyphScale == 1 && reduced.IsCompleted && cue.MotionUpdate.IsCompleted,
                        "Disabling motion halfway through confirmation must immediately settle and release the obsolete animation.");
                    Emit(model, state, track, AudioDownloadState.Downloading);
                    Pump(window);
                    Emit(model, state, track, AudioDownloadState.Completed);
                    Pump(window);
                    model.IsSelected = false;
                    window.MouseMove(new Point(415, 235));
                    Pump(window);
                    TestAssert.True(cue.Reveal == 1 && cue.GlyphScale == 1 && feedback.Emphasis == 0,
                        "Motion Off must update completion and hover state immediately without retaining a transition.");

                    ArcMotion.SetLevel(window, ArcMotionLevel.Full);
                    Emit(model, state, track, AudioDownloadState.Downloading);
                    Pump(window);
                    using var cachedWhileLoading = new SongCardViewModel(track with { Id = "cached-after-loading" }, cache);
                    cachedWhileLoading.RestoreDownloadState(new SongDownloadState { IsAvailable = true });
                    model.IsSelected = true;
                    Advance(clock, window, 200);
                    card.DataContext = cachedWhileLoading;
                    Pump(window);
                    Advance(clock, window, 25);
                    TestAssert.True(cue.Reveal == 1 && cue.GlyphScale == 1 && cue.MotionUpdate.IsCompleted,
                        "Replacing a still-downloading song with an already cached completed song in the same attached control cannot borrow the old transfer's completion animation.");
                    TestAssert.True(feedback.Emphasis == 0 && feedback.MotionUpdate.IsCompleted,
                        "Recycling a selected song into an unselected one in the same attached control cannot leave the old song's highlight fading over the new item.");
                    card.DataContext = model;
                    Pump(window);
                    Emit(model, state, track, AudioDownloadState.Completed);
                    Pump(window);
                    Advance(clock, window, 35);
                    var detached = cue.MotionUpdate;
                    root.Children.Remove(card);
                    TestAssert.True(detached.IsCompleted && !await detached && cue.Reveal == 1 && cue.GlyphScale == 1,
                        "Detaching a card during confirmation must release its active Arc driver and retain only the stable completed state.");
                    using var cached = new SongCardViewModel(track with { Id = "cached" }, cache);
                    cached.RestoreDownloadState(new SongDownloadState { IsAvailable = true });
                    card.DataContext = cached;
                    ArcMotion.SetLevel(window, ArcMotionLevel.Full);
                    root.Children.Add(card);
                    Pump(window);
                    Advance(clock, window, 65);
                    TestAssert.True(cue.Reveal == 1 && cue.GlyphScale == 1 && cue.MotionUpdate.IsCompleted,
                        "A recycled card with an existing cached download must show its completed check immediately without replaying another song's response.");
                    AssertGeometry(card, button, bounds, paneBounds);
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Requires confirmation ink and complete artwork below the former animated label clip.</summary>
        /// <param name="window">The actual compositor window.</param>
        /// <param name="card">The production card containing the status row.</param>
        /// <param name="cue">The currently partially revealed confirmation.</param>
        private static void AssertCuePainted(Window window, SongCard card, DownloadCompletionCue cue)
        {
            var origin = cue.TranslatePoint(default, window)!.Value;
            var pane = card.FindControl<Grid>("CardInformationPane")!;
            var paneOrigin = pane.TranslatePoint(default, window)!.Value;
            TestAssert.True(origin.X >= paneOrigin.X && origin.Y >= paneOrigin.Y
                && origin.Y + cue.Bounds.Height <= paneOrigin.Y + pane.Bounds.Height,
                "The confirmation's actual status slot must remain inside the visible information pane.");
            using var frame = window.CaptureRenderedFrame()!;
            using var encoded = new MemoryStream();
            frame.Save(encoded, PngBitmapEncoderOptions.Default);
            using var pixels = SKBitmap.Decode(encoded.ToArray());
            card.TryFindResource("MusicDownloadBrush", card.ActualThemeVariant, out var fillResource);
            var background = ((ISolidColorBrush)fillResource!).Color;
            var ink = 0;
            for (var y = (int)Math.Ceiling(origin.Y); y < (int)(origin.Y + cue.Bounds.Height); y++)
            {
                for (var x = (int)Math.Ceiling(origin.X); x < (int)(origin.X + cue.Bounds.Width); x++)
                {
                    var pixel = pixels.GetPixel(x, y);
                    if (pixel.Blue > background.B + 3 && pixel.Red > background.R + 1)
                    {
                        ink++;
                    }
                }
            }

            TestAssert.True(ink >= 3, "The middle compositor frame must contain actual checkmark ink rather than only an ancestor-clipped numeric value.");
            var coverPoint = card.TranslatePoint(new Point(100, 180), window)!.Value;
            TestAssert.Equal(new SKColor(180, 123, 115), pixels.GetPixel((int)coverPoint.X, (int)coverPoint.Y),
                "Status-row changes cannot clip away or recolor the lower part of the artwork.");
        }

        /// <summary>Publishes real production card state without starting audio or contacting an endpoint.</summary>
        /// <param name="model">The card receiving the source state.</param>
        /// <param name="state">The shared source-revision download owner.</param>
        /// <param name="track">The isolated source descriptor.</param>
        /// <param name="status">The next transfer status.</param>
        private static void Emit(SongCardViewModel model, SongDownloadState state, MusicTrack track, AudioDownloadState status)
        {
            state.Progress = CardDownloadPresentationTests.Progress(track, "feedback-transfer", status, status == AudioDownloadState.Completed ? 100 : 50);
            state.IsAvailable = status == AudioDownloadState.Completed;
            model.NotifyDownloadState();
        }

        /// <summary>Requires feedback to preserve both the card and its information layout.</summary>
        /// <param name="card">The actual production artwork card.</param>
        /// <param name="button">The original interactive tile.</param>
        /// <param name="bounds">The original card geometry.</param>
        /// <param name="paneBounds">The original information geometry.</param>
        private static void AssertGeometry(SongCard card, ArcButton button, Rect bounds, Rect paneBounds)
        {
            var matrix = button.RenderTransform?.Value ?? Matrix.Identity;
            TestAssert.True(card.Bounds == bounds && card.FindControl<Grid>("CardInformationPane")!.Bounds == paneBounds
                && Math.Abs(matrix.M11 - 1) < .001 && Math.Abs(matrix.M22 - 1) < .001,
                "Hover, selection and download confirmation must preserve card spacing, information bounds and artwork scale.");
        }

        /// <summary>Checks that selection and hover leave actual completed download-fill pixels unchanged.</summary>
        /// <param name="window">The actual compositor window.</param>
        /// <param name="card">The selected and hovered completed card.</param>
        private static void AssertDownloadColor(Window window, SongCard card)
        {
            var pane = card.FindControl<Grid>("CardInformationPane")!;
            var point = pane.TranslatePoint(new Point(pane.Bounds.Width - 4, pane.Bounds.Height - 20), window)!.Value;
            using var frame = window.CaptureRenderedFrame()!;
            using var data = new MemoryStream();
            frame.Save(data, PngBitmapEncoderOptions.Default);
            using var image = SKBitmap.Decode(data.ToArray());
            card.TryFindResource("MusicDownloadBrush", card.ActualThemeVariant, out var resource);
            var expected = ((ISolidColorBrush)resource!).Color;
            var actual = image.GetPixel((int)point.X, (int)point.Y);
            TestAssert.True(actual.Red == expected.R && actual.Green == expected.G && actual.Blue == expected.B,
                "The hover wash must never recolor the meaningful download background.");
        }

        /// <summary>Captures a real composited frame and samples a clear information-background pixel.</summary>
        /// <param name="window">The actual compositor window.</param>
        /// <param name="path">The ignored image destination.</param>
        /// <returns>The sampled information background.</returns>
        private static SKColor Capture(Window window, string path)
        {
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The feedback frame was not rendered.");
            frame.Save(path, PngBitmapEncoderOptions.Default);
            using var image = SKBitmap.Decode(path);
            return image.GetPixel(300, 100);
        }

        /// <summary>Advances the real Arc driver while retaining intermediate frame samples.</summary>
        /// <param name="clock">The installed deterministic frame source.</param>
        /// <param name="window">The actual rendered view.</param>
        /// <param name="milliseconds">The elapsed interval.</param>
        private static void Advance(ManualFrameClock clock, Window window, int milliseconds)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMilliseconds(5));
            Pump(window);
        }

        /// <summary>Completes layout and commits actual compositor state without advancing Arc time.</summary>
        /// <param name="window">The production card's window.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
