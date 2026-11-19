#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using ArcSwitch = TKCode.Arc.Switch;

namespace MajMusic.Tests
{
    /// <summary>Checks independently editable endpoint cards within the actual account-page viewport.</summary>
    internal static class EndpointCardsLayoutTests
    {
        /// <summary>Checks two unsaved cards at compact and normal sizes using actual display scaling.</summary>
        /// <returns>A task representing the isolated headless layout checks.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "MajMusic-endpoint-layout-" + Guid.NewGuid().ToString("N"));
                var output = Path.Combine(Environment.CurrentDirectory, ".work");
                Directory.CreateDirectory(output);
                using var transport = new NoNetworkTransport();
                using var client = new HttpClient(transport);
                using var workspace = new MusicWorkspace(Path.Combine(root, "data"), client, client);
                var viewModel = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
                var view = new MainView { DataContext = viewModel };
                var window = new Window { Content = view, Width = 1100, Height = 760 };
                try
                {
                    await viewModel.InitializeAsync();
                    for (var index = 1; index <= 2; index++)
                    {
                        viewModel.AddEndpointCardCommand.Execute(null);
                        await WaitUntilAsync(() => viewModel.EndpointCards.Count == index && viewModel.IsIdle, timeout.Token);
                        var card = viewModel.EndpointCards[index - 1];
                        card.EndpointName = "Layout Source " + index;
                        card.EndpointUrl = "https://source-" + index + ".example.invalid/api/";
                        card.NewAccountCommand.Execute(null);
                        await WaitUntilAsync(() => card.IsIdle && card.CanEditAccount, timeout.Token);
                        card.Username = "Layout Account " + index;
                    }

                    window.Show();
                    view.FindControl<TabControl>("MainTabs")!.SelectedItem = view.FindControl<TabItem>("AccountsTab");
                    foreach (var scale in new[] { 1d, 1.25 })
                    {
                        window.SetRenderScaling(scale);
                        foreach (var size in new[] { new Size(720, 540), new Size(1100, 760) })
                        {
                            window.Width = size.Width;
                            window.Height = size.Height;
                            Layout(window);
                            TestAssert.Equal(scale, window.RenderScaling, "Endpoint-card checks must use actual platform scaling.");
                            var scroll = view.FindControl<ScrollViewer>("EndpointCardsScroll")!;
                            scroll.Offset = default;
                            Layout(window);
                            var cards = view.GetVisualDescendants().OfType<EndpointCardView>().ToArray();
                            TestAssert.Equal(2, cards.Length, "Two endpoint drafts must coexist as separate editable cards.");
                            foreach (var card in cards)
                            {
                                AssertCard(card);
                            }

                            AssertToolbar(view, scale);
                            AssertCardActionsVisible(cards[0], scroll);
                            if (scale == 1.25)
                            {
                                SaveFrame(window, output, size.Width == 720 ? "ui-endpoint-cards-small.png" : "ui-endpoint-cards.png");
                            }

                            scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                            Layout(window);
                            AssertCardActionsVisible(cards[1], scroll);
                            TestAssert.True(cards[1].FindControl<TextBox>("UsernameEditor")!.IsEnabled,
                                "The second card must remain editable after scrolling into view.");
                            if (scale == 1.25 && size.Width == 720)
                            {
                                SaveFrame(window, output, "ui-endpoint-cards-small-scrolled.png");
                            }
                        }
                    }

                    TestAssert.True(viewModel.EndpointCards.All(card => card.IsNew), "Layout checks must not save source drafts.");
                    TestAssert.True(!workspace.IsOnline && transport.RequestCount == 0,
                        "Endpoint-card layout checks must neither enable online mode nor contact a service.");
                }
                finally
                {
                    window.Close();
                    await viewModel.DisposeAsync();
                    workspace.Dispose();
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    if (Directory.Exists(root))
                    {
                        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        var resolved = Path.GetFullPath(root);
                        TestAssert.True(resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                            && Path.GetFileName(resolved).StartsWith("MajMusic-endpoint-layout-", StringComparison.Ordinal),
                            "Endpoint-layout fixtures must remain beneath the temporary directory.");
                        Directory.Delete(resolved, true);
                    }
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Checks separated source and account fields and complete button containment.</summary>
        /// <param name="card">The rendered endpoint editor.</param>
        private static void AssertCard(EndpointCardView card)
        {
            var panel = card.FindControl<Border>("EndpointCardPanel")!;
            TestAssert.True(Math.Abs(panel.Bounds.Height - 232) <= 0.01, "Every endpoint card must retain its intended height.");
            var source = card.FindControl<Grid>("EndpointEditorPanel")!;
            var account = card.FindControl<Grid>("AccountEditorPanel")!;
            var sourceBounds = BoundsIn(source, panel);
            var accountBounds = BoundsIn(account, panel);
            TestAssert.True(sourceBounds.Right + 20 <= accountBounds.Left, "Source and account fields must have a visible separating gap.");
            var cloudToggle = card.FindControl<CheckBox>("UseCloudCollectionsToggle")!;
            var cloudBounds = BoundsIn(cloudToggle, panel);
            var rememberBounds = BoundsIn(card.FindControl<CheckBox>("RememberPasswordToggle")!, panel);
            TestAssert.True(sourceBounds.Contains(cloudBounds) && Math.Abs(cloudBounds.Center.Y - rememberBounds.Center.Y) <= 0.51,
                "The collection option must fit the source column and align with the account options without increasing the card height.");
            var inputBounds = card.GetVisualDescendants().OfType<TextBox>().Select(input => BoundsIn(input, panel)).ToArray();
            for (var index = 0; index < inputBounds.Length; index++)
            {
                var bounds = inputBounds[index];
                TestAssert.True(new Rect(panel.Bounds.Size).Contains(bounds), "Every endpoint and account field must fit its card.");
                TestAssert.True(inputBounds.Skip(index + 1).All(other => !bounds.Intersects(other)),
                    "Endpoint, username and password inputs must never overlap each other.");
            }

            foreach (var button in card.GetVisualDescendants().OfType<Button>())
            {
                var bounds = BoundsIn(button, panel);
                TestAssert.True(new Rect(panel.Bounds.Size).Contains(bounds), "Every endpoint-card action must fit inside the card border.");
            }
        }

        /// <summary>Checks the actual switch paint and inline label rather than the outer toggle layout box.</summary>
        /// <param name="view">The rendered account page.</param>
        /// <param name="scale">The actual platform display scale.</param>
        private static void AssertToolbar(MainView view, double scale)
        {
            var toggle = view.FindControl<ArcSwitch>("OnlineModeToggle")!;
            ArcTestVisuals.AssertOnlineSwitchGeometry(toggle);
            var inline = (StackPanel)toggle.Parent!;
            var toolbar = (Grid)inline.Parent!;
            var label = inline.Children.OfType<TextBlock>().Single();
            var labelCenter = BoundsIn(label, toolbar).Center.Y;
            var knob = toggle.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "PART_Thumb");
            var outer = toggle.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "PART_Track");
            foreach (var paint in new Control[] { knob, outer })
            {
                var bounds = BoundsIn(paint, toolbar);
                TestAssert.True(new Rect(toolbar.Bounds.Size).Contains(bounds), "The complete online switch must fit the account toolbar.");
                TestAssert.True(Math.Abs(bounds.Center.Y - labelCenter) * scale <= 0.51,
                    "The online switch paint and its inline label must share a visual baseline.");
            }
        }

