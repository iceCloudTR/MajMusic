#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;

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
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await session.Dispatch<bool>(async () =>
            {
                var workspace = new SocialPresentationTests.SocialWorkspace();
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
                    Tap(window, main.FindControl<Control>("DesktopChartPreviewButton")!);
                    await flight.MoveUpdate.WaitAsync(timeout.Token);
                    await model.SongSocialUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    TestAssert.True(model.IsSongDetailsExpanded && model.IsSongChartPreviewSelected
                        && Find(desktop, "ChartPreviewHost").IsEffectivelyVisible && !Find(desktop, "SongCommentsList").IsEffectivelyVisible,
                        "The artwork preview action must open the reserved chart viewport directly, instead of playing or opening the discussion first.");
                    AssertPlaybackUnchanged(playback, backend, playing, queue);
                    SaveFrame(window, Path.Combine(output, "ui-social-desktop-chart-preview.png"));
                    Tap(window, Find(desktop, "DesktopCommentsPaneButton"));
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
                    var half = VisiblePanelHeight(panel, sheet);
                    var fullPanelHeight = panel.Bounds.Height;
                    TestAssert.True(sheet.IsOpen && !sheet.IsExpanded && half > sheet.Bounds.Height * .35 && half < sheet.Bounds.Height * .65,
                        "A newly opened mobile song sheet must display its half-height resting point rather than immediately filling the phone.");
                    SaveFrame(window, Path.Combine(output, "ui-social-phone-half.png"));
                    Tap(window, Find(mobile, "MobileExpandDetailsButton"));
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    await model.SongSocialUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    TestAssert.True(sheet.IsExpanded && model.IsSongDetailsExpanded && VisiblePanelHeight(panel, sheet) > half * 1.65
                        && Math.Abs(panel.Bounds.Height - fullPanelHeight) < 1,
                        "Mobile expansion moves the same fixed-height pane to its full resting point without rescaling its content.");
                    var expanded = mobile.GetVisualDescendants().OfType<SongDetailsView>().Single(view => view.IsEffectivelyVisible);
                    SaveFrame(window, Path.Combine(output, "ui-social-phone-full.png"));
                    ValidateMobileComments(window, expanded, model);
                    SaveFrame(window, Path.Combine(output, "ui-social-phone-comments.png"));
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
                    Tap(window, Find(expanded, "CloseExpandedSongDetailsButton"));
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    TestAssert.True(sheet.IsOpen && !sheet.IsExpanded && !model.IsSongDetailsExpanded && Math.Abs(VisiblePanelHeight(panel, sheet) - half) < 2,
                        "The expanded close action returns to half height and keeps the same inspected song open.");
                    TestAssert.Equal(0, workspace.Posts.Count, "Rendering and inspecting synthetic source comments must never submit a comment implicitly.");
                    AssertPlaybackUnchanged(playback, backend, playing, queue);
                }
                finally
                {
                    window.Close();
                }
                return true;
            }, timeout.Token);
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
            Tap(window, Find(root, "DesktopCommentsPaneButton"));
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
        private static void AssertPlaybackUnchanged(PlaybackService playback, LibraryExperienceTests.ExperienceBackend backend, string playing, string[] queue)
        {
            TestAssert.True(backend.OpenCount == 1 && playback.Snapshot.Track?.Id == playing && playback.Queue.Current?.Id == playing
                && playback.Snapshot.State == PlaybackState.Playing && playback.Queue.Tracks.Select(track => track.Id).SequenceEqual(queue),
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
            Tap(window, Find(root, "SongCommentsTab"));
            Layout(window);
            var input = Find(root, "SongCommentInput");
            var send = Find(root, "PostSongCommentButton");
            TestAssert.True(model.SongCommentDraft.Length == 0 && input.IsEffectivelyVisible && input.IsEffectivelyEnabled
                && input.Bounds.Height >= 44 && !send.IsEffectivelyEnabled,
                "The empty authenticated composer must be visible and editable, with only its empty submission disabled.");
            TestAssert.True(root.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == model.SongComments.Single().Content),
                "The actual comment template must display the source-confirmed row rather than a static sample.");
            var reply = root.GetVisualDescendants().OfType<TKCode.Arc.Button>().Single(button => Equals(button.Content, "回复"));
            var item = reply.FindAncestorOfType<ListBoxItem>()!;
            var replyOffset = reply.TranslatePoint(default, item)!.Value;
            TestAssert.True(reply.IsEffectivelyVisible && reply.IsEffectivelyEnabled && reply.Bounds.Height >= 44
                && replyOffset.Y >= -1 && replyOffset.Y + reply.Bounds.Height <= item.Bounds.Height + 1,
                "Comment actions must fit inside their actual list item rather than being clipped by the music-list row height.");
            ValidateMobileComposerBounds(window, root, root);
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
                    "The complete composer and send control must fit inside the real detail viewport: " + control.Name);
            }
            var inputTop = input.TranslatePoint(default, root)!.Value.Y;
            var sendTop = send.TranslatePoint(default, root)!.Value.Y;
            TestAssert.True(Math.Abs(inputTop - sendTop) < 1 && Math.Abs(input.Bounds.Height - send.Bounds.Height) < 1,
                "The multiline composer and send control must retain the same top and bottom edges at each viewport height.");
            TestAssert.True(Find(root, "SongCommentsList").Bounds.Height >= 44,
                "Comments must retain a scrollable reading area even with a short window, reply target and another available page.");
        }

        /// <summary>Measures the actual intersection of the translated full panel and the clipped sheet viewport.</summary>
        /// <param name="panel">The full template panel.</param>
        /// <param name="sheet">The fixed clipped overlay.</param>
        /// <returns>The currently visible panel height.</returns>
        private static double VisiblePanelHeight(Control panel, Control sheet)
            => new Rect(panel.TranslatePoint(default, sheet)!.Value, panel.Bounds.Size).Intersect(new Rect(sheet.Bounds.Size)).Height;

        /// <summary>Locates a named production control without depending on its XAML namescope.</summary>
        /// <param name="root">The active view subtree.</param>
        /// <param name="name">The stable production control name.</param>
        /// <returns>The real control, preferring its visible instance.</returns>
        private static Control Find(Control root, string name)
        {
            var matches = root.GetVisualDescendants().OfType<Control>().Where(control => control.Name == name).ToArray();
            return matches.FirstOrDefault(control => control.IsEffectivelyVisible) ?? matches.Single();
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
    }
}
