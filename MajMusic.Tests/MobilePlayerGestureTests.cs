#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using MajMusic.Presentation.Theme;
using ArcSheet = TKCode.Arc.Mobile.Sheet;
using ArcSlider = TKCode.Arc.Slider;

namespace MajMusic.Tests
{
    /// <summary>Checks that a player-only upward swipe opens the actual loaded song without stealing browser or seek gestures.</summary>
    internal static class MobilePlayerGestureTests
    {
        /// <summary>Runs the actual mobile controls in their own complete application lifetime.</summary>
        /// <returns>A task representing all real gesture and playback-owner assertions.</returns>
        public static Task RunAsync()
        {
            return HeadlessProcessIsolation.RunAsync("mobile-player-gesture", RunHeadlessAsync);
        }

        /// <summary>Exercises player swipes, missing playback, competing browse selection and existing timeline gestures.</summary>
        /// <returns>A task representing the real mobile pointer regressions.</returns>
        private static async Task RunHeadlessAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await session.Dispatch<bool>(async () =>
            {
                var workspace = new LibraryExperienceTests.ExperienceWorkspace();
                workspace.Library.AddRange(Enumerable.Range(0, 120).Select(index => new MusicTrack
                {
                    Id = "gesture-" + index,
                    Title = "Generated gesture song " + index,
                    Audio = new MediaResource { LocalPath = Path.Combine(Path.GetTempPath(), "generated-gesture-" + index + ".mp3") },
                    Duration = TimeSpan.FromMinutes(2)
                }));
                var backend = new HeadlessTests.TimelineBackend();
                var playback = new PlaybackService(backend, resolveAudioPath: (track, token) => workspace.ResolveAudioAsync(track, false, token));
                await using var model = new MainViewModel(workspace, playback);
                await model.InitializeAsync().WaitAsync(timeout.Token);
                model.ShowLibraryModeCommand.Execute(null);
                await model.HomeUpdate.WaitAsync(timeout.Token);
                var mobile = new MobileMusicView { DataContext = model };
                var window = new Window { Content = mobile, Width = 390, Height = 844 };
                try
                {
                    window.Show();
                    Layout(window);
                    var player = mobile.FindControl<Border>("MobileMiniPlayer")!;
                    var sheet = mobile.FindControl<ArcSheet>("MobileTrackSheet")!;
                    Swipe(window, PlayerPoint(player, window), new Vector(0, -90));
                    TestAssert.True(!sheet.IsOpen && model.PlaybackTrack is null,
                        "An empty player's upward swipe cannot open an unrelated browsing selection.");

                    var playing = model.Tracks[0];
                    var selected = model.Tracks[1];
                    model.SelectedTrack = playing;
                    await model.PlaySelectedAsync().WaitAsync(timeout.Token);
                    await WaitAsync(() => model.PlaybackTrack?.Id == playing.Id, timeout.Token);
                    model.SelectedTrack = selected;
                    Swipe(window, PlayerPoint(player, window), new Vector(0, -90));
                    await WaitAsync(() => sheet.IsOpen, timeout.Token);
                    TestAssert.True(model.SelectedTrack?.Id == playing.Id && playback.Queue.Current?.Id == playing.Id
                        && model.IsPlaying && playback.Queue.Tracks.Count == 120,
                        "The player gesture must inspect actual playing A even when browsing selected B, while retaining the loaded queue and transport.");
                    await ValidateSheetDetentsAsync(window, (InterruptibleSongSheet)sheet, model, playback, timeout.Token);
                    sheet.IsOpen = false;
                    await ((InterruptibleSongSheet)sheet).SheetUpdate.WaitAsync(timeout.Token);
                    Layout(window);

                    foreach (var movement in new[] { new Vector(0, 0), new Vector(0, -20) })
                    {
                        model.SelectedTrack = selected;
                        Swipe(window, PlayerPoint(player, window), movement);
                        await WaitAsync(() => sheet.IsOpen, timeout.Token);
                        await ((InterruptibleSongSheet)sheet).SheetUpdate.WaitAsync(timeout.Token);
                        TestAssert.True(!((InterruptibleSongSheet)sheet).IsExpanded && model.SelectedTrack?.Id == playing.Id
                            && playback.Queue.Current?.Id == playing.Id && model.IsPlaying,
                            "Both a player tap and a light upward pull must open half-height details for the actual playing song.");
                        sheet.IsOpen = false;
                        await ((InterruptibleSongSheet)sheet).SheetUpdate.WaitAsync(timeout.Token);
                        Layout(window);
                    }

                    foreach (var movement in new[] { new Vector(100, -30), new Vector(0, 60) })
                    {
                        model.SelectedTrack = selected;
                        Swipe(window, PlayerPoint(player, window), movement);
                        await ((InterruptibleSongSheet)sheet).SheetUpdate.WaitAsync(timeout.Token);
                        TestAssert.True(!sheet.IsOpen && model.SelectedTrack?.Id == selected.Id,
                            "A horizontal or downward player movement cannot replace the inspected song or open the sheet.");
                    }

                    var browser = mobile.GetVisualDescendants().OfType<BrowseTracksView>().Single();
                    var list = browser.ActiveList;
                    var point = list.TranslatePoint(new Point(8, Math.Min(220, list.Bounds.Height * .7)), window)!.Value;
                    window.MouseWheel(point, new Vector(0, -8), RawInputModifiers.None);
                    Layout(window);
                    TestAssert.True(list.GetVisualDescendants().OfType<ScrollViewer>().Any(scroll => scroll.Offset.Y > 0) && !sheet.IsOpen,
                        "Normal song-list vertical scrolling must remain available without invoking the bottom-player gesture.");

                    var timeline = mobile.FindControl<ArcSlider>("MobilePlaybackTimeline")!;
                    var thumb = ArcTestVisuals.SliderThumb(timeline);
                    var thumbPoint = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
                    var before = backend.SeekCount;
                    Swipe(window, thumbPoint, new Vector(70, 0));
                    await model.TransportUpdate.WaitAsync(timeout.Token);
                    await WaitAsync(() => backend.SeekCount > before, timeout.Token);
                    TestAssert.True(!sheet.IsOpen && model.PlaybackTrack?.Id == playing.Id,
                        "A horizontal touch on the existing timeline must still seek the playing song instead of opening details.");
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Exercises real half-to-full handle dragging and cancellation at both resting points without changing playback.</summary>
        /// <param name="window">The headless native input owner.</param>
        /// <param name="sheet">The actual production details sheet.</param>
        /// <param name="model">The inspected-song presentation owner.</param>
        /// <param name="playback">The existing playing queue.</param>
        /// <param name="token">Cancels a missing gesture settlement.</param>
        /// <returns>A task representing direct tracking, expansion and cancellation.</returns>
        private static async Task ValidateSheetDetentsAsync(Window window, InterruptibleSongSheet sheet, MainViewModel model,
            PlaybackService playback, CancellationToken token)
        {
            await sheet.SheetUpdate.WaitAsync(token);
            Layout(window);
            var track = playback.Snapshot.Track!.Id;
            var handle = sheet.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Handle");
            var halfOffset = sheet.PanelOffset;
            TestAssert.True(sheet.IsOpen && !sheet.IsExpanded && halfOffset > 100,
                "A player pull must begin at the real half-height song-details resting point.");
            var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
            using (var cancelled = window.TouchBegin(start, RawInputModifiers.None))
            {
                window.TouchMove(cancelled, start + new Vector(0, -20), RawInputModifiers.None);
                window.TouchMove(cancelled, start + new Vector(0, -70), RawInputModifiers.None);
                Layout(window);
                TestAssert.True(sheet.PanelOffset < halfOffset - 20 && sheet.PanelOffset > 0,
                    "A held upward handle movement must directly expose more content before committing a detent.");
            }
            Layout(window);
            await sheet.SheetUpdate.WaitAsync(token);
            TestAssert.True(sheet.IsOpen && !sheet.IsExpanded && Math.Abs(sheet.PanelOffset - halfOffset) < 2,
                "Cancelling an upward contact must restore half height without committing a full view or dismissing it.");

            start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
            Swipe(window, start, new Vector(0, -90));
            await sheet.SheetUpdate.WaitAsync(token);
            Layout(window);
            TestAssert.True(sheet.IsOpen && sheet.IsExpanded && model.IsSongDetailsExpanded && sheet.PanelOffset < 1,
                "A committed upward handle gesture must move the existing details pane to its full resting point.");
            start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
            using (var cancelled = window.TouchBegin(start, RawInputModifiers.None))
            {
                window.TouchMove(cancelled, start + new Vector(0, 15), RawInputModifiers.None);
                window.TouchMove(cancelled, start + new Vector(0, 65), RawInputModifiers.None);
                Layout(window);
                TestAssert.True(sheet.PanelOffset > 20, "A held downward movement must track from the full resting point.");
            }
            Layout(window);
            await sheet.SheetUpdate.WaitAsync(token);
            TestAssert.True(sheet.IsOpen && sheet.IsExpanded && sheet.PanelOffset < 1,
                "Cancelling a downward contact must restore full height instead of retaining the gesture's transient displacement.");
            model.CloseExpandedSongDetailsCommand.Execute(null);
            await sheet.SheetUpdate.WaitAsync(token);
            Layout(window);
            TestAssert.True(sheet.IsOpen && !sheet.IsExpanded && Math.Abs(sheet.PanelOffset - halfOffset) < 2
                && playback.Snapshot.Track?.Id == track && playback.Queue.Current?.Id == track && model.IsPlaying,
                "Returning to compact details must preserve the original loaded track, queue and playing state.");
        }

        /// <summary>Locates the noninteractive information area inside the persistent player.</summary>
        /// <param name="player">The bottom player panel.</param>
        /// <param name="window">The native input root.</param>
        /// <returns>A point away from transport buttons and the seek slider.</returns>
        private static Point PlayerPoint(Border player, Window window)
        {
            return player.TranslatePoint(new Point(26, 25), window)!.Value;
        }

        /// <summary>Sends an actual touch gesture, including movement beyond the initially pressed panel.</summary>
        /// <param name="window">The headless native input root.</param>
        /// <param name="start">The pointer origin in root coordinates.</param>
        /// <param name="movement">The signed final pointer displacement.</param>
        private static void Swipe(Window window, Point start, Vector movement)
        {
            using var touch = window.TouchBegin(start, RawInputModifiers.None);
            window.TouchMove(touch, start + movement * .5, RawInputModifiers.None);
            window.TouchMove(touch, start + movement, RawInputModifiers.None);
            window.TouchEnd(touch, start + movement, RawInputModifiers.None);
            Layout(window);
        }

        /// <summary>Processes templates, bindings and actual compositor input state.</summary>
        /// <param name="window">The currently rendered mobile test window.</param>
        private static void Layout(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Waits for Core transport notifications while pumping the UI owner.</summary>
        /// <param name="condition">The expected bound result.</param>
        /// <param name="token">Cancels a missing result.</param>
        /// <returns>A task representing the observed UI or transport change.</returns>
        private static async Task WaitAsync(Func<bool> condition, CancellationToken token)
        {
            while (!condition())
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5, token);
            }

            Dispatcher.UIThread.RunJobs();
        }
    }
}
