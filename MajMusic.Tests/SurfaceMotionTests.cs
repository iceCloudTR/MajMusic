#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Presentation.Theme;
using TKCode.Arc.Motion;

namespace MajMusic.Tests
{
    /// <summary>Checks deterministic Arc surface timing, snapshot ownership and interruption without real I/O.</summary>
    internal static class SurfaceMotionTests
    {
        /// <summary>Checks surface and search motion on the actual production controls using Arc's frame clock.</summary>
        /// <returns>A task representing deterministic surface regressions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await session.Dispatch<bool>(async () =>
            {
                var clock = new ManualFrameClock();
                using var installed = clock.Install();
                var source = Page("Previous", Brushes.DarkRed);
                var destination = Page("Current", Brushes.DarkBlue);
                var host = new MusicSurfaceHost { Content = source };
                var search = new SearchRevealPanel
                {
                    Height = 44,
                    Children =
                    {
                        new Border { Background = Brushes.Gray, ClipToBounds = true },
                        new Border { Child = new TextBox { Height = 44 }, ClipToBounds = true },
                        new Button { Content = "Search", Width = 44, Height = 44 },
                        new Button { Content = "Close", Width = 44, Height = 44 }
                    }
                };
                var chrome = new Border { Height = 44, Child = search };
                var player = new Border { Height = 44, Background = Brushes.DarkGray };
                var root = new Grid { RowDefinitions = RowDefinitions.Parse("44,*,44"), Children = { chrome, host, player } };
                Grid.SetRow(host, 1);
                Grid.SetRow(player, 2);
                var window = new Window { Width = 640, Height = 480, Content = root };
                try
                {
                    window.Show();
                    Pump(window);
                    var chromeBounds = chrome.Bounds;
                    var playerBounds = player.Bounds;
                    TestAssert.True(host.TransitionProgress == 1 && !host.HasSnapshot,
                        "A first surface must be immediately stable without retaining a startup snapshot.");

                    host.Content = destination;
                    Pump(window);
                    TestAssert.True(host.HasSnapshot && host.TransitionProgress == 0 && host.IncomingOffset == 12,
                        "Changing pages must immediately capture the old surface and start the incoming page below its final position.");
                    TestAssert.Equal(1, host.GetVisualDescendants().OfType<Button>().Count(),
                        "The outgoing surface must be a bitmap, not another interactive page tree.");
                    TestAssert.True(ReferenceEquals(destination, host.Content), "Only the newest page may own the live presenter.");
                    var progress = Sample(clock, window, () => host.TransitionProgress, 4, 90);
                    AssertSlowFastSlow(progress, "The actual incoming surface");
                    TestAssert.True(host.TransitionProgress == 1 && !host.HasSnapshot && host.IncomingOffset == 0,
                        "The final Arc frame must restore the live page and release the old bitmap.");
                    TestAssert.True(chrome.Bounds == chromeBounds && player.Bounds == playerBounds,
                        "Whole-page motion must leave navigation and the player at their original layout coordinates.");

                    search.IsExpanded = true;
                    Pump(window);
                    var width = Sample(clock, window, () => search.DisplayedSearchWidth, 4, 65);
                    AssertSlowFastSlow(width, "The actual search extension");
                    TestAssert.True(Math.Abs(search.DisplayedSearchWidth - search.Bounds.Width) <= 1,
                        "The symmetric search motion must reach the complete reserved lane.");

                    host.Content = source;
                    Pump(window);
                    clock.Advance(TimeSpan.FromMilliseconds(110), TimeSpan.FromMilliseconds(10));
                    Pump(window);
                    var interrupted = host.TransitionUpdate;
                    host.Content = destination;
                    Pump(window);
                    TestAssert.True(interrupted.IsCompleted && !await interrupted,
                        "A rapid page reversal must finish the obsolete transition as superseded.");
                    TestAssert.True(host.HasSnapshot && host.GetVisualDescendants().OfType<Button>().Count() == 1,
                        "Rapid navigation must retain at most one outgoing snapshot and one live page.");
                    window.Width = 900;
                    window.Height = 600;
                    Pump(window);
                    clock.Advance(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(10));
                    Pump(window);
                    TestAssert.True(!host.HasSnapshot && host.TransitionProgress == 1 && ReferenceEquals(host.Content, destination),
                        "Resizing during reversal must settle on the newest content and free the old frame.");

                    host.ChangeVersion++;
                    Pump(window);
                    TestAssert.True(host.HasSnapshot && host.TransitionProgress == 0,
                        "A new browse generation must animate even when its live view object remains unchanged.");
                    var detached = host.TransitionUpdate;
                    window.Content = null;
                    Pump(window);
                    TestAssert.True(detached.IsCompleted && !await detached && !host.HasSnapshot,
                        "Detaching the surface must interrupt its Arc driver and release the native snapshot immediately.");
                    window.Content = root;
                    Pump(window);
                    TestAssert.True(host.TransitionProgress == 1 && !host.HasSnapshot,
                        "Reattaching a cached page must begin from a complete stable surface.");

                    foreach (var level in new[] { ArcMotionLevel.Reduced, ArcMotionLevel.Off })
                    {
                        ArcMotion.SetLevel(host, level);
                        host.Content = ReferenceEquals(host.Content, source) ? destination : source;
                        Pump(window);
                        TestAssert.True(host.TransitionProgress == 1 && !host.HasSnapshot && host.TransitionUpdate.IsCompleted,
                            "Reduced and disabled motion must install a complete page without snapshots or pending transitions.");
                    }
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Creates a page whose original controls can be distinguished from its rendered outgoing snapshot.</summary>
        /// <param name="name">The page's action label.</param>
        /// <param name="brush">The page's distinct surface paint.</param>
        /// <returns>A deterministic page with one live action.</returns>
        private static Border Page(string name, IBrush brush)
        {
            return new Border { Background = brush, Child = new Button { Content = name, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left } };
        }

        /// <summary>Samples equal elapsed intervals from Arc's real frame driver.</summary>
        /// <param name="clock">The deterministic Arc frame source.</param>
        /// <param name="window">The live rendering viewport.</param>
        /// <param name="read">The production geometry or progress being observed.</param>
        /// <param name="count">The number of equal intervals.</param>
        /// <param name="milliseconds">The length of each interval.</param>
        /// <returns>The initial value followed by one value per interval.</returns>
        private static double[] Sample(ManualFrameClock clock, Window window, Func<double> read, int count, int milliseconds)
        {
            var samples = new double[count + 1];
            samples[0] = read();
            for (var index = 1; index <= count; index++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMilliseconds(5));
                Pump(window);
                samples[index] = read();
            }

            return samples;
        }

        /// <summary>Requires a small beginning and ending displacement around a visibly faster middle interval.</summary>
        /// <param name="samples">Five equally spaced production values.</param>
        /// <param name="purpose">The transition described in assertion messages.</param>
        private static void AssertSlowFastSlow(IReadOnlyList<double> samples, string purpose)
        {
            var early = samples[1] - samples[0];
            var middle = samples[2] - samples[1];
            var late = samples[4] - samples[3];
            TestAssert.True(early > 0 && middle > early * 1.4 && middle > late * 1.4,
                $"{purpose} must accelerate and decelerate on the rendered S curve: early {early:0.###}, middle {middle:0.###}, late {late:0.###}.");
        }

        /// <summary>Completes layout and commits actual headless frames without advancing the deterministic Arc clock.</summary>
        /// <param name="window">The rendering viewport.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
