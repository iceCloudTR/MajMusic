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
using TKCode.Arc;

namespace MajMusic.Tests
{
    /// <summary>Exercises real context gestures and scrolling playlist menus against isolated local persistence.</summary>
    internal static class ContextMenusLayoutTests
    {
        /// <summary>Checks captured song actions, complete scrolling menus, local rename/delete and touch context access.</summary>
        /// <returns>A task representing actual input and persisted-target regressions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await session.Dispatch<bool>(async () =>
            {
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-context-ui-" + Guid.NewGuid().ToString("N")));
                try
                {
                    await ValidateAsync(root, timeout.Token);
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    TestAssert.True(root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(root).StartsWith("MajMusic-context-ui-", StringComparison.Ordinal),
                        "Context fixture cleanup must remain in its generated temporary directory.");
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Uses real right-clicks and native Arc rows while changing unrelated selections between gestures.</summary>
        /// <param name="root">The isolated application data directory.</param>
        /// <param name="token">Cancels stalled gestures or persistence.</param>
        /// <returns>A task representing completed desktop and phone cases.</returns>
        private static async Task ValidateAsync(string root, CancellationToken token)
        {
            using var handler = new NoNetwork();
            using var client = new HttpClient(handler);
            using var workspace = new MusicWorkspace(Path.Combine(root, "data"), client, client, credentialStore: new NoCredentials());
            await workspace.LoadSettingsAsync();
            var tracks = Enumerable.Range(0, 3).Select(index => new MusicTrack
            {
                Id = "menu-track-" + index,
                Title = "Menu music " + index,
                SourceId = "local",
                Audio = new MediaResource { LocalPath = "generated-menu-audio" },
                Duration = TimeSpan.FromMinutes(3),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 }
            }).ToArray();
            await new SqliteLibraryRepository(Path.Combine(root, "data", "library.db")).UpsertTracksAsync(tracks);
            for (var index = 0; index < 70; index++)
            {
                await workspace.SavePlaylistAsync(new MusicPlaylist
                {
                    Id = "menu-list-" + index.ToString("D3"),
                    Name = "Playlist " + index.ToString("D3"),
                    TrackIds = [tracks[index == 1 ? 1 : 0].Id]
                });
            }

            var backend = new LibraryExperienceTests.ExperienceBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: static (_, _) => Task.FromResult("generated-menu-audio"));
            await using var model = new MainViewModel(workspace, playback, collections: new MajCollectionExchange(workspace));
            await model.InitializeAsync();
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate.WaitAsync(token);
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 1100, Height = 760 };
            try
            {
                window.Show();
                Pump(window);
                var card = Card(view, tracks[1].Id);
                model.SelectedTrack = tracks[0];
                RightClick(card);
                Pump(window);
                var menu = MusicContextMenus.GetOpenMenu(card)!;
                TestAssert.True(menu is not null && !Row(menu, "下载").IsEffectivelyEnabled,
                    "A local song must expose its real actions while keeping online download unavailable.");
                model.SelectedTrack = tracks[0];
                Tap(Row(menu!, "喜欢"));
                await WaitAsync(window, () => model.IsIdle && model.IsTrackFavoriteForMenu(tracks[1]), token);
                TestAssert.True(model.SelectedTrack?.Id == tracks[0].Id && !model.IsTrackFavoriteForMenu(tracks[0]),
                    "The actual right-click action must like its captured row rather than the inspected song.");
                RightClick(card);
                Pump(window);
                menu = MusicContextMenus.GetOpenMenu(card)!;
                Tap(Row(menu, "取消喜欢"));
                await WaitAsync(window, () => model.IsIdle && !model.IsTrackFavoriteForMenu(tracks[1]), token);
                RightClick(card);
                Pump(window);
                menu = MusicContextMenus.GetOpenMenu(card)!;
                var add = Row(menu, "加入歌单");
                Tap(add);
                Pump(window);
                Tap(add.Items.OfType<MenuEntry>().Single(item => item.Header?.ToString()?.Contains("Playlist 000", StringComparison.Ordinal) == true));
                await WaitAsync(window, () => model.IsIdle && model.Playlists.Single(item => item.Id == "menu-list-000").TrackIds.Contains(tracks[1].Id), token);
                RightClick(card);
                Pump(window);
                menu = MusicContextMenus.GetOpenMenu(card)!;
                model.SelectedTrack = tracks[0];
                Tap(Row(menu, "播放"));
                await model.TransportUpdate.WaitAsync(token);
                TestAssert.Equal(tracks[1].Id, playback.Snapshot.Track!.Id,
                    "The actual song menu must start the clicked song even after another row is selected.");

                model.SelectedHomePlaylist = model.Playlists.Single(item => item.Id == "menu-list-000");
                await model.HomeUpdate.WaitAsync(token);
                Pump(window);
                card = Card(view, tracks[1].Id);
                model.SelectedPlaylist = model.Playlists.Single(item => item.Id == "menu-list-001");
                RightClick(card);
                Pump(window);
                menu = MusicContextMenus.GetOpenMenu(card)!;
                model.SelectedTrack = tracks[0];
                Tap(Row(menu, "从当前歌单移除"));
                await WaitAsync(window, () => model.IsIdle && !model.Playlists.Single(item => item.Id == "menu-list-000").TrackIds.Contains(tracks[1].Id), token);
                TestAssert.True(model.Playlists.Single(item => item.Id == "menu-list-001").TrackIds.Contains(tracks[1].Id),
                    "Removing through the song menu must preserve the unrelated insertion destination.");

                view.NavigateToSection(1);
                Pump(window);
                var list = view.FindControl<ListBox>("PlaylistList")!;
                TestAssert.True(list.Items.Count == model.Playlists.Count && list.Items.Count > 60,
                    "Playlist management must bind the complete collection rather than a viewport-sized page.");
                list.ScrollIntoView(model.Playlists[^1]);
                Pump(window);
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().Single(item => ReferenceEquals(item.TemplatedParent, list));
                TestAssert.True(scroll.Offset.Y > 0 && list.GetVisualDescendants().OfType<ListBoxItem>().Count() < list.Items.Count,
                    "All playlists must remain vertically scrollable and virtualized.");
                var captured = model.Playlists.Single(item => item.Id == "menu-list-001");
                var unchanged = model.Playlists.Single(item => item.Id == "menu-list-000");
                OpenPlaylistMenu(window, list, captured);
                menu = MusicContextMenus.GetOpenMenu(list)!;
                var selectedBeforePlay = model.SelectedPlaylist?.Id;
                Tap(Row(menu, "播放"));
                await model.TransportUpdate.WaitAsync(token);
                TestAssert.True(view.SelectedSectionIndex == 1 && playback.Queue.Current?.Id == tracks[1].Id
                    && model.SelectedPlaylist?.Id == selectedBeforePlay,
                    $"Playlist play must retain management position and the context gesture's selection while playing its captured list: section={view.SelectedSectionIndex}, track={playback.Queue.Current?.Id}, selection={model.SelectedPlaylist?.Id}.");
                OpenPlaylistMenu(window, list, captured);
                menu = MusicContextMenus.GetOpenMenu(list)!;
                Tap(Row(menu, "重命名"));
                await WaitAsync(window, () => window.GetVisualDescendants().OfType<TKCode.Arc.Input>().Any(item => item.Name == "RenamePlaylistInput"), token);
                var input = window.GetVisualDescendants().OfType<TKCode.Arc.Input>().Single(item => item.Name == "RenamePlaylistInput");
                var close = input.FindAncestorOfType<DialogSurface>()!.CloseButton!;
                TestAssert.True(close.IsEffectivelyVisible && close.GetVisualDescendants().OfType<ArcGlyph>().Single().Data!.CheckAccess(),
                    "The standard rename header must retain its close button with a glyph owned by this rendering dispatcher.");
                Tap(close);
                await WaitAsync(window, () => !window.GetVisualDescendants().OfType<TKCode.Arc.Input>().Any(item => item.Name == "RenamePlaylistInput"), token);
                TestAssert.Equal(captured.Name, model.Playlists.Single(item => item.Id == captured.Id).Name,
                    "Closing the editor through its header must preserve the playlist name.");
                OpenPlaylistMenu(window, list, captured);
                Tap(Row(MusicContextMenus.GetOpenMenu(list)!, "重命名"));
                await WaitAsync(window, () => window.GetVisualDescendants().OfType<TKCode.Arc.Input>().Any(item => item.Name == "RenamePlaylistInput"), token);
                input = window.GetVisualDescendants().OfType<TKCode.Arc.Input>().Single(item => item.Name == "RenamePlaylistInput");
                var save = window.GetVisualDescendants().OfType<TKCode.Arc.Button>().Single(item => item.Name == "RenamePlaylistSave");
                TestAssert.Equal(captured.Name, input.Text, "The name editor must start with the captured row's name.");
                input.Text = "   ";
                Pump(window);
                TestAssert.True(!save.IsEffectivelyEnabled && TopLevel.GetTopLevel(input) is not null,
                    "An empty rename must keep its editor open with save unavailable.");
                input.Text = "Renamed menu row";
                Pump(window);
                Tap(save);
                await WaitAsync(window, () => model.IsIdle && model.Playlists.Single(item => item.Id == captured.Id).Name == "Renamed menu row", token);
                TestAssert.Equal(unchanged.Name, model.Playlists.Single(item => item.Id == unchanged.Id).Name,
                    "Renaming the clicked row cannot rename another playlist.");
                captured = model.Playlists.Single(item => item.Id == captured.Id);
                OpenPlaylistMenu(window, list, captured);
                menu = MusicContextMenus.GetOpenMenu(list)!;
                Tap(Row(menu, "删除"));
                await WaitAsync(window, () => model.IsIdle && model.Playlists.All(item => item.Id != captured.Id), token);
                TestAssert.True(model.Playlists.Any(item => item.Id == unchanged.Id) && (await workspace.SearchAsync(null, token)).Count == tracks.Length,
                    "Deleting the captured list must preserve the other list and every original music entry.");
                OpenPlaylistMenu(window, list, model.Playlists.Single(item => item.IsFavorite));
                menu = MusicContextMenus.GetOpenMenu(list)!;
                TestAssert.True(!Row(menu, "重命名").IsEffectivelyEnabled && !Row(menu, "删除").IsEffectivelyEnabled,
                    "Built-in favorites must keep rename and whole-list deletion unavailable.");
                MusicContextMenus.Close(list);
                var cloud = new MusicPlaylist { Id = "foreign-cloud", Name = "Cloud only", EndpointId = "fixture", RemoteId = "foreign" };
                model.Playlists.Add(cloud);
                OpenPlaylistMenu(window, list, cloud);
                menu = MusicContextMenus.GetOpenMenu(list)!;
                TestAssert.True(!Row(menu, "重命名").IsEffectivelyEnabled && !Row(menu, "删除").IsEffectivelyEnabled,
                    "An unmapped cloud row cannot expose local mutation capabilities.");
                SaveMenu(menu, "ui-context-playlist.png");
                MusicContextMenus.Close(list);

                window.Width = 390;
                window.Height = 844;
                Pump(window);
                var mobileList = view.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "MobilePlaylistsList");
                TestAssert.Equal(model.Playlists.Count, mobileList.Items.Count, "The phone must share the full scrolling playlist collection.");
                var target = model.Playlists.Single(item => item.Id == unchanged.Id);
                mobileList.ScrollIntoView(target);
                Pump(window);
                var container = mobileList.ContainerFromItem(target)!;
                var point = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;
                using var touch = window.TouchBegin(point, RawInputModifiers.None);
                await WaitAsync(window, () => MusicContextMenus.GetOpenMenu(mobileList) is not null, token);
                window.TouchEnd(touch, point, RawInputModifiers.None);
                Pump(window);
                TestAssert.True(Row(MusicContextMenus.GetOpenMenu(mobileList)!, "打开").IsEffectivelyEnabled,
                    "A real phone long press must expose the same playlist menu without adding permanent buttons.");
                Tap(Row(MusicContextMenus.GetOpenMenu(mobileList)!, "打开"));
                await WaitAsync(window, () => model.IsIdle && view.SelectedSectionIndex == 0
                    && model.BrowsedPlaylistForMenu?.Id == unchanged.Id, token);
                TestAssert.Equal(tracks[1].Id, playback.Snapshot.Track!.Id,
                    "Opening a playlist from the touch menu must retain the music already playing in the background.");
                var phoneCard = Card(view, tracks[0].Id);
                point = phoneCard.TranslatePoint(new Point(phoneCard.Bounds.Width / 2, phoneCard.Bounds.Height / 2), window)!.Value;
                using var cardTouch = window.TouchBegin(point, RawInputModifiers.None);
                await WaitAsync(window, () => MusicContextMenus.GetOpenMenu(phoneCard) is not null, token);
                window.TouchEnd(cardTouch, point, RawInputModifiers.None);
                Pump(window);
                var mobile = view.GetVisualDescendants().OfType<MobileMusicView>().Single();
                TestAssert.True(!mobile.FindControl<TKCode.Arc.Mobile.Sheet>("MobileTrackSheet")!.IsOpen,
                    "A card long press must open actions without also triggering its normal inspector tap.");
                MusicContextMenus.Close(phoneCard);
                TestAssert.Equal(0, handler.Requests, "Local context actions cannot contact a source or credential provider.");
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>Gets a realized music card by stable song identity.</summary>
        /// <param name="view">The production root.</param>
        /// <param name="id">The exact song identity.</param>
        /// <returns>The visible artwork control.</returns>
        private static SongCard Card(MainView view, string id)
        {
            return view.GetVisualDescendants().OfType<SongCard>().Single(item => item.DataContext is SongCardViewModel card && card.Track.Id == id);
        }

