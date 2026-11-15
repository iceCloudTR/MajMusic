#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using SkiaSharp;

namespace MajMusic.Tests
{
    /// <summary>Checks equal home segments and a playback group centered independently of adjacent text.</summary>
    internal static class AlignmentTests
    {
        /// <summary>Renders compact, normal, wide and scaled windows and checks their control geometry.</summary>
        /// <returns>A task representing the isolated headless alignment checks.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "MajMusic-alignment-" + Guid.NewGuid().ToString("N"));
                var output = Path.Combine(Environment.CurrentDirectory, ".work");
                Directory.CreateDirectory(output);
                using var workspace = new MusicWorkspace(Path.Combine(root, "data"));
                var playback = new PlaybackService(new AlignmentBackend(),
                    resolveAudioPath: (_, _) => Task.FromResult("alignment-media"));
                var viewModel = new MainViewModel(workspace, playback);
                var view = new MainView { DataContext = viewModel };
                var window = new Window { Content = view, Width = 1100, Height = 760 };
                try
                {
                    await viewModel.InitializeAsync();
                    window.Show();
                    await playback.SetQueueAsync([new MusicTrack
                    {
                        Id = "alignment-track",
                        Title = "A long playback title that must never push the controls away from the window center",
                        Artist = "An equally long artist name for the fixed metadata area",
                        Audio = new MediaResource { LocalPath = "alignment-media" }
                    }], cancellationToken: timeout.Token);
                    await WaitForPlaybackAsync(viewModel, true, timeout.Token);
                    foreach (var size in new[] { new Size(720, 540), new Size(1100, 760), new Size(1377, 900) })
                    {
                        window.Width = size.Width;
                        window.Height = size.Height;
                        RenderFrame(window, output, $"ui-alignment-{size.Width:0}.png");
                        foreach (var mode in new[]
                        {
                            viewModel.ShowRecommendationsModeCommand,
                            viewModel.ShowPlaylistsModeCommand,
                            viewModel.ShowLibraryModeCommand
                        })
                        {
                            mode.Execute(null);
                            await viewModel.HomeUpdate;
                            Dispatcher.UIThread.RunJobs();
                            AssertSegments(view);
                            AssertTransport(view);
                        }

                        await playback.PauseAsync(timeout.Token);
                        await WaitForPlaybackAsync(viewModel, false, timeout.Token);
                        AssertTransport(view);
                        await playback.ResumeAsync(timeout.Token);
                        await WaitForPlaybackAsync(viewModel, true, timeout.Token);
                        AssertTransport(view);
                    }

                    foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
                    {
                        window.SetRenderScaling(scale);
                        foreach (var size in new[] { new Size(720, 540), new Size(1100, 760) })
                        {
                            window.Width = size.Width;
                            window.Height = size.Height;
                            window.UpdateLayout();
                            Dispatcher.UIThread.RunJobs();
                            TestAssert.Equal(scale, window.RenderScaling, "The window must use actual platform layout scaling.");
                            await playback.PauseAsync(timeout.Token);
                            await WaitForPlaybackAsync(viewModel, false, timeout.Token);
                            window.UpdateLayout();
                            AssertSegments(view);
                            AssertTransport(view);
                            AssertVolumeAndPlaylistGeometry(view, window);
                            if (scale == 1.25 && size.Width == 1100)
                            {
                                using var scaled = new RenderTargetBitmap(new PixelSize(1375, 950), new Vector(120, 120));
                                scaled.Render(window);
                                scaled.Save(Path.Combine(output, "ui-alignment-125.png"), PngBitmapEncoderOptions.Default);
                            }

                            await playback.ResumeAsync(timeout.Token);
                            await WaitForPlaybackAsync(viewModel, true, timeout.Token);
                            window.UpdateLayout();
                            AssertTransport(view);
                        }
                    }
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
                            && Path.GetFileName(resolved).StartsWith("MajMusic-alignment-", StringComparison.Ordinal),
                            "Alignment fixtures must remain beneath the temporary directory.");
                        Directory.Delete(resolved, true);
                    }
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Checks that each segment and its painted presenter fill one equal grid column.</summary>
        /// <param name="view">The rendered shared view.</param>
        private static void AssertSegments(MainView view)
        {
            var group = view.FindControl<Grid>("HomeModeSegments")!;
            var buttons = new[] { "RecommendationsModeButton", "PlaylistsModeButton", "LibraryModeButton" }
                .Select(name => view.FindControl<ToggleButton>(name)!).ToArray();
            var width = group.Bounds.Width / 3;
            for (var index = 0; index < buttons.Length; index++)
            {
                var button = buttons[index];
                var origin = button.TranslatePoint(default, group)!.Value;
                Close(width, button.Bounds.Width, "Every mode must fill one equal column.");
                Close(group.Bounds.Height, button.Bounds.Height, "Every mode must fill the segment height.");
                Close(width * index, origin.X, "Mode segments must meet without uneven gaps.");
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
                    .Single(item => item.Name == "PART_ContentPresenter");
                var presenterOrigin = presenter.TranslatePoint(default, button)!.Value;
                Close(0, presenterOrigin.X, "Mode backgrounds must begin at their column edge.");
                Close(button.Bounds.Width, presenter.Bounds.Width, "Mode backgrounds must fill the entire column.");
                Close(button.Bounds.Height, presenter.Bounds.Height, "Mode backgrounds must fill the entire height.");
            }
        }

