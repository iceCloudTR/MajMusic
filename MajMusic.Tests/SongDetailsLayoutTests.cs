#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using TKCode.Arc.Motion;
using ArcKeyboard = TKCode.Arc.Mobile.KeyboardInsetSource;
using ArcPage = TKCode.Arc.Mobile.Page;

namespace MajMusic.Tests
{
    /// <summary>Renders real desktop and phone song details with synthetic comments and no remote service.</summary>
    internal static class SongDetailsLayoutTests
    {
        /// <summary>Runs the complete details view in its own isolated headless application.</summary>
        /// <returns>A task representing real pointer, geometry and image checks.</returns>
        public static Task RunAsync() => HeadlessProcessIsolation.RunAsync("song-details-layout", RunHeadlessAsync);

        /// <summary>Exercises actual expanded controls and captures desktop and two mobile resting points.</summary>
        /// <returns>A task representing the real bound layouts.</returns>
        private static async Task RunHeadlessAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await session.Dispatch<bool>(async () =>
            {
                var workspace = new DetailsWorkspace();
                workspace.CommentsOverride = (track, account, page, _) => Task.FromResult(SocialPresentationTests.SocialWorkspace.Page(track, account, page) with { Total = 21 });
                var backend = new LibraryExperienceTests.ExperienceBackend();
                var playback = new PlaybackService(backend, resolveAudioPath: (track, token) => workspace.ResolveAudioAsync(track, false, token));
                await using var model = new MainViewModel(workspace, playback);
                await model.InitializeAsync().WaitAsync(timeout.Token);
                model.ShowLibraryModeCommand.Execute(null);
                await model.HomeUpdate.WaitAsync(timeout.Token);
                model.SelectedTrack = workspace.Library[0];
                await model.PlaySelectedAsync().WaitAsync(timeout.Token);
                var playing = playback.Snapshot.Track!.Id;
                var queue = playback.Queue.Tracks.Select(track => track.Id).ToArray();
                model.SelectedTrack = workspace.Library[1];
                var main = new MainView { DataContext = model };
                var window = new Window { Content = main, Width = 1100, Height = 800 };
                ArcMotion.SetLevel(window, ArcMotionLevel.Off);
                var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "social-details");
                Directory.CreateDirectory(output);
                try
                {
                    window.Show();
                    Layout(window);
                    var source = main.FindControl<ContentControl>("DesktopSidebarDetailsSlot")!;
                    var flight = main.FindControl<SharedSongCardFlight>("DesktopSongCardFlight")!;
                    var card = main.FindControl<Border>("LibraryDetails")!;
                    var originalX = card.TranslatePoint(default, window)!.Value.X;
                    var originalSize = card.Bounds.Size;
                    TestAssert.True(ReferenceEquals(source.Content, card), "The ordinary inspector must initially own the one existing information card.");
                    var expand = main.FindControl<Control>("DesktopExpandDetailsButton")!;
                    var actions = main.FindControl<Control>("DesktopPlaylistActions")!;
                    TestAssert.True(expand.TranslatePoint(default, card)!.Value.Y >= actions.TranslatePoint(new Point(0, actions.Bounds.Height), card)!.Value.Y,
                        "The ordinary details action must sit below the song information and playlist controls instead of covering artwork.");
                    Tap(window, expand);
                    await flight.MoveUpdate.WaitAsync(timeout.Token);
                    await model.SongSocialUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    var desktop = Find(main, "DesktopExpandedSongDetails");
                    var destination = main.FindControl<DesktopSongDetailsView>("DesktopSongDetails")!.FindControl<ContentControl>("DesktopSongSummarySlot")!;
                    TestAssert.True(model.IsSongDetailsExpanded && desktop.IsEffectivelyVisible && desktop.Bounds.Height > 320,
                        "The real desktop expand button must reveal an actionable details panel with the selected song's metadata.");
                    TestAssert.True(ReferenceEquals(destination.Content, card) && ReferenceEquals(main.FindControl<Border>("LibraryDetails"), card)
                        && source.Content is null && card.TranslatePoint(default, window)!.Value.X < originalX - 100,
                        "Expansion must move the original information Border into the left slot rather than constructing a second card.");
                    ValidateDesktopPanes(window, desktop);
                    AssertPlaybackUnchanged(playback, backend, playing, queue);
                    SaveFrame(window, Path.Combine(output, "ui-social-desktop-expanded.png"));
                    ValidateDesktopComments(window, desktop, model);
                    SaveFrame(window, Path.Combine(output, "ui-social-desktop-comments.png"));
                    await ValidateDesktopScrollingAsync(window, desktop, workspace, model, output, timeout.Token);
                    window.Width = 720;
                    window.Height = 540;
                    model.SongComments.Single().ReplyCommand.Execute(null);
                    Layout(window);
                    await flight.MoveUpdate.WaitAsync(timeout.Token);
                    ValidateDesktopPanes(window, desktop);
                    SaveFrame(window, Path.Combine(output, "ui-social-desktop-short-comments.png"));
                    model.CancelSongReplyCommand.Execute(null);
                    Tap(window, Find(desktop, "CloseExpandedSongDetailsButton"));
                    await flight.MoveUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    TestAssert.True(!model.IsSongDetailsExpanded, "The desktop close action returns to the existing music view.");
                    TestAssert.True(ReferenceEquals(source.Content, card) && destination.Content is null,
                        "Closing expanded details must return the same card to its original sidebar slot.");
                    AssertFillsSlot(card, source, "The returned card must fill the original sidebar slot at the current window size.");
                    TestAssert.True(main.GetVisualDescendants().All(view => view.Name != "DesktopChartPreviewButton"), "Artwork must have no separate chart entry.");
                    Tap(window, main.FindControl<Control>("DesktopExpandDetailsButton")!);
                    await flight.MoveUpdate.WaitAsync(timeout.Token);
                    await model.SongSocialUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    await ValidateDesktopPracticeAsync(window, desktop, model, workspace, timeout.Token);
                    Layout(window);
                    TestAssert.True(model.IsSongDetailsExpanded && model.IsSongChartPreviewSelected
                        && Find(desktop, "ChartPreviewHost").IsEffectivelyVisible && !Find(desktop, "SongCommentsList").IsEffectivelyVisible,
                        "Selecting a difficulty from song details must mount the chart in its dedicated pane.");
                    AssertPlaybackUnchanged(playback, backend, playing, queue, PlaybackState.Paused);
                    SaveFrame(window, Path.Combine(output, "ui-social-desktop-chart-preview.png"));
                    TapTab(window, desktop, "DesktopSongPreviewTabs", 0);
                    TestAssert.True(!model.IsSongChartPreviewSelected && Find(desktop, "SongCommentsList").IsEffectivelyVisible,
                        "The desktop comments action must restore the discussion without changing the inspected song or the original information card.");
                    Tap(window, Find(desktop, "CloseExpandedSongDetailsButton"));
                    await flight.MoveUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    TestAssert.True(ReferenceEquals(source.Content, card), "Preview navigation must return the original card without leaving another owner behind.");
                    AssertFillsSlot(card, source, "Returning from preview must restore the original sidebar layout rather than retain the enlarged target size.");
                    window.Width = 1100;
                    window.Height = 800;
                    Layout(window);
                    TestAssert.True(Math.Abs(card.Bounds.Width - originalSize.Width) < 1 && Math.Abs(card.Bounds.Height - originalSize.Height) < 1,
                        "Restoring the original window after the return flight must reproduce the card's original sidebar width and height.");

                    window.Width = 390;
                    window.Height = 844;
                    Layout(window);
                    var mobile = main.GetVisualDescendants().OfType<MobileMusicView>().Single();
                    mobile.OpenTrackDetails();
                    Layout(window);
                    var sheet = mobile.FindControl<InterruptibleSongSheet>("MobileTrackSheet")!;
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    var panel = Find(sheet, "PART_Panel");
                    var header = Find(mobile, "MobileSongHeader");
                    var compactHeight = VisiblePanelHeight(panel, sheet);
                    var compactHeader = BoundsIn(header, panel);
                    AssertCompactPanel(sheet, panel, header);
                    SaveFrame(window, Path.Combine(output, "ui-social-phone-compact.png"));
                    window.Height = 940;
                    Layout(window);
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    TestAssert.True(Math.Abs(VisiblePanelHeight(panel, sheet) - compactHeight) < 2,
                        "Extra screen height must not add blank rows to the compact song card.");
                    AssertCompactPanel(sheet, panel, header);
                    window.Height = 844;
                    Layout(window);
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    var dragStart = header.TranslatePoint(new Point(24, 24), window)!.Value;
                    using (var drag = window.TouchBegin(dragStart, RawInputModifiers.None))
                    {
                        window.TouchMove(drag, dragStart + new Vector(0, -12), RawInputModifiers.None);
                        window.TouchMove(drag, dragStart + new Vector(0, -48), RawInputModifiers.None);
                        window.TouchEnd(drag, dragStart + new Vector(0, -48), RawInputModifiers.None);
                    }
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    await model.SongSocialUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    TestAssert.True(sheet.IsExpanded && model.IsSongDetailsExpanded && VisiblePanelHeight(panel, sheet) > compactHeight + 120,
                        "A short upward drag on the mobile summary must reveal the lower panes with meaningful space for their content.");
                    TestAssert.True(ReferenceEquals(header, Find(mobile, "MobileSongHeader")) && header.IsEffectivelyVisible
                        && BoundsIn(header, panel) == compactHeader,
                        "The original song summary stays in the same position within the sheet as it expands.");
                    var expanded = mobile.GetVisualDescendants().OfType<SongDetailsView>().Single(view => view.IsEffectivelyVisible);
                    SaveFrame(window, Path.Combine(output, "ui-social-phone-full.png"));
                    var expandedHeader = BoundsIn(header, window);
                    var mobileTabs = Find(expanded, "SongDetailsTabs");
                    TestAssert.True(expandedHeader.Bottom <= BoundsIn(mobileTabs, window).Y + 1,
                        "The expanded tabs must be arranged below the persistent song summary.");
                    foreach (var level in new[] { 2, 5 })
                    {
                        TapTab(window, expanded, "SongPreviewSegments", level);
                        TestAssert.True(model.SelectedMajViewDifficulty == level && Find(expanded, "ChartPreviewHost").IsEffectivelyVisible
                            && !Find(expanded, "SongCommentsList").IsEffectivelyVisible,
                            "Selecting a real mobile difficulty switches only the lower content to the chart preview.");
                        TestAssert.True(header.IsEffectivelyVisible && BoundsIn(header, window) == expandedHeader,
                            "Switching difficulties must not move or replace the song summary.");
                    }
                    ValidateMobileComments(window, expanded, model);
                    TestAssert.True(header.IsEffectivelyVisible && BoundsIn(header, window) == expandedHeader,
                        "Returning to comments must keep the same song summary fixed above the discussion.");
                    SaveFrame(window, Path.Combine(output, "ui-social-phone-comments.png"));
                    var keyboard = new ArcKeyboard();
                    ArcPage.SetKeyboardInsetSource(mobile, keyboard);
                    var composer = (TextBox)Find(expanded, "SongCommentInput");
                    Tap(window, composer);
                    TestAssert.True(composer.IsFocused, "The comment field must receive focus when tapped.");
                    var previousSheetHeight = sheet.Bounds.Height;
                    keyboard.Set(280, TimeSpan.Zero);
                    Layout(window);
                    TestAssert.True(sheet.Bounds.Height < previousSheetHeight,
                        "Showing the keyboard must resize the song details.");
                    TestAssert.True(composer.IsFocused,
                        "The comment field must keep focus when the keyboard resizes the song details.");
                    keyboard.Set(0, TimeSpan.Zero);
                    Layout(window);
                    window.Height = 420;
                    model.SongComments.Single().ReplyCommand.Execute(null);
                    Layout(window);
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    ValidateMobileComposerBounds(window, expanded, sheet);
                    SaveFrame(window, Path.Combine(output, "ui-social-phone-short-comments.png"));
                    model.CancelSongReplyCommand.Execute(null);
                    window.Height = 844;
                    Layout(window);
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    Tap(window, Find(mobile, "CloseExpandedSongDetailsButton"));
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    TestAssert.True(!model.IsSongDetailsExpanded && Math.Abs(VisiblePanelHeight(panel, sheet) - compactHeight) < 2,
                        "The expanded close action returns to the compact content height.");
                    AssertCompactPanel(sheet, panel, header);
                    TestAssert.Equal(0, workspace.Posts.Count, "Rendering and inspecting synthetic source comments must never submit a comment implicitly.");
                    AssertPlaybackUnchanged(playback, backend, playing, queue, PlaybackState.Paused);
                    await ValidatePreviewDrawerMotionAsync(window, sheet, workspace, model, timeout.Token);
                }
                finally
                {
                    window.Close();
                }
                return true;
            }, timeout.Token);
        }

        /// <summary>Checks the normal desktop entry, corner clearance, client-area fullscreen and return to the same chart.</summary>
        private static async Task ValidateDesktopPracticeAsync(Window window, Control desktop, MainViewModel model, DetailsWorkspace workspace, CancellationToken token)
        {
            var factory = ChartPreviewPane.CreateEmbeddedView;
            var renderer = new MeasuringDisc();
            var session = new DrawerPreviewSession();
            workspace.Preview = session;
            ChartPreviewPane.CreateEmbeddedView = (_, _) => renderer;
            try
            {
                TapTab(window, desktop, "DesktopSongPreviewTabs", 2);
                Layout(window);
                var host = (ContentControl)Find(desktop, "PreviewSurface");
                TestAssert.True(ReferenceEquals(host.Content, renderer), "The normal difficulty tab must mount the tested renderer.");
                var original = (window.WindowState, window.ClientSize, window.Position);
                var document = session.Intent!;
                var times = ChartNoteTimeline.Create([[1000], [2000], [3000], [4000], [5000]], 120)!;
                session.Report(new(document.Generation, document.Revision, 0, 120, false, Notes: times));
                session.Report(new(document.Generation, document.Revision, 62, 120, false));
                Layout(window);
                var stage = (PracticeStage)Find(desktop, "Stage");
                var inlineDisc = BoundsIn(host, stage);
                AssertCornerLayout(stage, inlineDisc);
                Tap(window, Find(desktop, "PracticeFullscreen"));
                Layout(window);
                var pane = desktop.GetVisualDescendants().OfType<ChartPreviewPane>().Single();
                var bounds = BoundsIn(pane, window);
                TestAssert.True(original == (window.WindowState, window.ClientSize, window.Position) && model.IsChartFullscreen
                    && Math.Abs(bounds.X) < 2 && Math.Abs(bounds.Y) < 2 && Math.Abs(bounds.Width - window.ClientSize.Width) < 3
                    && Math.Abs(bounds.Height - window.ClientSize.Height) < 3 && !Find(window, "PlaybackBar").IsEffectivelyVisible,
                    $"Practice should fill the existing app client area, with no desktop chrome: {bounds} within {window.ClientSize}.");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Layout(window);
                TestAssert.True(!model.IsChartFullscreen && model.IsSongDetailsExpanded && model.SelectedMajViewDifficulty == 2
                    && model.PositionSeconds == 62 && ReferenceEquals(host.Content, renderer) && !session.IsReleased
                    && BoundsIn(host, stage) == inlineDisc && original == (window.WindowState, window.ClientSize, window.Position),
                    "Escape must restore the exact inline chart, cursor and window.");

                TestAssert.True(PracticeStage.GetDiscClip(435, 434, 1) == new PixelRect(0, 0, 434, 434)
                    && PracticeStage.GetDiscClip(400, 650, .5) == new PixelRect(100, 225, 200, 200),
                    "DPI rounding and a rotating rectangular host must keep the native aperture circular and centered.");
                ArcMotion.SetLevel(pane, ArcMotionLevel.Full);
                async Task WaitForAsync(Func<bool> condition)
                {
                    while (!condition())
                    {
                        await Task.Delay(8, token);
                        Layout(window);
                    }
                }
                void Back() => window.RaiseEvent(new RoutedEventArgs(TopLevel.BackRequestedEvent));

                model.TogglePlayPauseCommand.Execute(null);
                Tap(window, Find(desktop, "PracticeFullscreen"));
                TestAssert.True(!model.IsPlaying, "The first touch must pause at the current beat.");
                await WaitForAsync(() => renderer.LastAperture < .98);
                Back();
                await WaitForAsync(() => renderer.LastAperture == 1);
                TestAssert.True(!model.IsChartFullscreen && ReferenceEquals(host.Content, renderer),
                    "Back during the closing iris must cancel entry without remounting the chart.");
                Tap(window, Find(desktop, "PracticeFullscreen"));
                await WaitForAsync(() => model.IsChartFullscreen && renderer.LastAperture == 1);
                renderer.SawIntermediate = false;
                Back();
                await WaitForAsync(() => !model.IsChartFullscreen && renderer.LastAperture == 1);
                TestAssert.True(renderer.SawIntermediate && model.PositionSeconds == 62 && BoundsIn(host, stage) == inlineDisc,
                    "Returning must animate the native surface and restore the original bounds and playhead.");
                ArcMotion.SetLevel(pane, ArcMotionLevel.Off);
            }
            finally
            {
                ChartPreviewPane.CreateEmbeddedView = factory;
            }
        }

        /// <summary>Records the aperture delivered to the same surface that owns the native pixels.</summary>
        private sealed class MeasuringDisc : Border, IPracticeDisc
        {
            public double LastAperture { get; private set; } = 1;
            public bool SawIntermediate { get; set; }

            public void SetAperture(double fraction)
            {
                LastAperture = fraction;
                SawIntermediate |= fraction is > 0 and < 1;
            }
        }

        /// <summary>Verifies four native corner groups clear the disc and the limiting dimension is actually used.</summary>
        private static void AssertCornerLayout(PracticeStage stage, Rect disc)
        {
            var navigation = BoundsIn(Find(stage, "PracticeNavigation"), stage);
            var options = BoundsIn(Find(stage, "PracticeTools"), stage);
            var left = BoundsIn(Find(stage, "PracticeNotes"), stage);
            var right = BoundsIn(Find(stage, "PracticeRightNotes"), stage);
            var area = new Rect(stage.Bounds.Size);
            TestAssert.True(navigation.X < 8 && navigation.Y < 8 && options.Y < 8 && area.Right - options.Right < 8
                && left.X < 8 && area.Bottom - left.Bottom < 8 && area.Right - right.Right < 8 && area.Bottom - right.Bottom < 8,
                "The two tool groups and two note groups must occupy the stage corners.");
            TestAssert.True(Math.Abs(disc.Width - disc.Height) <= 1 && Math.Abs(disc.Center.X - area.Center.X) <= 1
                && Math.Abs(disc.Center.Y - area.Center.Y) <= 1 && disc.Width > 0,
                $"The centered chart must use the available short side: {disc} in {area}.");
            var maximum = Math.Min(area.Width, area.Height) / 2 - 4;
            foreach (var corner in new[] { navigation, options, left, right })
            {
                var closest = new Point(Math.Clamp(disc.Center.X, corner.X, corner.Right), Math.Clamp(disc.Center.Y, corner.Y, corner.Bottom));
                var distance = new Vector(closest.X - disc.Center.X, closest.Y - disc.Center.Y).Length;
                maximum = Math.Min(maximum, distance - 4);
                TestAssert.True(distance >= disc.Width / 2 - 1,
                    $"Neither a corner button nor the count text may cover the playfield: {corner} / {disc}.");
            }
            TestAssert.True(Math.Abs(disc.Width - 2 * maximum) <= 3, $"Use the largest centered disc up to layout rounding: {disc.Width} / {maximum * 2}.");
        }

        /// <summary>Keeps the same rendered preview in the drawer through a pressed drag, reversal and collapse.</summary>
        /// <param name="window">The headless input owner.</param>
        /// <param name="sheet">The real mobile details sheet.</param>
        /// <param name="workspace">The owner of the synthetic loopback session.</param>
        /// <param name="model">The view model to inspect and open the chart.</param>
        /// <param name="token">Cancels an unfinished transition.</param>
        private static async Task ValidatePreviewDrawerMotionAsync(Window window, InterruptibleSongSheet sheet, DetailsWorkspace workspace,
            MainViewModel model, CancellationToken token)
        {
            var factory = ChartPreviewPane.CreateEmbeddedView;
            var preview = new Border();
            ChartPreviewPane.CreateEmbeddedView = (_, _) => preview;
            try
            {
                var session = new DrawerPreviewSession();
                workspace.Preview = session;
                var entry = (ContentControl)Find(sheet, "MobileExpandDetailsButton");
                TestAssert.Equal("歌曲详情", entry.Content as string, "The compact card must expose the named song details entry.");
                Tap(window, entry);
                await sheet.SheetUpdate.WaitAsync(token);
                Layout(window);
                await model.SelectMajViewDifficultyAsync(2).WaitAsync(token);
                Layout(window);
                var host = (ContentControl)Find(sheet, "PreviewSurface");
                TestAssert.True(ReferenceEquals(host.Content, preview), "A resolved chart must mount its preview before the drawer moves.");

                var document = session.Intent!;
                var notes = ChartNoteTimeline.Create([[500, 5000, 5000], [], [], [8000], []], 90)!;
                session.Report(new(document.Generation, document.Revision, 0, 90, false, Notes: notes));
                session.Report(new(document.Generation, document.Revision, 5, 90, false));
                Layout(window);
                var inlineStage = (PracticeStage)Find(sheet, "Stage");
                AssertCornerLayout(inlineStage, BoundsIn(host, inlineStage));
                var legend = (TextBlock)Find(sheet, "PracticeNotes");
                var tapCount = legend.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Single(run => run.Text == "3/3");
                TestAssert.True(model.BeginSeek(), "Practice must allow scrubbing a prepared chart.");
                model.PositionSeconds = 1;
                TestAssert.Equal("1/3", tapCount.Text, "The on-screen note row must update backwards during the drag.");
                await model.CommitSeekAsync();
                Layout(window);
                var lockButton = Find(sheet, "PracticeLock");
                var lockPoint = lockButton.TranslatePoint(new Point(lockButton.Bounds.Width / 2, lockButton.Bounds.Height / 2), window)!.Value;
                Tap(window, lockButton);
                TestAssert.True(model.IsPracticeLocked, "Inline practice must lock with one tap.");
                Tap(window, Find(sheet, "CloseExpandedSongDetailsButton"));
                window.RaiseEvent(new RoutedEventArgs(TopLevel.BackRequestedEvent));
                TestAssert.True(model.IsPracticeLocked && sheet.IsExpanded, "Locked practice must consume sheet actions and system back.");
                using (var hold = window.TouchBegin(lockPoint, RawInputModifiers.None))
                {
                    await Task.Delay(180, token);
                    window.TouchEnd(hold, lockPoint, RawInputModifiers.None);
                }
                TestAssert.True(model.IsPracticeLocked, "Releasing before half a second must cancel unlock.");
                using (var hold = window.TouchBegin(lockPoint, RawInputModifiers.None))
                {
                    await Task.Delay(580, token);
                    window.TouchEnd(hold, lockPoint, RawInputModifiers.None);
                }
                TestAssert.True(!model.IsPracticeLocked, "A continuous half-second hold must unlock without another tap.");
                Tap(window, Find(sheet, "PracticeFullscreen"));
                Layout(window);
                TestAssert.True(model.IsChartFullscreen && ReferenceEquals(host.Content, preview) && !model.IsPlaying,
                    "Entering practice must keep the renderer mounted and pause it.");
                var timeline = (PracticeTimeline)Find(sheet, "PracticeOverview");
                TestAssert.True(!timeline.Vertical && timeline.IsEffectivelyVisible, "Portrait practice uses a horizontal timeline.");
                var width = window.Width;
                var height = window.Height;
                window.Width = 844;
                window.Height = 390;
                Layout(window);
                TestAssert.True(timeline.Vertical && ReferenceEquals(host.Content, preview), "Rotating must preserve the chart and turn its time axis vertical.");
                window.Width = width;
                window.Height = height;
                Layout(window);
                window.RaiseEvent(new RoutedEventArgs(TopLevel.BackRequestedEvent));
                Layout(window);
                TestAssert.True(!model.IsChartFullscreen && sheet.IsExpanded && host.IsEffectivelyVisible && ReferenceEquals(host.Content, preview),
                    "System back must return to the same inline difficulty.");
                Tap(window, Find(sheet, "PracticeSettings"));
                Layout(window);
                var settings = window.GetVisualDescendants().OfType<ChartPracticeDialog>().Single();
                var rows = settings.GetVisualDescendants().OfType<TKCode.Arc.Slider>().ToArray();
                var list = settings.GetVisualDescendants().OfType<ScrollViewer>().First();
                TestAssert.True(!host.IsVisible && rows.Length == 6 && list.Extent.Height <= list.Viewport.Height + 1,
                    "The anchored settings must cover the native surface and show all six controls together in a tall viewport.");
                var speed = rows.Single(row => Avalonia.Automation.AutomationProperties.GetName(row) == "音符流速");
                var saved = model.ChartOptions.NoteSpeed;
                speed.Value = saved + 1;
                window.RaiseEvent(new RoutedEventArgs(TopLevel.BackRequestedEvent));
                Layout(window);
                while (!host.IsVisible)
                {
                    await Task.Delay(16, token);
                    Layout(window);
                }
                TestAssert.True(ReferenceEquals(host.Content, preview) && model.ChartOptions.NoteSpeed == saved, "Backing out must discard the draft and restore the same renderer after the exit motion.");

                var origin = BoundsIn(preview, window);
                var handle = Find(sheet, "PART_Handle");
                var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
                using (var drag = window.TouchBegin(start, RawInputModifiers.None))
                {
                    window.TouchMove(drag, start + new Vector(0, 15), RawInputModifiers.None);
                    window.TouchMove(drag, start + new Vector(0, 100), RawInputModifiers.None);
                    Layout(window);
                    TestAssert.True(ReferenceEquals(host.Content, preview) && !session.IsReleased
                        && Math.Abs(BoundsIn(preview, window).Y - origin.Y - sheet.PanelOffset) < 2 && sheet.PanelOffset > 40,
                        "The original preview must move with the sheet while the user holds the downward pull.");
                    var lowered = sheet.PanelOffset;
                    window.TouchMove(drag, start + new Vector(0, 45), RawInputModifiers.None);
                    Layout(window);
                    TestAssert.True(ReferenceEquals(host.Content, preview) && Math.Abs(sheet.PanelOffset - (lowered - 55)) < 2,
                        "Reversing a held gesture must return the same preview toward the expanded position.");
                }
                Layout(window);
                TestAssert.True(sheet.IsExpanded && ReferenceEquals(host.Content, preview),
                    "Cancelling a pull must restore expanded details without replacing the preview.");

                ArcMotion.SetLevel(sheet, ArcMotionLevel.Full);
                sheet.IsExpanded = false;
                TestAssert.True(!sheet.IsExpanded && ReferenceEquals(host.Content, preview) && !session.IsReleased,
                    "Committing collapse must keep the preview alive while it still occupies the moving viewport.");
                while (!sheet.SheetUpdate.IsCompleted)
                {
                    await Task.Delay(12, token);
                    Layout(window);
                }
                await sheet.SheetUpdate.WaitAsync(token);
                Layout(window);
                TestAssert.True(host.Content is null && session.IsReleased && !model.IsSongDetailsExpanded,
                    "The compact resting point must release the offscreen preview and its session.");
            }
            finally
            {
                ChartPreviewPane.CreateEmbeddedView = factory;
            }
        }

        /// <summary>Checks simultaneous desktop panes and the right-hand multiline composer at regular and minimum sizes.</summary>
        /// <param name="window">The real rendered desktop window.</param>
        /// <param name="root">The expanded desktop overlay.</param>
        private static void ValidateDesktopPanes(Window window, Control root)
        {
            var summary = Find(root, "DesktopSongSummarySlot");
            var discussion = Find(root, "DesktopDiscussionPane");
            var composer = Find(root, "DesktopComposerPane");
            var left = BoundsIn(summary, root);
            var middle = BoundsIn(discussion, root);
            var right = BoundsIn(composer, root);
            TestAssert.True(left.Width >= 179 && middle.Width >= 180 && right.Width >= 179
                && left.Right <= middle.X + 1 && middle.Right <= right.X + 1
                && Math.Abs(left.Y - middle.Y) < 1 && Math.Abs(middle.Y - right.Y) < 1
                && Math.Abs(left.Bottom - middle.Bottom) < 1 && Math.Abs(middle.Bottom - right.Bottom) < 1
                && Math.Abs(left.Width - right.Width) < 1,
                $"Expanded desktop details must align all three panes at the top and bottom, give both side columns equal width and avoid overlap: left={left}, middle={middle}, right={right}.");
            foreach (var pane in new[] { summary, discussion, composer })
            {
                AssertContained(pane, root, "Every desktop pane must fit the actual expanded viewport.");
                var bounds = BoundsIn(pane, window);
                TestAssert.True(bounds.X >= -1 && bounds.Y >= -1 && bounds.Right <= window.ClientSize.Width + 1 && bounds.Bottom <= window.ClientSize.Height + 1,
                    "Desktop panes must remain inside the native client area at the minimum supported window size.");
            }
            AssertFillsSlot(Find(root, "LibraryDetails"), summary, "The same relocated information card must fill the enlarged left slot on all four edges.");
            var input = Find(root, "SongCommentInput");
            var send = Find(root, "PostSongCommentButton");
            var like = Find(root, "SongLikeButton");
            foreach (var control in new[] { input, send, like })
            {
                TestAssert.True(control.IsEffectivelyVisible && control.Bounds.Height >= 44,
                    "The independent right-hand column must retain complete visible composer, send and vote controls.");
                AssertContained(control, composer, "Comment composition and votes must stay inside their own right-hand pane.");
            }
            var inputBounds = BoundsIn(input, composer);
            var sendBounds = BoundsIn(send, composer);
            var likeBounds = BoundsIn(like, composer);
            TestAssert.True(input.Bounds.Height >= 66 && likeBounds.Bottom <= inputBounds.Y + 1 && inputBounds.Bottom <= sendBounds.Y + 1
                && Math.Abs(inputBounds.X - sendBounds.X) < 1 && Math.Abs(inputBounds.Right - sendBounds.Right) < 1,
                "Desktop votes, textarea and the separate bottom send action must not overlap, while input and send share the column edges.");
            TestAssert.True(Find(root, "SongCommentsList").Bounds.Height >= 44, "The independent middle column must keep a finite comment reading area.");
        }

        /// <summary>Checks real comments without requiring the desktop composer to live inside a mobile tab.</summary>
        /// <param name="window">The pointer input owner.</param>
        /// <param name="root">The expanded desktop subtree.</param>
        /// <param name="model">The real source-confirmed model.</param>
        private static void ValidateDesktopComments(Window window, Control root, MainViewModel model)
        {
            TapTab(window, root, "DesktopSongPreviewTabs", 0);
            var input = Find(root, "SongCommentInput");
            TestAssert.True(model.SongCommentDraft.Length == 0 && input.IsEffectivelyVisible && input.IsEffectivelyEnabled
                && !Find(root, "PostSongCommentButton").IsEffectivelyEnabled,
                "An authenticated desktop user must be able to start an empty draft while an empty submission remains disabled.");
            TestAssert.True(root.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == model.SongComments.Single().Content),
                "The middle discussion pane must paint the current source comment rather than a sample or stale song's row.");
            ValidateDesktopPanes(window, root);
        }

        /// <summary>Scrolls an actual finite comment page and verifies both side columns remain stationary.</summary>
        /// <param name="window">The real input target.</param>
        /// <param name="root">The expanded desktop overlay.</param>
        /// <param name="workspace">The generated source page provider.</param>
        /// <param name="model">The selected source and comment owner.</param>
        /// <param name="output">The ignored screenshot directory.</param>
        /// <param name="token">Cancels controlled refresh completion.</param>
        /// <returns>A task representing middle-only scrolling and restored single-row fixture state.</returns>
        private static async Task ValidateDesktopScrollingAsync(Window window, Control root, SocialPresentationTests.SocialWorkspace workspace,
            MainViewModel model, string output, CancellationToken token)
        {
            var original = workspace.CommentsOverride;
            workspace.CommentsOverride = (track, account, page, _) => Task.FromResult(SocialPresentationTests.SocialWorkspace.Page(track, account, page) with
            {
                Total = 40,
                Items = Enumerable.Range(0, 20).Select(index => SocialPresentationTests.SocialWorkspace.Comment(track, account) with
                {
                    Id = "scroll-thread-" + index,
                    Body = "Generated discussion " + index + "\nA second generated line keeps the real comment list taller than its viewport."
                }).ToArray()
            });
            try
            {
                await TestAssert.ExecuteAsync(model.RefreshSongSocialCommand).WaitAsync(token);
                Layout(window);
                var summary = Find(root, "DesktopSongSummarySlot");
                var composer = Find(root, "DesktopComposerPane");
                var input = Find(root, "SongCommentInput");
                var left = BoundsIn(summary, window);
                var right = BoundsIn(composer, window);
                var inputBefore = BoundsIn(input, window);
                var list = (ListBox)Find(root, "SongCommentsList");
                var cards = ValidateCommentCards(list).OrderBy(card => BoundsIn(card, list).Y).ToArray();
                TestAssert.True(cards.Length >= 2 && BoundsIn(cards[1], list).Y - BoundsIn(cards[0], list).Bottom >= 5,
                    "Adjacent comments must have a visible gap between their rounded outlines.");

                var item = cards[0].FindAncestorOfType<ListBoxItem>()!;
                var presenter = item.GetVisualDescendants().OfType<ContentPresenter>()
                    .Single(control => control.TemplatedParent == item && control.Name == "PART_ContentPresenter");
                list.SelectedIndex = 0;
                window.MouseMove(cards[0].TranslatePoint(new Point(20, 16), window)!.Value, RawInputModifiers.None);
                Layout(window);
                TestAssert.True(item.IsPointerOver && item.IsSelected && presenter.Background is ISolidColorBrush { Color.A: 0 },
                    "Hover and selection must leave the area around the comment card transparent.");
                var point = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;
                window.MouseWheel(point, new Vector(0, -8), RawInputModifiers.None);
                Layout(window);
                TestAssert.True(list.GetVisualDescendants().OfType<ScrollViewer>().Any(scroll => scroll.Offset.Y > 0)
                    && BoundsIn(summary, window) == left && BoundsIn(composer, window) == right && BoundsIn(input, window) == inputBefore,
                    "Scrolling source comments must move only the central list, keeping song information and the complete composer fixed.");
                SaveFrame(window, Path.Combine(output, "ui-social-desktop-comments-scrolled.png"));
            }
            finally
            {
                workspace.CommentsOverride = original;
                await TestAssert.ExecuteAsync(model.RefreshSongSocialCommand).WaitAsync(token);
                Layout(window);
            }
        }

        /// <summary>Confirms that inspecting or previewing another song never changes the audio owner or opens its source.</summary>
        /// <param name="playback">The authoritative playing queue.</param>
        /// <param name="backend">The controlled native-audio replacement.</param>
        /// <param name="playing">The originally loaded song identity.</param>
        /// <param name="queue">The originally loaded ordered queue.</param>
        /// <param name="expected">The expected transport state after the inspected action.</param>
        private static void AssertPlaybackUnchanged(PlaybackService playback, LibraryExperienceTests.ExperienceBackend backend, string playing, string[] queue, PlaybackState expected = PlaybackState.Playing)
        {
            TestAssert.True(backend.OpenCount == 1 && playback.Snapshot.Track?.Id == playing && playback.Queue.Current?.Id == playing
                && playback.Snapshot.State == expected && playback.Queue.Tracks.Select(track => track.Id).SequenceEqual(queue),
                "Expanding, moving and previewing the inspected song must preserve the original audio, playing state and queue without reopening it.");
        }

        /// <summary>Measures a laid-out control in another control's coordinate system.</summary>
        /// <param name="control">The actual arranged production element.</param>
        /// <param name="relativeTo">The coordinate owner.</param>
        /// <returns>The translated layout rectangle.</returns>
        private static Rect BoundsIn(Control control, Control relativeTo) => new(control.TranslatePoint(default, relativeTo)!.Value, control.Bounds.Size);

        /// <summary>Checks that a shared information card uses its current slot's complete arranged size after a completed move.</summary>
        /// <param name="card">The original reparented information card.</param>
        /// <param name="slot">The active expanded or ordinary sidebar slot.</param>
        /// <param name="message">The intended filled-slot boundary.</param>
        private static void AssertFillsSlot(Control card, Control slot, string message)
        {
            var bounds = BoundsIn(card, slot);
            TestAssert.True(Math.Abs(bounds.X) < 1 && Math.Abs(bounds.Y) < 1
                && Math.Abs(bounds.Right - slot.Bounds.Width) < 1 && Math.Abs(bounds.Bottom - slot.Bounds.Height) < 1,
                message + $" card={bounds}, slot={slot.Bounds.Size}.");
        }

        /// <summary>Checks complete layout containment without substituting window size for a clipped child viewport.</summary>
        /// <param name="control">The visible element under test.</param>
        /// <param name="container">The actual containing pane or slot.</param>
        /// <param name="message">The intended geometry boundary.</param>
        private static void AssertContained(Control control, Control container, string message)
        {
            var bounds = BoundsIn(control, container);
            TestAssert.True(bounds.X >= -1 && bounds.Y >= -1 && bounds.Right <= container.Bounds.Width + 1 && bounds.Bottom <= container.Bounds.Height + 1, message);
        }

        /// <summary>Opens the actual mobile comment tab and checks that an authenticated empty draft remains editable.</summary>
        /// <param name="window">The pointer input owner.</param>
        /// <param name="root">The visible expanded details subtree.</param>
        /// <param name="model">The source-confirmed presentation owner.</param>
        private static void ValidateMobileComments(Window window, Control root, MainViewModel model)
        {
            TapTab(window, root, "SongPreviewSegments", 0);
            Layout(window);
            var input = Find(root, "SongCommentInput");
            var send = Find(root, "PostSongCommentButton");
            TestAssert.True(model.SongCommentDraft.Length == 0 && input.IsEffectivelyVisible && input.IsEffectivelyEnabled
                && input.Bounds.Height >= 44 && !send.IsEffectivelyEnabled,
                "The empty authenticated composer must be visible and editable, with only its empty submission disabled.");
            TestAssert.True(root.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == model.SongComments.Single().Content),
                "The actual comment template must display the source-confirmed row rather than a static sample.");
            ValidateCommentCards((ListBox)Find(root, "SongCommentsList"));
            var reply = root.GetVisualDescendants().OfType<TKCode.Arc.Button>().Single(button => Equals(button.Content, "回复"));
            var item = reply.FindAncestorOfType<ListBoxItem>()!;
            var replyOffset = reply.TranslatePoint(default, item)!.Value;
            TestAssert.True(reply.IsEffectivelyVisible && reply.IsEffectivelyEnabled && reply.Bounds.Height >= 44
                && replyOffset.Y >= -1 && replyOffset.Y + reply.Bounds.Height <= item.Bounds.Height + 1,
                "Comment actions must fit inside their actual list item rather than being clipped by the music-list row height.");
            ValidateMobileComposerBounds(window, root, root);
        }

        /// <summary>Checks the outlined cards realized by a discussion list.</summary>
        /// <param name="list">The visible desktop or mobile discussion list.</param>
        /// <returns>The realized comment card borders.</returns>
        private static Border[] ValidateCommentCards(ListBox list)
        {
            var cards = list.GetVisualDescendants().OfType<SongCommentRow>().Where(row => row.IsEffectivelyVisible)
                .Select(row => row.FindControl<Border>("CommentCard")!).ToArray();
            TestAssert.True(cards.Length > 0 && cards.All(card => card.CornerRadius == new CornerRadius(12)
                && card.BorderThickness == MusicGeometry.OutlineThickness && card.Background is ISolidColorBrush { Color.A: > 0 }),
                "The shared comment row must render a filled card with a uniform rounded outline.");
            return cards;
        }

        /// <summary>Keeps the complete composer and send control inside the actual clipped detail viewport, including short windows.</summary>
        /// <param name="window">The current rendered window.</param>
        /// <param name="root">The expanded details subtree.</param>
        /// <param name="clip">The outer detail or sheet viewport which limits painted and interactive content.</param>
        private static void ValidateMobileComposerBounds(Window window, Control root, Control clip)
        {
            var input = Find(root, "SongCommentInput");
            var send = Find(root, "PostSongCommentButton");
            var tabs = Find(root, "SongDetailsTabs");
            foreach (var control in new[] { input, send })
            {
                var origin = control.TranslatePoint(default, window)!.Value;
                var local = control.TranslatePoint(default, clip)!.Value;
                var tabLocal = control.TranslatePoint(default, tabs)!.Value;
                TestAssert.True(control.IsEffectivelyVisible && control.Bounds.Height >= 44
                    && origin.X >= -1 && origin.X + control.Bounds.Width <= window.ClientSize.Width + 1
                    && origin.Y >= -1 && origin.Y + control.Bounds.Height <= window.ClientSize.Height + 1
                    && local.X >= -1 && local.X + control.Bounds.Width <= clip.Bounds.Width + 1
                    && local.Y >= -1 && local.Y + control.Bounds.Height <= clip.Bounds.Height + 1
                    && tabLocal.Y >= -1 && tabLocal.Y + control.Bounds.Height <= tabs.Bounds.Height + 1,
                    $"Composer {control.Name} must fit the sheet ({clip.Bounds.Height:F1}) and the tab viewport ({tabs.Bounds.Height:F1}).");
            }
            var inputTop = input.TranslatePoint(default, root)!.Value.Y;
            var sendTop = send.TranslatePoint(default, root)!.Value.Y;
            TestAssert.True(Math.Abs(inputTop - sendTop) < 1 && Math.Abs(input.Bounds.Height - send.Bounds.Height) < 1,
                "The multiline composer and send control must retain the same top and bottom edges at each viewport height.");
            var listHeight = Find(root, "SongCommentsList").Bounds.Height;
            TestAssert.True(listHeight >= 44,
                $"Comments must retain a scrollable reading area while replying. List: {listHeight:F1} DIP; composer: {input.Bounds.Height:F1} DIP; pane: {root.Bounds.Height:F1} DIP.");
        }

        /// <summary>Measures the actual intersection of the translated full panel and the clipped sheet viewport.</summary>
        /// <param name="panel">The full template panel.</param>
        /// <param name="sheet">The fixed clipped overlay.</param>
        /// <returns>The currently visible panel height.</returns>
        private static double VisiblePanelHeight(Control panel, Control sheet)
            => new Rect(panel.TranslatePoint(default, sheet)!.Value, panel.Bounds.Size).Intersect(new Rect(sheet.Bounds.Size)).Height;

        /// <summary>Checks that the compact panel contains the song controls and only its normal bottom padding.</summary>
        /// <param name="sheet">The fixed clipped overlay.</param>
        /// <param name="panel">The translated template panel.</param>
        /// <param name="header">The persistent song summary and actions.</param>
        private static void AssertCompactPanel(InterruptibleSongSheet sheet, Control panel, Control header)
        {
            var contentBottom = BoundsIn(header, panel).Bottom;
            var bottomPadding = VisiblePanelHeight(panel, sheet) - contentBottom;
            TestAssert.True(sheet.IsOpen && !sheet.IsExpanded && header.IsEffectivelyVisible && bottomPadding is >= -1 and <= 16,
                $"The compact sheet must fit the complete summary with only normal bottom padding, measured {bottomPadding:F1} DIP.");
        }

        /// <summary>Locates a named production control without depending on its XAML namescope.</summary>
        /// <param name="root">The active view subtree.</param>
        /// <param name="name">The stable production control name.</param>
        /// <returns>The real control, preferring its visible instance.</returns>
        private static Control Find(Control root, string name)
        {
            var matches = root.GetVisualDescendants().OfType<Control>().Where(control => control.Name == name).ToArray();
            return matches.FirstOrDefault(control => control.IsEffectivelyVisible) ?? matches.Single();
        }

        private static void TapTab(Window window, Control root, string name, int slot)
        {
            var tabs = (SongPreviewTabs)Find(root, name);
            var option = tabs.Items.OfType<TKCode.Arc.SegmentedOption>().Single(item => Equals(item.Value, slot));
            Tap(window, tabs.ContainerFromIndex(tabs.Items.IndexOf(option))!);
        }

        /// <summary>Invokes actual pointer routing on an enabled production control.</summary>
        /// <param name="window">The headless pointer owner.</param>
        /// <param name="control">The actual visible target.</param>
        private static void Tap(Window window, Control control)
        {
            TestAssert.True(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Bounds.Width > 0 && control.Bounds.Height > 0,
                "A details interaction must target a visible, enabled and laid-out control: " + control.Name);
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            Layout(window);
        }

        /// <summary>Advances bindings, layout and the real headless compositor.</summary>
        /// <param name="window">The rendered view owner.</param>
        private static void Layout(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Captures a rendered synthetic UI frame into ignored artifacts for visual review.</summary>
        /// <param name="window">The current rendered window.</param>
        /// <param name="path">The ignored diagnostic PNG path.</param>
        private static void SaveFrame(Window window, string path)
        {
            Layout(window);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Round(window.Width * window.RenderScaling), (int)Math.Round(window.Height * window.RenderScaling)),
                new Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
            bitmap.Render(window);
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
        }

        /// <summary>Provides two populated chart slots without contacting or mounting an actual renderer.</summary>
        private sealed class DetailsWorkspace : SocialPresentationTests.SocialWorkspace, IChartPreviewWorkspace, IMajViewPreviewHost
        {
            public DrawerPreviewSession? Preview { get; set; }

            /// <inheritdoc />
            public Task<IReadOnlyList<ChartPreviewDifficulty>> GetChartPreviewDifficultiesAsync(MusicTrack track,
                CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChartPreviewDifficulty>>([new(2, "4"), new(5, "13+")]);

            /// <inheritdoc />
            public Task<ChartPreviewAssets> ResolveChartPreviewAsync(MusicTrack track, bool includeVideo,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<IMajViewPreviewSession> CreateMajViewPreviewAsync(MusicTrack track, bool includeVideo,
                CancellationToken cancellationToken = default) => Preview is { } preview
                ? Task.FromResult<IMajViewPreviewSession>(preview) : throw new NotSupportedException();
        }

        /// <summary>Records exactly when the visible chart's renderer can be released.</summary>
        private sealed class DrawerPreviewSession : IMajViewPreviewSession
        {
            public event Action<ChartFrame>? FrameReceived;
            public ChartIntent? Intent { get; private set; }
            public void SetIntent(ChartIntent intent) => Intent = intent;
            public void Report(ChartFrame frame) => FrameReceived?.Invoke(frame);
            public Uri Address { get; } = new("http://127.0.0.1:1234/drawer-preview/");
            public bool IsReleased { get; private set; }

            public ValueTask DisposeAsync()
            {
                IsReleased = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
