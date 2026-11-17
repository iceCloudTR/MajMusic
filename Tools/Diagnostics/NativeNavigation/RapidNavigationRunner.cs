#nullable enable

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using TKCode.Arc;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace MajMusic.Diagnostics.NativeNavigation
{
    /// <summary>Exercises the real segmented input while sampling actual vector geometry in a native Win32 window.</summary>
    internal static class RapidNavigationRunner
    {
        private const int ResidentPrefix = 25;
        private static readonly HomeBrowseMode[] Modes = Enum.GetValues<HomeBrowseMode>();
        private static readonly MethodInfo? GetBrowsePage = typeof(MainViewModel).GetMethod("GetBrowsePage", [typeof(HomeBrowseMode)]);
        private static readonly MethodInfo? GetResidentList = typeof(BrowseTracksView).GetMethod("GetResidentList", [typeof(HomeBrowseMode)]);
        private static readonly PropertyInfo? ResidentUpdate = typeof(BrowseTracksView).GetProperty("ResidentTransitionUpdate");
        private static readonly MethodInfo QuickPlayInitialPose = typeof(CoverPlayGesture).GetMethod("InitialButton", BindingFlags.Instance | BindingFlags.NonPublic)!;

        /// <summary>Runs finite warmed comparisons with a populated synthetic feed and writes the measured geometry and timings.</summary>
        public static async Task RunAsync(Window window, string output)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var token = timeout.Token;
            using var fixture = await SyntheticLibrary.CreateAsync(Path.Combine(output, "generated-data"), token, separateArtwork: true);
            var workspace = new RapidNavigationWorkspace(fixture);
            var playback = new PlaybackService(new SilentPlaybackBackend(), resolveAudioPath: (_, _) => Task.FromResult("silent-fixture"));
            await using var model = new MainViewModel(workspace, playback);
            await model.InitializeAsync().WaitAsync(token);
            await model.EnsureHomeRecommendationsAsync().WaitAsync(token);
            await playback.SetQueueAsync((await workspace.SearchAsync(null, token)).Take(2), cancellationToken: token);
            var view = new MainView { DataContext = model };
            var probe = new SceneRecordProbe { Child = view };
            window.Content = probe;
            window.UpdateLayout();
            await model.BrowsePreloadUpdate.WaitAsync(token);
            await Task.Delay(350, token);
            var browser = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
            var selector = Named<SelectingItemsControl>(view, view.IsMobileLayout ? "MobileHomeModes" : "HomeModeSegments");
            var original = new Dictionary<HomeBrowseMode, PageSample>();
            if (GetResidentList is not null && GetBrowsePage is not null)
            {
                foreach (var mode in Modes)
                {
                    original[mode] = ReadPage(model, browser, mode);
                }
            }
            else
            {
                foreach (var mode in Modes.Skip(1).Concat(Modes.Take(1)))
                {
                    await SelectModeAsync(window, selector, model, mode, token);
                    await model.HomeUpdate.WaitAsync(token);
                    await Transition(browser).WaitAsync(token);
                    await model.BrowsePreloadUpdate.WaitAsync(token);
                    await Task.Delay(60, token);
                    original[mode] = ReadPage(model, browser, mode);
                }
            }

            var fixedIcons = FixedIcons(view, model.IsPlaying);
            var frames = new List<RapidFrame>();
            var actions = new List<RapidAction>();
            var startingInk = fixedIcons.Select(icon => Ink(icon.Glyph, window)).ToArray();
            var clock = Stopwatch.StartNew();
            var activeMode = model.HomeMode;
            var quickPlayProbes = CreateQuickPlayProbes(browser, activeMode);
            var hoveredOrigins = new List<string>();
            var observe = true;
            void RecordFrame(TimeSpan _)
            {
                if (!observe)
                {
                    return;
                }
                var clip = browser.TranslatePoint(default, window) is { } origin ? new Rect(origin, browser.Bounds.Size) : default;
                var quickPlay = quickPlayProbes.Select(probe => QuickPlayInk(probe, window, clip)).OfType<QuickPlaySample>().ToArray();
                frames.Add(new RapidFrame(clock.Elapsed.TotalMilliseconds, activeMode.ToString(), PanelPositions(browser),
                    fixedIcons.Select(icon => Ink(icon.Glyph, window)).ToArray(), quickPlay, !Transition(browser).IsCompleted));
                window.RequestAnimationFrame(RecordFrame);
            }
            window.RequestAnimationFrame(RecordFrame);
            try
            {
                await Task.Delay(35, token);
                var bursts = new HomeBrowseMode[][]
                {
                    [HomeBrowseMode.Playlists, HomeBrowseMode.Recommendations, HomeBrowseMode.Playlists, HomeBrowseMode.Library],
                    [HomeBrowseMode.Playlists, HomeBrowseMode.Library, HomeBrowseMode.Recommendations, HomeBrowseMode.Playlists],
                    [HomeBrowseMode.Library, HomeBrowseMode.Playlists, HomeBrowseMode.Library, HomeBrowseMode.Recommendations]
                };
                foreach (var burst in bursts)
                {
                    if (quickPlayProbes.FirstOrDefault(probe => probe.Mode == activeMode.ToString()) is { } quickPlayOrigin
                        && HoverQuickPlay(quickPlayOrigin.Icon, window))
                    {
                        hoveredOrigins.Add(activeMode.ToString());
                    }
                    foreach (var mode in burst)
                    {
                        var interrupted = !Transition(browser).IsCompleted;
                        var positionsBefore = PanelPositions(browser);
                        var requestAt = clock.Elapsed.TotalMilliseconds;
                        var acceptedAt = await SelectModeAsync(window, selector, model, mode, token, clock);
                        activeMode = mode;
                        var pageAtAcceptance = ReadPage(model, browser, mode);
                        var positionsAfter = PanelPositions(browser);
                        if (GetResidentList is null)
                        {
                            quickPlayProbes = CreateQuickPlayProbes(browser, activeMode);
                        }
                        actions.Add(new RapidAction(mode.ToString(), requestAt, acceptedAt, interrupted,
                            positionsBefore, positionsAfter, pageAtAcceptance,
                            fixedIcons.Select(icon => Ink(icon.Glyph, window)).ToArray()));
                        await Task.Delay(42, token);
                    }
                    await model.HomeUpdate.WaitAsync(token);
                    await Transition(browser).WaitAsync(token);
                    await Task.Delay(70, token);
                }
            }
            finally
            {
                observe = false;
            }

            var cardinalFrames = frames.Where(frame => frame.AtMs >= actions[0].RequestedAtMs && frame.AtMs <= actions[^1].AcceptedAtMs + 60).ToArray();
            var snapshots = frames.Select(frame => frame.FixedIcons).Concat(actions.Select(action => action.FixedIcons)).ToArray();
            var maxIconDelta = snapshots.Max(icons => InkDifference(startingInk, icons));
            var iconVisibilityChanges = snapshots.Count(icons => startingInk.Zip(icons).Any(pair => (pair.First is null) != (pair.Second is null)));
            var quickPlayMovements = frames.SelectMany(frame => frame.QuickPlay.Select(icon => (Icon: icon, frame.InMotion)))
                .GroupBy(frame => (frame.Icon.Mode, frame.Icon.ControlIdentity)).Select(group => new
                {
                    group.Key.Mode,
                    group.Key.ControlIdentity,
                    TrackIds = group.Select(frame => frame.Icon.TrackId).Distinct().ToArray(),
                    ArtworkIdentities = group.Select(frame => frame.Icon.ArtworkIdentity).Distinct().ToArray(),
                    VisibleFramesInMotion = group.Count(frame => frame.InMotion && frame.Icon.InViewport),
                    MissingArtworkFrames = group.Count(frame => frame.Icon.ArtworkIdentity is null),
                    LocalXRange = Range(group.Select(frame => frame.Icon.LocalX)),
                    LocalYRange = Range(group.Select(frame => frame.Icon.LocalY)),
                    WindowYRange = Range(group.Select(frame => frame.Icon.WindowY))
                }).ToArray();
            var finalPages = Modes.Select(mode => ReadPage(model, browser, mode)).ToArray();
            var stablePreparedPages = GetResidentList is not null && finalPages.Concat(actions.Select(action => action.AtAcceptance)).All(page =>
            {
                var initial = original[Enum.Parse<HomeBrowseMode>(page.Mode)];
                return page.ReadyCoverCount == ResidentPrefix && page.FirstCards.SequenceEqual(initial.FirstCards)
                    && page.MountedPrefix.Length > 0 && page.MountedPrefix.ToHashSet().SetEquals(initial.MountedPrefix);
            });
            var result = new
            {
                Platform = RuntimeInformation.OSDescription,
                window.RenderScaling,
                window.ClientSize.Width,
                window.ClientSize.Height,
                view.IsMobileLayout,
                HasResidentViews = GetResidentList is not null,
                BeforeSwitches = original.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
                AfterSwitches = finalPages,
                StablePreparedPages = stablePreparedPages,
                AcceptedSwitches = actions.Count,
                InterruptedSwitches = actions.Count(action => action.Interrupted),
                FrameSamplesDuringSwitches = cardinalFrames.Length,
                MaximumFrameGapMs = cardinalFrames.Zip(cardinalFrames.Skip(1), (previous, next) => next.AtMs - previous.AtMs).DefaultIfEmpty().Max(),
                MaxStationaryInkDelta = maxIconDelta,
                StationaryInkVisibilityChanges = iconVisibilityChanges,
                HoveredOrigins = hoveredOrigins,
                PlaybackStayedActive = model.IsPlaying,
                QuickPlayMovements = quickPlayMovements,
                RecommendedReads = workspace.RecommendationReads,
                HttpRequests = fixture.Transport.RequestCount,
                CredentialCalls = fixture.Credentials.Calls,
                IconNames = fixedIcons.Select(icon => icon.Label).ToArray(),
                Actions = actions,
                Frames = frames,
                Limits = "Win32 input, real Avalonia frames and transformed vector geometry; no GPU-present timing or device verification."
            };
            await File.WriteAllTextAsync(Path.Combine(output, "rapid-report.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), token);
            if (result.HttpRequests != 0 || result.CredentialCalls != 0 || result.InterruptedSwitches < 3
                || result.FrameSamplesDuringSwitches < 8 || workspace.RecommendationReads != 3 || startingInk.Any(ink => ink is null)
                || !result.PlaybackStayedActive || hoveredOrigins.Count != Modes.Length)
            {
                throw new InvalidOperationException("The rapid run did not exercise interrupted frame delivery on its private populated fixture.");
            }
            if (result.HasResidentViews && (!stablePreparedPages || iconVisibilityChanges > 0 || maxIconDelta > 1 / window.RenderScaling
                || quickPlayMovements.Length != Modes.Length || quickPlayMovements.Any(motion => motion.TrackIds.Length != 1
                    || motion.ArtworkIdentities.Length != 1 || motion.MissingArtworkFrames > 0 || motion.VisibleFramesInMotion < 2
                    || motion.LocalXRange > 1 / window.RenderScaling || motion.LocalYRange > 1 / window.RenderScaling
                    || motion.WindowYRange > 1 / window.RenderScaling)))
            {
                throw new InvalidOperationException("A rapid switch mutated a prepared page or moved stationary icon geometry.");
            }
        }

        private static async Task<double> SelectModeAsync(Window window, SelectingItemsControl selector, MainViewModel model, HomeBrowseMode mode,
            CancellationToken token, Stopwatch? clock = null)
        {
            var segment = selector.ContainerFromIndex((int)mode) ?? throw new InvalidOperationException("The native home segment was not realized.");
            var center = segment.TranslatePoint(new Point(segment.Bounds.Width * .5, segment.Bounds.Height * .5), window)!.Value;
            var hwnd = window.TryGetPlatformHandle()!.Handle;
            var packed = PackPoint(center, window.RenderScaling);
            var accepted = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
            {
                if (change.PropertyName == nameof(MainViewModel.HomeMode) && model.HomeMode == mode)
                {
                    accepted.TrySetResult(clock?.Elapsed.TotalMilliseconds ?? 0);
                }
            }
            model.PropertyChanged += OnChanged;
            try
            {
                SendMessage(hwnd, 0x0200, 0, packed);
                SendMessage(hwnd, 0x0201, 1, packed);
                SendMessage(hwnd, 0x0202, 0, packed);
                return await accepted.Task.WaitAsync(TimeSpan.FromMilliseconds(700), token);
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException($"The native pointer did not select {mode}: selector={selector.SelectedValue}, position={center}.", exception);
            }
            finally
            {
                model.PropertyChanged -= OnChanged;
            }
        }

        private static Task Transition(BrowseTracksView browser) => (Task?)ResidentUpdate?.GetValue(browser)
            ?? browser.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!.TransitionUpdate;

        private static Control? ResidentList(BrowseTracksView browser, HomeBrowseMode mode) =>
            (Control?)GetResidentList?.Invoke(browser, [mode]);

        private static PageSample ReadPage(MainViewModel model, BrowseTracksView browser, HomeBrowseMode mode)
        {
            var state = GetBrowsePage?.Invoke(model, [mode]);
            var cards = state?.GetType().GetProperty("CardTracks")?.GetValue(state) as IReadOnlyList<SongCardViewModel>
                ?? (model.HomeMode == mode ? model.CardTracks : Array.Empty<SongCardViewModel>());
            var prefix = cards.Take(ResidentPrefix).ToArray();
            var controls = (ResidentList(browser, mode) ?? browser).GetVisualDescendants().OfType<SongCard>()
                .Where(card => card.DataContext is SongCardViewModel data && (prefix.Contains(data) || cards.Count == 0))
                .Select(card => new MountedCard(RuntimeHelpers.GetHashCode(card), ((SongCardViewModel)card.DataContext!).Track.Id)).ToArray();
            return new PageSample(mode.ToString(), cards.Count, prefix.Count(card => card.Cover is not null),
                prefix.Select(card => new WarmCard(RuntimeHelpers.GetHashCode(card), card.Track.Id,
                    card.Cover is { } bitmap ? RuntimeHelpers.GetHashCode(bitmap) : null)).ToArray(), controls);
        }

        private static QuickPlayProbe[] CreateQuickPlayProbes(BrowseTracksView browser, HomeBrowseMode activeMode)
        {
            var modes = GetResidentList is null ? [activeMode] : Modes;
            return modes.Select(mode =>
            {
                var list = ResidentList(browser, mode) ?? browser;
                var card = list.GetVisualDescendants().OfType<SongCard>().FirstOrDefault(control => control.DataContext is SongCardViewModel);
                var icon = card?.GetVisualDescendants().OfType<CoverPlayGesture>().FirstOrDefault();
                var artwork = card?.GetVisualDescendants().OfType<Image>().FirstOrDefault();
                return icon is null ? null : new QuickPlayProbe(mode.ToString(), icon, artwork, list.GetVisualParent());
            }).OfType<QuickPlayProbe>().ToArray();
        }

        private static T Named<T>(Visual view, string name) where T : Control =>
            view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

        private static (string Label, Control Glyph)[] FixedIcons(MainView view, bool isPlaying)
        {
            if (view.IsMobileLayout)
            {
                return new[] { "MobilePreviousButton", "MobileNextButton", "MobileSearchButton" }
                    .Select(name => (name, (Control)Named<Control>(view, name).GetVisualDescendants().OfType<ArcGlyph>().Single()))
                    .Append(("MobilePlayPauseButton.Path", Named<Control>(view, "MobilePlayPauseButton").GetVisualDescendants().OfType<ShapePath>().Single(path => path.IsEffectivelyVisible)))
                    .ToArray();
            }
            return new[] { isPlaying ? "TransportPauseIcon" : "TransportPlayIcon", "TransportPreviousIcon", "TransportNextIcon", "TransportShuffleIcon", "TransportRepeatIcon" }
                .Select(name => (name, (Control)Named<ShapePath>(view, name)))
                .Append(("BeginSearchButton.Path", Named<Control>(view, "BeginSearchButton").GetVisualDescendants().OfType<ShapePath>().Single()))
                .ToArray();
        }

        private static InkBounds? Ink(Control icon, Window window)
        {
            var ink = icon switch
            {
                ShapePath shape => (shape.RenderedGeometry ?? shape.Data)?.Bounds,
                ArcGlyph glyph when glyph.Data is not null => ArcGlyphBounds(glyph),
                _ => null
            };
            return icon.IsEffectivelyVisible && ink is { } rectangle && icon.TranslatePoint(rectangle.TopLeft, window) is { } first
                && icon.TranslatePoint(rectangle.BottomRight, window) is { } last
                ? new InkBounds(first.X, first.Y, last.X - first.X, last.Y - first.Y) : null;
        }

        private static Rect ArcGlyphBounds(ArcGlyph glyph)
        {
            var size = glyph.Bounds.Size;
            var scale = Math.Min(size.Width, size.Height) / 24;
            var bounds = glyph.Data!.Bounds;
            return new Rect((size.Width - 24 * scale) / 2 + bounds.X * scale, (size.Height - 24 * scale) / 2 + bounds.Y * scale,
                bounds.Width * scale, bounds.Height * scale);
        }

        private static QuickPlaySample? QuickPlayInk(QuickPlayProbe probe, Window window, Rect clip)
        {
            var icon = probe.Icon;
            if (!icon.IsEffectivelyVisible || icon.Bounds.Width <= 0 || icon.Bounds.Height <= 0 || icon.Progress != 0
                || QuickPlayInitialPose.Invoke(icon, null) is not ValueTuple<Point, double> initial)
            {
                return null;
            }
            var point = new Point(initial.Item1.X + initial.Item2 / 10 * .65, initial.Item1.Y);
            return icon.TranslatePoint(point, window) is { } translated
                ? new QuickPlaySample(probe.Mode, RuntimeHelpers.GetHashCode(icon), (icon.DataContext as SongCardViewModel)?.Track.Id,
                    probe.Artwork?.Source is { } source ? RuntimeHelpers.GetHashCode(source) : null,
                    probe.Page is not { Opacity: 0 } && clip.Contains(translated),
                    point.X, point.Y, translated.X, translated.Y) : null;
        }

        private static double?[] PanelPositions(BrowseTracksView browser) => new[] { "RecommendationBrowsePage", "PlaylistBrowsePage", "LibraryBrowsePage" }
            .Select(name => browser.FindControl<Control>(name) is { Opacity: > 0 } page ? page.TranslatePoint(default, browser)?.X : null).ToArray();

        private static double InkDifference(IReadOnlyList<InkBounds?> expected, IReadOnlyList<InkBounds?> current) =>
            expected.Zip(current, (first, next) => (first, next) switch
            {
                ({ } initial, { } actual) => new[] { Math.Abs(initial.X - actual.X), Math.Abs(initial.Y - actual.Y), Math.Abs(initial.Width - actual.Width), Math.Abs(initial.Height - actual.Height) }.Max(),
                (null, null) => 0,
                _ => 0
            }).Max();

        private static double Range(IEnumerable<double> values) => values.Max() - values.Min();

        private static bool HoverQuickPlay(CoverPlayGesture icon, Window window)
        {
            if (QuickPlayInitialPose.Invoke(icon, null) is not ValueTuple<Point, double> pose
                || icon.TranslatePoint(pose.Item1, window) is not { } point)
            {
                return false;
            }
            SendMessage(window.TryGetPlatformHandle()!.Handle, 0x0200, 0, PackPoint(point, window.RenderScaling));
            return icon.IsPointerOver;
        }

        private static int PackPoint(Point point, double scaling) => (int)Math.Round(point.X * scaling) | ((int)Math.Round(point.Y * scaling) << 16);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

        private sealed record InkBounds(double X, double Y, double Width, double Height);
        private sealed record QuickPlayProbe(string Mode, CoverPlayGesture Icon, Image? Artwork, Visual? Page);
        private sealed record QuickPlaySample(string Mode, int ControlIdentity, string? TrackId, int? ArtworkIdentity, bool InViewport, double LocalX, double LocalY, double WindowX, double WindowY);
        private sealed record WarmCard(int DataIdentity, string Track, int? ArtworkIdentity);
        private sealed record MountedCard(int ControlIdentity, string Track);
        private sealed record PageSample(string Mode, int Available, int ReadyCoverCount, WarmCard[] FirstCards, MountedCard[] MountedPrefix);
        private sealed record RapidFrame(double AtMs, string Mode, double?[] PanelX, InkBounds?[] FixedIcons, QuickPlaySample[] QuickPlay, bool InMotion);
        private sealed record RapidAction(string Target, double RequestedAtMs, double AcceptedAtMs, bool Interrupted, double?[] BeforePanelX, double?[] AfterPanelX,
            PageSample AtAcceptance, InkBounds?[] FixedIcons);

        private sealed class SilentPlaybackBackend : IPlaybackBackend
        {
            public bool IsAvailable => true;
            public PlaybackCapabilities Capabilities => PlaybackCapabilities.Playback | PlaybackCapabilities.Seeking;
            public PlaybackSnapshot Snapshot { get; private set; } = new();
            public event EventHandler? StateChanged;

            public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
                => Publish(new PlaybackSnapshot { State = PlaybackState.Stopped, Duration = TimeSpan.FromMinutes(3) });
            public Task PlayAsync(CancellationToken cancellationToken = default) => Publish(Snapshot with { State = PlaybackState.Playing });
            public Task PauseAsync(CancellationToken cancellationToken = default) => Publish(Snapshot with { State = PlaybackState.Paused });
            public Task StopAsync(CancellationToken cancellationToken = default) => Publish(Snapshot with { State = PlaybackState.Stopped });
            public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Publish(Snapshot with { Position = position });
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private Task Publish(PlaybackSnapshot snapshot)
            {
                Snapshot = snapshot;
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }
        }
    }
}
