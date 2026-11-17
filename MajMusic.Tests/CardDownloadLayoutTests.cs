#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using MajMusic.Presentation.Theme;
using SkiaSharp;

namespace MajMusic.Tests
{
    /// <summary>Checks the rendered bottom-up download fill independently of song selection.</summary>
    internal static class CardDownloadLayoutTests
    {
        /// <summary>Renders download cards at desktop sizes and actual display scaling.</summary>
        /// <returns>A task representing the completed visual regressions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "MajMusic-card-download-layout-" + Guid.NewGuid().ToString("N"));
                var output = Path.Combine(Environment.CurrentDirectory, ".work");
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(output);
                var workspace = new CardDownloadPresentationTests.DownloadWorkspace();
                var names = new[] { "Skyline", "ゲシュタルト", "Albireo", "After the Rain — A Journey Beyond the Horizon", "星の旅", "Long Journey" };
                for (var index = 0; index < names.Length; index++)
                {
                    var cover = Path.Combine(root, "cover-" + index + ".png");
                    WriteCover(cover, index);
                    workspace.Inner.Library.Add(new MusicTrack
                    {
                        Id = "visual-download-" + index,
                        Title = names[index],
                        Artist = index == 1 ? "花譜" : "Fixture artist",
                        SourceId = "visual-source",
                        SourceName = "MajNet",
                        RemoteId = "remote-" + index,
                        EndpointId = "visual-endpoint",
                        Revision = "visual-revision",
                        Duration = TimeSpan.FromSeconds(155 + index),
                        AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 },
                        Audio = new MediaResource { RemoteUri = new Uri("https://audio.example.invalid/" + index) },
                        Cover = new MediaResource { LocalPath = cover }
                    });
                }

