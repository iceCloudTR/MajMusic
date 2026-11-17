#nullable enable

using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using SkiaSharp;
using TKCode.Arc;
using TKCode.Arc.Motion;

namespace MajMusic.Tests
{
    /// <summary>Checks that incomplete recommendation targets retain complete placeholders instead of flashing or exposing old artwork.</summary>
    internal static class BrowsePlaceholderLifecycleTests
    {
        /// <summary>Exercises slow, rejected and empty targets, retained refresh snapshots and superseded requests using actual frames.</summary>
        /// <returns>A task representing the rendered placeholder lifecycle.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var clock = new ManualFrameClock();
                using var installed = clock.Install();
                await ValidateLifecycleAsync(clock, timeout.Token);
                return true;
            }, timeout.Token);
        }

        /// <summary>Observes the real browser through the same home-mode actions used by desktop navigation.</summary>
        /// <param name="clock">The production Arc frame source.</param>
        /// <param name="token">Cancels a stalled fixture.</param>
        /// <returns>A task representing completed lifecycle and pixel assertions.</returns>
        private static async Task ValidateLifecycleAsync(ManualFrameClock clock, CancellationToken token)
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-placeholder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var redPath = Path.Combine(root, "old-red.png");
            var greenPath = Path.Combine(root, "ready-green.png");
            WriteCover(redPath, new SKColor(230, 24, 36));
            WriteCover(greenPath, new SKColor(24, 190, 60));
            var first = Endpoint("placeholder-a", "Alice");
            var second = Endpoint("placeholder-b", "Bob");
            var workspace = new LibraryExperienceTests.ExperienceWorkspace
            {
                Settings = new AppSettings { ActiveEndpointId = first.Id, Endpoints = [first, second] },
                ResolveCoverOverride = (track, _) => Task.FromResult(track.Cover?.LocalPath)
            };
            foreach (var endpoint in new[] { first, second })
            {
                workspace.Sessions[endpoint.Id] = new EndpointSession { EndpointId = endpoint.Id, Username = endpoint.Accounts[0].Username, IsLoggedIn = true };
            }

            workspace.Library.AddRange(Enumerable.Range(0, 24).Select(index => Track("local-" + index, redPath)));
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 1100, Height = 760 };
            var pending = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var obsolete = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var refresh = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await model.InitializeAsync();
                model.ShowLibraryModeCommand.Execute(null);
                await model.HomeUpdate.WaitAsync(token);
                window.Show();
                window.SetRenderScaling(1.25);
                await AdvanceAsync(clock, window, 500, token);
                await model.BrowsePreloadUpdate.WaitAsync(token);
                var browser = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
                var list = browser.GetResidentList(HomeBrowseMode.Recommendations);
                var overlay = browser.FindControl<Grid>("BrowseSkeletonPresentation")!;
                var skeleton = browser.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!;
                TestAssert.True(CountColor(window, browser, true) > 500, "The source fixture must actually paint old red artwork before navigation.");
                model.OnlineEnabled = true;
                await model.AutomaticLogin.WaitAsync(token);
                workspace.RecommendationOverride = (_, _, _) => pending.Task;
                TapMode(window, view, 0);
                var update = model.HomeUpdate;
                for (var frame = 0; frame < 12; frame++)
                {
                    await AdvanceAsync(clock, window, 36, token);
                    AssertPlaceholder(model, browser, overlay, skeleton, list);
                    TestAssert.Equal(0, CountColor(window, browser, true, true), "The visible incoming placeholder pane cannot expose old red cover fragments during its horizontal transition.");
                }

                Save(window, "ui-browse-placeholder-pending.png");
                pending.TrySetException(new HttpRequestException("Generated source rejection.", null, HttpStatusCode.Forbidden));
                await update.WaitAsync(token);
                await AdvanceAsync(clock, window, 600, token);
                AssertPlaceholder(model, browser, overlay, skeleton, list);
                TestAssert.Equal(0, CountColor(window, browser, true), "A settled rejected target must retain only placeholders across the entire viewport.");
                TestAssert.True(!model.IsBrowseLoading, "A failed request must finish its operation without falsely marking the workspace as still busy.");
                Save(window, "ui-browse-placeholder-rejected.png");

                workspace.RecommendationOverride = (_, _, _) => Task.FromResult<IReadOnlyList<MusicTrack>>([]);
                model.ShowRecommendationsModeCommand.Execute(null);
                await model.HomeUpdate.WaitAsync(token);
                await AdvanceAsync(clock, window, 600, token);
                AssertPlaceholder(model, browser, overlay, skeleton, list);
                TestAssert.True(!model.IsBrowseLoading && model.CardTracks.Count == 0,
                    "A quickly completed empty recommendation response must retain placeholders rather than flashing to an empty panel.");

                var ready = Enumerable.Range(0, 8).Select(index => Track("ready-" + index, greenPath, first)).ToArray();
                workspace.RecommendationOverride = (_, _, _) => Task.FromResult<IReadOnlyList<MusicTrack>>(ready);
                var started = workspace.RecommendationRequests.Count;
                model.RecommendationsCommand.Execute(null);
                await WaitUntilAsync(() => workspace.RecommendationRequests.Count > started && model.CardTracks.Count == ready.Length, clock, window, token);
                await model.RecommendationBatchUpdate.WaitAsync(token);
                await AdvanceAsync(clock, window, 450, token);
                TestAssert.True(!model.IsBrowsePlaceholderVisible && list.IsVisible && skeleton.PlaceholderCount == 0
                    && CountColor(window, browser, false) > 500, "Only a complete real current-target snapshot may replace the persistent placeholders.");

                var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                workspace.RecommendationOverride = (_, _, _) =>
                {
                    refreshStarted.TrySetResult();
                    return refresh.Task;
                };
                model.RecommendationsCommand.Execute(null);
                await WaitUntilAsync(() => refreshStarted.Task.IsCompleted, clock, window, token);
                await AdvanceAsync(clock, window, 450, token);
                TestAssert.True(!model.IsBrowsePlaceholderVisible && list.IsVisible && skeleton.PlaceholderCount == 0,
                    "Refreshing the same populated target must retain its existing cards without a placeholder flash.");
                refresh.TrySetException(new HttpRequestException("Generated refresh rejection.", null, HttpStatusCode.Forbidden));
                await model.RecommendationBatchUpdate.WaitAsync(token);
                await AdvanceAsync(clock, window, 100, token);
                TestAssert.True(model.CardTracks.Select(card => card.Track.Id).SequenceEqual(ready.Select(track => track.Id))
                    && !model.IsBrowsePlaceholderVisible, "A failed same-target refresh cannot erase a previously successful recommendation snapshot.");

                workspace.RecommendationOverride = (_, _, _) => obsolete.Task;
                model.SelectedEndpointProfile = model.EndpointProfiles.Single(profile => profile.Configuration.Id == second.Id);
                var superseded = model.HomeUpdate;
                await AdvanceAsync(clock, window, 120, token);
                AssertPlaceholder(model, browser, overlay, skeleton, list);
                TestAssert.Equal(0, CountColor(window, browser, false, true), "Another account's incoming placeholder pane cannot reveal the previous account's green artwork.");
                TapMode(window, view, 2);
                for (var frame = 0; frame < 6; frame++)
                {
                    await AdvanceAsync(clock, window, 24, token);
                    TestAssert.Equal(0, CountColor(window, browser, false, true),
                        "The incoming library pane cannot contain the previous recommendation account's covers during a rapid return.");
                }

                await AdvanceAsync(clock, window, 450, token);
                await model.HomeUpdate.WaitAsync(token);
                obsolete.TrySetResult([Track("late-other-owner", greenPath, second)]);
                await superseded.WaitAsync(token);
                await AdvanceAsync(clock, window, 450, token);
                TestAssert.True(model.IsLibraryMode && !model.IsBrowsePlaceholderVisible && browser.ActiveList.IsVisible && skeleton.PlaceholderCount == 0,
                    "An old recommendation request finishing after navigation cannot restore its placeholders or replace the library.");
                TestAssert.Equal(0, CountColor(window, browser, false), "A settled library viewport cannot retain obsolete recommendation artwork.");
            }
            finally
            {
                pending.TrySetResult([]);
                obsolete.TrySetResult([]);
                refresh.TrySetResult([]);
                window.Close();
                await model.DisposeAsync();
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("MajMusic-placeholder-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated placeholder fixture.");
                }

                Directory.Delete(root, true);
            }
        }

        /// <summary>Checks the actual full-viewport placeholder surface independently of pending network work.</summary>
        /// <param name="model">The current shared presentation.</param>
        /// <param name="browser">The fixed browsing rectangle.</param>
        /// <param name="overlay">The complete incoming placeholder pane inside the translated card host.</param>
        /// <param name="skeleton">The bounded placeholder panel.</param>
        /// <param name="list">The old interactive card tree.</param>
        private static void AssertPlaceholder(MainViewModel model, BrowseTracksView browser, Grid overlay, BrowseSkeletonView skeleton, ListBox list)
        {
            TestAssert.True(model.IsBrowsePlaceholderVisible && overlay.IsEffectivelyVisible && browser.FindControl<Grid>("BrowseSurface")!.Background is not null
                && skeleton.PlaceholderCount > 0 && !list.IsVisible && overlay.RenderTransform is null,
                "An incomplete target must retain a complete placeholder pane on an opaque incoming surface while its old interactive cards remain hidden.");
            TestAssert.True(Math.Abs(overlay.Bounds.Width - browser.Bounds.Width) < 1 && Math.Abs(overlay.Bounds.Height - browser.Bounds.Height) < 1,
                "The placeholder must cover the entire existing browser viewport rather than only its first columns.");
        }

        /// <summary>Counts distinctive old or ready artwork in the actual composited browsing viewport.</summary>
        /// <param name="window">The real rendering owner.</param>
        /// <param name="browser">The browser region to inspect.</param>
        /// <param name="red">Whether to detect obsolete red covers rather than ready green covers.</param>
        /// <param name="incomingOnly">Whether to inspect only the actual visible incoming pane, allowing the outgoing page to slide away normally.</param>
        /// <returns>The number of matching pixels inside the browser.</returns>
        private static int CountColor(Window window, BrowseTracksView browser, bool red, bool incomingOnly = false)
        {
            using var frame = window.CaptureRenderedFrame()!;
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            using var pixels = SKBitmap.Decode(stream.ToArray());
            var origin = browser.TranslatePoint(default, window)!.Value;
            var scale = window.RenderScaling;
            var offset = incomingOnly ? browser.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!.IncomingOffset : 0;
            var left = Math.Max(origin.X, origin.X + offset);
            var right = Math.Min(origin.X + browser.Bounds.Width, origin.X + browser.Bounds.Width + offset);
            var count = 0;
            for (var y = (int)Math.Ceiling(origin.Y * scale); y < (int)Math.Floor((origin.Y + browser.Bounds.Height) * scale); y++)
            {
                for (var x = (int)Math.Ceiling(left * scale); x < (int)Math.Floor(right * scale); x++)
                {
                    var color = pixels.GetPixel(x, y);
                    if (red ? color.Red > 180 && color.Green < 100 && color.Blue < 100 : color.Green > 150 && color.Red < 100 && color.Blue < 140)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>Creates complete synthetic song metadata with generated artwork and no native playback.</summary>
        /// <param name="id">The deterministic entry identity.</param>
        /// <param name="path">The generated image path.</param>
        /// <param name="endpoint">The optional source owner.</param>
        /// <returns>The complete fixture song.</returns>
        private static MusicTrack Track(string id, string path, EndpointConfiguration? endpoint = null)
        {
            return new MusicTrack
            {
                Id = id,
                Title = id,
                SourceId = endpoint?.Id ?? "local",
                EndpointId = endpoint?.Id,
                RemoteId = endpoint is null ? null : id,
                Audio = new MediaResource(),
                Cover = new MediaResource { LocalPath = path },
                Duration = TimeSpan.FromMinutes(3),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000 }
            };
        }

        /// <summary>Creates a synthetic selected account without reading any credentials.</summary>
        /// <param name="id">The source slot identity.</param>
        /// <param name="username">The synthetic session label.</param>
        /// <returns>The saved fake endpoint.</returns>
        private static EndpointConfiguration Endpoint(string id, string username)
        {
            return new EndpointConfiguration
            {
                Id = id,
                Name = id,
                BaseUri = new Uri("https://" + id + ".example.invalid/api/"),
                Accounts = [new SavedEndpointAccount { Id = id + "-account", Username = username }],
                SelectedAccountId = id + "-account"
            };
        }

        /// <summary>Creates bounded real artwork for pixel-level old-content detection.</summary>
        /// <param name="path">The generated image destination.</param>
        /// <param name="color">The opaque fixture color.</param>
        private static void WriteCover(string path, SKColor color)
        {
            using var bitmap = new SKBitmap(64, 64);
            bitmap.Erase(color);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }

        /// <summary>Selects a real segmented mode option using pointer input.</summary>
        /// <param name="window">The input owner.</param>
        /// <param name="view">The shared desktop shell.</param>
        /// <param name="index">The target mode index.</param>
        private static void TapMode(Window window, MainView view, int index)
        {
            var selector = view.FindControl<SegmentedControl>("HomeModeSegments")!;
            var option = (Control)selector.ContainerFromIndex(index)!;
            var point = option.TranslatePoint(new Point(option.Bounds.Width / 2, option.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        /// <summary>Waits for a controlled operation while continuing real layout and motion.</summary>
        /// <param name="ready">The operation boundary to observe.</param>
        /// <param name="clock">The production Arc clock.</param>
        /// <param name="window">The current rendering owner.</param>
        /// <param name="token">Cancels a stalled fixture.</param>
        /// <returns>A task representing the completed wait.</returns>
        private static async Task WaitUntilAsync(Func<bool> ready, ManualFrameClock clock, Window window, CancellationToken token)
        {
            while (!ready())
            {
                await AdvanceAsync(clock, window, 24, token);
            }
        }

        /// <summary>Advances Arc and the actual compositor without using an implementation-specific timer.</summary>
        /// <param name="clock">The production Arc clock.</param>
        /// <param name="window">The rendering owner.</param>
        /// <param name="milliseconds">The observation period.</param>
        /// <param name="token">Cancels a stalled fixture.</param>
        /// <returns>A task representing all rendered frames.</returns>
        private static async Task AdvanceAsync(ManualFrameClock clock, Window window, int milliseconds, CancellationToken token)
        {
            for (var elapsed = 0; elapsed < milliseconds; elapsed += 12)
            {
                clock.Advance(TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(4));
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(1, token);
            }
        }

        /// <summary>Saves a real intermediate frame under the ignored work directory.</summary>
        /// <param name="window">The current rendering owner.</param>
        /// <param name="name">The generated screenshot filename.</param>
        private static void Save(Window window, string name)
        {
            var output = Path.Combine(Environment.CurrentDirectory, ".work");
            Directory.CreateDirectory(output);
            using var frame = window.CaptureRenderedFrame()!;
            frame.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
        }
    }
}
