#nullable enable

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
using TKCode.Arc;
using TKCode.Arc.Motion;

namespace MajMusic.Tests
{
    /// <summary>Renders immediate target placeholders while a real home request is deliberately held open.</summary>
    internal static class BrowseLoadingPresentationTests
    {
        /// <summary>Exercises bounded placeholders, obsolete requests, real card publication and lazy keyboard navigation.</summary>
        /// <returns>A task representing the complete rendered loading regression.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await session.Dispatch<bool>(async () =>
            {
                var clock = new ManualFrameClock();
                using var installed = clock.Install();
                var first = Endpoint("loading-a", "Alice");
                var second = Endpoint("loading-b", "Bob");
                var workspace = new LibraryExperienceTests.ExperienceWorkspace
                {
                    Settings = new AppSettings { ActiveEndpointId = first.Id, Endpoints = [first, second] }
                };
                foreach (var endpoint in new[] { first, second })
                {
                    workspace.Sessions[endpoint.Id] = new EndpointSession
                    {
                        EndpointId = endpoint.Id,
                        Username = endpoint.Accounts[0].Username,
                        IsLoggedIn = true
                    };
                }

                for (var index = 0; index < 24000; index++)
                {
                    workspace.Library.Add(Track(index));
                }

                workspace.LocalPlaylists["phone-loading"] = new MusicPlaylist
                {
                    Id = "phone-loading",
                    Name = "Phone loading target",
                    TrackIds = ["loading-track-00000", "loading-track-00001"]
                };

                await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
                await model.InitializeAsync();
                model.ShowLibraryModeCommand.Execute(null);
                await model.HomeUpdate;
                var view = new MainView { DataContext = model };
                var window = new Window { Content = view, Width = 1100, Height = 760 };
                var firstRelease = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var secondRelease = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var phoneRelease = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
                workspace.RecommendationOverride = (endpoint, _, _) =>
                {
                    if (endpoint.Id == first.Id)
                    {
                        firstStarted.TrySetResult();
                        return firstRelease.Task;
                    }

                    secondStarted.TrySetResult();
                    return secondRelease.Task;
                };
                try
                {
                    window.Show();
                    await AdvanceAsync(clock, window, 450, timeout.Token);
                    var browser = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
                    var list = browser.FindControl<ListBox>("CardRowsList")!;
                    var surface = browser.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!;
                    var skeleton = browser.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!;
                    TestAssert.True(model.MaterializedCardCount > 0 && model.MaterializedCardCount < 100,
                        "A 24,000-song library must materialize only viewport presentations.");
                    var original = Capture(window, "ui-browse-loading-library.png");
                    var stableBounds = browser.Bounds;
                    model.OnlineEnabled = true;
                    await model.AutomaticLogin;
                    var transition = model.BrowseTransitionVersion;
                    TapMode(window, view, 0);
                    var obsolete = model.HomeUpdate;
                    TestAssert.True(model.IsBrowseLoading && model.IsRecommendationsMode && model.BrowseTransitionVersion > transition,
                        "The actual mode click must announce its target and loading presentation before awaiting the network.");
                    TestAssert.True(!list.IsVisible && !browser.FindControl<Grid>("BrowseContentPresentation")!.IsEnabled,
                        "Old songs must immediately stop accepting interaction while the target loads.");
                    await firstStarted.Task.WaitAsync(timeout.Token);
                    await AdvanceAsync(clock, window, 110, timeout.Token);
                    AssertSkeleton(browser, skeleton, model.BrowseColumns);
                    TestAssert.True(surface.HasSnapshot && surface.TransitionProgress > 0 && surface.TransitionProgress < 1,
                        "A slow target must already be painting its complete incoming surface while retaining one outgoing snapshot.");
                    var loading = Capture(window, "ui-browse-loading-pending.png");
                    TestAssert.True(!original.SequenceEqual(loading), "The pending request must paint a distinct loading frame instead of retaining the old songs.");
                    TestAssert.Equal(stableBounds, browser.Bounds, "A loading presentation must preserve the browser's reserved rectangle.");

                    TapMode(window, view, 2);
                    await AdvanceAsync(clock, window, 450, timeout.Token);
                    await model.HomeUpdate.WaitAsync(timeout.Token);
                    firstRelease.TrySetResult([Track(24000, first)]);
                    await obsolete.WaitAsync(timeout.Token);
                    await AdvanceAsync(clock, window, 450, timeout.Token);
                    TestAssert.True(model.IsLibraryMode && !model.IsBrowseLoading && list.IsVisible,
                        "A late recommendation response must not return the UI to the abandoned mode or hide the completed catalog.");
                    TestAssert.True(skeleton.PlaceholderCount == 0 && !surface.HasSnapshot,
                        "Completed skeleton pulses and outgoing snapshots must be released after their exit.");

                    model.SelectedEndpointProfile = model.EndpointProfiles.Single(profile => profile.Configuration.Id == second.Id);
                    TapMode(window, view, 0);
                    await secondStarted.Task.WaitAsync(timeout.Token);
                    await AdvanceAsync(clock, window, 400, timeout.Token);
                    AssertSkeleton(browser, skeleton, model.BrowseColumns);
                    Capture(window, "ui-browse-loading-skeleton.png");
                    var content = browser.FindControl<Grid>("BrowseContentPresentation")!;
                    secondRelease.TrySetResult(Enumerable.Range(24001, 50).Select(index => Track(index, second)).ToArray());
                    await model.HomeUpdate.WaitAsync(timeout.Token);
                    Pump(window);
                    TestAssert.True(content.Opacity == 1 && content.RenderTransform is null
                        && skeleton.Opacity == 1 && skeleton.RenderTransform is null,
                        "Completed songs must replace their placeholders immediately without a second fade, scale or vertical entrance.");
                    await AdvanceAsync(clock, window, 420, timeout.Token);
                    TestAssert.True(!model.IsBrowseLoading && model.Tracks.Count == 50 && list.IsVisible
                        && model.MaterializedCardCount < 50 && skeleton.PlaceholderCount == 0,
                        "Completion must replace bounded placeholders with only the first viewport of the current account's songs.");
                    var completed = Capture(window, "ui-browse-loading-completed.png");
                    TestAssert.True(!completed.SequenceEqual(loading), "The loaded card frame must visibly replace its placeholders.");

                    TapMode(window, view, 2);
                    await AdvanceAsync(clock, window, 450, timeout.Token);
                    await model.HomeUpdate.WaitAsync(timeout.Token);
                    var materialized = model.MaterializedCardCount;
                    list.Focus();
                    window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
                    window.KeyRelease(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
                    Pump(window);
                    TestAssert.True(model.GetBrowseCardIndex(model.SelectedBrowseCard) == model.CardTracks.Count - 1
                        && model.MaterializedCardCount < materialized + 50,
                        "A real End key must jump to the final song without instantiating the entire library to find the selection.");
                    var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().Single(control => ReferenceEquals(control.TemplatedParent, list));
                    TestAssert.True(scroll.Offset.Y > 0, "Lazy keyboard navigation must still scroll the selected last card into the viewport.");

                    window.Width = 390;
                    window.Height = 844;
                    await AdvanceAsync(clock, window, 100, timeout.Token);
                    TestAssert.True(skeleton.PlaceholderCount == 0, "A detached desktop browser must retain no placeholder pulse controls.");
                    var phoneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    workspace.SearchOverride = (_, _) =>
                    {
                        phoneStarted.TrySetResult();
                        return phoneRelease.Task;
                    };
                    model.SelectedPlaylist = model.Playlists.Single(item => item.Id == "phone-loading");
                    model.ShowPlaylistsModeCommand.Execute(null);
                    await phoneStarted.Task.WaitAsync(timeout.Token);
                    await AdvanceAsync(clock, window, 400, timeout.Token);
                    var phoneBrowser = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
                    var phoneSkeleton = phoneBrowser.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!;
                    AssertSkeleton(phoneBrowser, phoneSkeleton, 1);
                    Capture(window, "ui-browse-loading-phone.png");
                    window.Width = 1100;
                    window.Height = 760;
                    await AdvanceAsync(clock, window, 400, timeout.Token);
                    TestAssert.True(phoneSkeleton.PlaceholderCount == 0 && model.IsBrowseLoading,
                        "Replacing a loading phone layout must release its pulses while the current request remains pending.");
                    AssertSkeleton(browser, skeleton, model.BrowseColumns);
                    phoneRelease.TrySetResult(workspace.Library.ToArray());
                    await model.HomeUpdate.WaitAsync(timeout.Token);
                    await AdvanceAsync(clock, window, 420, timeout.Token);
                    TestAssert.True(skeleton.PlaceholderCount == 0 && !model.IsBrowseLoading && list.IsVisible,
                        "A request completed after resizing must publish into the current viewport and release its loading controls.");
                }
                finally
                {
                    firstRelease.TrySetResult(Array.Empty<MusicTrack>());
                    secondRelease.TrySetResult(Array.Empty<MusicTrack>());
                    phoneRelease.TrySetResult(Array.Empty<MusicTrack>());
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Checks finite visible placeholders, real Arc text skeletons and square artwork geometry.</summary>
        /// <param name="browser">The currently attached target viewport.</param>
        /// <param name="skeleton">The bounded placeholder panel.</param>
        /// <param name="columns">The same responsive column count as the real card rows.</param>
        private static void AssertSkeleton(BrowseTracksView browser, BrowseSkeletonView skeleton, int columns)
        {
            TestAssert.True(skeleton.IsEffectivelyVisible && skeleton.Columns == columns
                && skeleton.PlaceholderCount >= columns && skeleton.PlaceholderCount < 40,
                "Loading must instantiate only the current viewport's responsive placeholder rows.");
            var cards = skeleton.Children.OfType<SongCardSkeleton>().ToArray();
            var visible = cards.Where(card => card.Bounds.Top < browser.Bounds.Height).ToArray();
            var outline = MusicGeometry.OutlineWidth * 2;
            TestAssert.True(visible.Length >= columns && visible.All(card => Math.Abs((card.Bounds.Width - outline) - (card.Bounds.Height - outline) * 2) <= 1),
                "Every loading music card must reserve the real two-to-one card geometry: "
                + string.Join("; ", cards.Select(card => card.Bounds.ToString())) + "; surface=" + skeleton.Bounds + "; viewport=" + browser.Bounds);
            TestAssert.True(cards.SelectMany(card => card.GetVisualDescendants()).OfType<Skeleton>().Any(),
                "The placeholder must use real Arc skeleton controls.");
            foreach (var card in visible)
            {
                var cover = card.FindControl<Border>("SkeletonCover")!;
                TestAssert.True(Math.Abs(cover.Bounds.Width - cover.Bounds.Height) <= 1,
                    "A loading cover must occupy the same square half as a real song's cover.");
            }
        }

        /// <summary>Creates a synthetic account slot without credentials or real service access.</summary>
        /// <param name="id">The independent account-slot identity.</param>
        /// <param name="username">The synthetic active account name.</param>
        /// <returns>The fixture source configuration.</returns>
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

        /// <summary>Creates fully described local or source-scoped fixture songs without file or metadata requests.</summary>
        /// <param name="index">The deterministic song identity.</param>
        /// <param name="endpoint">The optional remote owner.</param>
        /// <returns>A complete song descriptor.</returns>
        private static MusicTrack Track(int index, EndpointConfiguration? endpoint = null)
        {
            return new MusicTrack
            {
                Id = "loading-track-" + index.ToString("D5"),
                Title = (endpoint is null ? "Local music " : "Recommended music ") + index.ToString("D5"),
                Artist = "Fixture artist",
                SourceId = endpoint?.Id ?? "local",
                EndpointId = endpoint?.Id,
                RemoteId = endpoint is null ? null : index.ToString(),
                Duration = TimeSpan.FromSeconds(180),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 },
                Audio = new MediaResource { LocalPath = "generated-loading-audio" }
            };
        }

        /// <summary>Clicks the production segmented option through actual pointer input.</summary>
        /// <param name="window">The input owner.</param>
        /// <param name="view">The real home presentation.</param>
        /// <param name="index">The target mode option.</param>
        private static void TapMode(Window window, MainView view, int index)
        {
            var selector = view.FindControl<SegmentedControl>("HomeModeSegments")!;
            var option = (Control)selector.ContainerFromIndex(index)!;
            var point = option.TranslatePoint(new Point(option.Bounds.Width / 2, option.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        /// <summary>Advances real rendering while Arc's production clock runs.</summary>
        /// <param name="clock">The deterministic production Arc frame driver.</param>
        /// <param name="window">The rendering window.</param>
        /// <param name="milliseconds">The finite observation period.</param>
        /// <param name="token">Cancels a stalled regression.</param>
        /// <returns>A task representing the rendered frame interval.</returns>
        private static async Task AdvanceAsync(ManualFrameClock clock, Window window, int milliseconds, CancellationToken token)
        {
            for (var elapsed = 0; elapsed < milliseconds; elapsed += 12)
            {
                clock.Advance(TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(4));
                Pump(window);
                await Task.Delay(1, token);
            }

            Pump(window);
        }

        /// <summary>Flushes layout and the actual in-memory renderer.</summary>
        /// <param name="window">The rendering window.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Writes a real Skia frame and returns its encoded bytes for frame comparison.</summary>
        /// <param name="window">The current rendering window.</param>
        /// <param name="name">The ignored screenshot name.</param>
        /// <returns>The exact rendered PNG bytes.</returns>
        private static byte[] Capture(Window window, string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
            bitmap.Render(window);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            var bytes = stream.ToArray();
            var output = Path.Combine(Environment.CurrentDirectory, ".work");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, name), bytes);
            return bytes;
        }
    }
}