        /// <summary>Opens the row's actual native context gesture after scrolling it into view.</summary>
        /// <param name="window">The rendering window.</param>
        /// <param name="list">The full playlist menu.</param>
        /// <param name="playlist">The captured row to open.</param>
        private static void OpenPlaylistMenu(Window window, ListBox list, MusicPlaylist playlist)
        {
            list.ScrollIntoView(playlist);
            Pump(window);
            RightClick(list.ContainerFromItem(playlist)!);
            Pump(window);
            TestAssert.True(MusicContextMenus.GetOpenMenu(list) is not null, "A real right click must open the exact playlist row's Arc menu.");
        }

        /// <summary>Gets a named native Arc menu action.</summary>
        /// <param name="menu">The opened surface.</param>
        /// <param name="header">The exact action label.</param>
        /// <returns>The corresponding actual menu row.</returns>
        private static MenuEntry Row(MenuSurface menu, string header) => menu.Items.OfType<MenuEntry>().Single(item => Equals(item.Header, header));

        /// <summary>Injects an actual right-button gesture into the target's current native root.</summary>
        /// <param name="control">The exact visible card or row.</param>
        private static void RightClick(Control control)
        {
            var top = TopLevel.GetTopLevel(control)!;
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
            top.MouseDown(point, MouseButton.Right);
            top.MouseUp(point, MouseButton.Right);
        }

