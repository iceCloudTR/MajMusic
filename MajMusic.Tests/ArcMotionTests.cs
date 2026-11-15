#nullable enable

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Playback;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;
using ArcButton = TKCode.Arc.Button;

namespace MajMusic.Tests
{
    /// <summary>Observes actual intermediate Arc animation geometry under the production motion configuration.</summary>
    internal static class ArcMotionTests
    {
        /// <summary>Checks button compression, segmented selection motion and stable page and search reveals.</summary>
        /// <returns>A task representing frame-based motion regressions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var workspace = new CardDownloadPresentationTests.DownloadWorkspace();
                workspace.Inner.Library.Add(new MusicTrack
                {
                    Id = "motion-song",
                    Title = "Motion artwork fixture",
                    Audio = new MediaResource { LocalPath = "generated-motion-audio" }
                });
                await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
                await model.InitializeAsync();
                var view = new MainView { DataContext = model };
                var window = new Window { Content = view, Width = 1100, Height = 760 };
                var output = Path.Combine(Environment.CurrentDirectory, ".work");
                Directory.CreateDirectory(output);
                try
                {
                    window.Show();
                    await SampleAsync(window, static () => 0, 70, timeout.Token);
                    TestAssert.Equal(ArcMotionLevel.Full, ArcTheme.State.MotionLevel, "The production Arc configuration must enable complete motion by default.");
                    await WarmTemplatesAsync(window, view, model, timeout.Token);
                    await ValidateButtonAsync(window, view, output, timeout.Token);
                    await ValidateSegmentsAsync(window, view, model, output, timeout.Token);
                    await ValidateArtworkStabilityAsync(window, view, timeout.Token);
                    await ValidateRevealsAsync(window, view, model, output, timeout.Token);
                    await ValidateResponsiveInterruptionAsync(window, view, output, timeout.Token);
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Renders each page before timing motion so first-time template and font work cannot consume the animation interval.</summary>
        /// <param name="window">The rendering window.</param>
        /// <param name="view">The production page host.</param>
        /// <param name="model">The local fixture's shared browse owner.</param>
        /// <param name="token">Cancels the finite warmup.</param>
        /// <returns>A task representing completed template and rendering warmup.</returns>
        private static async Task WarmTemplatesAsync(Window window, MainView view, MainViewModel model, CancellationToken token)
        {
            foreach (var section in new[] { 0, 1, 2, 3, 4, 0 })
            {
                view.NavigateToSection(section);
                Pump(window);
                using var frame = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
                frame.Render(window);
            }

            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate;
            Pump(window);
            using (var frame = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height)))
            {
                frame.Render(window);
            }