        /// <summary>Checks equal buttons and spacing, global centering and the visible primary icon center.</summary>
        /// <param name="view">The rendered shared view.</param>
        private static void AssertTransport(MainView view)
        {
            var player = view.FindControl<Border>("PlaybackBar")!;
            var group = view.FindControl<Grid>("TransportGroup")!;
            var groupCenter = group.TranslatePoint(new Point(group.Bounds.Width / 2, group.Bounds.Height / 2), player)!.Value;
            Close(player.Bounds.Width / 2, groupCenter.X, "The transport group must be centered on the playback bar.");
            var names = new[] { "ShuffleButton", "PreviousTrackButton", "TransportButton", "NextTrackButton", "RepeatButton" };
            for (var index = 0; index < names.Length; index++)
            {
                var button = view.FindControl<Button>(names[index])!;
                var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), group)!.Value;
                Close(36, button.Bounds.Width, "Every playback control must have an equal hit width.");
                Close(36, button.Bounds.Height, "Every playback control must have an equal hit height.");
                Close(20 + 40 * index, center.X, "Playback controls must have equal horizontal spacing.");
                Close(20, center.Y, "Playback controls must share one vertical center.");
            }

            var main = view.FindControl<Button>("TransportButton")!;
            var icon = new[] { "TransportPlayIcon", "TransportPauseIcon" }
                .Select(name => view.FindControl<Avalonia.Controls.Shapes.Path>(name)!).Single(path => path.IsVisible);
            var iconCenter = icon.TranslatePoint(new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), main)!.Value;
            Close(main.Bounds.Width / 2, iconCenter.X, "The primary playback icon must be horizontally centered.");
            Close(main.Bounds.Height / 2, iconCenter.Y, "The primary playback icon must be vertically centered.");
            AssertGlyphInk(view);
        }

        /// <summary>Checks painted geometry and rasterized ink rather than the surrounding Path layout boxes.</summary>
        /// <param name="view">The rendered shared view.</param>
        private static void AssertGlyphInk(MainView view)
        {
            var scale = TopLevel.GetTopLevel(view)!.RenderScaling;
            var group = view.FindControl<Grid>("TransportGroup")!;
            var geometryBaselines = new List<double>();
            var pixelBaselines = new List<double>();
            foreach (var name in new[] { "ShuffleButton", "PreviousTrackButton", "TransportButton", "NextTrackButton", "RepeatButton" })
            {
                var button = view.FindControl<Button>(name)!;
                var path = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(item => item.IsVisible);
                var geometry = path.RenderedGeometry!;
                var pen = path.Stroke is null ? null
                    : new Pen(Brushes.White, path.StrokeThickness, null, path.StrokeLineCap, path.StrokeJoin);
                var localInk = pen is null ? geometry.Bounds : geometry.GetRenderBounds(pen);
                var transform = path.TransformToVisual(button)!.Value;
                var ink = localInk.TransformToAABB(transform);
                var opticalCorrection = path.Name == "TransportPlayIcon" ? 1d : 0d;
                var tolerance = 0.51 / scale;
                TestAssert.True(Math.Abs(ink.Center.X - button.Bounds.Width / 2 - opticalCorrection) <= tolerance,
                    $"The painted {path.Name} must retain its intended horizontal center at {scale * 100:0}%.");
                TestAssert.True(Math.Abs(ink.Center.Y - button.Bounds.Height / 2) <= tolerance,
                    $"The painted {path.Name} must retain its vertical center at {scale * 100:0}%.");
                geometryBaselines.Add(button.TranslatePoint(ink.Center, group)!.Value.Y);

                using var mask = new RenderTargetBitmap(
                    new PixelSize((int)Math.Round(button.Bounds.Width * scale), (int)Math.Round(button.Bounds.Height * scale)),
                    new Vector(96 * scale, 96 * scale));
                using (var context = mask.CreateDrawingContext())
                using (context.PushTransform(transform))
                {
                    context.DrawGeometry(path.Fill is null ? null : Brushes.White, pen, geometry);
                }

                using var encoded = new MemoryStream();
                mask.Save(encoded, PngBitmapEncoderOptions.Default);
                encoded.Position = 0;
                using var pixels = SKBitmap.Decode(encoded);
                double weight = 0;
                double horizontal = 0;
                double vertical = 0;
                for (var y = 0; y < pixels.Height; y++)
                {
                    for (var x = 0; x < pixels.Width; x++)
                    {
                        var alpha = pixels.GetPixel(x, y).Alpha;
                        weight += alpha;
                        horizontal += alpha * (x + 0.5);
                        vertical += alpha * (y + 0.5);
                    }
                }

                TestAssert.True(weight > 0, "A visible transport glyph must paint nonempty pixels.");
                pixelBaselines.Add(vertical / weight - pixels.Height / 2d);
                if (path.Fill is not null)
                {
                    TestAssert.True(Math.Abs(horizontal / weight - pixels.Width / 2d) <= 1.25,
                        $"The filled {path.Name} must be optically centered within its button.");
                }
            }

            TestAssert.True(geometryBaselines.Max() - geometryBaselines.Min() <= 0.05,
                "All five painted glyphs must share one geometric baseline.");
            TestAssert.True(pixelBaselines.Max() - pixelBaselines.Min() <= 0.15,
                "All five rasterized glyphs must share one visual baseline, including fractional display scales.");
        }

        /// <summary>Compares logical coordinates while allowing normal subpixel layout rounding.</summary>
        /// <param name="expected">The intended coordinate or size.</param>
        /// <param name="actual">The rendered coordinate or size.</param>
        /// <param name="message">The purpose of the comparison.</param>
        private static void Close(double expected, double actual, string message)
        {
            TestAssert.True(Math.Abs(expected - actual) <= 1, $"{message} Expected {expected:0.##}; actual {actual:0.##}.");
        }

        /// <summary>Checks painted volume baselines and the complete playlist creation row at the current platform scale.</summary>
        /// <param name="view">The rendered shared view.</param>
        /// <param name="window">The scaled headless window.</param>
        private static void AssertVolumeAndPlaylistGeometry(MainView view, Window window)
        {
            var playbackVolume = view.FindControl<Grid>("PlaybackVolume")!;
            var transport = view.FindControl<Grid>("TransportGroup")!;
            var playbackCenter = transport.TranslatePoint(new Point(0, transport.Bounds.Height / 2), view)!.Value.Y;
            AssertVolumeBaseline(playbackVolume, view, playbackCenter, window.RenderScaling, "Playback");
            var tabs = view.FindControl<TabControl>("MainTabs")!;
            var previousTab = tabs.SelectedItem;
            try
            {
                tabs.SelectedItem = view.FindControl<TabItem>("SettingsTab");
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var audio = view.FindControl<Border>("AudioSettingsPanel")!;
                var checkbox = audio.GetVisualDescendants().OfType<CheckBox>().Single();
                var indicator = checkbox.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "NormalRectangle");
                var settingCenter = indicator.TranslatePoint(new Point(0, indicator.Bounds.Height / 2), view)!.Value.Y;
                AssertVolumeBaseline(audio, view, settingCenter, window.RenderScaling, "Settings");

                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Header, "歌单管理"));
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var input = view.GetVisualDescendants().OfType<TextBox>().Single(item => item.PlaceholderText == "新歌单名称");
                var row = (Grid)input.Parent!;
                var border = input.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "PART_BorderElement");
                var paintedTop = border.TranslatePoint(default, row)!.Value.Y;
                var paintedBottom = paintedTop + border.Bounds.Height;
                TestAssert.True(paintedTop >= -0.01 && paintedBottom <= row.Bounds.Height + 0.01,
                    $"The complete playlist input border must fit its row at {window.RenderScaling * 100:0}%: {paintedTop:0.##}..{paintedBottom:0.##} within {row.Bounds.Height:0.##}.");
                var rowBottom = row.TranslatePoint(new Point(0, row.Bounds.Height), tabs)!.Value.Y;
                TestAssert.True(rowBottom <= tabs.Bounds.Height + 0.01,
                    "The playlist creation row must remain inside the tab viewport.");
                foreach (var button in row.Children.OfType<Button>())
                {
                    var center = button.TranslatePoint(new Point(0, button.Bounds.Height / 2), row)!.Value.Y;
                    TestAssert.True(Math.Abs(center - row.Bounds.Height / 2) * window.RenderScaling <= 0.51,
                        "Playlist creation and paging buttons must share the center of their footer row.");
                }
            }
            finally
            {
                tabs.SelectedItem = previousTab;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>Compares the actual thumb, track paint and checkbox indicator with the intended visual baseline.</summary>
        /// <param name="container">The section containing one volume slider and mute checkbox.</param>
        /// <param name="view">The common coordinate owner.</param>
        /// <param name="center">The intended baseline in view coordinates.</param>
        /// <param name="scale">The actual platform display scale.</param>
        /// <param name="section">The section identified in assertion failures.</param>
        private static void AssertVolumeBaseline(Control container, MainView view, double center, double scale, string section)
        {
            var slider = container.GetVisualDescendants().OfType<Slider>().Single();
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            var checkbox = container.GetVisualDescendants().OfType<CheckBox>().Single();
            var indicator = checkbox.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "NormalRectangle");
            var tracks = slider.GetVisualDescendants().OfType<Border>().Where(item => item.Name == "TrackBackground");
            foreach (var paintedControl in tracks.Cast<Control>().Append(thumb).Append(indicator))
            {
                var paintedCenter = paintedControl.TranslatePoint(new Point(0, paintedControl.Bounds.Height / 2), view)!.Value.Y;
                TestAssert.True(Math.Abs(paintedCenter - center) * scale <= 0.51,
                    $"{section} {paintedControl.GetType().Name}/{paintedControl.Name} must share the painted baseline at {scale * 100:0}%: {paintedCenter:0.###} versus {center:0.###}.");
            }
        }

        /// <summary>Runs layout and saves one reproducible headless rendering.</summary>
        /// <param name="window">The rendered window.</param>
        /// <param name="output">The ignored artifact directory.</param>
        /// <param name="name">The diagnostic image name.</param>
        private static void RenderFrame(Window window, string output, string name)
        {
            Dispatcher.UIThread.RunJobs();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
        }

        /// <summary>Waits for backend notifications to update the visible playback icon.</summary>
        /// <param name="viewModel">The bound playback owner.</param>
        /// <param name="playing">The expected authoritative playback state.</param>
        /// <param name="cancellationToken">Cancels a notification that never arrives.</param>
        /// <returns>A task representing the observed UI state.</returns>
        private static async Task WaitForPlaybackAsync(MainViewModel viewModel, bool playing, CancellationToken cancellationToken)
        {
            while (viewModel.IsPlaying != playing)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5, cancellationToken);
            }

            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Publishes managed playback state without loading native audio or reading media files.</summary>
        private sealed class AlignmentBackend : IPlaybackBackend
        {
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
                Snapshot = new PlaybackSnapshot { State = PlaybackState.Paused, Duration = TimeSpan.FromMinutes(3) };
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
                Snapshot = Snapshot with { Position = position };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            /// <summary>Publishes an explicit managed media lifecycle transition.</summary>
            /// <param name="state">The state to expose.</param>
            /// <returns>A completed transition operation.</returns>
            private Task ChangeStateAsync(PlaybackState state)
            {
                Snapshot = Snapshot with { State = state };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }
        }
    }
}