        /// <summary>Activates a real menu or dialog row, including native popup roots.</summary>
        /// <param name="control">The visible native action.</param>
        private static void Tap(Control control)
        {
            var top = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("The context action was not attached to a real popup.");
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
            top.MouseDown(point, MouseButton.Left);
            top.MouseUp(point, MouseButton.Left);
        }

        /// <summary>Advances actual native frames until a specific operation publishes its result.</summary>
        /// <param name="window">The rendering window.</param>
        /// <param name="ready">The semantic completion condition.</param>
        /// <param name="token">Cancels a stalled operation.</param>
        /// <returns>A task representing the completed condition.</returns>
        private static async Task WaitAsync(Window window, Func<bool> ready, CancellationToken token)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(TimeSpan.FromSeconds(4));
            while (!ready())
            {
                Pump(window);
                await Task.Delay(10, bounded.Token);
            }

            Pump(window);
        }

        /// <summary>Flushes layout and all real headless popup renderers.</summary>
        /// <param name="window">The main rendering window.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Captures the actual popup surface without including local application data paths.</summary>
        /// <param name="menu">The opened native Arc surface.</param>
        /// <param name="name">The ignored diagnostic file name.</param>
        private static void SaveMenu(MenuSurface menu, string name)
        {
            using var frame = TopLevel.GetTopLevel(menu)!.CaptureRenderedFrame()!;
            var output = Path.Combine(Environment.CurrentDirectory, ".work");
            Directory.CreateDirectory(output);
            frame.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
        }

        /// <summary>Rejects every unexpected network operation.</summary>
        private sealed class NoNetwork : HttpMessageHandler
        {
            /// <summary>Gets the attempted request count.</summary>
            public int Requests { get; private set; }
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                throw new InvalidOperationException("Context UI fixtures cannot use HTTP.");
            }
        }

        /// <summary>Keeps every fixture outside the operating system's credential store.</summary>
        private sealed class NoCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No fixture credentials exist.");
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No fixture credentials exist.");
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No fixture credentials exist.");
        }
    }
}
