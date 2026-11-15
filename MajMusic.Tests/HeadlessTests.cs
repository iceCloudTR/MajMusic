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
using SkiaSharp;
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
            await session.Dispatch(async () =>
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
                    TestAssert.True(!view.FindControl<Slider>("PlaybackTimeline")!.IsEnabled,
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
                    SaveFrame(window, Path.Combine(output, "ui-library-dense-dark.png"));
                    var trackList = view.FindControl<ListBox>("LibraryTracks")!;
                    var visibleRows = trackList.GetVisualDescendants().OfType<ListBoxItem>().Where(item =>
                    {
                        var position = item.TranslatePoint(default, trackList);
                        return position is { } point && point.Y >= 0 && point.Y + item.Bounds.Height <= trackList.Bounds.Height;
                    }).ToArray();
                    TestAssert.True(visibleRows.Length >= 6 && visibleRows.All(item => item.Bounds.Height <= 76),
                        "A normal desktop viewport must show at least six compact music rows.");
                    TestAssert.True(view.FindControl<Border>("PlaybackBar")!.Bounds.Height <= 115,
                        "The shared playback bar must leave room for the library.");
                    var timeline = view.FindControl<Slider>("PlaybackTimeline")!;
                    var thumb = timeline.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
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
                    var permission = view.FindControl<CheckBox>("OnlineModeToggle")!;
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
                    var accountPicker = view.FindControl<ComboBox>("SavedAccountPicker")!;
                    TestAssert.Equal("Render B", viewModel.SelectedSavedAccount?.DisplayName,
                        "The actual selector must restore the configured account without logging in.");
                    accountPicker.SelectedIndex = 0;
                    Dispatcher.UIThread.RunJobs();
                    while (viewModel.IsBusy)
                    {
                        await Task.Delay(10, timeout.Token);
                    }

                    TestAssert.True((await workspace.LoadSettingsAsync(timeout.Token)).Endpoints.First().SelectedAccountId is null,
                        "Selecting Guest through the actual control must persist that choice.");
                    accountPicker.SelectedIndex = 1;
                    Dispatcher.UIThread.RunJobs();
                    while (viewModel.IsBusy)
                    {
                        await Task.Delay(10, timeout.Token);
                    }

                    TestAssert.Equal("Render A", viewModel.SelectedSavedAccount?.DisplayName,
                        "Selecting a saved account through the actual control must update the editor.");
                    view.FindControl<CheckBox>("AutoLoginToggle")!.IsChecked = true;
                    Dispatcher.UIThread.RunJobs();
                    TestAssert.True(viewModel.AutoLogin && viewModel.RememberPassword
                        && view.FindControl<CheckBox>("RememberPasswordToggle")!.IsChecked == true,
                        "Checking automatic login through the actual control must enable protected password retention.");
                    view.FindControl<CheckBox>("AutoLoginToggle")!.IsChecked = false;
                    view.FindControl<CheckBox>("RememberPasswordToggle")!.IsChecked = false;
                    TestAssert.Equal(string.Empty, viewModel.LoginPassword, "A saved-account selector must never fill the password input from storage.");
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
            AssertChromeGeometry(view);
            var searchButton = view.FindControl<Button>("BeginSearchButton")!;
            var searchPoint = searchButton.TranslatePoint(new Point(searchButton.Bounds.Width / 2, searchButton.Bounds.Height / 2), window)!.Value;
            window.MouseDown(searchPoint, MouseButton.Left);
            window.MouseUp(searchPoint, MouseButton.Left);
            await viewModel.HomeUpdate;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(viewModel.IsSearchExpanded && view.FindControl<TextBox>("LibrarySearch")!.IsFocused,
                "The search icon must reveal and focus the single search input.");
            view.FindControl<TextBox>("LibrarySearch")!.Text = "Bio";
            await viewModel.HomeUpdate;
            TestAssert.True(viewModel.IsSearching && viewModel.Tracks.All(track => track.Title.Contains("Bio", StringComparison.OrdinalIgnoreCase)),
                "Entering a search must display matching indexed music rather than the feed.");
            SaveFrame(window, Path.Combine(output, "ui-search.png"));
            AssertChromeGeometry(view);
            viewModel.CancelSearchCommand.Execute(null);
            await viewModel.HomeUpdate;
            TestAssert.True(!viewModel.IsSearchExpanded && viewModel.IsRecommendationsMode, "Canceling search must restore recommendations.");
            window.Width = 720;
            window.Height = 540;
            SaveFrame(window, Path.Combine(output, "ui-home-small.png"));
            AssertChromeGeometry(view);
            viewModel.SelectedTrack = viewModel.VisibleTracks[0];
            Dispatcher.UIThread.RunJobs();
            SaveFrame(window, Path.Combine(output, "ui-home-small-selected.png"));
            AssertChromeGeometry(view);
            var inspector = view.FindControl<Border>("LibraryDetails")!;
            foreach (var control in inspector.GetVisualDescendants().OfType<Control>()
                .Where(item => item.IsEffectivelyVisible && item is Button or ComboBox))
            {
                var position = control.TranslatePoint(default, inspector)!.Value;
                TestAssert.True(position.X >= 0 && position.Y >= 0
                    && position.X + control.Bounds.Width <= inspector.Bounds.Width + 1
                    && position.Y + control.Bounds.Height <= inspector.Bounds.Height + 1,
                    "Selected-song actions must fit the permanent inspector at the minimum window size.");
            }

            foreach (var page in new[] { "AccountsTab", "SettingsTab", "DirectoriesTab" })
            {
                view.FindControl<TabControl>("MainTabs")!.SelectedItem = view.FindControl<TabItem>(page);
                SaveFrame(window, Path.Combine(output, "ui-small-" + page + ".png"));
                AssertVisibleActions(view);
            }

            view.FindControl<TabControl>("MainTabs")!.SelectedItem = view.FindControl<TabItem>("LibraryTab");
            window.Width = 1100;
            window.Height = 760;
            viewModel.OnlineEnabled = false;
            viewModel.ShowLibraryModeCommand.Execute(null);
            await viewModel.HomeUpdate;
            viewModel.SelectedTrack = viewModel.Tracks[0];
            await viewModel.PlaySelectedAsync();
            Dispatcher.UIThread.RunJobs();
            var slider = view.FindControl<Slider>("PlaybackTimeline")!;
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

            slider.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            TestAssert.True(viewModel.IsSeeking, "Keyboard navigation must begin a user preview.");
            TestAssert.Equal(0, backend.SeekCount, "A held navigation key cannot seek before its release.");
            var proposed = slider.Value;
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
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
            TestAssert.Equal(2, backend.SeekCount, "Pointer release must commit exactly once.");
            TestAssert.Equal(TimeSpan.FromSeconds(proposed), backend.Snapshot.Position, "The pointer preview must reach the backend clock.");

            slider.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(2, backend.SeekCount, "Escape must cancel a real keyboard preview.");
            TestAssert.True(!workspace.IsOnline, "Timeline and playback input must leave default offline mode unchanged.");
            SaveFrame(window, Path.Combine(output, "ui-playback.png"));
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
                .Where(item => item.IsEffectivelyVisible && item is Button or ComboBox or CheckBox or TextBox))
            {
                var position = control.TranslatePoint(default, view)!.Value;
                var maximum = control.GetVisualAncestors().Contains(player) ? view.Bounds.Height : contentBottom;
                TestAssert.True(position.X >= 0 && position.X + control.Bounds.Width <= view.Bounds.Width + 1
                    && position.Y >= 0 && position.Y + control.Bounds.Height <= maximum + 1,
                    $"A common control must remain visible without scrolling: {control.Name ?? control.GetType().Name} at {position}.");
            }

            foreach (var list in view.GetVisualDescendants().OfType<ListBox>().Where(item => item.IsEffectivelyVisible))
            {
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                TestAssert.True(scroll is null || scroll.Extent.Height <= scroll.Viewport.Height + 2,
                    $"A visible list page must contain complete rows without scrolling: {list.Name}.");
            }
        }

        /// <summary>Serves deterministic visual fixtures without external network access.</summary>
        private sealed class VisualSourceTransport : HttpMessageHandler
        {
            /// <summary>Gets the number of simulated source requests.</summary>
            public int RequestCount { get; private set; }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestCount++;
                var path = request.RequestUri!.AbsolutePath;
                var titles = new[] { "Bio-Engineering", "new world", "Walk Away (Disphing Remix)", "星の旅", "After the rain" };
                var json = path.EndsWith("account/login", StringComparison.Ordinal) ? "{}"
                    : path.EndsWith("recommend/feed", StringComparison.Ordinal) || path.EndsWith("maichart/list", StringComparison.Ordinal)
                        ? JsonSerializer.Serialize(Enumerable.Range(1, 18).Select(index => new
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
        }

        /// <summary>Provides an explicit test-only native clock substitute for actual input and binding checks.</summary>
        private sealed class TimelineBackend : IPlaybackBackend, IAudioOutputControl
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
        /// <summary>Loads the same Fluent theme used by the desktop host.</summary>
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
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        }
    }
}