            model.ShowRecommendationsModeCommand.Execute(null);
            await model.HomeUpdate;
            model.BeginSearchCommand.Execute(null);
            await model.HomeUpdate;
            await SampleAsync(window, static () => 0, 400, token);
            using (var frame = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height)))
            {
                frame.Render(window);
            }

            model.CancelSearchCommand.Execute(null);
            await model.HomeUpdate;
            await SampleAsync(window, static () => 0, 400, token);
        }

        /// <summary>Verifies restoring ordinary button motion does not compress large song artwork.</summary>
        /// <param name="window">The real pointer input and rendering window.</param>
        /// <param name="view">The production music library.</param>
        /// <param name="token">Cancels the finite press observation.</param>
        /// <returns>A task representing stable artwork geometry checks.</returns>
        private static async Task ValidateArtworkStabilityAsync(Window window, MainView view, CancellationToken token)
        {
            var card = view.GetVisualDescendants().OfType<SongCard>().Single();
            var button = card.GetVisualDescendants().OfType<ArcButton>().Single();
            var point = Center(button, window);
            window.MouseDown(point, MouseButton.Left);
            var pressed = await SampleAsync(window, () => Scale(button), 120, token);
            window.MouseUp(point, MouseButton.Left);
            var released = await SampleAsync(window, () => Scale(button), 120, token);
            TestAssert.True(pressed.Concat(released).All(value => Math.Abs(value - 1) < 0.0001),
                "Large song artwork must retain stable geometry while ordinary action buttons animate their presses.");
        }

        /// <summary>Observes a real ordinary action compress through intermediate scales and recover after release.</summary>
        /// <param name="window">The real rendering and input window.</param>
        /// <param name="view">The production shared music view.</param>
        /// <param name="output">The ignored frame destination.</param>
        /// <param name="token">Cancels stalled animation sampling.</param>
        /// <returns>A task representing press and release geometry checks.</returns>
        private static async Task ValidateButtonAsync(Window window, MainView view, string output, CancellationToken token)
        {
            var button = view.FindControl<ArcButton>("NavigateHome")!;
            var initial = Scale(button);
            var point = Center(button, window);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            var pressing = await SampleAsync(window, () => Scale(button), 110, token, () => SaveFrame(window, Path.Combine(output, "ui-arc-motion-button-press.png")));
            await SampleAsync(window, () => Scale(button), 300, token);
            var pressed = Scale(button);
            TestAssert.True(initial >= 0.999 && pressed < 0.99 && pressed > 0.94, "An ordinary production action must visibly compress while held.");
            TestAssert.True(pressing.Any(value => value < initial - 0.0005 && value > pressed + 0.0005),
                "Button compression must pass through actual intermediate render scales instead of jumping to a static pressed transform.");
            window.MouseUp(point, MouseButton.Left);
            var releasing = await SampleAsync(window, () => Scale(button), 110, token);
            await SampleAsync(window, () => Scale(button), 350, token);
            TestAssert.True(releasing.Any(value => value > pressed + 0.0005 && value < initial - 0.0005),
                "Releasing an ordinary button must animate through intermediate scales.");
            TestAssert.True(Math.Abs(Scale(button) - initial) < 0.001, "Released buttons must settle at their original geometry.");
        }

        /// <summary>Observes the real home indicator travel between two mode choices without replacing the browser.</summary>
        /// <param name="window">The rendering and input window.</param>
        /// <param name="view">The production desktop home.</param>
        /// <param name="model">The shared browse owner.</param>
        /// <param name="output">The ignored frame destination.</param>
        /// <param name="token">Cancels stalled animation sampling.</param>
        /// <returns>A task representing real segmented motion.</returns>
        private static async Task ValidateSegmentsAsync(Window window, MainView view, MainViewModel model, string output, CancellationToken token)
        {
            var selector = view.FindControl<SegmentedControl>("HomeModeSegments")!;
            var indicator = selector.GetVisualDescendants().OfType<GlidingIndicator>().Single(control => control.Name == "PART_Selection");
            var initial = indicator.CurrentRect.X;
            var target = selector.ContainerFromIndex(2)!;
            var point = Center(target, window);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await model.HomeUpdate;
            var moving = await SampleAsync(window, () => indicator.CurrentRect.X, 140, token,
                () => SaveFrame(window, Path.Combine(output, "ui-arc-motion-segment-middle.png")));
            await SampleAsync(window, () => indicator.CurrentRect.X, 550, token);
            var settled = indicator.CurrentRect.X;
            TestAssert.True(model.IsLibraryMode && settled > initial + 50, "Selecting the real library segment must move both the browse mode and painted indicator.");
            TestAssert.True(moving.Any(value => value > initial + 1 && value < settled - 1),
                "The home indicator must be rendered between its initial and final choices while gliding.");
            var targetOrigin = target.TranslatePoint(default, indicator)!.Value;
            TestAssert.True(Math.Abs(settled - targetOrigin.X - 0.5) <= 1,
                "The moving mode indicator must settle beneath its selected interactive item.");
            SaveFrame(window, Path.Combine(output, "ui-arc-motion-segment-settled.png"));
        }

        /// <summary>Observes horizontal page frames and search width frames, including cancellation by rapid repeated user actions.</summary>
        /// <param name="window">The rendering and input window.</param>
        /// <param name="view">The production page host.</param>
        /// <param name="model">The shared search owner.</param>
        /// <param name="output">The ignored frame destination.</param>
        /// <param name="token">Cancels stalled animation sampling.</param>
        /// <returns>A task representing reveal and interruption checks.</returns>
        private static async Task ValidateRevealsAsync(Window window, MainView view, MainViewModel model, string output, CancellationToken token)
        {
            var tabs = view.FindControl<TabControl>("MainTabs")!;
            var page = tabs.GetVisualDescendants().OfType<MusicSurfaceHost>().Single(host => host.Name == "PageSurfaceTransition");
            Tap(window, view.FindControl<ArcButton>("NavigateAccounts")!);
            var pageFrames = await SampleAsync(window, () => ReadSlideProgress(page), 110, token,
                () => SaveFrame(window, Path.Combine(output, "ui-arc-motion-page-middle.png")));
            await SampleAsync(window, () => ReadSlideProgress(page), 340, token);
            TestAssert.True(pageFrames.Any(value => value > 0 && value < 0.99),
                "A page change must paint intermediate whole-surface Arc frames. Page=" + tabs.SelectedIndex + "; frames=" + string.Join(",", pageFrames.Select(value => value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))));
            TestAssert.True(tabs.SelectedIndex == 3 && Math.Abs(page.TransitionProgress - 1) < 0.001 && !page.HasSnapshot && Math.Abs(page.IncomingOffset) < 0.001, "The selected page must settle fully visible.");
            Tap(window, view.FindControl<ArcButton>("NavigateSettings")!);
            await SampleAsync(window, () => ReadSlideProgress(page), 20, token);
            Tap(window, view.FindControl<ArcButton>("NavigateDirectories")!);
            Tap(window, view.FindControl<ArcButton>("NavigateHome")!);
            await SampleAsync(window, () => ReadSlideProgress(page), 420, token);
            TestAssert.True(tabs.SelectedIndex == 0 && Math.Abs(page.TransitionProgress - 1) < 0.001 && !page.HasSnapshot, "Rapid page changes must cancel older slides without retaining an outgoing page.");
            await ValidateSearchExtensionAsync(window, view, model, output, token);
        }

        /// <summary>Observes the actual search frame expand from and retract to its fixed right-hand icon without moving the home menu.</summary>
        /// <param name="window">The real rendering and input window.</param>
        /// <param name="view">The production desktop home.</param>
        /// <param name="model">The unchanged search and browsing owner.</param>
        /// <param name="output">The ignored intermediate-frame directory.</param>
        /// <param name="token">Cancels stalled animation or search work.</param>
        /// <returns>A task representing search geometry, keyboard and interruption checks.</returns>
        private static async Task ValidateSearchExtensionAsync(Window window, MainView view, MainViewModel model, string output, CancellationToken token)
        {
            var panel = view.FindControl<SearchRevealPanel>("DesktopSearchReveal")!;
            var host = view.FindControl<Border>("LibrarySearchRevealHost")!;
            var input = view.FindControl<TextBox>("LibrarySearch")!;
            var selector = view.FindControl<SegmentedControl>("HomeModeSegments")!;
            var menuFrame = selector.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_Frame");
            foreach (var width in new[] { 720d, 1100d })
            {
                window.Width = width;
                window.Height = width == 720 ? 540 : 760;
                Pump(window);
                model.CancelSearchCommand.Execute(null);
                await model.HomeUpdate;
                await SettleSearchAsync(window, panel, token);
                var menu = BoundsIn(menuFrame, window);
                var collapsed = panel.DisplayedSearchWidth;
                TestAssert.True(Math.Abs(collapsed - 44) <= 1 && panel.RevealProgress <= 0.001,
                    "A closed desktop search must reserve exactly its 44-DIP icon rather than a persistent input.");
                Tap(window, view.FindControl<ArcButton>("BeginSearchButton")!);
                var opening = await SampleAsync(window, () => ReadSearchWidth(window, panel, host, menuFrame, menu), 140, token,
                    () => SaveFrame(window, Path.Combine(output, $"ui-search-extension-opening-{width:0}.png")));
                await SettleSearchAsync(window, panel, token, () => ReadSearchWidth(window, panel, host, menuFrame, menu));
                await model.HomeUpdate;
                var expanded = panel.DisplayedSearchWidth;
                TestAssert.True(model.IsSearchExpanded && input.IsFocused && panel.RevealProgress >= 0.999
                    && Math.Abs(expanded - panel.Bounds.Width) <= 1, "Expanding search must fill its available lane, retain keyboard focus and preserve the mode menu.");
                AssertWidthTravel(opening, collapsed, expanded, true);
                var sources = view.FindControl<Grid>("HomeSourceSelectors")!;
                TestAssert.True(!sources.IsEffectivelyVisible && !view.FindControl<TKCode.Arc.Select>("HomeEndpointPicker")!.Focus(),
                    "Source selectors covered by expanded search must not remain focusable through an invisible toolbar.");
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
                window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
                var keyboardFocus = window.FocusManager!.GetFocusedElement() as Control;
                TestAssert.True(keyboardFocus is null || !keyboardFocus.GetVisualAncestors().Contains(sources),
                    "Backward tab navigation from search must skip its hidden source selectors.");
                input.Text = "Motion";
                input.Focus();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await model.HomeUpdate;
                TestAssert.True(model.IsSearching && model.Tracks.All(track => track.Title.Contains("Motion", StringComparison.OrdinalIgnoreCase)),
                    "Enter in the extending search must continue to run the original indexed search.");
                TestAssert.True(input.IsFocused, "Replacing search results must retain the actual search field's keyboard focus for Escape.");
                input.Text = string.Concat(Enumerable.Repeat("Long search text ", 30));
                input.CaretIndex = input.Text.Length;
                Pump(window);
                var textViewport = input.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "PART_ScrollViewer");
                var cancel = view.FindControl<ArcButton>("CancelSearchButton")!;
                var clearPresence = input.GetVisualDescendants().OfType<PresenceHost>().Single(control => control.Name == "PART_ClearPresence");
                TestAssert.True(!clearPresence.IsEffectivelyVisible,
                    "A filled extending search must expose one closing action instead of overlapping it with the field's internal clear button.");
                TestAssert.True(BoundsIn(textViewport, window).Right <= BoundsIn(cancel, window).Left + 1,
                    "Long search text must stay inside its text viewport instead of painting beneath the fixed closing icon.");
                Tap(window, view.FindControl<ArcButton>("CancelSearchButton")!);
                var checkedClosingFocus = false;
                double ReadClosingWidth()
                {
                    if (!checkedClosingFocus)
                    {
                        checkedClosingFocus = true;
                        var focus = window.FocusManager!.GetFocusedElement() as Control;
                        TestAssert.True(!input.IsFocused && view.FindControl<ArcButton>("BeginSearchButton")!.IsFocused,
                            $"The first rendered closing frame must return focus to its icon: model={model.IsSearchExpanded}, panel={panel.IsExpanded}, inputFocus={input.IsFocused}, beginFocus={view.FindControl<ArcButton>("BeginSearchButton")!.IsFocused}, actualFocus={focus?.GetType().Name}/{focus?.Name}.");
                    }

                    return ReadSearchWidth(window, panel, host, menuFrame, menu);
                }

                var closing = await SampleAsync(window, ReadClosingWidth, 140, token,
                    () => SaveFrame(window, Path.Combine(output, $"ui-search-extension-closing-{width:0}.png")));
                await SettleSearchAsync(window, panel, token, () => ReadSearchWidth(window, panel, host, menuFrame, menu));
                await model.HomeUpdate;
                AssertWidthTravel(closing, collapsed, expanded, false);
                TestAssert.True(!model.IsSearchExpanded && panel.RevealProgress <= 0.001 && Math.Abs(panel.DisplayedSearchWidth - collapsed) <= 1
                    && !input.IsVisible && view.FindControl<ArcButton>("BeginSearchButton")!.IsVisible,
                    $"Closing search must animate all the way back to the same icon before hiding the input frame: viewport={width}, model={model.IsSearchExpanded}, panel={panel.IsExpanded}, progress={panel.RevealProgress}, width={panel.DisplayedSearchWidth}, inputVisible={input.IsVisible}, beginVisible={view.FindControl<ArcButton>("BeginSearchButton")!.IsVisible}.");
                Tap(window, view.FindControl<ArcButton>("BeginSearchButton")!);
                await SettleSearchAsync(window, panel, token);
                input.Focus();
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                await SettleSearchAsync(window, panel, token);
                await model.HomeUpdate;
                TestAssert.True(!model.IsSearchExpanded && panel.RevealProgress <= 0.001 && !input.IsVisible,
                    "Escape in the actual search input must close the same extending toolbar and restore the icon.");
            }

            var stableMenu = BoundsIn(menuFrame, window);
            Tap(window, view.FindControl<ArcButton>("BeginSearchButton")!);
            await SampleAsync(window, () => ReadSearchWidth(window, panel, host, menuFrame, stableMenu), 30, token);
            Tap(window, view.FindControl<ArcButton>("CancelSearchButton")!);
            await SampleAsync(window, () => ReadSearchWidth(window, panel, host, menuFrame, stableMenu), 30, token);
            Tap(window, view.FindControl<ArcButton>("BeginSearchButton")!);
            await SettleSearchAsync(window, panel, token, () => ReadSearchWidth(window, panel, host, menuFrame, stableMenu));
            TestAssert.True(model.IsSearchExpanded && panel.RevealProgress >= 0.999 && Math.Abs(panel.DisplayedSearchWidth - panel.Bounds.Width) <= 1,
                "Rapid opening and closing must reverse the current search motion and settle at the newest open intent.");
            Tap(window, view.FindControl<ArcButton>("CancelSearchButton")!);
            await SampleAsync(window, () => panel.DisplayedSearchWidth, 30, token);
            window.Width = 900;
            Pump(window);
            var resizedMenu = BoundsIn(menuFrame, window);
            Tap(window, view.FindControl<ArcButton>("BeginSearchButton")!);
            await SettleSearchAsync(window, panel, token, () => ReadSearchWidth(window, panel, host, menuFrame, resizedMenu));
            TestAssert.True(model.IsSearchExpanded && panel.RevealProgress >= 0.999 && Math.Abs(panel.DisplayedSearchWidth - panel.Bounds.Width) <= 1,
                "Resizing during a reversing search must use the new lane width and retain a complete expanded input.");
            Tap(window, view.FindControl<ArcButton>("CancelSearchButton")!);
            await model.HomeUpdate;
            await SettleSearchAsync(window, panel, token);
            window.Width = 1100;
            window.Height = 760;
            Pump(window);
            TestAssert.True(!model.IsSearchExpanded && panel.RevealProgress <= 0.001 && Math.Abs(panel.DisplayedSearchWidth - 44) <= 1,
                "The final closed search must remain a single icon after resize and cancellation.");
            foreach (var policy in new[] { ArcMotionLevel.Reduced, ArcMotionLevel.Off })
            {
                ArcMotion.SetLevel(window, policy);
                Pump(window);
                Tap(window, view.FindControl<ArcButton>("BeginSearchButton")!);
                Pump(window);
                TestAssert.True(model.IsSearchExpanded && panel.RevealProgress == 1 && panel.RevealUpdate.IsCompleted
                    && Math.Abs(panel.DisplayedSearchWidth - panel.Bounds.Width) <= 1 && !view.FindControl<Grid>("HomeSourceSelectors")!.IsVisible,
                    "Reduced or disabled motion must open search immediately and remove covered selectors from navigation.");
                Tap(window, view.FindControl<ArcButton>("CancelSearchButton")!);
                Pump(window);
                TestAssert.True(!model.IsSearchExpanded && panel.RevealProgress == 0 && panel.RevealUpdate.IsCompleted
                    && Math.Abs(panel.DisplayedSearchWidth - 44) <= 1 && !input.IsVisible
                    && view.FindControl<ArcButton>("BeginSearchButton")!.IsFocused,
                    "Reduced or disabled motion must close immediately with the input hidden and the icon ready for keyboard use.");
            }

            ArcMotion.SetLevel(window, ArcMotionLevel.Full);
            Pump(window);
        }

        /// <summary>Observes the current Arc spring to its actual completion instead of assuming a fixed settling duration.</summary>
        /// <param name="window">The real rendering window.</param>
        /// <param name="panel">The interruptible search presentation.</param>
        /// <param name="token">Cancels a stalled transition.</param>
        /// <param name="read">Optionally checks each remaining frame's actual geometry.</param>
        /// <returns>A task representing the completed current reveal generation.</returns>
        private static async Task SettleSearchAsync(Window window, SearchRevealPanel panel, CancellationToken token, Func<double>? read = null)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                Pump(window);
                read?.Invoke();
                TestAssert.True(timer.Elapsed < TimeSpan.FromSeconds(3), "The current search animation must settle within a finite interaction interval.");
                if (!panel.RevealUpdate.IsCompleted)
                {
                    await Task.Delay(12, token);
                }
            }
            while (!panel.RevealUpdate.IsCompleted);

            Pump(window);
            read?.Invoke();
        }

        /// <summary>Reads the actual displayed search width while checking every observed frame's stable menu, right anchor and aligned paint.</summary>
        /// <param name="window">The shared visual coordinate owner.</param>
        /// <param name="panel">The fixed search lane.</param>
        /// <param name="host">The animated search frame.</param>
        /// <param name="menuFrame">The neighboring menu's painted border.</param>
        /// <param name="menu">The menu rectangle captured before the user gesture.</param>
        /// <returns>The real arranged animated-frame width.</returns>
        private static double ReadSearchWidth(Window window, SearchRevealPanel panel, Border host, Border menuFrame, Rect menu)
        {
            AssertSearchIconProgress(panel);
            var currentMenu = BoundsIn(menuFrame, window);
            TestAssert.True(Math.Abs(currentMenu.X - menu.X) <= 1 && Math.Abs(currentMenu.Y - menu.Y) <= 1
                && Math.Abs(currentMenu.Width - menu.Width) <= 1 && Math.Abs(currentMenu.Height - menu.Height) <= 1,
                "Extending and retracting search must not move or resize the neighboring mode menu in any frame.");
            if (host.IsVisible)
            {
                var search = BoundsIn(host, window);
                var lane = BoundsIn(panel, window);
                TestAssert.True(Math.Abs(search.Right - lane.Right) <= 1 && Math.Abs(search.Top - menu.Top) <= 1
                    && Math.Abs(search.Bottom - menu.Bottom) <= 1,
                    "The moving search frame must remain anchored at the right and share the menu's painted top and bottom edges.");
                return host.Bounds.Width;
            }

            return panel.DisplayedSearchWidth;
        }

        /// <summary>Checks search glyphs follow the same frame progress without a second opacity transition lagging behind.</summary>
        /// <param name="panel">The shared desktop or phone search presentation.</param>
        private static void AssertSearchIconProgress(SearchRevealPanel panel)
        {
            TestAssert.True(Math.Abs(panel.Children[2].Opacity - (1 - panel.RevealProgress)) < 0.0001
                && Math.Abs(panel.Children[3].Opacity - panel.RevealProgress) < 0.0001,
                "Search and cancel glyph opacity must follow the current reveal progress directly without another animation delaying their state.");
        }

        /// <summary>Requires multiple distinct real widths between the search's two endpoints in the intended direction.</summary>
        /// <param name="samples">The real widths observed while the clock advanced.</param>
        /// <param name="collapsed">The icon endpoint width.</param>
        /// <param name="expanded">The available-lane endpoint width.</param>
        /// <param name="opening">Whether the observed motion grows toward the left.</param>
        private static void AssertWidthTravel(IReadOnlyList<double> samples, double collapsed, double expanded, bool opening)
        {
            var middle = samples.Where(width => width > collapsed + 2 && width < expanded - 2).ToArray();
            TestAssert.True(middle.Length >= 2 && middle.Max() - middle.Min() > 2,
                $"Search must render multiple distinct intermediate widths rather than appear or disappear at its endpoint. Opening={opening}, range={collapsed:0.##}..{expanded:0.##}, samples="
                + string.Join(",", samples.Select(value => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));
            TestAssert.True(opening ? samples[^1] > samples[0] + 2 : samples[^1] < samples[0] - 2,
                "The actual search frame must move continuously in the requested expansion or retraction direction.");
        }

        /// <summary>Expresses a rendered control rectangle in the common window coordinates.</summary>
        /// <param name="control">The painted control or lane being measured.</param>
        /// <param name="window">The common coordinate owner.</param>
        /// <returns>The current rendered bounds in window coordinates.</returns>
        private static Rect BoundsIn(Control control, Window window)
        {
            return new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
        }

        /// <summary>Checks live mobile content slides and cancels old page motion when a resize replaces the presentation tree.</summary>
        /// <param name="window">The responsive rendering window.</param>
        /// <param name="view">The shared desktop and touch host.</param>
        /// <param name="output">The ignored intermediate-frame directory.</param>
        /// <param name="token">Cancels stalled motion.</param>
        /// <returns>A task representing resize cancellation and mobile page reveal checks.</returns>
        private static async Task ValidateResponsiveInterruptionAsync(Window window, MainView view, string output, CancellationToken token)
        {
            var tabs = view.FindControl<TabControl>("MainTabs")!;
            var page = tabs.GetVisualDescendants().OfType<MusicSurfaceHost>().Single(host => host.Name == "PageSurfaceTransition");
            Tap(window, view.FindControl<ArcButton>("NavigateAccounts")!);
            await SampleAsync(window, () => ReadSlideProgress(page), 25, token);
            window.Width = 390;
            window.Height = 844;
            Pump(window);
            var mobile = view.GetVisualDescendants().OfType<MobileMusicView>().Single();
            var host = mobile.FindControl<MusicSurfaceHost>("MobileSectionHost")!;
            view.NavigateToSection(4);
            var changing = await SampleAsync(window, () => ReadSlideProgress(host), 110, token,
                () => SaveFrame(window, Path.Combine(output, "ui-arc-motion-mobile-page-middle.png")));
            await SampleAsync(window, () => ReadSlideProgress(host), 340, token);
            TestAssert.True(changing.Any(value => value > 0 && value < 0.99) && Math.Abs(host.TransitionProgress - 1) < 0.001,
                "Actual mobile page replacement must render intermediate whole-surface Arc frames and settle fully visible.");
            view.NavigateToSection(0);
            await SampleAsync(window, () => ReadSlideProgress(host), 420, token);
            var mobileSearch = mobile.GetVisualDescendants().OfType<SearchRevealPanel>().Single(control => control.Name == "MobileSearchReveal");
            var searchHost = (Border)mobileSearch.Children[1];
            var browser = mobile.GetVisualDescendants().OfType<BrowseTracksView>().Single(control => control.Name == "MobileSongBrowser");
            var initialBrowser = BoundsIn(browser, window);
            var modes = mobile.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "MobileHomeModes");
            var initialModes = BoundsIn(modes, window);
            var collapsedWidth = mobileSearch.DisplayedSearchWidth;
            double ReadMobileWidth()
            {
                AssertSearchIconProgress(mobileSearch);
                TestAssert.True(BoundsIn(browser, window) == initialBrowser && BoundsIn(modes, window) == initialModes,
                    "Phone search motion must retain the browser viewport and mode menu rectangles in every frame.");
                var frame = BoundsIn(searchHost, window);
                var lane = BoundsIn(mobileSearch, window);
                TestAssert.True(Math.Abs(frame.Right - lane.Right) <= 1 && Math.Abs(frame.Top - lane.Top) <= 1 && Math.Abs(frame.Bottom - lane.Bottom) <= 1,
                    "The phone's growing search must remain in its fixed 44-DIP lane with a stable right anchor.");
                return searchHost.Bounds.Width;
            }

            Tap(window, mobile.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "MobileSearchButton"));
            var search = mobile.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "MobileSearchBox");
            var searchFrames = await SampleAsync(window, ReadMobileWidth, 140, token,
                () => SaveFrame(window, Path.Combine(output, "ui-arc-motion-mobile-search-middle.png")));
            await SettleSearchAsync(window, mobileSearch, token, ReadMobileWidth);
            var expandedWidth = mobileSearch.DisplayedSearchWidth;
            AssertWidthTravel(searchFrames, collapsedWidth, expandedWidth, true);
            TestAssert.True(search.IsVisible && mobileSearch.RevealProgress >= 0.999 && Math.Abs(expandedWidth - mobileSearch.Bounds.Width) <= 1,
                "The actual phone search must grow from its icon into the whole reserved toolbar lane.");
            Tap(window, mobile.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "MobileCancelSearchButton"));
            var closingFrames = await SampleAsync(window, ReadMobileWidth, 140, token);
            await SettleSearchAsync(window, mobileSearch, token, ReadMobileWidth);
            AssertWidthTravel(closingFrames, collapsedWidth, expandedWidth, false);
            TestAssert.True(!search.IsVisible && mobileSearch.RevealProgress <= 0.001 && Math.Abs(mobileSearch.DisplayedSearchWidth - 44) <= 1,
                "Closing phone search must retract continuously to its icon without moving the browser.");
            view.NavigateToSection(2);
            await SampleAsync(window, () => ReadSlideProgress(host), 25, token);
            window.Width = 1100;
            window.Height = 760;
            await SampleAsync(window, () => ReadSlideProgress(page), 420, token);
            TestAssert.True(!view.IsMobileLayout && view.SelectedSectionIndex == 2 && Math.Abs(page.TransitionProgress - 1) < 0.001
                && Math.Abs(host.TransitionProgress - 1) < 0.001, "Resizing away during page motion must cancel detached animations without leaving a detached snapshot.");
        }

        /// <summary>Samples production page motion while ruling out opacity, scale and vertical entrances.</summary>
        /// <param name="host">The actual desktop or mobile surface presenter.</param>
        /// <returns>The current horizontal slide progress.</returns>
        private static double ReadSlideProgress(MusicSurfaceHost host)
        {
            foreach (var surface in host.GetVisualDescendants().OfType<Control>()
                .Where(control => control.Name is "PART_LiveSurface" or "PART_OutgoingSurface"))
            {
                var matrix = surface.RenderTransform?.Value ?? Matrix.Identity;
                TestAssert.True(surface.Opacity == 1 && Math.Abs(matrix.M32) < .001
                    && Math.Abs(matrix.M11 - 1) < .001 && Math.Abs(matrix.M22 - 1) < .001,
                    "Production page motion must remain fully opaque and translate horizontally without changing scale or Y.");
            }

            return host.TransitionProgress;
        }

        /// <summary>Samples real rendering frames while the regular animation clock continues advancing.</summary>
        /// <param name="window">The rendering window.</param>
        /// <param name="read">Reads actual animated geometry or opacity.</param>
        /// <param name="milliseconds">The finite observation period.</param>
        /// <param name="token">Cancels the observation.</param>
        /// <param name="capture">Optionally captures one intermediate rendered frame.</param>
        /// <returns>The observed animated values in frame order.</returns>
        private static async Task<IReadOnlyList<double>> SampleAsync(Window window, Func<double> read, int milliseconds,
            CancellationToken token, Action? capture = null)
        {
            var samples = new List<double>();
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < milliseconds)
            {
                Pump(window);
                samples.Add(read());
                if (capture is not null && timer.ElapsedMilliseconds >= 25)
                {
                    capture();
                    capture = null;
                }

                await Task.Delay(12, token);
            }

            Pump(window);
            samples.Add(read());
            return samples;
        }

        /// <summary>Reads the actual button render matrix horizontal scale.</summary>
        /// <param name="button">The rendered ordinary Arc button.</param>
        /// <returns>The current rendered horizontal scale.</returns>
        private static double Scale(ArcButton button)
        {
            return button.RenderTransform?.Value.M11 ?? 1;
        }

        /// <summary>Translates a rendered control's center to the input window.</summary>
        /// <param name="control">The interactive target.</param>
        /// <param name="window">The input owner.</param>
        /// <returns>The current target center.</returns>
        private static Point Center(Control control, Window window)
        {
            return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        }

        /// <summary>Exercises one actual pointer click without replacing the production command binding.</summary>
        /// <param name="window">The real input window.</param>
        /// <param name="control">The target control.</param>
        private static void Tap(Window window, Control control)
        {
            var point = Center(control, window);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        /// <summary>Completes layout and advances the actual in-memory rendering clock.</summary>
        /// <param name="window">The rendering window.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Writes a genuine Skia frame showing the sampled animated state.</summary>
        /// <param name="window">The rendered window.</param>
        /// <param name="path">The ignored local frame path.</param>
        private static void SaveFrame(Window window, string path)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
            bitmap.Render(window);
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
        }
    }

    /// <summary>Uses the production default motion setting in a separate real-rendering headless application.</summary>
    public sealed class HeadlessFullMotionTestApplication : Application
    {
        /// <summary>Loads the same legacy virtualization base as the desktop application.</summary>
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
        }

        /// <summary>Builds the production Arc configuration without overriding its motion policy.</summary>
        /// <returns>The complete Skia headless application builder.</returns>
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<HeadlessFullMotionTestApplication>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseMajMusicArc();
        }
    }
}
