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
                await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
                await model.InitializeAsync().WaitAsync(timeout.Token);
                model.ShowLibraryModeCommand.Execute(null);
                await model.HomeUpdate.WaitAsync(timeout.Token);
                model.SelectedTrack = workspace.Library[0];
                var main = new MainView { DataContext = model };
                var window = new Window { Content = main, Width = 1100, Height = 800 };
                var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "social-details");
                Directory.CreateDirectory(output);
                try
                {
                    window.Show();
                    Layout(window);
                    Tap(window, Find(main, "DesktopExpandDetailsButton"));
                    await model.SongSocialUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    var desktop = Find(main, "DesktopExpandedSongDetails");
                    TestAssert.True(model.IsSongDetailsExpanded && desktop.IsEffectivelyVisible && desktop.Bounds.Height > 320,
                        "The real desktop expand button must reveal an actionable details panel with the selected song's metadata.");
                    SaveFrame(window, Path.Combine(output, "ui-social-desktop-expanded.png"));
                    ValidateComments(window, desktop, model);
                    SaveFrame(window, Path.Combine(output, "ui-social-desktop-comments.png"));
                    window.Width = 720;
                    window.Height = 540;
                    model.SongComments.Single().ReplyCommand.Execute(null);
                    Layout(window);
                    ValidateComposerBounds(window, desktop, desktop);
                    SaveFrame(window, Path.Combine(output, "ui-social-desktop-short-comments.png"));
                    model.CancelSongReplyCommand.Execute(null);
                    Tap(window, Find(desktop, "CloseExpandedSongDetailsButton"));
                    TestAssert.True(!model.IsSongDetailsExpanded, "The desktop close action returns to the existing music view.");

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
                    ValidateComments(window, expanded, model);
                    SaveFrame(window, Path.Combine(output, "ui-social-phone-comments.png"));
                    window.Height = 420;
                    model.SongComments.Single().ReplyCommand.Execute(null);
                    Layout(window);
                    await sheet.SheetUpdate.WaitAsync(timeout.Token);
                    Layout(window);
                    ValidateComposerBounds(window, expanded, sheet);
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
                }
                finally
                {
                    window.Close();
                }
                return true;
            }, timeout.Token);
        }

        /// <summary>Opens the actual comment tab and checks that an authenticated empty draft remains editable.</summary>
        /// <param name="window">The pointer input owner.</param>
        /// <param name="root">The visible expanded details subtree.</param>
        /// <param name="model">The source-confirmed presentation owner.</param>
        private static void ValidateComments(Window window, Control root, MainViewModel model)
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
            ValidateComposerBounds(window, root, root);
        }

        /// <summary>Keeps the complete composer and send control inside the actual clipped detail viewport, including short windows.</summary>
        /// <param name="window">The current rendered window.</param>
        /// <param name="root">The expanded details subtree.</param>
        /// <param name="clip">The outer detail or sheet viewport which limits painted and interactive content.</param>
        private static void ValidateComposerBounds(Window window, Control root, Control clip)
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
