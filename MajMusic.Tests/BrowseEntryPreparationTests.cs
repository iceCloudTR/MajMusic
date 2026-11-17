#nullable enable

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using SkiaSharp;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;

namespace MajMusic.Tests
{
    /// <summary>Checks that incoming artwork is ready before its real card enters the visible viewport.</summary>
    internal static class BrowseEntryPreparationTests
    {
        /// <summary>Exercises delayed queries and covers, cached revisits and real top-navigation motion.</summary>
        /// <returns>A task representing the complete entry-preparation regressions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessFullMotionTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await session.Dispatch<bool>(async () =>
            {
                var clock = new ManualFrameClock();
                using var installed = clock.Install();
                await ValidatePreparedEntryAsync(clock, timeout.Token);
                return true;
            }, timeout.Token);
        }

        /// <summary>Holds storage and cover resolution across the first half of a real mode transition.</summary>
        /// <param name="clock">The actual Arc animation driver.</param>
        /// <param name="token">Cancels stalled fixture operations.</param>
        /// <returns>A task representing stable first-screen entry and cache reuse assertions.</returns>
        private static async Task ValidatePreparedEntryAsync(ManualFrameClock clock, CancellationToken token)
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-entry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var oldCover = Path.Combine(root, "old.png");
            var newCover = Path.Combine(root, "new.png");
            var timedOutCover = Path.Combine(root, "timeout.png");
            var staleCover = Path.Combine(root, "obsolete.png");
            WriteCover(oldCover, new SKColor(222, 118, 32));
            WriteCover(newCover, TargetColor);
            WriteCover(timedOutCover, TimedOutColor);
            WriteCover(staleCover, StaleColor);
            var workspace = new CardDownloadPresentationTests.DownloadWorkspace();
            for (var index = 0; index < 24000; index++)
            {
                workspace.Inner.Library.Add(new MusicTrack
                {
                    Id = "entry-" + index.ToString("D5"),
                    Title = "Prepared music " + index.ToString("D5"),
                    Audio = new MediaResource { LocalPath = "generated-entry-audio" },
                    Cover = index is 0 or 23996 or 23997 or 23998 or 23999 ? new MediaResource
                    {
                        LocalPath = index == 0 ? oldCover : index == 23996 ? staleCover
                            : index == 23997 ? Path.Combine(root, "missing.png") : index == 23998 ? timedOutCover : newCover
                    } : null,
                    Duration = TimeSpan.FromMinutes(3),
                    AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000, SampleRateHz = 44100 }
                });
            }

            var target = workspace.Inner.Library[^1];
            workspace.Inner.LocalPlaylists["entry-target"] = new MusicPlaylist { Id = "entry-target", Name = "Prepared target", TrackIds = [target.Id] };
            workspace.Inner.LocalPlaylists["entry-timeout"] = new MusicPlaylist { Id = "entry-timeout", Name = "Delayed target", TrackIds = ["entry-23998"] };
            workspace.Inner.LocalPlaylists["entry-missing"] = new MusicPlaylist { Id = "entry-missing", Name = "Unavailable artwork", TrackIds = ["entry-23997"] };
            workspace.Inner.LocalPlaylists["entry-obsolete"] = new MusicPlaylist { Id = "entry-obsolete", Name = "Obsolete artwork", TrackIds = ["entry-23996"] };
            var query = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cover = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var coverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timedOut = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeoutStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var obsoleteRead = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var obsoleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleArtwork = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.ResolveCoverOverride = (track, _) =>
            {
                if (track.Id == target.Id)
                {
                    coverStarted.TrySetResult();
                    return cover.Task;
                }

                if (track.Id == "entry-23998")
                {
                    timeoutStarted.TrySetResult();
                    return timedOut.Task;
                }

                if (track.Id == "entry-23996")
                {
                    staleStarted.TrySetResult();
                    return staleArtwork.Task;
                }

                return Task.FromResult(track.Cover?.LocalPath);
            };
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
            await model.InitializeAsync();
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate.WaitAsync(token);
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 1100, Height = 760 };
            try
            {
                window.Show();
                Pump(window);
                await model.CardTracks[0].ActivateAsync();
                Pump(window);
                var browser = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
                var surface = browser.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!;
                workspace.Inner.SearchOverride = (_, _) =>
                {
                    queryStarted.TrySetResult();
                    return query.Task;
                };
                model.SelectedPlaylist = model.Playlists.Single(item => item.Id == "entry-target");
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(1)!);
                await queryStarted.Task.WaitAsync(token);
                Advance(clock, window, 90);
                AssertSkeletonEntry(window, browser, surface, "query");
                query.TrySetResult(workspace.Inner.Library.ToArray());
                await coverStarted.Task.WaitAsync(token);
                var observingCard = model.CardTracks[^1];
                var ready = observingCard.ActivateAsync();
                cover.TrySetResult(newCover);
                await ready.WaitAsync(token);
                Advance(clock, window, 90);
                AssertSkeletonEntry(window, browser, surface, "cover-ready");
                Advance(clock, window, 90);
                AssertSkeletonEntry(window, browser, surface, "three-quarter");
                Advance(clock, window, 110);
                await model.HomeUpdate.WaitAsync(token);
                Pump(window);
                TestAssert.True(!model.IsBrowseLoading && model.CardTracks.Count == 1 && model.CardTracks[0].Cover is not null,
                    "The first visible real target card must already own its decoded cover.");
                TestAssert.True(ContainsTargetColor(window, "ui-entry-ready.png"),
                    "The decoded target artwork must appear in the first complete real-card frame.");
                observingCard.Deactivate();
                TestAssert.True(model.MaterializedCardCount < 100,
                    "Preparing a 24,000-song catalog must retain only the bounded entry presentations.");
                var requests = workspace.CoverRequests[target.Id];
                workspace.Inner.SearchOverride = null;
                await model.BrowsePreloadUpdate.WaitAsync(token);
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(2)!);
                TestAssert.Equal(12, model.CardTracks.Count, "A warm library entry must first publish its bounded prepared prefix.");
                var preparedRows = model.BrowseRows.ToArray();
                var expansion = new List<NotifyCollectionChangedEventArgs>();
                void OnLibraryExpanded(object? sender, NotifyCollectionChangedEventArgs args)
                {
                    expansion.Add(args);
                }

                model.BrowseRows.CollectionChanged += OnLibraryExpanded;
                try
                {
                    Advance(clock, window, 420);
                    await model.HomeUpdate.WaitAsync(token);
                }
                finally
                {
                    model.BrowseRows.CollectionChanged -= OnLibraryExpanded;
                }
                TestAssert.True(expansion.Count == 1 && expansion[0].Action == NotifyCollectionChangedAction.Add
                    && expansion[0].NewStartingIndex == preparedRows.Length
                    && expansion[0].NewItems!.Count == model.BrowseRows.Count - preparedRows.Length,
                    "Completing a prepared library prefix must append its full tail once without resetting existing virtualized rows.");
                TestAssert.True(preparedRows.Select((row, index) => ReferenceEquals(row, model.BrowseRows[index])).All(retained => retained),
                    "Prepared first-screen rows must keep their presentation identities when the complete catalog arrives.");
                var skeleton = browser.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!;
                var reusedBeforeEntry = skeleton.ReusedPlaceholderCount;
                var createdBeforeEntry = skeleton.CreatedPlaceholderCount;
                var notificationStates = new List<(string? Name, bool Placeholder)>();
                void OnReadyEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(MainViewModel.IsBrowsePlaceholderVisible))
                    {
                        notificationStates.Add((args.PropertyName, model.IsBrowsePlaceholderVisible));
                    }
                }

                model.PropertyChanged += OnReadyEntryChanged;
                try
                {
                    Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(1)!);
                    Pump(window);
                }
                finally
                {
                    model.PropertyChanged -= OnReadyEntryChanged;
                }
                TestAssert.True(notificationStates.All(item => !item.Placeholder),
                    "Ready-preview navigation must never notify bindings to display placeholders: "
                    + string.Join(", ", notificationStates.Where(item => item.Placeholder).Select(item => item.Name)));
                TestAssert.True(skeleton.ReusedPlaceholderCount == reusedBeforeEntry && skeleton.CreatedPlaceholderCount == createdBeforeEntry,
                    "Entering an already prepared playlist must neither borrow nor construct placeholder controls on the input stack.");
                var incoming = model.CardTracks.Count > 0 ? model.CardTracks[0] : null;
                TestAssert.True(incoming is { Cover: not null } && incoming.Track.Id == target.Id,
                    "A cached playlist's first real card must have its cover before its horizontal entry starts.");
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                TestAssert.Equal(requests, workspace.CoverRequests[target.Id],
                    "Revisiting ready artwork must not resolve or download the same cover again.");
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(2)!);
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                model.SelectedPlaylist = model.Playlists.Single(item => item.Id == "entry-timeout");
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(1)!);
                await timeoutStarted.Task.WaitAsync(token);
                Advance(clock, window, 90);
                await Task.Delay(550, token);
                AssertSkeletonEntry(window, browser, surface, "timeout", TimedOutColor);
                TestAssert.True(!model.HomeUpdate.IsCompleted,
                    "An expired cover preparation must still keep the incoming skeleton fixed until the actual slide completes.");
                Advance(clock, window, 310);
                await model.HomeUpdate.WaitAsync(token);
                Pump(window);
                TestAssert.True(!model.IsBrowseLoading && model.CardTracks.Count == 1 && model.CardTracks[0].Cover is null,
                    "A slow or unavailable cover must not keep the target playlist permanently loading.");
                var timeoutCard = model.CardTracks[0];
                var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnResumedCover(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(SongCardViewModel.Cover) && timeoutCard.Cover is not null)
                    {
                        TestAssert.True(surface.TransitionProgress == 1,
                            "An expired cover may update its visible placeholder only after the entry slide finishes.");
                        resumed.TrySetResult();
                    }
                }

                timeoutCard.PropertyChanged += OnResumedCover;
                try
                {
                    timedOut.TrySetResult(timedOutCover);
                    await resumed.Task.WaitAsync(token);
                    Pump(window);
                    TestAssert.True(ContainsTargetColor(window, "ui-entry-timeout-resumed.png", TimedOutColor),
                        "A timed-out placeholder must resume its cover request after entry without requiring the user to scroll away.");
                }
                finally
                {
                    timeoutCard.PropertyChanged -= OnResumedCover;
                }

                var resumedRequests = workspace.CoverRequests["entry-23998"];
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(2)!);
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                model.SelectedPlaylist = model.Playlists.Single(item => item.Id == "entry-timeout");
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(1)!);
                Pump(window);
                TestAssert.True(model.CardTracks.Count == 1 && model.CardTracks[0].Track.Id == "entry-23998"
                    && model.CardTracks[0].Cover is not null,
                    "Artwork completed after an earlier timeout must be ready on the first real frame of its next cached entry.");
                TestAssert.Equal(resumedRequests, workspace.CoverRequests["entry-23998"],
                    "Refreshing a timed-out preview from its now-ready cache must not issue another source request.");
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                workspace.Inner.SearchOverride = (_, _) =>
                {
                    obsoleteStarted.TrySetResult();
                    return obsoleteRead.Task;
                };
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(2)!);
                var obsoleteUpdate = model.HomeUpdate;
                await obsoleteStarted.Task.WaitAsync(token);
                workspace.Inner.SearchOverride = null;
                model.SelectedPlaylist = model.Playlists.Single(item => item.Id == "entry-target");
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(1)!);
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                obsoleteRead.TrySetResult([workspace.Inner.Library[0] with { Title = "Obsolete query result" }]);
                await obsoleteUpdate.WaitAsync(token);
                Pump(window);
                TestAssert.True(!model.IsBrowseLoading && model.IsPlaylistsMode && model.Tracks.Single().Id == target.Id
                    && model.CardTracks[0].Cover is not null,
                    "A superseded query must not replace the newer prepared target or restart its loading presentation.");
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(2)!);
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                model.SelectedPlaylist = model.Playlists.Single(item => item.Id == "entry-missing");
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(1)!);
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3), token);
                Pump(window);
                TestAssert.True(!model.IsBrowseLoading && model.CardTracks.Single().Cover is null
                    && model.ShowLibraryModeCommand.CanExecute(null) && model.ShowRecommendationsModeCommand.CanExecute(null),
                    "Unavailable artwork must finish with an interactive placeholder rather than permanently blocking navigation.");
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(2)!);
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                model.SelectedPlaylist = model.Playlists.Single(item => item.Id == "entry-obsolete");
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(1)!);
                var obsoleteCoverUpdate = model.HomeUpdate;
                await staleStarted.Task.WaitAsync(token);
                Advance(clock, window, 90);
                Tap(window, view.FindControl<SegmentedControl>("HomeModeSegments")!.ContainerFromIndex(2)!);
                Advance(clock, window, 420);
                await model.HomeUpdate.WaitAsync(token);
                staleArtwork.TrySetResult(staleCover);
                await obsoleteCoverUpdate.WaitAsync(token);
                Pump(window);
                TestAssert.True(model.IsLibraryMode && model.CardTracks.Count == 24000 && model.CardTracks[0].Track.Id == "entry-00000"
                    && !ContainsTargetColor(window, "ui-entry-obsolete-cover.png", StaleColor),
                    "A cover source that finishes after cancellation must not replace the newer target's cards or artwork.");
                ValidateTopNavigation(clock, window, view);
            }
            finally
            {
                query.TrySetResult(Array.Empty<MusicTrack>());
                cover.TrySetResult(newCover);
                timedOut.TrySetResult(timedOutCover);
                obsoleteRead.TrySetResult(Array.Empty<MusicTrack>());
                staleArtwork.TrySetResult(staleCover);
                window.Close();
                await model.DisposeAsync();
                File.Delete(oldCover);
                File.Delete(newCover);
                File.Delete(timedOutCover);
                File.Delete(staleCover);
                Directory.Delete(root);
            }
        }

        /// <summary>Checks a genuine top segmented glider and a single S-eased page transition from one pointer action.</summary>
        /// <param name="clock">The real Arc frame driver.</param>
        /// <param name="window">The rendered input viewport.</param>
        /// <param name="view">The production desktop navigation host.</param>
        private static void ValidateTopNavigation(ManualFrameClock clock, Window window, MainView view)
        {
            var navigation = view.FindControl<SegmentedControl>("DesktopTopNavigation")!;
            var indicator = navigation.GetVisualDescendants().OfType<GlidingIndicator>().Single(control => control.Name == "PART_Selection");
            var host = view.GetVisualDescendants().OfType<MusicSurfaceHost>().Single(control => control.Name == "PageSurfaceTransition");
            var initial = indicator.CurrentRect.X;
            var starts = 0;
            void OnProgress(object? sender, AvaloniaPropertyChangedEventArgs args)
            {
                if (args.Property == MusicSurfaceHost.TransitionProgressProperty && host.TransitionProgress == 0)
                {
                    starts++;
                }
            }

            host.PropertyChanged += OnProgress;
            try
            {
                Tap(window, navigation.ContainerFromIndex(3)!);
                Pump(window);
                var transition = host.TransitionUpdate;
                var progress = new List<double> { host.TransitionProgress };
                var glide = new List<double> { indicator.CurrentRect.X };
                var capturedGlide = false;
                for (var index = 0; index < 4; index++)
                {
                    Advance(clock, window, 90);
                    progress.Add(host.TransitionProgress);
                    glide.Add(indicator.CurrentRect.X);
                    if (!capturedGlide && indicator.CurrentRect.X > initial + 1
                        && indicator.CurrentRect.X < navigation.ContainerFromIndex(3)!.TranslatePoint(default, indicator)!.Value.X - 1)
                    {
                        capturedGlide = true;
                        AssertPaintedGlider(window, indicator);
                    }
                    TestAssert.True(ReferenceEquals(transition, host.TransitionUpdate),
                        "A single top-navigation gesture must retain one page-transition task while its labels and selected content update.");
                }

                TestAssert.True(view.SelectedSectionIndex == 3 && starts == 1 && !host.HasSnapshot,
                    "Top navigation must open its original page with exactly one surface entry.");
                var early = progress[1] - progress[0];
                var middle = progress[2] - progress[1];
                var late = progress[4] - progress[3];
                TestAssert.True(early > 0 && middle > early * 1.4 && middle > late * 1.4,
                    "The real top-page slide must accelerate and decelerate on its shared S curve.");
                TestAssert.True(capturedGlide && glide.Any(value => value > initial + 1 && value < glide[^1] - 1),
                    $"The top navigation's real Arc selection pill must paint intermediate positions like the home mode selector: initial {initial}, level {ArcMotion.GetEffectiveLevel(indicator)}, "
                    + string.Join(", ", glide.Select(value => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))));
                view.NavigateToSection(0);
                Advance(clock, window, 420);
            }
            finally
            {
                host.PropertyChanged -= OnProgress;
            }
        }

        /// <summary>Gets the target artwork's exclusive opaque test color.</summary>
        private static SKColor TargetColor => new(37, 188, 211);
        /// <summary>Gets the slow cover's exclusive opaque test color.</summary>
        private static SKColor TimedOutColor => new(187, 57, 209);
        /// <summary>Gets an obsolete request's exclusive opaque artwork color.</summary>
        private static SKColor StaleColor => new(90, 210, 70);

        /// <summary>Checks the compositor paints the moving selection pill at its intermediate geometry.</summary>
        /// <param name="window">The actual rendering viewport.</param>
        /// <param name="indicator">The production Arc indicator at an intermediate frame.</param>
        private static void AssertPaintedGlider(Window window, GlidingIndicator indicator)
        {
            using var frame = window.CaptureRenderedFrame()!;
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            var output = Path.Combine(Environment.CurrentDirectory, ".work");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "ui-top-navigation-middle.png"), stream.ToArray());
            stream.Position = 0;
            using var pixels = SKBitmap.Decode(stream);
            var point = indicator.TranslatePoint(new Point(indicator.CurrentRect.X + 6,
                indicator.CurrentRect.Y + indicator.CurrentRect.Height / 2), window)!.Value;
            TestAssert.True(indicator.Fill is ISolidColorBrush, "The real navigation selection must have a visible solid fill.");
            var expected = ((ISolidColorBrush)indicator.Fill!).Color;
            var actual = pixels.GetPixel((int)Math.Floor(point.X * window.RenderScaling), (int)Math.Floor(point.Y * window.RenderScaling));
            TestAssert.True(Math.Abs(expected.R - actual.Red) <= 3 && Math.Abs(expected.G - actual.Green) <= 3
                && Math.Abs(expected.B - actual.Blue) <= 3,
                "The actual top-navigation frame must paint the selection pill at its intermediate location.");
        }

        /// <summary>Requires one stable incoming skeleton rather than replacing it with late artwork halfway through its slide.</summary>
        /// <param name="window">The rendering viewport.</param>
        /// <param name="browser">The current browser.</param>
        /// <param name="surface">The current horizontal transition.</param>
        /// <param name="phase">The diagnostic fixture phase.</param>
        /// <param name="lateColor">The artwork color that must remain unpublished during entry.</param>
        private static void AssertSkeletonEntry(Window window, BrowseTracksView browser, MusicSurfaceHost surface, string phase, SKColor? lateColor = null)
        {
            TestAssert.True(surface.TransitionProgress is > 0 and < 1 && browser.FindControl<Grid>("BrowseSkeletonPresentation")!.IsVisible
                && !browser.FindControl<ListBox>("CardRowsList")!.IsVisible,
                "The incoming skeleton must stay fixed until the complete mode slide finishes: " + phase);
            TestAssert.True(!ContainsTargetColor(window, "ui-entry-" + phase + ".png", lateColor),
                "A completed late cover must not suddenly paint over the entering skeleton: " + phase);
        }

        /// <summary>Captures real output and checks whether the new artwork is actually painted.</summary>
        /// <param name="window">The actual rendering window.</param>
        /// <param name="name">The ignored diagnostic frame name.</param>
        /// <param name="expected">The exclusive artwork color to find.</param>
        /// <returns>Whether the exclusive target color appears in the frame.</returns>
        private static bool ContainsTargetColor(Window window, string name, SKColor? expected = null)
        {
            Pump(window);
            using var frame = window.CaptureRenderedFrame()!;
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            var output = Path.Combine(Environment.CurrentDirectory, ".work");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, name), stream.ToArray());
            stream.Position = 0;
            using var pixels = SKBitmap.Decode(stream);
            for (var y = 0; y < pixels.Height; y++)
            {
                for (var x = 0; x < pixels.Width; x++)
                {
                    if (pixels.GetPixel(x, y) == (expected ?? TargetColor))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Writes generated artwork without accessing an external music library.</summary>
        /// <param name="path">The generated image location.</param>
        /// <param name="color">The unique diagnostic image color.</param>
        private static void WriteCover(string path, SKColor color)
        {
            using var bitmap = new SKBitmap(128, 128);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(color);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
        }

        /// <summary>Advances deterministic animation and commits the actual renderer.</summary>
        /// <param name="clock">The installed Arc frame driver.</param>
        /// <param name="window">The rendering viewport.</param>
        /// <param name="milliseconds">The finite time interval.</param>
        private static void Advance(ManualFrameClock clock, Window window, int milliseconds)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMilliseconds(5));
            Pump(window);
        }

        /// <summary>Executes a real pointer gesture on a production navigation option.</summary>
        /// <param name="window">The input owner.</param>
        /// <param name="control">The actual option container.</param>
        private static void Tap(Window window, Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        /// <summary>Flushes layout and actual in-memory rendering.</summary>
        /// <param name="window">The current rendering viewport.</param>
        private static void Pump(Window window)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