                var playback = new PlaybackService(new UnavailablePlaybackBackend());
                await using var model = new MainViewModel(workspace, playback, static action => action());
                var view = new MainView { DataContext = model };
                var window = new Window { Content = view, Width = 1100, Height = 760 };
                try
                {
                    await model.InitializeAsync();
                    model.ShowLibraryModeCommand.Execute(null);
                    await model.HomeUpdate;
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    await Task.WhenAll(model.CardTracks.Select(card => card.ActivateAsync()));
                    var tracks = model.CardTracks.Select(card => card.Track).ToArray();
                    Emit(workspace, tracks[1], AudioDownloadState.Preparing);
                    Emit(workspace, tracks[1], AudioDownloadState.Downloading, new MediaTransferProgress
                    {
                        ReceivedBytes = 250_000,
                        TotalBytes = 1_000_000,
                        BytesPerSecond = 125_000
                    });
                    var completedPath = Path.Combine(root, "completed.mp3");
                    await File.WriteAllTextAsync(completedPath, "generated audio availability fixture", timeout.Token);
                    Emit(workspace, tracks[2], AudioDownloadState.Preparing);
                    Emit(workspace, tracks[2], AudioDownloadState.Completed, path: completedPath);
                    Emit(workspace, tracks[3], AudioDownloadState.Preparing);
                    Emit(workspace, tracks[3], AudioDownloadState.Downloading, new MediaTransferProgress
                    {
                        ReceivedBytes = 32_000,
                        BytesPerSecond = 16_000
                    });
                    model.SelectedTrack = tracks[1];
                    await model.LoadCoverAsync();

                    foreach (var scale in new[] { 1d, 1.25 })
                    {
                        window.SetRenderScaling(scale);
                        foreach (var size in new[] { new Size(720, 540), new Size(1100, 760), new Size(1540, 900) })
                        {
                            window.Width = size.Width;
                            window.Height = size.Height;
                            window.UpdateLayout();
                            Dispatcher.UIThread.RunJobs();
                            var list = view.GetVisualDescendants().OfType<BrowseTracksView>().Single().ActiveList;
                            list.ScrollIntoView(model.BrowseRows[0]);
                            window.UpdateLayout();
                            Dispatcher.UIThread.RunJobs();
                            SaveFrame(window, Path.Combine(output, $"ui-card-downloads-{size.Width:0}-{scale * 100:0}.png"));
                            foreach (var index in new[] { 0, 1, 2, 3 })
                            {
                                var control = list.GetVisualDescendants().OfType<SongCard>()
                                    .FirstOrDefault(item => ReferenceEquals(item.DataContext, model.CardTracks[index]));
                                TestAssert.True(control is not null, "The initial viewport must realize the download-state fixture cards.");
                                AssertDownloadCard(control!, index == 1 ? 25 : index == 2 ? 100 : 0, index == 1, scale);
                            }

                        }
                    }

                    TestAssert.True(!model.CardTracks[0].HasDownloadStatus && model.CardTracks[2].DownloadStatus == "下载完成",
                        "Only a completed audio file may display the persistent completed-download label.");
                    TestAssert.True(model.CardTracks[3].IsDownloadIndeterminate && model.CardTracks[3].DownloadPercent == 0,
                        "Unknown transfer length must retain real byte details without inventing a fill percentage.");
                    model.SelectedTrack = tracks[0];
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var finalCards = view.GetVisualDescendants().OfType<SongCard>().ToArray();
                    AssertDownloadCard(finalCards.Single(card => ReferenceEquals(card.DataContext, model.CardTracks[0])), 0, true, window.RenderScaling);
                    AssertDownloadCard(finalCards.Single(card => ReferenceEquals(card.DataContext, model.CardTracks[1])), 25, false, window.RenderScaling);
                }
                finally
                {
                    window.Close();
                    await model.DisposeAsync();
                    var resolved = Path.GetFullPath(root);
                    var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    TestAssert.True(resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(resolved).StartsWith("MajMusic-card-download-layout-", StringComparison.Ordinal),
                        "Visual fixture cleanup must stay inside its generated temporary directory.");
                    Directory.Delete(resolved, true);
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Checks actual fill geometry, background pixels, text bounds and selection-border styling.</summary>
        /// <param name="card">The realized song card.</param>
        /// <param name="percent">The expected bottom-up fill percentage.</param>
        /// <param name="selected">Whether this card is the independently selected song.</param>
        /// <param name="scale">The current real display scaling.</param>
        private static void AssertDownloadCard(SongCard card, double percent, bool selected, double scale)
        {
            var pane = card.FindControl<Grid>("CardInformationPane")!;
            var fill = card.FindControl<Border>("CardDownloadFill")!;
            var origin = fill.TranslatePoint(default, pane)!.Value;
            var tolerance = 1.01 / scale;
            TestAssert.True(Math.Abs(fill.Bounds.Height - pane.Bounds.Height * percent / 100) <= tolerance,
                "The painted fill height must represent actual downloaded bytes.");
            TestAssert.True(Math.Abs(origin.Y + fill.Bounds.Height - pane.Bounds.Height) <= tolerance,
                "Download fill must remain anchored to the bottom of the information half.");
            var button = card.GetVisualDescendants().OfType<Button>().Single();
            var presenter = button.GetVisualDescendants().OfType<Border>()
                .Single(control => control.Name == "PART_Frame");
            TestAssert.Equal(ResourceColor(card, selected ? "MusicAccentBrush" : "MusicLineBrush"),
                ((ISolidColorBrush)presenter.BorderBrush!).Color,
                "Selection must use its own border color independently of download fill.");
            foreach (var name in new[] { "CardDownloadStatus", "CardDownloadDetails" })
            {
                var text = card.FindControl<TextBlock>(name)!;
                if (text.IsVisible && !string.IsNullOrEmpty(text.Text))
                {
                    var textOrigin = text.TranslatePoint(default, pane)!.Value;
                    TestAssert.True(text.Bounds.Height >= 10 && textOrigin.Y >= 0
                        && textOrigin.Y + text.Bounds.Height <= pane.Bounds.Height + tolerance,
                        $"Visible {name} must fit within the card: text={text.Bounds}, origin={textOrigin}, pane={pane.Bounds}, scale={scale}.");
                }
            }

            using var rendered = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(card.Bounds.Width), (int)Math.Ceiling(card.Bounds.Height)));
            rendered.Render(card);
            using var encoded = new MemoryStream();
            rendered.Save(encoded, PngBitmapEncoderOptions.Default);
            encoded.Position = 0;
            using var pixels = SKBitmap.Decode(encoded);
            var paneOrigin = pane.TranslatePoint(default, card)!.Value;
            var x = (int)Math.Ceiling(paneOrigin.X + 3);
            foreach (var fraction in new[] { 0.12, 0.5, 0.88 })
            {
                var y = (int)Math.Round(paneOrigin.Y + pane.Bounds.Height * fraction);
                var color = ResourceColor(card, fraction >= 1 - percent / 100 ? "MusicDownloadBrush" : "MusicPanelBrush");
                var expected = new SKColor(color.R, color.G, color.B, color.A);
                if (selected && card.DataContext is SongCardViewModel { HasDownloadStatus: false })
                {
                    var feedback = card.FindControl<CardFeedbackBorder>("CardFeedback")!;
                    var accent = ResourceColor(card, "MusicAccentBrush");
                    var actual = pixels.GetPixel(x, y);
                    TestAssert.True(feedback.InformationOpacity is > 0 and <= .04 && actual != expected
                        && Math.Abs(actual.Red - color.R) <= Math.Ceiling(Math.Abs(accent.R - color.R) * .04) + 1
                        && Math.Abs(actual.Green - color.G) <= Math.Ceiling(Math.Abs(accent.G - color.G) * .04) + 1
                        && Math.Abs(actual.Blue - color.B) <= Math.Ceiling(Math.Abs(accent.B - color.B) * .04) + 1,
                        "An idle selected card may apply only its restrained four-percent information wash.");
                    continue;
                }

                TestAssert.Equal(expected, pixels.GetPixel(x, y),
                    "Information background pixels must distinguish undownloaded and downloaded areas without tinting the cover.");
            }

