#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using TKCode.Arc;
using TKCode.Arc.Mobile;
using TKCode.Arc.Motion;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace MajMusic.Tests
{
    internal static class RapidNavigationIconTests
    {
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await session.Dispatch(async () =>
            {
                var clock = new ManualFrameClock();
                using var installed = clock.Install();
                var workspace = new CardDownloadPresentationTests.DownloadWorkspace();
                await using var model = new MainViewModel(workspace, new PlaybackService(new HeadlessTests.TimelineBackend()));
                await model.InitializeAsync();
                var view = new MainView { DataContext = model };
                var window = new Window { Width = 390, Height = 844, Content = view };
                try
                {
                    window.Show();
                    window.SetRenderScaling(1.25);
                    Frame(window, clock);
                    var mobile = view.GetVisualDescendants().OfType<MobileMusicView>().Single();
                    var nav = mobile.FindControl<Tabbar>("MobileNavigationBar")!;
                    var transport = mobile.FindControl<Control>("MobilePlayPauseButton")!;
                    var transportGlyph = transport.GetVisualDescendants().OfType<ShapePath>().Single(path => path.IsEffectivelyVisible);
                    var fixedGlyphs = Enumerable.Range(0, 5).Select(index => (Control)Glyph(nav.ContainerFromIndex(index)!))
                        .Append(Glyph(mobile.FindControl<Control>("MobilePreviousButton")!))
                        .Append(Glyph(mobile.FindControl<Control>("MobileNextButton")!))
                        .Append(transportGlyph).ToArray();
                    var fixedPositions = fixedGlyphs.Select(glyph => PaintedBounds(glyph, window)).ToArray();
                    var search = mobile.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "MobileSearchButton");
                    var searchGlyph = Glyph(search);
                    var searchPosition = PaintedBounds(searchGlyph, window);
                    var searchPoint = Center(search, window);

                    window.MouseDown(searchPoint, MouseButton.Left);
                    Frame(window, clock);
                    AssertPosition(searchPosition, searchGlyph, window, "pressed search glyph");
                    window.MouseUp(new Point(0, 0), MouseButton.Left);
                    Frame(window, clock);

                    model.ReportError(new TimeoutException());
                    Frame(window, clock);
                    TestAssert.True(model.HasStatus && !model.HasPlaybackHint, "The fixture must exercise the optional footer status lane alone.");
                    AssertFixedGlyphs("status appears");
                    await model.InitializeAsync();
                    Frame(window, clock);
                    TestAssert.True(!model.HasStatus, "The fixture must clear the footer status again.");
                    AssertFixedGlyphs("status cleared");

                    var modes = mobile.GetVisualDescendants().OfType<Segmented>().Single();
                    foreach (var index in new[] { 1, 2, 0, 2, 1, 0, 1, 0 })
                    {
                        var point = Center(modes.ContainerFromIndex(index)!, window);
                        window.MouseDown(point, MouseButton.Left);
                        window.MouseUp(point, MouseButton.Left);
                        Frame(window, clock);
                        TestAssert.Equal((HomeBrowseMode)index, model.HomeMode, "Every rapid mode click must reach the real home mode.");
                        AssertPosition(searchPosition, searchGlyph, window, "rapid home switch search glyph");
                        AssertFixedGlyphs("rapid home switch");
                    }

                    foreach (var index in new[] { 1, 4, 0, 2, 0, 3, 0 })
                    {
                        var point = Center(nav.ContainerFromIndex(index)!, window);
                        window.MouseDown(point, MouseButton.Left);
                        Frame(window, clock);
                        AssertFixedGlyphs("pressed navigation glyph");
                        window.MouseUp(point, MouseButton.Left);
                        Frame(window, clock);
                        TestAssert.Equal(index, mobile.SelectedSectionIndex, "Rapid section clicks must still select the requested tab.");
                        AssertFixedGlyphs("rapid section switch");
                    }

                    void AssertFixedGlyphs(string stage)
                    {
                        for (var index = 0; index < fixedGlyphs.Length; index++)
                        {
                            AssertPosition(fixedPositions[index], fixedGlyphs[index], window, $"{stage}, icon {index}");
                        }
                    }
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        private static ArcGlyph Glyph(Control parent) => parent.GetVisualDescendants().OfType<ArcGlyph>().Single();

        private static Point Center(Control control, Window window) =>
            control.TranslatePoint(new Rect(control.Bounds.Size).Center, window)!.Value;

        private static Rect PaintedBounds(Control glyph, Window window) =>
            new(glyph.TranslatePoint(default, window)!.Value,
                glyph.TranslatePoint(new Point(glyph.Bounds.Width, glyph.Bounds.Height), window)!.Value);

        private static void AssertPosition(Rect before, Control glyph, Window window, string stage)
        {
            var after = PaintedBounds(glyph, window);
            TestAssert.True(Math.Abs(before.X - after.X) < .1 && Math.Abs(before.Y - after.Y) < .1
                && Math.Abs(before.Width - after.Width) < .1 && Math.Abs(before.Height - after.Height) < .1,
                $"{stage} must preserve the painted icon rectangle: {before} → {after}.");
        }

        private static void Frame(Window window, ManualFrameClock clock)
        {
            clock.Advance(TimeSpan.FromMilliseconds(24), TimeSpan.FromMilliseconds(6));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