        /// <summary>Checks all actions in a source card against the real scroll viewport.</summary>
        /// <param name="card">The card expected to be currently usable.</param>
        /// <param name="scroll">The account-card scroll owner.</param>
        private static void AssertCardActionsVisible(EndpointCardView card, ScrollViewer scroll)
        {
            var viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>()
                .First(item => ReferenceEquals(item.TemplatedParent, scroll));
            foreach (var button in card.GetVisualDescendants().OfType<Button>())
            {
                var bounds = BoundsIn(button, viewport);
                TestAssert.True(bounds.Top >= -0.01 && bounds.Bottom <= viewport.Bounds.Height + 0.01,
                    "Every action in the visible endpoint card must be reachable without another scroll.");
            }
        }

        /// <summary>Translates a complete rendered control rectangle into a common coordinate owner.</summary>
        /// <param name="control">The rendered control.</param>
        /// <param name="owner">The coordinate owner.</param>
        /// <returns>The control bounds expressed in the owner's coordinate space.</returns>
        private static Rect BoundsIn(Control control, Control owner)
        {
            return new Rect(control.TranslatePoint(default, owner)!.Value, control.Bounds.Size);
        }

        /// <summary>Completes pending layout and styling work on the isolated rendering thread.</summary>
        /// <param name="window">The headless window being checked.</param>
        private static void Layout(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Writes the actual scaled account-page rendering for visual inspection.</summary>
        /// <param name="window">The rendered headless window.</param>
        /// <param name="output">The ignored artifact directory.</param>
        /// <param name="name">The diagnostic image name.</param>
        private static void SaveFrame(Window window, string output, string name)
        {
            var scale = window.RenderScaling;
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Round(window.Width * scale), (int)Math.Round(window.Height * scale)),
                new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
        }

        /// <summary>Observes completion of an existing asynchronous presentation command.</summary>
        /// <param name="condition">The state expected after the command.</param>
        /// <param name="cancellationToken">Cancels an operation that never settles.</param>
        /// <returns>A task representing the observed state.</returns>
        private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            while (!condition())
            {
                await Task.Delay(5, cancellationToken);
            }
        }

        /// <summary>Fails any accidental service request during a purely local geometry check.</summary>
        private sealed class NoNetworkTransport : HttpMessageHandler
        {
            /// <summary>Gets the number of attempted service requests.</summary>
            public int RequestCount { get; private set; }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return Task.FromException<HttpResponseMessage>(new InvalidOperationException("Endpoint layout checks cannot contact a service."));
            }
        }
    }
}