            var artwork = card.GetVisualDescendants().OfType<Image>().First(image => image.IsEffectivelyVisible);
            var artworkOrigin = artwork.TranslatePoint(default, card)!.Value;
            var referenceImage = new Image { Source = artwork.Source, Stretch = artwork.Stretch, Width = artwork.Bounds.Width, Height = artwork.Bounds.Height, UseLayoutRounding = false };
            var reference = new Canvas { Width = card.Bounds.Width, Height = card.Bounds.Height, UseLayoutRounding = false, Children = { referenceImage } };
            Canvas.SetLeft(referenceImage, artworkOrigin.X);
            Canvas.SetTop(referenceImage, artworkOrigin.Y);
            reference.Measure(card.Bounds.Size);
            reference.Arrange(new Rect(card.Bounds.Size));
            using var referenceFrame = new RenderTargetBitmap(rendered.PixelSize);
            referenceFrame.Render(reference);
            using var referenceEncoded = new MemoryStream();
            referenceFrame.Save(referenceEncoded, PngBitmapEncoderOptions.Default);
            using var referencePixels = SKBitmap.Decode(referenceEncoded.ToArray());
            foreach (var fraction in new[] { .25, .5, .75 })
            {
                var coverX = (int)Math.Round(artworkOrigin.X + artwork.Bounds.Width * fraction);
                var coverY = (int)Math.Round(artworkOrigin.Y + artwork.Bounds.Height * fraction);
                TestAssert.Equal(referencePixels.GetPixel(coverX, coverY), pixels.GetPixel(coverX, coverY),
                    "The selected wash and transfer fill must preserve exact independently rendered artwork pixels.");
            }
        }

        /// <summary>Resolves the active theme's opaque paint rather than fixing visual tests to one palette.</summary>
        /// <param name="control">The card participating in the current theme scope.</param>
        /// <param name="key">The semantic brush resource.</param>
        /// <returns>The resolved solid color.</returns>
        private static Color ResourceColor(Control control, string key)
        {
            TestAssert.True(control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is ISolidColorBrush,
                "The active theme must provide a solid " + key + " brush.");
            return ((ISolidColorBrush)value!).Color;
        }

        /// <summary>Publishes one track-owned visual state without contacting a remote source.</summary>
        /// <param name="workspace">The controlled progress source.</param>
        /// <param name="track">The song owning the progress.</param>
        /// <param name="state">The operation lifecycle state.</param>
        /// <param name="transfer">The actual byte-count fixture.</param>
        /// <param name="path">The generated available file after completion.</param>
        private static void Emit(CardDownloadPresentationTests.DownloadWorkspace workspace, MusicTrack track,
            AudioDownloadState state, MediaTransferProgress? transfer = null, string? path = null)
        {
            workspace.Emit(new AudioDownloadProgress
            {
                OperationId = "visual-operation-" + track.Id,
                TrackId = track.Id,
                Title = track.Title,
                SourceId = track.SourceId,
                EndpointId = track.EndpointId,
                Revision = track.Revision,
                State = state,
                Transfer = transfer,
                LocalAudioPath = path
            });
        }

        /// <summary>Creates bounded fixture artwork using no external media files.</summary>
        /// <param name="path">The generated image location.</param>
        /// <param name="index">The deterministic palette offset.</param>
        private static void WriteCover(string path, int index)
        {
            using var bitmap = new SKBitmap(256, 256);
            using var canvas = new SKCanvas(bitmap);
            using var gradient = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(256, 256),
                [new SKColor((byte)(40 + index * 26), 92, 142), new SKColor(190, (byte)(118 + index * 19), 168)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = gradient, IsAntialias = true };
            canvas.DrawRect(0, 0, 256, 256, paint);
            using var rings = new SKPaint { Color = new SKColor(255, 255, 255, 65), Style = SKPaintStyle.Stroke, StrokeWidth = 20, IsAntialias = true };
            canvas.DrawCircle(128, 128, 76, rings);
            canvas.DrawCircle(128, 128, 36, rings);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
        }

        /// <summary>Saves the actual rendered window at its current display scale.</summary>
        /// <param name="window">The laid-out in-memory window.</param>
        /// <param name="path">The ignored visual artifact location.</param>
        private static void SaveFrame(Window window, string path)
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = new RenderTargetBitmap(new PixelSize((int)Math.Round(window.Width * window.RenderScaling),
                (int)Math.Round(window.Height * window.RenderScaling)), new Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
            frame.Render(window);
            frame.Save(path, PngBitmapEncoderOptions.Default);
        }
    }
}
