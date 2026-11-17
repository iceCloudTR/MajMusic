#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
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
    /// <summary>Exercises real recommendation-page controls within the existing virtualized scrolling surface.</summary>
    internal static class RecommendationPagingLayoutTests
    {
        /// <summary>Checks trailing pagination, touch layout, page entry and search's library destination.</summary>
        /// <returns>A task representing complete rendering and input regressions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await session.Dispatch<bool>(async () =>
            {
                var clock = new ManualFrameClock();
                using var installed = clock.Install();
                foreach (var scenario in new[] { (Width: 1100, Height: 760, Songs: 150), (Width: 390, Height: 844, Songs: 150),
                    (Width: 1100, Height: 760, Songs: 61), (Width: 1100, Height: 1050, Songs: 51),
                    (Width: 390, Height: 844, Songs: 51), (Width: 390, Height: 844, Songs: 7), (Width: 1100, Height: 760, Songs: 0) })
                {
                    await ValidateAsync(clock, scenario.Width, scenario.Height, scenario.Songs, timeout.Token);
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Uses actual End and pointer gestures to navigate one finite source batch.</summary>
        /// <param name="clock">The production Arc frame driver.</param>
        /// <param name="width">The desktop or touch viewport width.</param>
        /// <param name="height">The viewport height.</param>
        /// <param name="songs">The finite source count, including short and empty batches.</param>
        /// <param name="token">Cancels stalled source or rendering work.</param>
        /// <returns>A task representing the scenario.</returns>
        private static async Task ValidateAsync(ManualFrameClock clock, int width, int height, int songs, CancellationToken token)
        {
            var endpoint = new EndpointConfiguration
            {
                Id = "pager-source",
                Name = "Fixture source",
                BaseUri = new Uri("https://pager.example.invalid/api/"),
                Accounts = [new SavedEndpointAccount { Id = "pager-account", Username = "Fixture" }],
                SelectedAccountId = "pager-account"
            };
            var workspace = new LibraryExperienceTests.ExperienceWorkspace
            {
                Settings = new AppSettings { OnlineEnabled = true, ActiveEndpointId = endpoint.Id, Endpoints = [endpoint] }
            };
            workspace.Sessions[endpoint.Id] = new EndpointSession { EndpointId = endpoint.Id, Username = "Fixture", IsLoggedIn = true };
            var localCount = width == 1100 && songs == 150 ? 24000 : 40;
            for (var index = 0; index < localCount; index++)
            {
                workspace.Library.Add(Track(index, false));
            }

            var recommended = Enumerable.Range(0, songs).Select(index => Track(index, true)).ToArray();
            for (var page = 0; page < 3; page++)
            {
                workspace.Recommendations[(endpoint.Id, page)] = recommended.Skip(page * 50).Take(50).ToArray();
            }

            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
            await model.InitializeAsync();
            model.ShowRecommendationsModeCommand.Execute(null);
            await model.HomeUpdate.WaitAsync(token);
            await model.RecommendationBatchUpdate.WaitAsync(token);
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = width, Height = height };
            try
            {
                window.Show();
                Pump(window);
                var browser = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
                var list = browser.FindControl<ListBox>("CardRowsList")!;
                var surface = browser.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!;
                var viewport = browser.Bounds;
                var requests = workspace.RecommendationRequests.Count;
                TestAssert.True(model.CardTracks.Count == Math.Min(50, songs) && model.RecommendationTotalCount == songs,
                    "The visible page must contain at most fifty entries from the complete finite source batch.");
                if (songs <= 50)
                {
                    TestAssert.True(!model.HasRecommendationPages && list.Items.Count == model.BrowseRows.Count
                        && !browser.GetVisualDescendants().OfType<Grid>().Any(item => item.Name == "RecommendationPagination"),
                        "An empty or single-page batch must have no pagination item or reserved footer space.");
                    Save(window, $"ui-recommendation-pages-{width}-{songs}.png");
                    return;
                }

                TestAssert.True(list.Items.Count == model.BrowseRows.Count + 1 && list.Items[^1] is RecommendationFooterRow
                    && !browser.GetVisualDescendants().OfType<Grid>().Any(item => item.Name == "RecommendationPagination"),
                    "A multipage recommendation footer must be the actual final scrolling item, outside the initial viewport.");
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().Single(item => ReferenceEquals(item.TemplatedParent, list));
                var lane = browser.ContentViewport;
                var cardSize = browser.GetVisualDescendants().OfType<SongCard>().First(card => card.IsEffectivelyVisible).Bounds.Size;
                var footer = ScrollToFooter(window, browser, list);
                AssertStableLane(browser, scroll, lane, cardSize, "The first full page and visible footer");
                TestAssert.True(scroll.Offset.Y > 0 && browser.Bounds == viewport,
                    "Reaching the footer must scroll music content without reducing the browser viewport.");
                Save(window, $"ui-recommendation-pages-{width}-bottom.png");
                Tap(window, PageButton(footer, 2));
                TestAssert.True(model.RecommendationPageNumber == 2 && surface.TransitionProgress == 0 && surface.IncomingOffset > 0,
                    "A real numbered footer click must immediately start the existing forward horizontal page entry.");
                Advance(clock, window, 90);
                TestAssert.Equal(songs + " 首推荐", model.BrowseSummary,
                    "The recommendation batch count must stay stable while a twelve-song warm prefix enters.");
                TestAssert.True(surface.TransitionProgress is > 0 and < 1 && surface.IncomingOffset > 0
                    && list.Items.Cast<object>().All(item => item is not RecommendationFooterRow),
                    "The moving target must not display a footer beneath its temporary twelve-song warm prefix.");
                AssertStableLane(browser, scroll, lane, cardSize, "The entering warm prefix before its footer");
                Save(window, $"ui-recommendation-pages-{width}-middle.png");
                Advance(clock, window, 330);
                await model.HomeUpdate.WaitAsync(token);
                Pump(window);
                TestAssert.True(model.CardTracks[0].Track.Id == "recommendation-050" && model.CardTracks.Count == Math.Min(50, songs - 50)
                    && scroll.Offset.Y <= 1 && browser.Bounds == viewport,
                    "The complete second page must start at its first song and retain the existing viewport geometry.");
                AssertStableLane(browser, scroll, lane, cardSize, "The complete target after its footer is appended");
                if (songs == 51)
                {
                    var vertical = scroll.GetVisualDescendants().OfType<ScrollBar>()
                        .Single(bar => ReferenceEquals(bar.TemplatedParent, scroll) && bar.Orientation == Avalonia.Layout.Orientation.Vertical);
                    TestAssert.True(vertical.IsVisible && vertical.Maximum == 0 && vertical.Opacity == 0 && !vertical.IsHitTestVisible,
                        "A one-song last page must keep its empty scroll lane without painting or activating a useless scrollbar.");
                }
                TestAssert.Equal(requests, workspace.RecommendationRequests.Count, "Selecting a cached recommendation page must send no new source request.");
                footer = ScrollToFooter(window, browser, list);
                Tap(window, PageButton(footer, 1));
                TestAssert.True(surface.IncomingOffset < 0, "Selecting an earlier numbered page must reverse the existing horizontal entry direction.");
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                Pump(window);
                TestAssert.Equal("recommendation-000", model.CardTracks[0].Track.Id, "Backward paging must restore the original ordered first page.");
                footer = ScrollToFooter(window, browser, list);
                var lastPage = model.RecommendationPageCount;
                Tap(window, PageButton(footer, lastPage));
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                Pump(window);
                TestAssert.True(model.CardTracks.Count == songs - (lastPage - 1) * 50
                    && model.CardTracks[0].Track.Id == "recommendation-" + ((lastPage - 1) * 50).ToString("D3"),
                    "A short last page must display only its real remaining songs.");
                footer = ScrollToFooter(window, browser, list);
                Tap(window, PageButton(footer, 1));
                Advance(clock, window, 90);
                var search = width < 680
                    ? view.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "MobileSearchButton")
                    : view.FindControl<Control>("BeginSearchButton")!;
                Tap(window, search);
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                Pump(window);
                TestAssert.True(model.IsSearchExpanded && model.HomeMode == HomeBrowseMode.Library && !model.HasRecommendationPages
                    && list.Items.Count == model.BrowseRows.Count && model.CardTracks.Count == localCount + songs,
                    "Search opened during a page entry must own the library and discard the old recommendation footer and page result.");
                TestAssert.True(model.MaterializedCardCount < 100,
                    "Adding pagination must preserve lazy presentation when search opens a 24,000-song local catalog.");
                TestAssert.Equal(requests, workspace.RecommendationRequests.Count, "Paging and opening indexed search must not fetch another recommendation batch.");
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>Checks real content and card geometry before, during and after recommendation-page publication.</summary>
        /// <param name="browser">The unchanged production browser.</param>
        /// <param name="viewer">The native viewer retaining the reserved lane.</param>
        /// <param name="lane">The measured initial content viewport.</param>
        /// <param name="cardSize">The initial real card size.</param>
        /// <param name="phase">The publication phase being checked.</param>
        private static void AssertStableLane(BrowseTracksView browser, ScrollViewer viewer, Size lane, Size cardSize, string phase)
        {
            var presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>()
                .Single(control => ReferenceEquals(control.TemplatedParent, viewer));
            TestAssert.True(Math.Abs(presenter.Bounds.Width - lane.Width) <= 0.01 && browser.ContentViewport == lane,
                phase + " must retain the same native content lane, including when content starts or stops overflowing.");
            TestAssert.True(browser.GetVisualDescendants().OfType<SongCard>().Where(card => card.IsEffectivelyVisible)
                .All(card => Math.Abs(card.Bounds.Width - cardSize.Width) <= 1 && Math.Abs(card.Bounds.Height - cardSize.Height) <= 1),
                phase + " must preserve card geometry rather than reallocating space when the scrollbar or pager appears.");
        }

        /// <summary>Reaches pagination by real keyboard scrolling and verifies all page actions fit its viewport.</summary>
        /// <param name="window">The input window.</param>
        /// <param name="browser">The production artwork browser.</param>
        /// <param name="list">The unchanged virtualized list.</param>
        /// <returns>The realized trailing pagination surface.</returns>
        private static Grid ScrollToFooter(Window window, BrowseTracksView browser, ListBox list)
        {
            list.Focus();
            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.End, RawInputModifiers.None);
            Pump(window);
            var footer = browser.GetVisualDescendants().OfType<Grid>().Single(item => item.Name == "RecommendationPagination");
            var top = footer.TranslatePoint(default, browser)!.Value;
            TestAssert.True(top.Y >= -1 && top.Y + footer.Bounds.Height <= browser.Bounds.Height + 1,
                $"The realized footer must fit after scrolling to the actual content end: top {top}, footer {footer.Bounds}, viewport {browser.Bounds}.");
            foreach (var button in footer.GetVisualDescendants().OfType<TKCode.Arc.Button>())
            {
                var origin = button.TranslatePoint(default, browser)!.Value;
                TestAssert.True(button.Bounds.Width >= 44 && button.Bounds.Height >= 44 && origin.X >= 0
                    && origin.X + button.Bounds.Width <= browser.Bounds.Width + 1,
                    "Desktop and touch pagination must retain complete forty-four-pixel actions without horizontal overflow.");
            }

            TestAssert.True(window.FocusManager!.GetFocusedElement() is TKCode.Arc.Button,
                "End must scroll to the footer and leave an available numbered action keyboard-focusable.");
            return footer;
        }

        /// <summary>Gets a real numbered action from its page-item binding.</summary>
        /// <param name="footer">The realized trailing pagination surface.</param>
        /// <param name="number">The one-based target page.</param>
        /// <returns>The corresponding native Arc action.</returns>
        private static TKCode.Arc.Button PageButton(Grid footer, int number)
        {
            return footer.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .Single(item => item.DataContext is RecommendationPageItem page && page.Number == number);
        }

        /// <summary>Creates fully described synthetic music without audio or cover requests.</summary>
        /// <param name="index">The deterministic song identity.</param>
        /// <param name="remote">Whether the song belongs to the account batch.</param>
        /// <returns>The local or remote descriptor.</returns>
        private static MusicTrack Track(int index, bool remote)
        {
            return new MusicTrack
            {
                Id = (remote ? "recommendation-" : "local-") + index.ToString("D3"),
                Title = (remote ? "Recommended music " : "Local music ") + index.ToString("D3"),
                SourceId = remote ? "majnet" : "local",
                EndpointId = remote ? "pager-source" : null,
                RemoteId = remote ? index.ToString() : null,
                Audio = new MediaResource { LocalPath = "generated-pager-audio" },
                Duration = TimeSpan.FromMinutes(3),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 }
            };
        }

        /// <summary>Clicks a production page action without invoking its command directly.</summary>
        /// <param name="window">The real input owner.</param>
        /// <param name="control">The realized action.</param>
        private static void Tap(Window window, Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        /// <summary>Advances Arc's real driver and flushes its rendered output.</summary>
        /// <param name="clock">The installed Arc frame source.</param>
        /// <param name="window">The rendering viewport.</param>
        /// <param name="milliseconds">The finite animation interval.</param>
        private static void Advance(ManualFrameClock clock, Window window, int milliseconds)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMilliseconds(5));
            Pump(window);
        }

        /// <summary>Flushes layout, deferred focus and actual compositor frames.</summary>
        /// <param name="window">The current rendering window.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Captures the actual display frame in the ignored validation directory.</summary>
        /// <param name="window">The current rendering window.</param>
        /// <param name="name">The diagnostic image name.</param>
        private static void Save(Window window, string name)
        {
            using var frame = window.CaptureRenderedFrame()!;
            var output = Path.Combine(Environment.CurrentDirectory, ".work");
            Directory.CreateDirectory(output);
            frame.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
        }
    }
}
