#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using MajMusic.Presentation.Theme;
using TKCode.Arc.Motion;

namespace MajMusic.Tests
{
    /// <summary>Protects bounded navigation rasterization and one capture per delayed page publication.</summary>
    internal static partial class SurfaceMotionTests
    {
        /// <summary>Checks a high-DPI capture budget and avoids rendering the old tree again when new content arrives late.</summary>
        /// <param name="clock">The installed deterministic Arc frame clock.</param>
        private static void ValidateBoundedCaptureAndLatePublication(ManualFrameClock clock)
        {
            var source = new RenderCountPage { Background = Brushes.DarkRed };
            var target = Page("Delayed page", Brushes.DarkBlue);
            var host = new MusicSurfaceHost { Content = source };
            var window = new Window { Width = 1200, Height = 900, Content = host };
            try
            {
                window.Show();
                window.SetRenderScaling(2);
                Pump(window);
                host.ChangeVersion++;
                Pump(window);
                var viewport = host.SnapshotViewport;
                var pixels = host.SnapshotPixelSize;
                TestAssert.True(host.HasSnapshot && viewport == host.Bounds.Size && pixels.Width > 0 && pixels.Height > 0,
                    "The outgoing surface must keep its original logical viewport at a real high display scale.");
                TestAssert.True((long)pixels.Width * pixels.Height <= 2 * 1024 * 1024 && Math.Max(pixels.Width, pixels.Height) <= 1920,
                    "Navigation must bound transient rasterization by total pixel count as well as longest edge.");

                clock.Advance(TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(5));
                Pump(window);
                var update = host.TransitionUpdate;
                var progress = host.TransitionProgress;
                var renders = source.RenderCount;
                TestAssert.True(progress is > 0 and < 1, "The version must already be moving before delayed page publication.");
                host.Content = target;
                TestAssert.Equal(renders, source.RenderCount,
                    "Publishing the content of an already moving version must not synchronously rasterize its old control tree again.");
                Pump(window);
                TestAssert.True(ReferenceEquals(update, host.TransitionUpdate) && Math.Abs(progress - host.TransitionProgress) < 0.000001
                    && host.SnapshotViewport == viewport && host.SnapshotPixelSize == pixels,
                    "Late content must join the same transition and keep its existing outgoing snapshot without resetting progress.");

                clock.Advance(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(5));
                Pump(window);
                TestAssert.True(host.TransitionProgress == 1 && !host.HasSnapshot && host.SnapshotPixelSize == default
                    && ReferenceEquals(host.Content, target), "A joined transition must finish normally and release its native snapshot.");
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>Observes actual synchronous old-tree rendering independently of the host's diagnostics.</summary>
        private sealed class RenderCountPage : Control
        {
            /// <summary>Gets the generated page color used by synchronous rasterization checks.</summary>
            public IBrush? Background { get; init; }
            /// <summary>Gets the number of times this real page has painted.</summary>
            public int RenderCount { get; private set; }

            /// <inheritdoc />
            public override void Render(DrawingContext context)
            {
                RenderCount++;
                if (Background is { } background)
                {
                    context.FillRectangle(background, new Rect(Bounds.Size));
                }
            }
        }
    }
}
