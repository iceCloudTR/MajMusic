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
            await SampleAsync(window, static () => 0, 300, token);
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

        /// <summary>Observes page and search fade frames, including cancellation by rapid repeated user actions.</summary>
        /// <param name="window">The rendering and input window.</param>
        /// <param name="view">The production page host.</param>
        /// <param name="model">The shared search owner.</param>
        /// <param name="output">The ignored frame destination.</param>
        /// <param name="token">Cancels stalled animation sampling.</param>
        /// <returns>A task representing reveal and interruption checks.</returns>
        private static async Task ValidateRevealsAsync(Window window, MainView view, MainViewModel model, string output, CancellationToken token)
        {
            var tabs = view.FindControl<TabControl>("MainTabs")!;
            Tap(window, view.FindControl<ArcButton>("NavigateAccounts")!);
            var pageFrames = await SampleAsync(window, () => tabs.Opacity, 110, token,
                () => SaveFrame(window, Path.Combine(output, "ui-arc-motion-page-middle.png")));
            await SampleAsync(window, () => tabs.Opacity, 180, token);
            TestAssert.True(pageFrames.Any(value => value > 0.36 && value < 0.99),
                "A page change must paint intermediate opacity frames. Page=" + tabs.SelectedIndex + "; frames=" + string.Join(",", pageFrames.Select(value => value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))));
            TestAssert.True(tabs.SelectedIndex == 3 && Math.Abs(tabs.Opacity - 1) < 0.001, "The selected page must settle fully visible.");
            Tap(window, view.FindControl<ArcButton>("NavigateSettings")!);
            await SampleAsync(window, () => tabs.Opacity, 20, token);
            Tap(window, view.FindControl<ArcButton>("NavigateDirectories")!);
            Tap(window, view.FindControl<ArcButton>("NavigateHome")!);
            await SampleAsync(window, () => tabs.Opacity, 270, token);
            TestAssert.True(tabs.SelectedIndex == 0 && Math.Abs(tabs.Opacity - 1) < 0.001, "Rapid page changes must cancel older fades without leaving the latest page translucent.");
            Tap(window, view.FindControl<ArcButton>("BeginSearchButton")!);
            await model.HomeUpdate;
            var input = view.FindControl<TextBox>("LibrarySearch")!;
            var searchFrames = await SampleAsync(window, () => input.Opacity, 110, token,
                () => SaveFrame(window, Path.Combine(output, "ui-arc-motion-search-middle.png")));
            await SampleAsync(window, () => input.Opacity, 180, token);
            TestAssert.True(model.IsSearchExpanded && searchFrames.Any(value => value > 0.36 && value < 0.99)
                && Math.Abs(input.Opacity - 1) < 0.001, "Expanding the actual search must fade into complete visibility.");
            model.CancelSearchCommand.Execute(null);
            await model.HomeUpdate;
            Tap(window, view.FindControl<ArcButton>("BeginSearchButton")!);
            await model.HomeUpdate;
            await SampleAsync(window, () => input.Opacity, 25, token);
            model.CancelSearchCommand.Execute(null);
            await model.HomeUpdate;
            await SampleAsync(window, () => input.Opacity, 240, token);
            TestAssert.True(!model.IsSearchExpanded && Math.Abs(input.Opacity - 1) < 0.001 && Math.Abs(tabs.Opacity - 1) < 0.001,
                "Closing a moving search must restore its opacity and leave the current page fully visible.");
        }

        /// <summary>Checks live mobile content fades and cancels old page motion when a resize replaces the presentation tree.</summary>
        /// <param name="window">The responsive rendering window.</param>
        /// <param name="view">The shared desktop and touch host.</param>
        /// <param name="output">The ignored intermediate-frame directory.</param>
        /// <param name="token">Cancels stalled motion.</param>
        /// <returns>A task representing resize cancellation and mobile page reveal checks.</returns>
        private static async Task ValidateResponsiveInterruptionAsync(Window window, MainView view, string output, CancellationToken token)
        {
            var tabs = view.FindControl<TabControl>("MainTabs")!;
            Tap(window, view.FindControl<ArcButton>("NavigateAccounts")!);
            await SampleAsync(window, () => tabs.Opacity, 25, token);
            window.Width = 390;
            window.Height = 844;
            Pump(window);
            var mobile = view.GetVisualDescendants().OfType<MobileMusicView>().Single();
            var host = mobile.FindControl<ContentControl>("MobileSectionHost")!;
            view.NavigateToSection(4);
            var changing = await SampleAsync(window, () => host.Opacity, 110, token,
                () => SaveFrame(window, Path.Combine(output, "ui-arc-motion-mobile-page-middle.png")));
            await SampleAsync(window, () => host.Opacity, 180, token);
            TestAssert.True(changing.Any(value => value > 0.36 && value < 0.99) && Math.Abs(host.Opacity - 1) < 0.001,
                "Actual mobile page replacement must render intermediate opacity and then settle fully visible.");
            view.NavigateToSection(0);
            await SampleAsync(window, () => host.Opacity, 270, token);
            Tap(window, mobile.FindControl<Control>("MobileSearchButton")!);
            if (view.DataContext is MainViewModel model)
            {
                await model.HomeUpdate;
            }

            var search = mobile.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "MobileSearchBox");
            var searchFrames = await SampleAsync(window, () => search.Opacity, 110, token,
                () => SaveFrame(window, Path.Combine(output, "ui-arc-motion-mobile-search-middle.png")));
            await SampleAsync(window, () => search.Opacity, 180, token);
            TestAssert.True(search.IsVisible && searchFrames.Any(value => value > 0.36 && value < 0.99) && Math.Abs(search.Opacity - 1) < 0.001,
                "Expanding the actual phone search must produce intermediate fade frames and settle fully visible.");
            Tap(window, mobile.FindControl<Control>("MobileSearchButton")!);
            await SampleAsync(window, () => search.Opacity, 210, token);
            TestAssert.True(!search.IsVisible && Math.Abs(search.Opacity - 1) < 0.001,
                "Closing the phone search must restore its opacity without retaining a translucent inactive input.");
            view.NavigateToSection(2);
            await SampleAsync(window, () => host.Opacity, 25, token);
            window.Width = 1100;
            window.Height = 760;
            await SampleAsync(window, () => tabs.Opacity, 270, token);
            TestAssert.True(!view.IsMobileLayout && view.SelectedSectionIndex == 2 && Math.Abs(tabs.Opacity - 1) < 0.001
                && Math.Abs(host.Opacity - 1) < 0.001, "Resizing away during page motion must cancel detached animations without leaving either presentation translucent.");
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
            Pump(window);
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
