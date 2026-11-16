#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using TKCode.Arc.Primitives;
using MobileSegments = TKCode.Arc.Mobile.Segmented;

namespace MajMusic.Tests
{
    /// <summary>Checks actual phone selection insets and responsive desktop artwork using generated catalog entries.</summary>
    internal static class SurfaceGeometryTests
    {
        /// <summary>Uses a process-owned UI lifetime to retain third-party geometry dispatcher ownership.</summary>
        /// <returns>A task representing the rendered surface checks.</returns>
        public static Task RunAsync()
        {
            return HeadlessProcessIsolation.RunAsync("surface-geometry", RunHeadlessAsync);
        }

        /// <summary>Checks full-width desktop artwork, stable controls and inset mobile selection at multiple display scales.</summary>
        /// <returns>A task representing actual production-template layout assertions.</returns>
        private static async Task RunHeadlessAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await session.Dispatch<bool>(async () =>
            {
                var workspace = new LibraryExperienceTests.ExperienceWorkspace();
                workspace.Library.Add(new MusicTrack
                {
                    Id = "surface-track", Title = "Generated artwork", Artist = "Generated artist", SourceId = "local/surface",
                    Audio = new MediaResource { LocalPath = Path.GetFullPath("generated-surface-audio.mp3") }
                });
                await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
                await model.InitializeAsync();
                model.ShowLibraryModeCommand.Execute(null);
                await model.HomeUpdate;
                model.SelectedTrack = model.Tracks.Single();
                var view = new MainView { DataContext = model };
                var window = new Window { Content = view, Width = 1100, Height = 760 };
                try
                {
                    window.Show();
                    foreach (var scale in new[] { 1d, 1.25, 2, 3.25 })
                    {
                        window.SetRenderScaling(scale);
                        window.Width = 1100;
                        window.Height = 760;
                        Layout(window);
                        var details = view.FindControl<Border>("LibraryDetails")!;
                        var artwork = view.FindControl<Viewbox>("DesktopDetailsCover")!;
                        var available = details.Bounds.Width - details.Padding.Left - details.Padding.Right
                            - details.BorderThickness.Left - details.BorderThickness.Right;
                        TestAssert.True(artwork.Bounds.Width >= available - 2 && artwork.Bounds.Width > 140,
                            "Normal desktop artwork must fill the detail column instead of remaining a fixed small thumbnail.");
                        TestAssert.True(Math.Abs(artwork.Bounds.Width - artwork.Bounds.Height) < 1,
                            "Desktop artwork must retain its square aspect ratio when the sidebar changes size.");
                        TestAssert.True(details.BorderThickness.IsUniform && details.BorderThickness.Top == MusicGeometry.OutlineWidth
                            && details.BackgroundSizing == Avalonia.Media.BackgroundSizing.OuterBorderEdge && details.UseLayoutRounding,
                            "Shared panels must use uniform rounded outline geometry while retaining pixel-aligned content layout.");
                        if (scale == 1)
                        {
                            var output = Path.Combine(Environment.CurrentDirectory, ".work");
                            Directory.CreateDirectory(output);
                            using var frame = new RenderTargetBitmap(new PixelSize(1100, 760));
                            frame.Render(window);
                            frame.Save(Path.Combine(output, "ui-desktop-details-width.png"), PngBitmapEncoderOptions.Default);
                        }

                        window.Width = 390;
                        window.Height = 844;
                        Layout(window);
                        var mobile = view.GetVisualDescendants().OfType<MobileMusicView>().Single();
                        var segments = mobile.GetVisualDescendants().OfType<MobileSegments>().Single();
                        var indicator = segments.GetVisualDescendants().OfType<GlidingIndicator>().Single();
                        var origin = indicator.TranslatePoint(indicator.CurrentRect.TopLeft, segments)!.Value;
                        var visibleTop = origin.Y - indicator.StrokeThickness / 2;
                        var visibleBottom = origin.Y + indicator.CurrentRect.Height + indicator.StrokeThickness / 2;
                        TestAssert.True(indicator.CurrentRect.Height > 30 && visibleTop >= 3 && visibleBottom <= segments.Bounds.Height - 3,
                            "The painted selected item must have a visible gap above and below, including its stroke.");
                        var widths = Enumerable.Range(0, 3).Select(index => segments.ContainerFromIndex(index)!.Bounds.Width).ToArray();
                        TestAssert.True(widths.Max() - widths.Min() < 1.1 && Enumerable.Range(0, 3)
                            .All(index => segments.ContainerFromIndex(index)!.Bounds.Height >= 43.9),
                            "The three mobile choices must remain evenly divided with full touch targets.");
                    }
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, timeout.Token);
        }

        /// <summary>Realizes templates and painting targets after an actual window size or display-scale change.</summary>
        /// <param name="window">The real headless window under test.</param>
        private static void Layout(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
