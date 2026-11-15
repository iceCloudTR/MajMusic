#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using ArcPage = TKCode.Arc.Mobile.Page;
using ArcSheet = TKCode.Arc.Mobile.Sheet;
using ArcKeyboard = TKCode.Arc.Mobile.KeyboardInsetSource;

namespace MajMusic.Tests
{
    /// <summary>Checks real Arc touch controls and responsive shell ownership using generated local music.</summary>
    internal static class ArcResponsiveLayoutTests
    {
        /// <summary>Renders phone widths, exercises navigation and music actions, and preserves accounts across shell changes.</summary>
        /// <returns>A task representing the responsive Arc interaction checks.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await session.Dispatch<bool>(async () =>
            {
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-arc-responsive-" + Guid.NewGuid().ToString("N")));
                var output = Path.Combine(Environment.CurrentDirectory, ".work");
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(output);
                using var transport = new NoNetworkTransport();
                using var client = new HttpClient(transport);
                using var workspace = new MusicWorkspace(Path.Combine(root, "data"), client, client, credentialStore: new NoCredentials());
                var backend = new HeadlessTests.TimelineBackend();
                var playback = new PlaybackService(backend, resolveAudioPath: (track, token) => workspace.ResolveAudioAsync(track, false, token));
                await using var model = new MainViewModel(workspace, playback);
                var view = new MainView { DataContext = model };
                var window = new Window { Content = view, Width = 390, Height = 844 };
                try
                {
                    await SeedAsync(root, workspace, timeout.Token);
                    await model.InitializeAsync();
                    model.ShowLibraryModeCommand.Execute(null);
                    await model.HomeUpdate;
                    window.Show();
                    Layout(window);
                    foreach (var size in new[] { new Size(360, 640), new Size(390, 844), new Size(430, 932) })
                    {
                        window.Width = size.Width;
                        window.Height = size.Height;
                        Layout(window);
                        TestAssert.True(view.IsMobileLayout, "Phone widths must select the touch presentation of the same workspace.");
                        var mobile = view.GetVisualDescendants().OfType<MobileMusicView>().Single();
                        var page = Find<ArcPage>(mobile, "MobilePage");
                        page.UsePlatformSafeArea = false;
                        page.SafeArea = new Thickness(0, 28, 0, 24);
                        Layout(window);
                        await ValidatePhoneAsync(window, view, mobile, model, playback, backend, output, timeout.Token);
                    }

                    await ValidateShellOwnershipAsync(window, view, model, workspace, transport, timeout.Token);
                    TestAssert.True(!workspace.IsOnline && transport.Requests == 0, "Rendering and touch controls must retain offline permission without remote calls.");
                }
                finally
                {
                    window.Close();
                    await model.DisposeAsync();
                    workspace.Dispose();
                    SqliteConnection.ClearAllPools();
                    var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    TestAssert.True(root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(root).StartsWith("MajMusic-arc-responsive-", StringComparison.Ordinal), "Responsive fixture cleanup must remain under its generated temporary directory.");
                    Directory.Delete(root, true);
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Exercises the actual phone navigation, expanded search, artwork inspector and transport controls.</summary>
        /// <param name="window">The real scaled headless window.</param>
        /// <param name="view">The responsive shell.</param>
        /// <param name="mobile">The active Arc mobile presentation.</param>
        /// <param name="model">The unchanged shared music presentation owner.</param>
        /// <param name="playback">The authoritative queue and loaded-track owner.</param>
        /// <param name="backend">The controlled audio clock observing actual seek commits.</param>
        /// <param name="output">The ignored diagnostic image directory.</param>
        /// <param name="token">Cancels a missing interaction result.</param>
        /// <returns>A task representing the phone workflow checks.</returns>
        private static async Task ValidatePhoneAsync(Window window, MainView view, MobileMusicView mobile,
            MainViewModel model, PlaybackService playback, HeadlessTests.TimelineBackend backend, string output, CancellationToken token)
        {
            var navigation = new[] { "MobileHomeNav", "MobilePlaylistsNav", "MobileDirectoriesNav", "MobileAccountsNav", "MobileSettingsNav" };
            var navbar = Find<Control>(mobile, "MobileNavbar");
            var tabbar = Find<Control>(mobile, "MobileNavigationBar");
            TestAssert.True(navbar.TranslatePoint(default, window)!.Value.Y >= 28 - 1
                && tabbar.TranslatePoint(new Point(0, tabbar.Bounds.Height), window)!.Value.Y <= window.ClientSize.Height - 24 + 1,
                "The phone header and navigation must keep the configured top and bottom safe areas clear.");
            for (var index = 0; index < navigation.Length; index++)
            {
                var item = Find<Control>(mobile, navigation[index]);
                AssertTouchTarget(item, window);
                Tap(window, item);
                await WaitAsync(() => view.SelectedSectionIndex == index, token);
                Layout(window);
                AssertHorizontalBounds(mobile, window);
                SaveFrame(window, Path.Combine(output, $"ui-arc-phone-{window.Width:0}-{index}.png"));
            }

            Tap(window, Find<Control>(mobile, "MobileHomeNav"));
            await WaitAsync(() => view.SelectedSectionIndex == 0, token);
            Layout(window);
            var search = Find<Control>(mobile, "MobileSearchButton");
            AssertTouchTarget(search, window);
            Tap(window, search);
            await model.HomeUpdate;
            Layout(window);
            var input = Find<TextBox>(mobile, "MobileSearchBox");
            TestAssert.True(model.IsSearchExpanded && input.IsFocused, "Touch search must expand and focus the real input.");
            input.Text = "Local";
            await model.HomeUpdate;
            Layout(window);
            TestAssert.True(model.IsSearching && model.Tracks.Count > 0 && model.Tracks.All(track => track.Title.Contains("Local", StringComparison.Ordinal)),
                "Phone search must use the same indexed search behavior as desktop.");
            var page = Find<ArcPage>(mobile, "MobilePage");
            var keyboard = new ArcKeyboard();
            ArcPage.SetKeyboardInsetSource(mobile, keyboard);
            keyboard.Set(280, TimeSpan.Zero);
            Layout(window);
            TestAssert.Equal(280d, page.KeyboardInset, "The touch page must observe the host keyboard inset.");
            var inputOrigin = input.TranslatePoint(default, window)!.Value;
            TestAssert.True(inputOrigin.Y >= 28 && inputOrigin.Y + input.Bounds.Height <= window.ClientSize.Height - 280 + 1,
                "The focused search field must stay above the keyboard and below the safe top edge.");
            SaveFrame(window, Path.Combine(output, $"ui-arc-phone-keyboard-{window.Width:0}.png"));
            keyboard.Set(0, TimeSpan.Zero);
            model.CancelSearchCommand.Execute(null);
            await model.HomeUpdate;
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate;
            Layout(window);
            var browser = Find<BrowseTracksView>(mobile, "MobileSongBrowser");
            var list = Find<ListBox>(browser, "CardRowsList");
            var cards = list.GetVisualDescendants().OfType<SongCard>().Where(card => card.IsEffectivelyVisible && card.Bounds.Height > 0).ToArray();
            TestAssert.True(cards.Length > 0 && cards.Length < model.CardTracks.Count, "Phone artwork must remain virtualized instead of constructing the whole library.");
            var card = cards.First(item => item.TranslatePoint(default, list) is { Y: >= 0 } origin && origin.Y + item.Bounds.Height <= list.Bounds.Height + 1);
            Tap(window, card);
            Layout(window);
            var sheet = Find<ArcSheet>(mobile, "MobileTrackSheet");
            TestAssert.True(sheet.IsOpen && model.SelectedBrowseTrack is not null, "Touching a song must select it and open its actual detail sheet.");
            var likedBefore = model.IsSelectedFavorite;
            var like = Find<Control>(window, "MobileDetailLikeButton");
            var play = Find<Control>(window, "MobileDetailPlayButton");
            AssertTouchTarget(like, window);
            AssertTouchTarget(play, window);
            TestAssert.True(play.TranslatePoint(new Point(0, play.Bounds.Height), window)!.Value.Y <= window.ClientSize.Height - 24 + 1,
                "The detail sheet's primary action must remain above the home-indicator safe area.");
            var shuffle = Find<Control>(window, "MobileShuffleButton");
            var repeat = Find<Control>(window, "MobileRepeatButton");
            AssertTouchTarget(shuffle, window);
            AssertTouchTarget(repeat, window);
            var shuffledBefore = model.IsShuffle;
            Tap(window, shuffle);
            await WaitAsync(() => !model.IsBusy && model.IsShuffle != shuffledBefore, token);
            var repeatBefore = model.RepeatModeText;
            Tap(window, repeat);
            await WaitAsync(() => !model.IsBusy && model.RepeatModeText != repeatBefore, token);
            Tap(window, like);
            await WaitAsync(() => !model.IsBusy && model.IsSelectedFavorite != likedBefore, token);
            Tap(window, play);
            await model.TransportUpdate.WaitAsync(token);
            await WaitAsync(() => model.IsPlaying, token);
            SaveFrame(window, Path.Combine(output, $"ui-arc-phone-selected-{window.Width:0}.png"));
            sheet.IsOpen = false;
            Layout(window);
            var toggle = Find<Control>(mobile, "MobilePlayPauseButton");
            AssertTouchTarget(toggle, window);
            AssertTouchTarget(Find<Control>(mobile, "MobilePreviousButton"), window);
            AssertTouchTarget(Find<Control>(mobile, "MobileNextButton"), window);
            Tap(window, toggle);
            await model.TransportUpdate.WaitAsync(token);
            await WaitAsync(() => !model.IsPlaying, token);
            await ValidateSelectedPlaybackAsync(window, mobile, model, playback, token);
            await ValidatePhoneSeekAsync(window, mobile, model, backend, token);
            AssertHorizontalBounds(mobile, window);
        }

        /// <summary>Starts the inspected song explicitly while an earlier queue entry is paused.</summary>
        /// <param name="window">The rendered pointer input target.</param>
        /// <param name="mobile">The active mobile view.</param>
        /// <param name="model">The shared browsing and transport presentation.</param>
        /// <param name="playback">The authoritative loaded resource and queue.</param>
        /// <param name="token">Cancels a missing playback result.</param>
        /// <returns>A task representing paused-A to selected-B playback.</returns>
        private static async Task ValidateSelectedPlaybackAsync(Window window, MobileMusicView mobile, MainViewModel model,
            PlaybackService playback, CancellationToken token)
        {
            var paused = playback.Snapshot.Track!;
            TestAssert.Equal(PlaybackState.Paused, playback.Snapshot.State, "The selection regression must begin with a real paused queue entry.");
            var next = model.CardTracks.First(card => card.Track.Id != paused.Id);
            var row = model.BrowseRows.Single(row => row.Tracks.Contains(next));
            var browser = Find<BrowseTracksView>(mobile, "MobileSongBrowser");
            var list = Find<ListBox>(browser, "CardRowsList");
            list.ScrollIntoView(row);
            Layout(window);
            var target = list.GetVisualDescendants().OfType<SongCard>().Single(card => ReferenceEquals(card.DataContext, next));
            Tap(window, target);
            TestAssert.True(Find<ArcSheet>(mobile, "MobileTrackSheet").IsOpen && model.SelectedBrowseTrack?.Id == next.Track.Id,
                "Selecting another song while paused must inspect that song without resuming the earlier entry.");
            Tap(window, Find<Control>(window, "MobileDetailPlayButton"));
            await model.TransportUpdate.WaitAsync(token);
            await WaitAsync(() => model.IsPlaying, token);
            TestAssert.True(playback.Snapshot.Track?.Id == next.Track.Id && playback.Queue.Current?.Id == next.Track.Id,
                "Play this song must load the inspected B and replace paused A as the actual resource and queue owner.");
            Find<ArcSheet>(mobile, "MobileTrackSheet").IsOpen = false;
            Layout(window);
            Tap(window, Find<Control>(mobile, "MobilePlayPauseButton"));
            await model.TransportUpdate.WaitAsync(token);
            await WaitAsync(() => !model.IsPlaying, token);
        }

        /// <summary>Preserves backend-clock, keyboard preview, pointer preview and cancellation behavior on the actual Arc phone slider.</summary>
        /// <param name="window">The rendered input window.</param>
        /// <param name="mobile">The current phone presentation.</param>
        /// <param name="model">The shared transport owner.</param>
        /// <param name="backend">The controlled native-clock substitute.</param>
        /// <param name="token">Cancels a missing transport result.</param>
        /// <returns>A task representing phone seek input checks.</returns>
        private static async Task ValidatePhoneSeekAsync(Window window, MobileMusicView mobile, MainViewModel model,
            HeadlessTests.TimelineBackend backend, CancellationToken token)
        {
            var slider = Find<TKCode.Arc.Slider>(mobile, "MobilePlaybackTimeline");
            var initialSeeks = backend.SeekCount;
            backend.SetPosition(TimeSpan.FromSeconds(20));
            await WaitAsync(() => model.PositionSeconds == 20, token);
            Layout(window);
            TestAssert.True(slider.IsEnabled && slider.Maximum == 120 && slider.Value == 20 && backend.SeekCount == initialSeeks,
                "The real phone slider must follow the loaded backend clock without creating a seek.");
            ArcTestVisuals.SliderThumb(slider).Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            TestAssert.True(model.IsSeeking && backend.SeekCount == initialSeeks, "Phone keyboard navigation must preview without seeking on key-down.");
            var proposed = slider.Value;
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            await model.TransportUpdate.WaitAsync(token);
            TestAssert.True(backend.SeekCount == initialSeeks + 1 && backend.Snapshot.Position == TimeSpan.FromSeconds(proposed),
                "Phone key release must commit exactly one preview value.");
            var start = slider.TranslatePoint(new Point(slider.Bounds.Width * 0.4, slider.Bounds.Height / 2), window)!.Value;
            var end = slider.TranslatePoint(new Point(slider.Bounds.Width * 0.7, slider.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            TestAssert.True(model.IsSeeking && backend.SeekCount == initialSeeks + 1, "Phone pointer motion must not flood the audio owner with seeks.");
            proposed = slider.Value;
            window.MouseUp(end, MouseButton.Left);
            await model.TransportUpdate.WaitAsync(token);
            TestAssert.True(backend.SeekCount == initialSeeks + 2 && backend.Snapshot.Position == TimeSpan.FromSeconds(proposed),
                "Phone pointer release must commit the displayed preview exactly once.");
            ArcTestVisuals.SliderThumb(slider).Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Layout(window);
            TestAssert.True(!model.IsSeeking && backend.SeekCount == initialSeeks + 2, "Escape must cancel the phone's pending keyboard seek.");
        }

        /// <summary>Keeps account and Guest choices stable while controls detach and reattach across responsive breakpoints.</summary>
        /// <param name="window">The shared rendering window.</param>
        /// <param name="view">The responsive view host.</param>
        /// <param name="model">The shared account editor owner.</param>
        /// <param name="workspace">The real local preference workspace.</param>
        /// <param name="transport">The isolated transport counting accidental authentication.</param>
        /// <param name="token">Cancels an incomplete shell transition.</param>
        /// <returns>A task representing detach and reattach ownership checks.</returns>
        private static async Task ValidateShellOwnershipAsync(Window window, MainView view, MainViewModel model,
            MusicWorkspace workspace, NoNetworkTransport transport, CancellationToken token)
        {
            foreach (var account in new string?[] { "render-account", null })
            {
                var endpoint = (await workspace.LoadSettingsAsync(token)).Endpoints.Single();
                await workspace.SelectAccountAsync(endpoint, account, token);
                await model.InitializeAsync();
                foreach (var width in new[] { 1100d, 390d, 1100d, 430d, 720d })
                {
                    window.Width = width;
                    window.Height = width < 680 ? 844 : 760;
                    view.NavigateToSection(3);
                    Layout(window);
                    await WaitAsync(() => model.EndpointCards.All(card => card.IsIdle), token);
                    TestAssert.Equal(account, (await workspace.LoadSettingsAsync(token)).Endpoints.Single().SelectedAccountId,
                        "Responsive account pickers cannot replace the saved account or Guest choice while their templates mount.");
                    TestAssert.True(view.IsMobileLayout == (width < 680), "Only the viewport chooses the active presentation.");
                }
            }

            TestAssert.Equal(0, transport.Requests, "Repeated shell transitions must not authenticate or synchronize implicitly.");
        }

        /// <summary>Seeds generated local files, metadata and a remembered nonsecret account choice.</summary>
        /// <param name="root">The explicit generated fixture directory.</param>
        /// <param name="workspace">The real local workspace.</param>
        /// <param name="token">Cancels generated local writes.</param>
        /// <returns>A task representing fixture setup.</returns>
        private static async Task SeedAsync(string root, MusicWorkspace workspace, CancellationToken token)
        {
            var settings = await workspace.LoadSettingsAsync(token);
            await workspace.SaveSettingsAsync(settings with
            {
                Endpoints = [new EndpointConfiguration
                {
                    Id = "render-endpoint", Name = "Render source", BaseUri = new Uri("https://render.example.invalid/api/"),
                    Accounts = [new SavedEndpointAccount { Id = "render-account", Username = "Render account" }], SelectedAccountId = "render-account"
                }]
            }, token);
            var audio = Path.Combine(root, "generated-audio.mp3");
            await File.WriteAllTextAsync(audio, "Generated playback fixture", token);
            var cover = Path.Combine(root, "generated-cover.png");
            using (var bitmap = new SKBitmap(128, 128))
            {
                bitmap.Erase(new SKColor(46, 124, 146));
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(cover, data.ToArray(), token);
            }

            var library = new SqliteLibraryRepository(Path.Combine(workspace.DataDirectory, "library.db"));
            await library.UpsertTracksAsync(Enumerable.Range(1, 80).Select(index => new MusicTrack
            {
                Id = "phone-" + index,
                Title = index % 2 == 0 ? "Local · 星の旅 " + index : "Local · A Journey Beyond the Horizon " + index,
                Artist = "Generated artist",
                Audio = new MediaResource { LocalPath = audio },
                Cover = new MediaResource { LocalPath = cover },
                Duration = TimeSpan.FromMinutes(2),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 }
            }).ToArray(), token);
        }

        /// <summary>Finds a named realized control across template and overlay name scopes.</summary>
        /// <typeparam name="T">The expected control type.</typeparam>
        /// <param name="root">The active visual tree root.</param>
        /// <param name="name">The stable application control name.</param>
        /// <returns>The single realized named control.</returns>
        private static T Find<T>(Control root, string name) where T : Control
        {
            return root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        }

        /// <summary>Checks a visible phone action has a complete minimum-size touch region.</summary>
        /// <param name="control">The actual pointer target.</param>
        /// <param name="window">The common rendering and hit-test owner.</param>
        private static void AssertTouchTarget(Control control, Window window)
        {
            var origin = control.TranslatePoint(default, window)!.Value;
            TestAssert.True(control.IsEffectivelyVisible && control.Bounds.Width >= 44 && control.Bounds.Height >= 44
                && origin.X >= -1 && origin.Y >= -1 && origin.X + control.Bounds.Width <= window.ClientSize.Width + 1
                && origin.Y + control.Bounds.Height <= window.ClientSize.Height + 1, "A phone action must expose a visible 44-DIP touch target: " + control.Name);
        }

        /// <summary>Checks effective controls remain inside the phone width without a horizontal scroller.</summary>
        /// <param name="mobile">The active phone visual tree.</param>
        /// <param name="window">The window defining the usable width.</param>
        private static void AssertHorizontalBounds(MobileMusicView mobile, Window window)
        {
            foreach (var control in mobile.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible
                && control.Bounds.Width > 0 && control is Button or TextBox or TKCode.Arc.Select or TKCode.Arc.Mobile.Picker))
            {
                var origin = control.TranslatePoint(default, window)!.Value;
                TestAssert.True(origin.X >= -1 && origin.X + control.Bounds.Width <= window.ClientSize.Width + 1,
                    "A phone input or action must remain within the viewport width: " + (control.Name ?? control.GetType().Name));
            }

            foreach (var card in mobile.GetVisualDescendants().OfType<SongCard>().Where(card => card.IsEffectivelyVisible && card.Bounds.Width > 0))
            {
                var origin = card.TranslatePoint(default, window)!.Value;
                TestAssert.True(origin.X >= -1 && origin.X + card.Bounds.Width <= window.ClientSize.Width + 1,
                    $"A complete phone song card must fit the viewport: {origin.X:0.##}+{card.Bounds.Width:0.##} within {window.ClientSize.Width:0.##}.");
                var pane = card.FindControl<Grid>("CardInformationPane")!;
                var paneOrigin = pane.TranslatePoint(default, card)!.Value;
                TestAssert.True(Math.Abs(paneOrigin.X - card.Bounds.Width / 2) <= 2
                    && Math.Abs(pane.Bounds.Width - card.Bounds.Width / 2) <= 2
                    && paneOrigin.X + pane.Bounds.Width <= card.Bounds.Width + 1,
                    $"Artwork and metadata must fill equal complete card halves: card={card.Bounds}, information={paneOrigin}/{pane.Bounds}.");
                foreach (var text in pane.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)))
                {
                    var textOrigin = text.TranslatePoint(default, pane)!.Value;
                    TestAssert.True(textOrigin.X >= -1 && textOrigin.X + text.Bounds.Width <= pane.Bounds.Width + 1,
                        $"Song title and metadata must fit the phone information panel: {text.Text} at {textOrigin.X:0.##}+{text.Bounds.Width:0.##} within {pane.Bounds.Width:0.##}.");
                }
            }
        }

