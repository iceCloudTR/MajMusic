#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using SkiaSharp;
using TKCode.Arc.Motion;

namespace MajMusic.Tests
{
    /// <summary>Checks real cards in the production auto and star layout while the target query remains pending.</summary>
    internal static partial class SurfaceMotionTests
    {
        /// <summary>Measures the user's window size at fractional display scales before and during a real mode change.</summary>
        /// <param name="clock">The installed production Arc frame driver.</param>
        /// <returns>A task representing the production-layout pixel assertions.</returns>
        private static async Task ValidateActualCardsAsync(ManualFrameClock clock)
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-slide-cards-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var coverPath = Path.Combine(root, "cover.png");
            using (var cover = new SKBitmap(256, 256))
            using (var canvas = new SKCanvas(cover))
            using (var mark = new SKPaint { Color = OldGlyph, IsAntialias = false })
            {
                canvas.Clear(OldCover);
                canvas.DrawRect(112, 96, 32, 24, mark);
                using var data = cover.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(coverPath);
                data.SaveTo(file);
            }

            try
            {
                foreach (var scale in new[] { 1.25, 1.5, 1.75 })
                {
                    var workspace = new CardDownloadPresentationTests.DownloadWorkspace();
                    for (var index = 0; index < 6; index++)
                    {
                        workspace.Inner.Library.Add(new MusicTrack
                        {
                            Id = "slide-card-" + index,
                            Title = "Card geometry " + index,
                            Artist = "Fixture artist",
                            Audio = new MediaResource { LocalPath = "generated-slide-audio" },
                            Cover = index == 0 ? new MediaResource { LocalPath = coverPath } : null,
                            Duration = TimeSpan.FromMinutes(3),
                            AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 }
                        });
                    }

                    workspace.Inner.LocalPlaylists["slide-ordered"] = new MusicPlaylist
                    {
                        Id = "slide-ordered",
                        Name = "Selected cards",
                        TrackIds = ["slide-card-0", "slide-card-1"]
                    };
                    workspace.Inner.LocalPlaylists["slide-favorites"] = new MusicPlaylist { Id = "slide-favorites", Name = "Favorites", IsFavorite = true };
                    await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
                    await model.InitializeAsync();
                    model.ShowLibraryModeCommand.Execute(null);
                    await model.HomeUpdate;
                    var view = new MainView { DataContext = model };
                    var window = new Window { Content = view, Width = 1377, Height = 900 };
                    var released = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        window.Show();
                        window.SetRenderScaling(scale);
                        Pump(window);
                        await model.CardTracks[0].ActivateAsync();
                        Pump(window);
                        var browser = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
                        var surface = browser.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!;
                        var viewport = BoundsIn(surface, window);
                        var card = browser.GetVisualDescendants().OfType<SongCard>()
                            .Single(control => control.DataContext is SongCardViewModel presentation && presentation.Track.Id == "slide-card-0");
                        var cardBounds = card.Bounds;
                        var columns = model.BrowseColumns;
                        var page = view.GetVisualDescendants().OfType<MusicSurfaceHost>().Single(control => control.Name == "PageSurfaceTransition");
                        using var before = CapturePixels(window, $"ui-surface-cards-{scale * 100:0}-before.png");
                        var oldCover = FindColor(before, OldCover) ?? throw new InvalidOperationException("The real music card's diagnostic cover was not painted.");
                        var oldGlyph = FindColor(before, OldGlyph) ?? throw new InvalidOperationException("The real cover's diagnostic glyph was not painted.");
                        workspace.Inner.SearchOverride = (_, _) => released.Task;
                        model.SelectedPlaylist = model.Playlists.Single(item => item.Id == "slide-ordered");
                        model.ShowPlaylistsModeCommand.Execute(null);
                        Pump(window);
                        TestAssert.True(model.IsBrowseLoading && surface.HasSnapshot && surface.TransitionProgress == 0,
                            "The real mode switch must synchronously retain the old card frame and start its horizontal target surface.");
                        for (var index = 0; index <= 4; index++)
                        {
                            if (index > 0)
                            {
                                clock.Advance(TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(5));
                                Pump(window);
                            }

                            var progress = surface.TransitionProgress;
                            var currentViewport = BoundsIn(surface, window);
                            var rounding = 1.01 / scale;
                            TestAssert.True(model.BrowseColumns == columns && Math.Abs(currentViewport.X - viewport.X) <= rounding
                                && Math.Abs(currentViewport.Y - viewport.Y) <= rounding && Math.Abs(currentViewport.Width - viewport.Width) <= rounding
                                && Math.Abs(currentViewport.Height - viewport.Height) <= rounding
                                && card.Bounds.Size == cardBounds.Size && !page.HasSnapshot,
                                $"Adding the outgoing frame must not feed its desired size into responsive card layout or start another page animation at {scale * 100:0}%: "
                                + $"columns {columns}->{model.BrowseColumns}; viewport {viewport}->{BoundsIn(surface, window)}; card {cardBounds}->{card.Bounds}; desired {surface.DesiredSize}.");
                            AssertOpaqueHorizontalSurfaces(surface, -viewport.Width * (1 - progress), viewport.Width * progress);
                            using var frame = CapturePixels(window, $"ui-surface-cards-{scale * 100:0}-{index}.png");
                            var expectedCover = new Rect(oldCover.X / scale, oldCover.Y / scale, oldCover.Width / scale, oldCover.Height / scale)
                                .Translate(new Vector(viewport.Width * progress, 0));
                            var expectedGlyph = new Rect(oldGlyph.X / scale, oldGlyph.Y / scale, oldGlyph.Width / scale, oldGlyph.Height / scale)
                                .Translate(new Vector(viewport.Width * progress, 0));
                            AssertMarker(frame, OldCover, expectedCover, viewport, scale, $"Actual cover at {scale * 100:0}% / {index}");
                            AssertMarker(frame, OldGlyph, expectedGlyph, viewport, scale, $"Actual cover glyph at {scale * 100:0}% / {index}");
                            if (index == 0)
                            {
                                TestAssert.Equal(oldCover, FindColor(frame, OldCover)!.Value,
                                    "The real music cover must keep exactly its pre-click pixel size on the first snapshot frame.");
                                TestAssert.Equal(oldGlyph, FindColor(frame, OldGlyph)!.Value,
                                    "The real artwork glyph must keep exactly its pre-click pixel bounds on the first snapshot frame.");
                            }
                        }

                        released.TrySetResult(workspace.Inner.Library.ToArray());
                        await model.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                        Pump(window);
                        var completed = browser.GetVisualDescendants().OfType<SongCard>()
                            .Single(control => control.DataContext is SongCardViewModel presentation && presentation.Track.Id == "slide-card-0");
                        TestAssert.True(completed.Bounds.Size == cardBounds.Size && !surface.HasSnapshot
                            && browser.FindControl<Grid>("BrowseContentPresentation")!.Opacity == 1,
                            "Replacing the target skeleton must keep the real card's dimensions without an additional entrance fade or scale.");
                        using var finished = CapturePixels(window, $"ui-surface-cards-{scale * 100:0}-completed.png");
                        TestAssert.Equal(oldCover, FindColor(finished, OldCover)!.Value,
                            "The completed playlist's first cover must paint at the original size and vertical position.");
                    }
                    finally
                    {
                        released.TrySetResult(Array.Empty<MusicTrack>());
                        window.Close();
                    }
                }
            }
            finally
            {
                File.Delete(coverPath);
                Directory.Delete(root);
            }
        }
    }
}
