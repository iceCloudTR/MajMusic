#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using MajMusic.Presentation.Theme;
using TKCode.Arc.Motion;
using ArcSlider = TKCode.Arc.Slider;
using ArcSelect = TKCode.Arc.Select;
using ArcSwitch = TKCode.Arc.Switch;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MajMusic.Tests
{
    /// <summary>Checks the actual shared view and bindings without opening a desktop window.</summary>
    internal static class HeadlessTests
    {
        /// <summary>Renders home modes, imported music and the account, directory and audio pages.</summary>
        /// <returns>A task representing the headless smoke checks.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "MajMusic-headless-" + Guid.NewGuid().ToString("N"));
                var output = Path.Combine(Environment.CurrentDirectory, ".work");
                Directory.CreateDirectory(output);
                using var visualSource = new VisualSourceTransport();
                using var visualClient = new HttpClient(visualSource);
                using var workspace = new MusicWorkspace(Path.Combine(root, "data"), visualClient, visualClient);
                var playback = new PlaybackService(new UnavailablePlaybackBackend());
                await using var viewModel = new MainViewModel(workspace, playback, collections: new MajCollectionExchange(workspace));
                var view = new MainView { DataContext = viewModel };
                var window = new Window { Content = view, Width = 1100, Height = 760, Title = "MajMusic" };
                try
                {
                    await viewModel.InitializeAsync();
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    TestAssert.True(!workspace.IsOnline, "Rendering startup must leave the application offline.");
                    TestAssert.True(!viewModel.PlayCommand.CanExecute(null), "An unavailable backend must not offer playback.");
                    TestAssert.True(!view.FindControl<ArcSlider>("PlaybackTimeline")!.IsEnabled,
                        "An unavailable backend must keep the real timeline disabled.");
                    SaveFrame(window, Path.Combine(output, "ui-empty.png"));

                    var source = Path.Combine(root, "source");
                    Directory.CreateDirectory(source);
                    await File.WriteAllBytesAsync(Path.Combine(source, "track.mp3"), "managed import fixture"u8.ToArray(), timeout.Token);
                    await File.WriteAllTextAsync(Path.Combine(source, "maidata.txt"), "&title=Local Music\n&artist=Local Artist", timeout.Token);
                    using (var fixture = new SKBitmap(128, 128))
                    {
                        fixture.Erase(SKColors.CornflowerBlue);
                        using var encoded = fixture.Encode(SKEncodedImageFormat.Png, 100);
                        await using var destination = File.Create(Path.Combine(source, "bg.png"));
                        encoded.SaveTo(destination);
                    }

                    await viewModel.ImportDirectoryAsync(source);
                    await viewModel.LoadCoverAsync();
                    Dispatcher.UIThread.RunJobs();
                    TestAssert.Equal(1, viewModel.Tracks.Count, "An imported track must be visible in the actual bound list.");
                    TestAssert.Equal("Local Music", viewModel.SelectedTitle, "Selection must reflect the imported music.");
                    TestAssert.True(viewModel.Cover is not null, "An imported cover must decode in the actual shared view.");
                    TestAssert.Equal(Path.Combine(source, "track.mp3"), viewModel.Tracks[0].Audio.LocalPath,
                        "The shared folder workflow must reference the original audio file.");
                    TestAssert.True(!Directory.Exists(Path.Combine(workspace.DataDirectory, "Library")),
                        "The shared folder workflow must not create audio copies.");
                    SaveFrame(window, Path.Combine(output, "ui-local-library.png"));
                    await ValidatePlaylistEditingAsync(window, view, viewModel, workspace, timeout.Token);

                    var repository = new SqliteLibraryRepository(Path.Combine(workspace.DataDirectory, "library.db"));
                    var titles = new[] { "Bio-Engineering", "new world", "Walk Away (Disphing Remix)", "星の旅", "After the rain" };
                    await repository.UpsertTracksAsync(Enumerable.Range(1, 149).Select(index => new MusicTrack
                    {
                        Id = "visual-" + index,
                        Title = titles[index % titles.Length] + " · " + index.ToString("00"),
                        Artist = index % 2 == 0 ? "RAINWORLD" : "Generated visual fixture",
                        RemoteId = "visual-" + index,
                        SourceId = "visual-server",
                        EndpointId = "guest-a",
                        SourceName = "MajNet",
                        Revision = "visual-fixture",
                        Duration = TimeSpan.FromSeconds(125 + index),
                        AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 },
                        Audio = new MediaResource { LocalPath = Path.Combine(source, "track.mp3") }
                    }).ToArray(), timeout.Token);
                    viewModel.ShowLibraryModeCommand.Execute(null);
                    await viewModel.HomeUpdate;
                    await viewModel.MetadataRefresh;
                    SaveFrame(window, Path.Combine(output, "ui-library-cards-dark.png"));
                    var trackList = view.GetVisualDescendants().OfType<BrowseTracksView>().Single().ActiveList;
                    var visibleCards = trackList.GetVisualDescendants().OfType<SongCard>().Where(item =>
                    {
                        var position = item.TranslatePoint(default, trackList);
                        return position is { } point && point.Y >= 0 && point.Y + item.Bounds.Height <= trackList.Bounds.Height;
                    }).ToArray();
                    TestAssert.True(visibleCards.Length >= viewModel.BrowseColumns && visibleCards.All(card => !card.IsCompact
                        && Math.Abs(card.Bounds.Width - 2 * card.Bounds.Height) < 2),
                        "The indexed library must use the same complete artwork cards as recommendations.");
                    TestAssert.True(trackList.GetVisualDescendants().OfType<SongCard>().Count() < viewModel.CardTracks.Count,
                        "The complete library must retain row virtualization rather than realizing every song.");
                    TestAssert.True(view.FindControl<Border>("PlaybackBar")!.Bounds.Height <= 115,
                        "The shared playback bar must leave room for the library.");
                    var timeline = view.FindControl<ArcSlider>("PlaybackTimeline")!;
                    var thumb = ArcTestVisuals.SliderThumb(timeline);
                    var thumbPosition = thumb.TranslatePoint(default, timeline);
                    TestAssert.True(thumbPosition is { Y: >= 0 } position && position.Y + thumb.Bounds.Height <= timeline.Bounds.Height,
                        $"The timeline thumb must fit its allocated height: {thumbPosition?.Y} + {thumb.Bounds.Height} within {timeline.Bounds.Height}.");
                    using (var scaled = new RenderTargetBitmap(new PixelSize(1375, 950), new Vector(120, 120)))
                    {
                        scaled.Render(window);
                        scaled.Save(Path.Combine(output, "ui-library-125.png"), PngBitmapEncoderOptions.Default);
                    }

                    window.RequestedThemeVariant = ThemeVariant.Light;
                    SaveFrame(window, Path.Combine(output, "ui-library-light.png"));
                    window.RequestedThemeVariant = ThemeVariant.Dark;

                    window.Width = 720;
                    window.Height = 640;
                    SaveFrame(window, Path.Combine(output, "ui-compact.png"));
                    window.Width = 1100;
                    window.Height = 760;

                    var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
                    tabs.SelectedItem = view.FindControl<TabItem>("DirectoriesTab");
                    Dispatcher.UIThread.RunJobs();
                    TestAssert.Equal(LocalDirectoryKind.MusicLibrary, viewModel.DirectorySources.Single().Kind,
                        "The existing music import must appear as an explicit music source.");
                    TestAssert.Equal(2, viewModel.DirectoryKinds.Count, "The directory editor must offer music and collection-index roles separately.");
                    SaveFrame(window, Path.Combine(output, "ui-directory-sources.png"));
                    tabs.SelectedItem = view.FindControl<TabItem>("SettingsTab");
                    Dispatcher.UIThread.RunJobs();
                    TestAssert.True(!view.GetVisualDescendants().OfType<Expander>().Any(),
                        "Settings must not contain a nested account expander.");
                    SaveFrame(window, Path.Combine(output, "ui-settings.png"));
                    tabs.SelectedItem = view.FindControl<TabItem>("AccountsTab");
                    Dispatcher.UIThread.RunJobs();
                    var permission = view.FindControl<ArcSwitch>("OnlineModeToggle")!;
                    TestAssert.True(permission.IsChecked != true, "Online permission must default to disabled.");
                    var preferences = await workspace.LoadSettingsAsync(timeout.Token);
                    await workspace.SaveSettingsAsync(preferences with
                    {
                        Endpoints =
                        [
                            new EndpointConfiguration { Id = "guest-a", Name = "Maj · A", BaseUri = new Uri("https://example.invalid/api/") },
                            new EndpointConfiguration { Id = "guest-b", Name = "Maj · B", BaseUri = new Uri("https://example.invalid/api/") }
                        ],
                        ActiveEndpointId = "guest-a"
                    }, timeout.Token);
                    var primary = (await workspace.LoadSettingsAsync(timeout.Token)).Endpoints.First();
                    await workspace.SaveAccountAsync(primary, "Render A", null, false, false, timeout.Token);
                    await workspace.SaveAccountAsync(primary, "Render B", null, false, false, timeout.Token);
                    await viewModel.InitializeAsync();
                    Dispatcher.UIThread.RunJobs();
                    TestAssert.Equal(2, viewModel.EndpointProfiles.Count, "The source editor must show multiple independent configurations at the same address.");
                    TestAssert.True(!viewModel.LoginCommand.CanExecute(null), "Source rendering cannot enable a login without online permission or credentials.");
                    var primaryCard = view.GetVisualDescendants().OfType<EndpointCardView>()
                        .Single(card => ((EndpointCardViewModel)card.DataContext!).Id == "guest-a");
                    var accountCard = (EndpointCardViewModel)primaryCard.DataContext!;
                    var accountPicker = primaryCard.FindControl<ArcSelect>("SavedAccountPicker")!;
                    TestAssert.Equal("Render B", accountCard.SelectedAccount?.DisplayName,
                        "The actual selector must restore the configured account without logging in.");
                    accountPicker.SelectedIndex = 0;
                    Dispatcher.UIThread.RunJobs();
                    while (!accountCard.IsIdle)
                    {
                        await Task.Delay(10, timeout.Token);
                    }

                    TestAssert.True((await workspace.LoadSettingsAsync(timeout.Token)).Endpoints.First().SelectedAccountId is null,
                        "Selecting Guest through the actual control must persist that choice.");
                    accountPicker.SelectedIndex = 1;
                    Dispatcher.UIThread.RunJobs();
                    while (!accountCard.IsIdle)
                    {
                        await Task.Delay(10, timeout.Token);
                    }

                    TestAssert.Equal("Render A", accountCard.SelectedAccount?.DisplayName,
                        "Selecting a saved account through the actual control must update the editor.");
                    primaryCard.FindControl<CheckBox>("AutoLoginToggle")!.IsChecked = true;
                    Dispatcher.UIThread.RunJobs();
                    TestAssert.True(accountCard.AutoLogin && accountCard.RememberPassword
                        && primaryCard.FindControl<CheckBox>("RememberPasswordToggle")!.IsChecked == true,
                        "Checking automatic login through the actual control must enable protected password retention.");
                    primaryCard.FindControl<CheckBox>("AutoLoginToggle")!.IsChecked = false;
                    primaryCard.FindControl<CheckBox>("RememberPasswordToggle")!.IsChecked = false;
                    TestAssert.Equal(string.Empty, accountCard.Password, "A saved-account selector must never fill the password input from storage.");
                    TestAssert.Equal(0, visualSource.RequestCount, "Local layout and account editing cannot contact a source while offline.");
                    SaveFrame(window, Path.Combine(output, "ui-sources.png"));
                    window.Width = 720;
                    window.Height = 640;
                    SaveFrame(window, Path.Combine(output, "ui-accounts-compact.png"));
                    window.Width = 1100;
                    window.Height = 760;
                    workspace.SetOnlineEnabled(true);
                    await workspace.LoginAsync(primary, "Demo", "generated-ui-fixture", timeout.Token);
                    await ValidateTimelineControlsAsync(window, workspace, output);
                    return true;
                }
                finally
                {
                    window.Close();
                    await viewModel.DisposeAsync();
                    workspace.Dispose();
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    if (Directory.Exists(root))
                    {
                        var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        var resolvedRoot = Path.GetFullPath(root);
                        if (!resolvedRoot.StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                            || !Path.GetFileName(resolvedRoot).StartsWith("MajMusic-headless-", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("Refusing to clean a directory outside the headless test workspace.");
                        }

                        Directory.Delete(root, true);
                    }
                }
            }, timeout.Token);
        }

        /// <summary>Exercises visible playlist removal controls while retaining original local music files.</summary>
        /// <param name="window">The rendered test window.</param>
        /// <param name="view">The actual shared view.</param>
        /// <param name="model">The bound local workspace presentation.</param>
        /// <param name="workspace">The isolated persistent workspace.</param>
        /// <param name="cancellationToken">Cancels an incomplete command.</param>
        /// <returns>A task representing the pointer-driven playlist operations.</returns>
        private static async Task ValidatePlaylistEditingAsync(Window window, MainView view, MainViewModel model,
            MusicWorkspace workspace, CancellationToken cancellationToken)
        {
            var original = model.SelectedTrack!;
            model.NewPlaylistName = "Render playlist";
            model.CreatePlaylistCommand.Execute(null);
            while (model.IsBusy)
            {
                await Task.Delay(10, cancellationToken);
            }

            model.SelectedPlaylist = model.Playlists.Single(item => item.Name == "Render playlist");
            var playlistId = model.SelectedPlaylist.Id;
            model.AddToPlaylistCommand.Execute(null);
            while (model.IsBusy)
            {
                await Task.Delay(10, cancellationToken);
            }

            model.ShowPlaylistsModeCommand.Execute(null);
            await model.HomeUpdate;
            Dispatcher.UIThread.RunJobs();
            var remove = view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, model.RemoveFromPlaylistCommand));
            TestAssert.True(remove.IsEffectivelyVisible && remove.IsEnabled, "An inspected local playlist song must offer its visible removal button.");
            var point = remove.TranslatePoint(new Point(remove.Bounds.Width / 2, remove.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            while (model.IsBusy)
            {
                await Task.Delay(10, cancellationToken);
            }

            TestAssert.Equal(0, (await workspace.GetPlaylistsAsync(cancellationToken)).Single(item => item.Id == playlistId).TrackIds.Count,
                "The visible removal action must persist the membership change.");
            var tabs = view.FindControl<TabControl>("MainTabs")!;
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "歌单管理"));
            Dispatcher.UIThread.RunJobs();
            model.SelectedPlaylist = model.Playlists.Single(item => item.Id == playlistId);
            var delete = view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, model.DeletePlaylistCommand));
            TestAssert.True(delete.IsEffectivelyVisible && delete.IsEnabled, "The management page must offer deletion of an ordinary local playlist.");
            point = delete.TranslatePoint(new Point(delete.Bounds.Width / 2, delete.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            while (model.IsBusy)
            {
                await Task.Delay(10, cancellationToken);
            }

            TestAssert.True((await workspace.GetPlaylistsAsync(cancellationToken)).All(item => item.Id != playlistId)
                && File.Exists(original.Audio.LocalPath), "Deleting the list must keep its source music file intact.");
            tabs.SelectedItem = view.FindControl<TabItem>("LibraryTab");
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate;
        }

        /// <summary>Exercises actual pointer and keyboard slider gestures against an authoritative fake audio clock.</summary>
        /// <param name="window">The existing in-memory window.</param>
        /// <param name="workspace">The isolated imported library.</param>
        /// <param name="output">The ignored render destination.</param>
        /// <returns>A task representing timeline input and binding checks.</returns>
        private static async Task ValidateTimelineControlsAsync(Window window, MusicWorkspace workspace, string output)
        {
            var backend = new TimelineBackend();
            await using var viewModel = new MainViewModel(workspace, new PlaybackService(backend));
            var view = new MainView { DataContext = viewModel };
            window.Content = view;
            await viewModel.InitializeAsync();
            await viewModel.AutomaticLogin;
            await viewModel.EnsureHomeRecommendationsAsync();
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(viewModel.IsRecommendationsMode && viewModel.Tracks.Count > 0,
                "An authenticated home must display its real source feed before searching.");
            SaveFrame(window, Path.Combine(output, "ui-home.png"));
            await WaitForBrowseCoversAsync(view);
            TestAssert.True(viewModel.SelectedTrack is null && viewModel.CardTracks.Any(card => card.Cover is not null),
                "Visible artwork must load before a song is selected or played.");
            var cardsList = view.GetVisualDescendants().OfType<BrowseTracksView>().Single().ActiveList;
            var initialCards = cardsList.GetVisualDescendants().OfType<SongCard>().ToArray();
            TestAssert.True(initialCards.Length < viewModel.CardTracks.Count,
                "A long artwork feed must realize only the viewport and its nearby rows.");
            TestAssert.Equal(2, viewModel.BrowseColumns, "A normal desktop must show two artwork columns.");
            TestAssert.True(initialCards.All(card => Math.Abs(card.Bounds.Width - 2 * card.Bounds.Height) < 2),
                "Artwork cards must retain a two-to-one aspect ratio.");
            SaveFrame(window, Path.Combine(output, "ui-home.png"));
            AssertChromeGeometry(view);
            var initiallyHidden = new SongCard { DataContext = viewModel.CardTracks[0], Width = 320, IsVisible = false };
            window.Content = initiallyHidden;
            Dispatcher.UIThread.RunJobs();
            initiallyHidden.IsVisible = true;
            SaveFrame(window, Path.Combine(output, "ui-card-revealed.png"));
            using (var revealed = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                while (viewModel.CardTracks[0].Cover is null)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10, revealed.Token);
                }
            }

            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            await WaitForBrowseCoversAsync(view);
            cardsList.ScrollIntoView(viewModel.BrowseRows[^1]);
            Dispatcher.UIThread.RunJobs();
            await WaitForBrowseCoversAsync(view);
            var last = viewModel.CardTracks[^1];
            var lastControl = cardsList.GetVisualDescendants().OfType<SongCard>().Single(card => ReferenceEquals(card.DataContext, last));
            var lastPoint = lastControl.TranslatePoint(new Point(lastControl.Bounds.Width / 2, lastControl.Bounds.Height / 2), window)!.Value;
            window.MouseDown(lastPoint, MouseButton.Left);
            window.MouseUp(lastPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(last.Track.Id, viewModel.SelectedBrowseTrack!.Id,
                "A scrolled song must update the inspector even when it is outside the old first result page.");
            TestAssert.True(backend.Snapshot.Track is null, "Selecting an artwork card must not begin playback.");
            SaveFrame(window, Path.Combine(output, "ui-home-scrolled.png"));
            cardsList.ScrollIntoView(viewModel.BrowseRows[0]);
            window.Width = 1540;
            window.Height = 900;
            Dispatcher.UIThread.RunJobs();
            await WaitForBrowseCoversAsync(view);
            TestAssert.Equal(3, viewModel.BrowseColumns, "A wide desktop must show three artwork columns.");
            SaveFrame(window, Path.Combine(output, "ui-home-wide.png"));
            window.Width = 1100;
            window.Height = 760;
            Dispatcher.UIThread.RunJobs();
            var searchButton = view.FindControl<Button>("BeginSearchButton")!;
            var searchPoint = searchButton.TranslatePoint(new Point(searchButton.Bounds.Width / 2, searchButton.Bounds.Height / 2), window)!.Value;
            window.MouseDown(searchPoint, MouseButton.Left);
            window.MouseUp(searchPoint, MouseButton.Left);
            await viewModel.HomeUpdate;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(viewModel.IsSearchExpanded && viewModel.HomeMode == HomeBrowseMode.Library
                && view.FindControl<TextBox>("LibrarySearch")!.IsFocused,
                "The search icon must select the library and focus its single search input.");
            view.FindControl<TextBox>("LibrarySearch")!.Text = "Bio";
            await viewModel.HomeUpdate;
            TestAssert.True(viewModel.IsSearching && viewModel.Tracks.All(track => track.Title.Contains("Bio", StringComparison.OrdinalIgnoreCase)),
                "Entering a search must display matching indexed music rather than the feed.");
            var searchInput = view.FindControl<TextBox>("LibrarySearch")!;
            searchInput.Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await viewModel.HomeUpdate;
            TestAssert.True(viewModel.IsSearchExpanded && viewModel.IsSearching, "Enter in the real Arc search input must retain the matching search results.");
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await viewModel.HomeUpdate;
            TestAssert.True(!viewModel.IsSearchExpanded && viewModel.IsLibraryMode, "Escape in the real Arc search input must retain the library.");
            window.MouseDown(searchPoint, MouseButton.Left);
            window.MouseUp(searchPoint, MouseButton.Left);
            await viewModel.HomeUpdate;
            searchInput.Text = "Bio";
            await viewModel.HomeUpdate;
            Dispatcher.UIThread.RunJobs();
            SaveFrame(window, Path.Combine(output, "ui-search.png"));
            AssertChromeGeometry(view);
            TestAssert.True(cardsList.IsVisible && !cardsList.GetVisualDescendants().OfType<SongCard>().Any(card => card.IsCompact),
                "Search results must reuse the recommendation card container without switching to compact rows.");
            var precedingCover = viewModel.Cover;
            TestAssert.True(cardsList.Focus(), "The shared artwork browser must accept keyboard focus.");
            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Home, RawInputModifiers.None);
            using (var keyboard = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                while (viewModel.Cover is null || ReferenceEquals(viewModel.Cover, precedingCover))
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10, keyboard.Token);
                }

                precedingCover = viewModel.Cover;
                TestAssert.Equal(viewModel.CardTracks[0].Track.Id, viewModel.SelectedBrowseTrack!.Id,
                    "Home navigation must select the first artwork card.");
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                TestAssert.Equal(viewModel.CardTracks[1].Track.Id, viewModel.SelectedBrowseTrack!.Id,
                    "Right-arrow navigation must select the next artwork card within a row.");
                while (viewModel.Cover is null || ReferenceEquals(viewModel.Cover, precedingCover))
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10, keyboard.Token);
                }
            }

            TestAssert.Equal(viewModel.SelectedBrowseCard!.Track.Id, viewModel.SelectedBrowseTrack!.Id,
                "Keyboard selection must update both inspector metadata and its cover.");
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(viewModel.CardTracks[Math.Min(viewModel.CardTracks.Count - 1, 1 + viewModel.BrowseColumns)].Track.Id,
                viewModel.SelectedBrowseTrack!.Id, "Down-arrow navigation must preserve the artwork column in the following row.");
            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.End, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(viewModel.CardTracks[^1].Track.Id, viewModel.SelectedBrowseTrack!.Id,
                "End navigation must reach the last search card and realize its virtualized row.");
            var browseScroll = cardsList.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(item => ReferenceEquals(item.TemplatedParent, cardsList));
            TestAssert.True(browseScroll.Offset.Y > 0, "End navigation must actually scroll the shared card viewport.");
            viewModel.SearchText = "world";
            await viewModel.HomeUpdate;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(browseScroll.Offset.Y <= 1, "A new search must begin at the top of the artwork results.");
            cardsList.ScrollIntoView(viewModel.BrowseRows[^1]);
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(browseScroll.Offset.Y > 0, "The search fixture must be scrolled before returning to the library.");
            viewModel.CancelSearchCommand.Execute(null);
            await viewModel.HomeUpdate;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(!viewModel.IsSearchExpanded && viewModel.IsLibraryMode, "Canceling search must restore the complete library.");
            TestAssert.True(browseScroll.Offset.Y <= 1, "Returning from a scrolled search must show the complete library from the beginning.");
            cardsList.ScrollIntoView(viewModel.BrowseRows[5]);
            Dispatcher.UIThread.RunJobs();
            var beforeResize = browseScroll.Offset.Y;
            window.Width = 1120;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(beforeResize > 0 && browseScroll.Offset.Y > 0,
                "Resizing the shared card container must not reset the current browsing position.");
            window.Width = 1100;
            Dispatcher.UIThread.RunJobs();
            cardsList.ScrollIntoView(viewModel.BrowseRows[0]);
            Dispatcher.UIThread.RunJobs();
            window.Width = 720;
            window.Height = 540;
            SaveFrame(window, Path.Combine(output, "ui-home-small.png"));
            TestAssert.Equal(2, viewModel.BrowseColumns, "The compact desktop must keep two complete artwork columns.");
            AssertChromeGeometry(view);
            viewModel.SelectedTrack = viewModel.VisibleTracks[0];
            Dispatcher.UIThread.RunJobs();
            SaveFrame(window, Path.Combine(output, "ui-home-small-selected.png"));
            AssertChromeGeometry(view);
            var inspector = view.FindControl<Border>("LibraryDetails")!;
            foreach (var control in inspector.GetVisualDescendants().OfType<Control>()
                .Where(item => item.IsEffectivelyVisible && item is Button or ArcSelect))
            {
                var position = control.TranslatePoint(default, inspector)!.Value;
                TestAssert.True(position.X >= 0 && position.Y >= 0
                    && position.X + control.Bounds.Width <= inspector.Bounds.Width + 1
                    && position.Y + control.Bounds.Height <= inspector.Bounds.Height + 1,
                    "Selected-song actions must fit the permanent inspector at the minimum window size.");
            }

            foreach (var page in new[] { (Tab: "AccountsTab", Index: 3), (Tab: "SettingsTab", Index: 4), (Tab: "DirectoriesTab", Index: 2) })
            {
                var navigation = view.FindControl<TKCode.Arc.SegmentedControl>("DesktopTopNavigation")!.ContainerFromIndex(page.Index)!;
                var navigationPoint = navigation.TranslatePoint(new Point(navigation.Bounds.Width / 2, navigation.Bounds.Height / 2), window)!.Value;
                window.MouseDown(navigationPoint, MouseButton.Left);
                window.MouseUp(navigationPoint, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                TestAssert.Equal(page.Index, view.SelectedSectionIndex, "The actual Arc navigation action must open its original page.");
                TestAssert.True(ReferenceEquals(view.FindControl<TabControl>("MainTabs")!.SelectedItem, view.FindControl<TabItem>(page.Tab)),
                    "Desktop navigation must preserve the original selected page host.");
                SaveFrame(window, Path.Combine(output, "ui-small-" + page.Tab + ".png"));
                AssertVisibleActions(view);
            }

            view.FindControl<TabControl>("MainTabs")!.SelectedItem = view.FindControl<TabItem>("LibraryTab");
            window.Width = 1100;
            window.Height = 760;
            viewModel.OnlineEnabled = false;
            viewModel.ShowLibraryModeCommand.Execute(null);
            await viewModel.HomeUpdate;
            Dispatcher.UIThread.RunJobs();
            var volume = view.FindControl<Grid>("PlaybackVolume")!.GetVisualDescendants().OfType<ArcSlider>().Single();
            var volumeThumb = ArcTestVisuals.SliderThumb(volume);
            var volumeStart = volumeThumb.TranslatePoint(new Point(volumeThumb.Bounds.Width / 2, volumeThumb.Bounds.Height / 2), window)!.Value;
            var volumeEnd = volume.TranslatePoint(new Point(volume.Bounds.Width * 0.4, volume.Bounds.Height / 2), window)!.Value;
            window.MouseDown(volumeStart, MouseButton.Left);
            window.MouseMove(volumeEnd, RawInputModifiers.LeftMouseButton);
            window.MouseUp(volumeEnd, MouseButton.Left);
            await viewModel.AudioSettingsUpdate;
            TestAssert.True(viewModel.Volume is > 0 and < 100 && backend.Preferences.Volume == (int)viewModel.Volume,
                "Dragging the aligned volume thumb must still update the actual output-control preference.");
            viewModel.SelectedTrack = viewModel.Tracks[0];
            await viewModel.PlaySelectedAsync();
            Dispatcher.UIThread.RunJobs();
            var slider = view.FindControl<ArcSlider>("PlaybackTimeline")!;
            TestAssert.True(slider.IsEnabled, "A prepared seekable backend must enable the actual slider.");
            TestAssert.Equal(120d, slider.Maximum, "The real slider must bind the audio duration.");
            var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(MainViewModel.PositionSeconds) && viewModel.PositionSeconds == 20)
                {
                    progress.TrySetResult();
                }
            };
            backend.SetPosition(TimeSpan.FromSeconds(20));
            await progress.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(20d, slider.Value, "The real slider must display backend progress.");
            TestAssert.Equal(0, backend.SeekCount, "A bound clock update cannot feed back as a seek.");

            ArcTestVisuals.SliderThumb(slider).Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            TestAssert.True(viewModel.IsSeeking, "Keyboard navigation must begin a user preview.");
            TestAssert.Equal(0, backend.SeekCount, "A held navigation key cannot seek before its release.");
            var proposed = slider.Value;
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await viewModel.TransportUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(1, backend.SeekCount, "Keyboard release must commit exactly one native seek.");
            TestAssert.Equal(TimeSpan.FromSeconds(proposed), backend.Snapshot.Position, "Keyboard navigation must commit its preview value.");

            var start = slider.TranslatePoint(new Point(slider.Bounds.Width * 0.6, slider.Bounds.Height / 2), window)!.Value;
            var end = slider.TranslatePoint(new Point(slider.Bounds.Width * 0.8, slider.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            TestAssert.True(viewModel.IsSeeking, "A pointer track gesture must preview before release.");
            TestAssert.Equal(1, backend.SeekCount, "Pointer motion cannot flood the audio owner with seeks.");
            proposed = slider.Value;
            window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            await viewModel.TransportUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(2, backend.SeekCount, "Pointer release must commit exactly once.");
            TestAssert.Equal(TimeSpan.FromSeconds(proposed), backend.Snapshot.Position, "The pointer preview must reach the backend clock.");

            ArcTestVisuals.SliderThumb(slider).Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(2, backend.SeekCount, "Escape must cancel a real keyboard preview.");
            TestAssert.True(!workspace.IsOnline, "Timeline and playback input must leave default offline mode unchanged.");
            SaveFrame(window, Path.Combine(output, "ui-playback.png"));
        }

        /// <summary>Waits for attached virtualized cards to publish their generated cover fixtures.</summary>
        /// <param name="view">The rendered browsing view.</param>
        /// <returns>A task completing after current visible artwork is available.</returns>
        private static async Task WaitForBrowseCoversAsync(MainView view)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Dispatcher.UIThread.RunJobs();
                var cards = view.GetVisualDescendants().OfType<SongCard>()
                    .Where(card => card.IsEffectivelyVisible && card.IsBrowsingActive && card.Bounds.Width > 0)
                    .Select(card => (SongCardViewModel)card.DataContext!).ToArray();
                if (cards.Length > 0 && cards.All(card => card.Cover is not null))
                {
                    return;
                }

                await Task.Delay(10, cancellation.Token);
            }
        }

        /// <summary>Captures an actual rendered headless frame.</summary>
        /// <param name="window">The window to capture.</param>
        /// <param name="path">The ignored validation image destination.</param>
        /// <exception cref="InvalidOperationException">Rendering did not produce a frame.</exception>
        private static void SaveFrame(Window window, string path)
        {
            using var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("The shared view did not render a frame.");
            TestAssert.True(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0, "A rendered frame must have visible bounds.");
            frame.Save(path, PngBitmapEncoderOptions.Default);
        }

        /// <summary>Checks shared card edges and complete visible list pages.</summary>
        /// <param name="view">The rendered player view.</param>
        private static void AssertChromeGeometry(MainView view)
        {
            var list = view.FindControl<Border>("LibraryListPanel")!;
            var details = view.FindControl<Border>("LibraryDetails")!;
            var player = view.FindControl<Border>("PlaybackBar")!;
            var left = list.TranslatePoint(default, view)!.Value;
            var right = details.TranslatePoint(default, view)!.Value;
            var bottom = player.TranslatePoint(default, view)!.Value;
            TestAssert.True(details.IsVisible && Math.Abs(left.X - bottom.X) < 1
                && Math.Abs(right.X + details.Bounds.Width - bottom.X - player.Bounds.Width) < 1,
                "The permanent inspector and content must share the player's outside edges.");
            TestAssert.True(Math.Abs(left.Y - right.Y) < 1 && Math.Abs(list.Bounds.Height - details.Bounds.Height) < 1,
                "The content and permanent inspector must remain aligned vertically.");
            TestAssert.True(list.CornerRadius == details.CornerRadius && list.CornerRadius == player.CornerRadius,
                "Main panels must use one corner radius.");
            AssertVisibleActions(view);
        }

        /// <summary>Checks that common actions and visible page contents fit their allocated area.</summary>
        /// <param name="view">The rendered player view.</param>
        private static void AssertVisibleActions(MainView view)
        {
            var player = view.FindControl<Border>("PlaybackBar")!;
            var contentBottom = player.TranslatePoint(default, view)!.Value.Y;
            foreach (var control in view.GetVisualDescendants().OfType<Control>()
                .Where(item => item.IsEffectivelyVisible && item is Button or ArcSelect or ArcSwitch or CheckBox or TextBox)
                .Where(item => !item.GetVisualAncestors().Any(ancestor => ancestor is SongCard or EndpointCardView)))
            {
                var position = control.TranslatePoint(default, view)!.Value;
                var maximum = control.GetVisualAncestors().Contains(player) ? view.Bounds.Height : contentBottom;
                TestAssert.True(position.X >= 0 && position.X + control.Bounds.Width <= view.Bounds.Width + 1
                    && position.Y >= 0 && position.Y + control.Bounds.Height <= maximum + 1,
                    $"A common control must remain visible without scrolling: {control.Name ?? control.GetType().Name} at {position}.");
            }

            foreach (var list in view.GetVisualDescendants().OfType<ListBox>().Where(item => item.IsEffectivelyVisible))
            {
                if (list is BrowseCardListBox)
                {
                    continue;
                }

                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                TestAssert.True(scroll is null || scroll.Extent.Height <= scroll.Viewport.Height + 2,
                    $"A visible list page must contain complete rows without scrolling: {list.Name}.");
            }
        }

        /// <summary>Serves deterministic visual fixtures without external network access.</summary>
        private sealed class VisualSourceTransport : HttpMessageHandler
        {
            /// <summary>Caches generated artwork by source image route.</summary>
            private readonly ConcurrentDictionary<string, byte[]> _images = new(StringComparer.Ordinal);
            /// <summary>Gets the number of simulated source requests.</summary>
            public int RequestCount { get; private set; }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestCount++;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/image", StringComparison.Ordinal))
                {
                    var bytes = _images.GetOrAdd(path, CreateCover);
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    return Task.FromResult(response);
                }

                var titles = new[] { "Bio-Engineering", "new world", "Walk Away (Disphing Remix)", "星の旅", "After the rain" };
                var json = path.EndsWith("account/login", StringComparison.Ordinal) ? "{}"
                    : path.EndsWith("recommend/feed", StringComparison.Ordinal) || path.EndsWith("maichart/list", StringComparison.Ordinal)
                        ? JsonSerializer.Serialize(Enumerable.Range(1, 50).Select(index => new
                        {
                            id = "visual-" + index,
                            title = titles[index % titles.Length] + " · " + index.ToString("00"),
                            artist = index % 2 == 0 ? "RAINWORLD" : "Visual fixture",
                            hash = "visual-fixture",
                            durationSeconds = 125 + index,
                            bitrate = 320000,
                            sampleRate = 44100
                        }))
                        : path.EndsWith("/info", StringComparison.Ordinal) && !path.Contains("/maichart/", StringComparison.Ordinal)
                            ? "{\"id\":\"visual-server\",\"name\":\"MajNet\"}" : null;
                return Task.FromResult(new HttpResponseMessage(json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                {
                    Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json")
                });
            }

            /// <summary>Creates distinct deterministic artwork without external media resources.</summary>
            /// <param name="path">The source route identifying this generated image.</param>
            /// <returns>The encoded PNG cover.</returns>
            private static byte[] CreateCover(string path)
            {
                var index = int.Parse(path.Split('/')[^2].Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
                var colors = new[] { new SKColor(68, 113, 156), new SKColor(170, 88, 111), new SKColor(53, 142, 130), new SKColor(139, 102, 179), new SKColor(179, 131, 62) };
                using var bitmap = new SKBitmap(256, 256);
                using var canvas = new SKCanvas(bitmap);
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(256, 256),
                    new[] { colors[index % colors.Length], new SKColor(20, 30, 50) }, null, SKShaderTileMode.Clamp);
                using var background = new SKPaint { Shader = shader, IsAntialias = true };
                canvas.DrawRect(0, 0, 256, 256, background);
                using var ring = new SKPaint { Color = new SKColor(255, 255, 255, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
                for (var circle = 0; circle < 5; circle++)
                {
                    canvas.DrawCircle(128, 128, 45 + circle * 26, ring);
                }

                using var note = new SKPaint { Color = new SKColor(240, 248, 255, 220), StrokeWidth = 9, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
                canvas.DrawLine(110, 145, 110, 82, note);
                canvas.DrawLine(110, 82, 160, 70, note);
                canvas.DrawLine(160, 70, 160, 132, note);
                canvas.DrawOval(97, 148, 17, 12, note);
                canvas.DrawOval(147, 135, 17, 12, note);
                using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                return encoded.ToArray();
            }
        }

        /// <summary>Provides an explicit test-only native clock substitute for actual input and binding checks.</summary>
        internal sealed class TimelineBackend : IPlaybackBackend, IAudioOutputControl
        {
            /// <inheritdoc />
            public AudioOutputPreferences Preferences { get; private set; } = new();
            /// <inheritdoc />
            public string? OutputDeviceNotice => null;
            /// <inheritdoc />
            public Task<IReadOnlyList<AudioOutputDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AudioOutputDevice>>([new AudioOutputDevice { Name = "系统默认" }]);
            }
            /// <inheritdoc />
            public Task SetOutputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
            {
                Preferences = Preferences with { DeviceId = deviceId };
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
            {
                Preferences = Preferences with { Volume = volume };
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
            {
                Preferences = Preferences with { Muted = muted };
                return Task.CompletedTask;
            }
            /// <summary>Gets the number of seek operations submitted by control gestures.</summary>
            public int SeekCount { get; private set; }
            /// <inheritdoc />
            public bool IsAvailable => true;
            /// <inheritdoc />
            public PlaybackCapabilities Capabilities => PlaybackCapabilities.Playback | PlaybackCapabilities.Seeking;
            /// <inheritdoc />
            public PlaybackSnapshot Snapshot { get; private set; } = new();
            /// <inheritdoc />
            public event EventHandler? StateChanged;

            /// <inheritdoc />
            public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
            {
                Snapshot = new PlaybackSnapshot { State = PlaybackState.Paused, Duration = TimeSpan.FromMinutes(2) };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task PlayAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Playing);
            }

            /// <inheritdoc />
            public Task PauseAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Paused);
            }

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Stopped);
            }

            /// <inheritdoc />
            public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SeekCount++;
                SetPosition(position);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            /// <summary>Publishes a position supplied by the simulated audio owner.</summary>
            /// <param name="position">The authoritative test media position.</param>
            public void SetPosition(TimeSpan position)
            {
                Snapshot = Snapshot with { Position = position };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            /// <summary>Publishes a test media lifecycle transition.</summary>
            /// <param name="state">The lifecycle state.</param>
            /// <returns>A completed test operation.</returns>
            private Task ChangeStateAsync(PlaybackState state)
            {
                Snapshot = Snapshot with { State = state };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>Provides real Skia rendering with an in-memory windowing platform.</summary>
    public sealed class HeadlessTestApplication : Application
    {
        /// <summary>Loads the same legacy virtualization base used by the Arc desktop host.</summary>
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
        }

        /// <summary>Builds an isolated rendering host without any native desktop windows.</summary>
        /// <returns>The configured headless application.</returns>
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<HeadlessTestApplication>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseMajMusicArc(ArcMotionLevel.Off);
        }
    }

    /// <summary>Locates the interactive Arc visual elements used by real input regressions.</summary>
    internal static class ArcTestVisuals
    {
        /// <summary>Finds the slider's actual focusable thumb instead of focusing its noninteractive group.</summary>
        /// <param name="slider">The realized single-value Arc slider.</param>
        /// <returns>The real pointer and keyboard target.</returns>
        public static Control SliderThumb(ArcSlider slider)
        {
            return slider.GetVisualDescendants().OfType<Control>().Single(control => control.IsVisible && control.GetType().Name == "SliderThumb");
        }
    }
}