        /// <summary>Sends an actual left-button tap to a realized control's visual center.</summary>
        /// <param name="window">The headless input target.</param>
        /// <param name="control">The rendered control being exercised.</param>
        private static void Tap(Window window, Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Layout(window);
        }

        /// <summary>Runs queued bindings and real layout before assertions and input.</summary>
        /// <param name="window">The rendered window.</param>
        private static void Layout(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Waits for observed asynchronous user-action state while continuing the UI dispatcher.</summary>
        /// <param name="condition">The required visible result.</param>
        /// <param name="token">Cancels a missing result.</param>
        /// <returns>A task representing the observed result.</returns>
        private static async Task WaitAsync(Func<bool> condition, CancellationToken token)
        {
            while (!condition())
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5, token);
            }

            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Saves an actual rendered Arc frame for independent visual review.</summary>
        /// <param name="window">The rendered window.</param>
        /// <param name="path">The ignored diagnostic image destination.</param>
        private static void SaveFrame(Window window, string path)
        {
            Layout(window);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Round(window.Width * window.RenderScaling), (int)Math.Round(window.Height * window.RenderScaling)),
                new Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
            bitmap.Render(window);
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
        }

        /// <summary>Rejects accidental network traffic from local responsive rendering and input checks.</summary>
        private sealed class NoNetworkTransport : HttpMessageHandler
        {
            /// <summary>Gets the number of attempted requests.</summary>
            public int Requests { get; private set; }
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                throw new InvalidOperationException("Responsive local fixtures cannot contact a server.");
            }
        }

        /// <summary>Rejects accidental operating-system credential reads during visual account restoration.</summary>
        private sealed class NoCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Responsive fixtures cannot read credentials.");
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Responsive fixtures cannot store credentials.");
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Responsive fixtures cannot remove credentials.");
            }
        }
    }
}
