#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Presentation.Views;
using ArcPage = TKCode.Arc.Mobile.Page;

namespace MajMusic.Tests
{
    /// <summary>Checks one-owner mobile insets against real phone scale and subsequent native changes.</summary>
    internal static class MobileSafeAreaTests
    {
        /// <summary>Runs actual Arc safe-area layout in one complete isolated application lifetime.</summary>
        /// <returns>A task representing all scale, system-change and lifetime regressions.</returns>
        public static Task RunAsync()
        {
            return HeadlessProcessIsolation.RunAsync("mobile-safe-area", RunHeadlessAsync);
        }

        /// <summary>Verifies normalized platform values are applied once and refreshed when scale or native bars change.</summary>
        /// <returns>A task representing actual safe-area border and header geometry checks.</returns>
        private static async Task RunHeadlessAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await session.Dispatch<bool>(() =>
            {
                var navbar = new TKCode.Arc.Mobile.Navbar { Title = "Generated safe-area fixture" };
                var page = new ArcPage { Header = navbar, Content = new Border(), UsePlatformSafeArea = false, SafeArea = new Thickness(0, 140, 0, 0) };
                var root = new UserControl { Content = page };
                var window = new Window { Content = root, Width = 1200d / 3.25, Height = 2670d / 3.25 };
                try
                {
                    window.Show();
                    window.SetRenderScaling(3.25);
                    Layout(window);
                    var insets = new ScaledInsets(window) { PhysicalInsets = new Thickness(0, 140, 0, 0) };
                    using var owner = new PageSafeAreaOwner(window, root, page, insets);
                    Layout(window);
                    TestAssert.True(!TopLevel.GetAutoSafeAreaPadding(root) && !page.UsePlatformSafeArea && insets.DisplayEdgeToEdgePreference,
                        "The explicitly opted-in Android root must have exactly one page-owned safe area while drawing edge to edge.");
                    AssertPadding(page, navbar, window, new Thickness(0, 140d / 3.25, 0, 0));
                    TestAssert.True(Math.Abs(navbar.TranslatePoint(default, window)!.Value.Y * window.RenderScaling - 140) < 1,
                        "The actual header must start at 140 physical pixels, with neither duplicated outer padding nor a second density division.");

                    insets.SetPhysicalInsets(new Thickness(65, 0, 0, 78));
                    Layout(window);
                    AssertPadding(page, navbar, window, new Thickness(20, 0, 0, 24));

                    insets.PhysicalInsets = new Thickness(0, 140, 0, 78);
                    window.SetRenderScaling(2);
                    Layout(window);
                    AssertPadding(page, navbar, window, new Thickness(0, 70, 0, 39));

                    insets.SetPhysicalInsets(default);
                    Layout(window);
                    AssertPadding(page, navbar, window, default);
                    owner.Dispose();
                    insets.SetPhysicalInsets(new Thickness(0, 200, 0, 80));
                    Layout(window);
                    TestAssert.Equal(default(Thickness), page.SafeArea,
                        "Disposing a mobile root must stop subsequent native safe-area callbacks from mutating its former page.");
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Checks the actual Arc border padding and root-relative header position.</summary>
        /// <param name="page">The active Arc mobile page.</param>
        /// <param name="navbar">The actual header laid out after safe-area padding.</param>
        /// <param name="window">The native test root.</param>
        /// <param name="expected">The expected logical padding reported by the platform.</param>
        private static void AssertPadding(ArcPage page, Control navbar, Window window, Thickness expected)
        {
            var border = page.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_Safe");
            var origin = navbar.TranslatePoint(default, window)!.Value;
            TestAssert.True(page.SafeArea == expected && border.Padding == expected
                && Math.Abs(origin.X - expected.Left) < 1 && Math.Abs(origin.Y - expected.Top) < 1,
                "The actual page, template border and header must share one current normalized platform safe area.");
        }

        /// <summary>Processes actual template layout after native size, scale or inset changes.</summary>
        /// <param name="window">The headless mobile presentation root.</param>
        private static void Layout(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Models the actual Android platform contract: raw physical insets are normalized by the current host scale.</summary>
        private sealed class ScaledInsets : PageSafeAreaOwner.ISafeAreaSource
        {
            /// <summary>Provides the current actual renderer scale.</summary>
            private readonly TopLevel _topLevel;
            /// <summary>Creates a deterministic native insets provider.</summary>
            /// <param name="topLevel">The presentation whose scale normalizes native measurements.</param>
            public ScaledInsets(TopLevel topLevel)
            {
                _topLevel = topLevel;
            }

            /// <summary>Gets or sets the measured native physical-pixel padding.</summary>
            public Thickness PhysicalInsets { get; set; }
            /// <inheritdoc />
            public Thickness Padding
            {
                get
                {
                    var scale = _topLevel.RenderScaling;
                    return new Thickness(PhysicalInsets.Left / scale, PhysicalInsets.Top / scale, PhysicalInsets.Right / scale, PhysicalInsets.Bottom / scale);
                }
            }
            /// <summary>Gets whether the host requested edge-to-edge drawing.</summary>
            public bool DisplayEdgeToEdgePreference { get; private set; }
            /// <inheritdoc />
            public event EventHandler? Changed;

            /// <inheritdoc />
            public void EnableEdgeToEdge()
            {
                DisplayEdgeToEdgePreference = true;
            }

            /// <inheritdoc />
            public void Dispose()
            {
            }

            /// <summary>Reports a real system-bar or cutout transition after updating the authoritative current values.</summary>
            /// <param name="physical">The native physical-pixel padding.</param>
            public void SetPhysicalInsets(Thickness physical)
            {
                PhysicalInsets = physical;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
