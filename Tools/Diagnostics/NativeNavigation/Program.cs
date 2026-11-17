#nullable enable

using System.Diagnostics;
using System.Collections.Specialized;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;
using TKCode.Arc.Motion;

namespace MajMusic.Diagnostics.NativeNavigation
{
    /// <summary>Starts an isolated real Win32 application without opening the production executable or its data.</summary>
    internal static class Program
    {
        /// <summary>Gets the uniquely owned generated-data and measurement directory.</summary>
        public static string OutputDirectory { get; private set; } = string.Empty;

        /// <summary>Gets whether to exercise interrupted home transitions instead of isolated navigation.</summary>
        public static bool IsRapidRun { get; private set; }
        /// <summary>Gets whether to launch the compact layout in its own native window.</summary>
        public static bool IsCompactRapidRun { get; private set; }

        /// <summary>Runs only the diagnostic's own desktop lifetime.</summary>
        /// <param name="args">An optional absolute artifact destination.</param>
        /// <returns>Zero after successful measurement, or one after a recorded diagnostic failure.</returns>
        [STAThread]
        public static int Main(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("This diagnostic requires actual Windows windowing.");
            }
            IsCompactRapidRun = args.Contains("--rapid-mobile", StringComparer.Ordinal);
            IsRapidRun = IsCompactRapidRun || args.Contains("--rapid", StringComparer.Ordinal);
            OutputDirectory = Path.GetFullPath(args.FirstOrDefault(argument => argument is not ("--rapid" or "--rapid-mobile")) ?? Path.Combine(Environment.CurrentDirectory, "artifacts",
                "native-navigation-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]));
            Directory.CreateDirectory(OutputDirectory);
            return AppBuilder.Configure<DiagnosticApplication>().UsePlatformDetect().WithInterFont()
                .UseMajMusicArc(ArcMotionLevel.Full).StartWithClassicDesktopLifetime([]);
        }
    }

    /// <summary>Owns a normal Win32/Skia window and automatically closes only that diagnostic window.</summary>
    public sealed class DiagnosticApplication : Application
    {
        /// <inheritdoc />
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
        }

        /// <inheritdoc />
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new Window
                {
                    Title = "MajMusic · synthetic navigation diagnostics",
                    Width = Program.IsCompactRapidRun ? 420 : 1377,
                    Height = Program.IsCompactRapidRun ? 820 : 900,
                    Content = new TextBlock { Text = "Preparing generated music…", Margin = new Thickness(24) }
                };
                desktop.MainWindow = window;
                Dispatcher.UIThread.Post(async () =>
                {
                    var exit = 0;
                    try
                    {
                        if (Program.IsRapidRun)
                        {
                            await RapidNavigationRunner.RunAsync(window, Program.OutputDirectory);
                        }
                        else
                        {
                            await NavigationRunner.RunAsync(window, Program.OutputDirectory);
                        }
                    }
                    catch (Exception exception)
                    {
                        exit = 1;
                        await File.WriteAllTextAsync(Path.Combine(Program.OutputDirectory, "failure.txt"), exception.ToString());
                    }
                    finally
                    {
                        desktop.Shutdown(exit);
                    }
                }, DispatcherPriority.Background);
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    /// <summary>Measures real dispatcher scheduling separately from capture and explicit first-layout work.</summary>
    internal static class NavigationRunner
    {
        /// <summary>Runs finite cold and warm navigation phases on generated data without screenshots in measured navigation.</summary>
        /// <param name="window">The diagnostic's own visible native window.</param>
        /// <param name="output">The uniquely owned artifact directory.</param>
        /// <returns>A task representing all measurement and report output.</returns>
        public static async Task RunAsync(Window window, string output)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var token = timeout.Token;
            using var fixture = await SyntheticLibrary.CreateAsync(Path.Combine(output, "generated-data"), token);
            await using var model = new MainViewModel(fixture.Workspace, new PlaybackService(new UnavailablePlaybackBackend()));
            await model.InitializeAsync().WaitAsync(token);
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate.WaitAsync(token);
            var timer = Stopwatch.StartNew();
            var view = new MainView { DataContext = model };
            var constructorMs = timer.Elapsed.TotalMilliseconds;
            var pageLoads = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in view.FindControl<TabControl>("MainTabs")!.Items.OfType<TabItem>())
            {
                if (item.Content is Control page)
                {
                    var label = item.Header?.ToString() ?? "unnamed";
                    pageLoads[label] = 0;
                    page.Loaded += (_, _) => pageLoads[label]++;
                }
            }
            var probe = new SceneRecordProbe { Child = view };
            timer.Restart();
            window.Content = probe;
            window.UpdateLayout();
            var attachLayoutMs = timer.Elapsed.TotalMilliseconds;
            await Task.Delay(300, token);
            var beforeNavigation = new
            {
                CompletedPages = view.DesktopPageWarmupCount,
                WorkMs = view.DesktopPageWarmupElapsed.TotalMilliseconds,
                Failures = view.DesktopPageWarmupFailures,
                PageLoadedEvents = new Dictionary<string, int>(pageLoads, StringComparer.Ordinal)
            };
            var placeholdersBeforeNavigation = CapturePlaceholderState(window);
            var pageHost = view.GetVisualDescendants().OfType<MusicSurfaceHost>().Single(host => host.Name == "PageSurfaceTransition");
            using var observer = new UiObserver(window, probe);
            var samples = new List<NavigationMeasurement>();
            var captures = new List<CaptureMeasurement>();
            var names = new[] { "home", "playlists", "directories", "accounts", "settings" };
            foreach (var pass in new[] { "first-use-full", "warm-full", "warm-off" })
            {
                ArcMotion.SetLevel(window, pass == "warm-off" ? ArcMotionLevel.Off : ArcMotionLevel.Full);
                await Task.Delay(180, token);
                foreach (var target in new[] { 4, 0, 2, 0 })
                {
                    samples.Add(await MeasureAsync(observer, window, pageHost, pass + ":" + names[target],
                        () => view.NavigateToSection(target), () => view.SelectedSectionIndex == target, token));
                    if (pass == "warm-full")
                    {
                        captures.Add(MeasureCapture(pageHost, names[target]));
                        await Task.Delay(80, token);
                    }
                }
            }
            ArcMotion.SetLevel(window, ArcMotionLevel.Full);
            await Task.Delay(180, token);
            var browse = view.GetVisualDescendants().OfType<BrowseTracksView>().Single();
            var browseHost = browse.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition")!;
            foreach (var mode in new[] { "playlists", "library" })
            {
                Action action = mode switch
                {
                    "playlists" => () => model.ShowPlaylistsModeCommand.Execute(null),
                    "recommendations" => () => model.ShowRecommendationsModeCommand.Execute(null),
                    _ => () => model.ShowLibraryModeCommand.Execute(null)
                };
                samples.Add(await MeasureAsync(observer, window, browseHost, "home-mode:" + mode, action,
                    () => mode == "playlists" ? model.IsPlaylistsMode : mode == "library" ? model.IsLibraryMode : model.IsRecommendationsMode, token));
                await model.HomeUpdate.WaitAsync(token);
            }
            await model.BrowsePreloadUpdate.WaitAsync(token);
            foreach (var mode in new[] { "playlists", "library" })
            {
                Action action = mode == "playlists" ? () => model.ShowPlaylistsModeCommand.Execute(null) : () => model.ShowLibraryModeCommand.Execute(null);
                samples.Add(await MeasureAsync(observer, window, browseHost, "home-mode-warm:" + mode, action,
                    () => mode == "playlists" ? model.IsPlaylistsMode : model.IsLibraryMode, token));
                await model.HomeUpdate.WaitAsync(token);
                await model.BrowsePreloadUpdate.WaitAsync(token);
            }
            observer.Stop();
            var report = new
            {
                Platform = RuntimeInformation.OSDescription,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                NativeWindowHandleType = window.TryGetPlatformHandle()?.HandleDescriptor,
                window.RenderScaling,
                ClientWidth = window.ClientSize.Width,
                ClientHeight = window.ClientSize.Height,
                ConstructorMs = constructorMs,
                AttachAndExplicitFirstLayoutMs = attachLayoutMs,
                IdlePageWarmupBeforeNavigation = beforeNavigation,
                observer.StartedUtc,
                observer.ProcessStartUtc,
                ObserverStartFromProcessMs = (observer.StartedUtc - observer.ProcessStartUtc).TotalMilliseconds,
                PlaceholdersBeforeNavigation = placeholdersBeforeNavigation,
                PlaceholdersAfterNavigation = CapturePlaceholderState(window),
                TrackCount = fixture.TrackCount,
                MaterializedCards = model.MaterializedCardCount,
                HttpRequests = fixture.Transport.RequestCount,
                CredentialCalls = fixture.Credentials.Calls,
                Navigation = samples,
                StandaloneProductionCapture = captures,
                Observer = observer.Summary(),
                Limits = new[]
                {
                    "Real Win32 window and normal platform render loop; no headless platform or manual frame clock.",
                    "Frame callbacks and scene recording do not measure GPU present or display scan-out.",
                    "Explicit first layout is measured separately and forced immediately after the programmatic action.",
                    "Production CaptureSurface is invoked separately through reflection; its time includes bitmap allocation, synchronous render and reflection overhead.",
                    "Full/Off differences also include motion setup; this is not an isolated capture-only subtraction.",
                    "All configured accounts and resources are synthetic; no source login, audio playback or user file is accessed."
                }
            };
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
            await File.WriteAllLinesAsync(Path.Combine(output, "timeline.csv"), observer.Csv(), token);
            var summary = new StringBuilder("phase,invoke_ms,layout_ms,feedback_ms,scene_ms,frame_ms,max_frame_gap_ms,max_heartbeat_gap_ms,total_ms\r\n");
            foreach (var sample in samples)
            {
                summary.AppendLine(string.Join(',', sample.Name, Format(sample.InvokeMs), Format(sample.ExplicitLayoutMs), Format(sample.FeedbackMs),
                    Format(sample.FirstSceneRecordMs), Format(sample.FirstFrameCallbackMs), Format(sample.MaxFrameGapMs), Format(sample.MaxHeartbeatGapMs), Format(sample.TotalMs)));
            }
            await File.WriteAllTextAsync(Path.Combine(output, "summary.csv"), summary.ToString(), token);
            if (fixture.Transport.RequestCount != 0 || fixture.Credentials.Calls != 0)
            {
                throw new InvalidOperationException("Synthetic diagnostics attempted network or credential access.");
            }
        }

        /// <summary>Measures synchronous navigation, forced layout and subsequent native scheduling without saving images.</summary>
        /// <param name="observer">The running UI scheduling observer.</param>
        /// <param name="window">The owned native window.</param>
        /// <param name="host">The production transitioning surface.</param>
        /// <param name="name">The finite scenario label.</param>
        /// <param name="action">The production navigation command.</param>
        /// <param name="feedback">The first observable selected-state confirmation.</param>
        /// <param name="token">Cancels a missing phase completion.</param>
        /// <returns>The measured phase and render-cycle timing.</returns>
        private static async Task<NavigationMeasurement> MeasureAsync(UiObserver observer, Window window, MusicSurfaceHost host, string name,
            Action action, Func<bool> feedback, CancellationToken token)
        {
            var placeholdersBefore = CapturePlaceholderState(window);
            var originalCards = window.GetVisualDescendants().OfType<SongCard>().ToArray();
            var originalSet = new HashSet<SongCard>(originalCards, ReferenceEqualityComparer.Instance);
            var cardsBefore = CaptureCardInstances(originalCards, window);
            using var rowChanges = new RowChangeObserver((host.DataContext as MainViewModel)?.BrowseRows);
            var phase = observer.Begin(name);
            phase.PlaceholdersBefore = placeholdersBefore;
            phase.CardInstancesBefore = cardsBefore;
            var hosts = window.GetVisualDescendants().OfType<MusicSurfaceHost>().ToDictionary(item => item, item => item.CaptureCount);
            var timer = Stopwatch.StartNew();
            action();
            phase.InvokeMs = timer.Elapsed.TotalMilliseconds;
            phase.RowsDuringAction = rowChanges.Snapshot();
            phase.CaptureDuringAction = CaptureChanges(window, hosts);
            if (feedback())
            {
                phase.FeedbackMs = observer.ElapsedMs - phase.StartMs;
            }
            timer.Restart();
            window.UpdateLayout();
            phase.ExplicitLayoutMs = timer.Elapsed.TotalMilliseconds;
            phase.RowsAfterLayout = rowChanges.Snapshot();
            var afterLayoutCards = window.GetVisualDescendants().OfType<SongCard>().ToArray();
            phase.CardInstancesAfterLayout = CaptureCardInstances(afterLayoutCards, window);
            phase.SnapshotWidth = host.SnapshotPixelSize.Width;
            phase.SnapshotHeight = host.SnapshotPixelSize.Height;
            await Task.Delay(440, token);
            await host.TransitionUpdate.WaitAsync(token);
            phase.CaptureDuringPhase = CaptureChanges(window, hosts);
            observer.End(phase);
            phase.RowsDuringPhase = rowChanges.Snapshot();
            phase.PlaceholdersAfter = CapturePlaceholderState(window);
            var finalCards = window.GetVisualDescendants().OfType<SongCard>().ToArray();
            phase.CardInstancesAtPhaseEnd = CaptureCardInstances(finalCards, window);
            var observed = new HashSet<SongCard>(afterLayoutCards, ReferenceEqualityComparer.Instance);
            observed.UnionWith(finalCards);
            phase.NewCardInstances = observed.Count(card => !originalSet.Contains(card));
            phase.ReusedCardInstances = observed.Count(originalSet.Contains);
            return phase;
        }

        /// <summary>Copies current real card identities at a few phase boundaries without retaining another visual tree.</summary>
        /// <param name="cards">The actually realized controls, not their backing view models.</param>
        /// <param name="window">The diagnostic's own native viewport.</param>
        /// <returns>Immutable identities, source songs and painted-viewport intersection observations.</returns>
        private static CardInstanceMeasurement[] CaptureCardInstances(IEnumerable<SongCard> cards, Window window)
        {
            return cards.Select(card =>
            {
                var origin = card.TranslatePoint(default, window);
                var rectangle = origin is { } point ? new Rect(point, card.Bounds.Size) : default;
                var visible = rectangle.Intersect(new Rect(window.ClientSize));
                if (card.FindAncestorOfType<ListBox>() is { } list && list.TranslatePoint(default, window) is { } listOrigin)
                {
                    visible = visible.Intersect(new Rect(listOrigin, list.Bounds.Size));
                }
                foreach (var ancestor in card.GetVisualAncestors().OfType<Control>().Where(control => control.ClipToBounds))
                {
                    if (ancestor.TranslatePoint(default, window) is { } clipOrigin)
                    {
                        visible = visible.Intersect(new Rect(clipOrigin, ancestor.Bounds.Size));
                    }
                }
                return new CardInstanceMeasurement(RuntimeHelpers.GetHashCode(card), (card.DataContext as SongCardViewModel)?.Track.Id,
                    card.IsEffectivelyVisible && visible.Width > 1 && visible.Height > 1, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            }).ToArray();
        }

        /// <summary>Reads actual placeholder pool and idle-preparation counters outside timed navigation work.</summary>
        /// <param name="window">The owned visual root.</param>
        /// <returns>Eager snapshots of currently realized browser controls and their counters.</returns>
        private static object[] CapturePlaceholderState(Window window)
        {
            return window.GetVisualDescendants().OfType<BrowseTracksView>().Select(browser => (object)new
            {
                browser.PlaceholderWarmupFailures,
                Skeletons = new[] { browser.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface") }.OfType<BrowseSkeletonView>().Select(skeleton => new
                {
                    skeleton.CreatedPlaceholderCount,
                    skeleton.ReusedPlaceholderCount,
                    skeleton.PooledPlaceholderCount,
                    skeleton.PrewarmCount,
                    TotalPrewarmMs = skeleton.TotalPrewarmElapsed.TotalMilliseconds,
                    LastSetViewportMs = skeleton.LastSetViewportElapsed.TotalMilliseconds
                }).ToArray()
            }).ToArray();
        }

        /// <summary>Reads production capture counters without performing additional rasterization during a navigation.</summary>
        /// <param name="window">The owned visual root.</param>
        /// <param name="before">The outgoing hosts and their counts before the request.</param>
        /// <returns>Every changed host's actual count and most recent capture duration.</returns>
        private static HostCaptureMeasurement[] CaptureChanges(Window window, IReadOnlyDictionary<MusicSurfaceHost, long> before)
        {
            return before.Keys.Union(window.GetVisualDescendants().OfType<MusicSurfaceHost>())
                .Select(host => new HostCaptureMeasurement(host.Name ?? "unnamed", host.CaptureCount - before.GetValueOrDefault(host), host.LastCaptureDuration.TotalMilliseconds))
                .Where(item => item.Count > 0).ToArray();
        }

        /// <summary>Times the exact current production capture method independently from navigation and file encoders.</summary>
        /// <param name="host">The settled real surface.</param>
        /// <param name="page">The page whose current tree is rasterized.</param>
        /// <returns>Three standalone capture durations and actual pixel dimensions.</returns>
        private static CaptureMeasurement MeasureCapture(MusicSurfaceHost host, string page)
        {
            var method = typeof(MusicSurfaceHost).GetMethod("CaptureSurface", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("Production CaptureSurface no longer exists; update the diagnostic.");
            var durations = new List<double>();
            var pixels = default(PixelSize);
            for (var index = 0; index < 3; index++)
            {
                var timer = Stopwatch.StartNew();
                var result = method.Invoke(host, null);
                var elapsed = timer.Elapsed.TotalMilliseconds;
                if (result is not ValueTuple<RenderTargetBitmap, Size> capture)
                {
                    throw new InvalidOperationException("A visible settled surface did not produce a diagnostic capture.");
                }
                pixels = capture.Item1.PixelSize;
                capture.Item1.Dispose();
                durations.Add(elapsed);
            }
            return new CaptureMeasurement(page, durations.ToArray(), pixels.Width, pixels.Height);
        }

        /// <summary>Formats CSV numbers independently of the system locale.</summary>
        /// <param name="value">The measured milliseconds, or null before an observed callback.</param>
        /// <returns>The invariant decimal representation.</returns>
        private static string Format(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Stores a finite programmatic navigation phase without collecting user content.</summary>
    internal sealed class NavigationMeasurement
    {
        /// <summary>Gets the generated scenario label.</summary>
        public required string Name { get; init; }
        /// <summary>Gets the observer-relative starting timestamp.</summary>
        public double StartMs { get; init; }
        /// <summary>Gets or sets synchronous command duration.</summary>
        public double InvokeMs { get; set; }
        /// <summary>Gets or sets forced first layout duration.</summary>
        public double ExplicitLayoutMs { get; set; }
        /// <summary>Gets or sets the first observed selected-state delay.</summary>
        public double? FeedbackMs { get; set; }
        /// <summary>Gets or sets the first actual control scene-recording delay.</summary>
        public double? FirstSceneRecordMs { get; set; }
        /// <summary>Gets or sets the first normal UI render-cycle callback delay.</summary>
        public double? FirstFrameCallbackMs { get; set; }
        /// <summary>Gets or sets the maximum UI-frame callback interval overlapping this phase.</summary>
        public double MaxFrameGapMs { get; set; }
        /// <summary>Gets or sets the maximum heartbeat interval overlapping this phase.</summary>
        public double MaxHeartbeatGapMs { get; set; }
        /// <summary>Gets or sets the complete observation period including the transition.</summary>
        public double TotalMs { get; set; }
        /// <summary>Gets or sets the production outgoing bitmap width immediately after layout.</summary>
        public int SnapshotWidth { get; set; }
        /// <summary>Gets or sets the production outgoing bitmap height immediately after layout.</summary>
        public int SnapshotHeight { get; set; }
        /// <summary>Gets or sets actual production captures performed before the navigation call returned.</summary>
        public HostCaptureMeasurement[] CaptureDuringAction { get; set; } = [];
        /// <summary>Gets or sets actual production captures performed during the complete observed transition.</summary>
        public HostCaptureMeasurement[] CaptureDuringPhase { get; set; } = [];
        /// <summary>Gets or sets placeholder counters read before the timed phase begins.</summary>
        public object[] PlaceholdersBefore { get; set; } = [];
        /// <summary>Gets or sets placeholder counters read after the timed phase completes.</summary>
        public object[] PlaceholdersAfter { get; set; } = [];
        /// <summary>Gets or sets realized control identities before the requested action.</summary>
        public CardInstanceMeasurement[] CardInstancesBefore { get; set; } = [];
        /// <summary>Gets or sets realized control identities after the initial layout stopwatch has stopped.</summary>
        public CardInstanceMeasurement[] CardInstancesAfterLayout { get; set; } = [];
        /// <summary>Gets or sets realized control identities after the complete transition observation.</summary>
        public CardInstanceMeasurement[] CardInstancesAtPhaseEnd { get; set; } = [];
        /// <summary>Gets or sets controls observed after the action which were absent from its initial reference set.</summary>
        public int NewCardInstances { get; set; }
        /// <summary>Gets or sets initially existing control references retained after the action.</summary>
        public int ReusedCardInstances { get; set; }
        /// <summary>Gets or sets collection notification totals when the synchronous navigation call returns.</summary>
        public RowCollectionMeasurement? RowsDuringAction { get; set; }
        /// <summary>Gets or sets collection notification totals after the separately timed first layout.</summary>
        public RowCollectionMeasurement? RowsAfterLayout { get; set; }
        /// <summary>Gets or sets all row collection notifications observed during this navigation phase.</summary>
        public RowCollectionMeasurement? RowsDuringPhase { get; set; }
        /// <summary>Gets the number of initially realized cards intersecting their actual visible viewport.</summary>
        public int VisibleCardsBefore => CardInstancesBefore.Count(card => card.VisibleInViewport);
        /// <summary>Gets the number of realized cards intersecting their viewport after the first layout.</summary>
        public int VisibleCardsAfterLayout => CardInstancesAfterLayout.Count(card => card.VisibleInViewport);
        /// <summary>Gets the number of visible realized cards at the end of the transition.</summary>
        public int VisibleCardsAtPhaseEnd => CardInstancesAtPhaseEnd.Count(card => card.VisibleInViewport);
    }

    /// <summary>Counts real row collection notifications without traversing or mutating the view.</summary>
    internal sealed class RowChangeObserver : IDisposable
    {
        /// <summary>Retains the current presentation's collection only for this finite phase.</summary>
        private readonly INotifyCollectionChanged? _rows;
        /// <summary>Counts complete collection resets.</summary>
        private int _resets;
        /// <summary>Counts tail or other add notifications.</summary>
        private int _adds;
        /// <summary>Counts removal notifications.</summary>
        private int _removes;
        /// <summary>Counts replacement notifications.</summary>
        private int _replaces;
        /// <summary>Counts move notifications.</summary>
        private int _moves;

        /// <summary>Subscribes before the action without changing the collection or timing policy.</summary>
        /// <param name="rows">The production owner collection, if this surface has a music owner.</param>
        public RowChangeObserver(INotifyCollectionChanged? rows)
        {
            _rows = rows;
            if (_rows is not null)
            {
                _rows.CollectionChanged += OnRowsChanged;
            }
        }

        /// <summary>Copies the notification counters outside the action and layout stopwatches.</summary>
        /// <returns>The observed notification count for each collection operation.</returns>
        public RowCollectionMeasurement Snapshot() => new(_resets, _adds, _removes, _replaces, _moves);

        /// <summary>Ends the phase's event subscription even when a diagnostic fails.</summary>
        public void Dispose()
        {
            if (_rows is not null)
            {
                _rows.CollectionChanged -= OnRowsChanged;
            }
        }

        /// <summary>Records one notification without accessing or materializing any song.</summary>
        /// <param name="sender">The production row collection.</param>
        /// <param name="change">The collection operation which can cause native container work.</param>
        private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs change)
        {
            switch (change.Action)
            {
                case NotifyCollectionChangedAction.Reset:
                    _resets++;
                    break;
                case NotifyCollectionChangedAction.Add:
                    _adds++;
                    break;
                case NotifyCollectionChangedAction.Remove:
                    _removes++;
                    break;
                case NotifyCollectionChangedAction.Replace:
                    _replaces++;
                    break;
                case NotifyCollectionChangedAction.Move:
                    _moves++;
                    break;
            }
        }
    }

    /// <summary>Describes notifications which can reset or extend the actual virtualized row containers.</summary>
    /// <param name="Resets">The number of whole-collection reset notifications.</param>
    /// <param name="Adds">The number of range or single-item add notifications.</param>
    /// <param name="Removes">The number of range or single-item removal notifications.</param>
    /// <param name="Replaces">The number of item replacement notifications.</param>
    /// <param name="Moves">The number of existing-item move notifications.</param>
    internal sealed record RowCollectionMeasurement(int Resets, int Adds, int Removes, int Replaces, int Moves);

    /// <summary>Describes one real SongCard control without serializing its visual tree or retaining a view-model graph.</summary>
    /// <param name="IdentityHash">The reference identity hash for human comparison; set comparisons use reference equality.</param>
    /// <param name="TrackId">The generated song currently displayed by this control.</param>
    /// <param name="VisibleInViewport">Whether the control intersects its native window and containing list viewport.</param>
    /// <param name="X">The translated native-window horizontal coordinate.</param>
    /// <param name="Y">The translated native-window vertical coordinate.</param>
    /// <param name="Width">The actually arranged card width.</param>
    /// <param name="Height">The actually arranged card height.</param>
    internal sealed record CardInstanceMeasurement(int IdentityHash, string? TrackId, bool VisibleInViewport, double X, double Y, double Width, double Height);

    /// <summary>Records a production host's real capture counter and last synchronous allocation-plus-render duration.</summary>
    /// <param name="Host">The stable production surface name.</param>
    /// <param name="Count">The number of captures since the navigation began.</param>
    /// <param name="LastDurationMs">The final capture duration; not a sum when Count exceeds one.</param>
    internal sealed record HostCaptureMeasurement(string Host, long Count, double LastDurationMs);

    /// <summary>Stores standalone production capture measurements.</summary>
    /// <param name="Page">The generated source-page label.</param>
    /// <param name="DurationMs">The three synchronous capture durations.</param>
    /// <param name="PixelWidth">The actual allocated raster width.</param>
    /// <param name="PixelHeight">The actual allocated raster height.</param>
    internal sealed record CaptureMeasurement(string Page, double[] DurationMs, int PixelWidth, int PixelHeight);

    /// <summary>Observes normal Win32 UI scheduling and scene recording without replacing its frame driver.</summary>
    internal sealed class UiObserver : IDisposable
    {
        /// <summary>Measures all callbacks from a single monotonic origin.</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        /// <summary>Stores finite callback timestamps and gaps.</summary>
        private readonly List<(string Kind, double AtMs, double GapMs)> _events = new();
        /// <summary>Owns the low-frequency dispatcher heartbeat.</summary>
        private readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromMilliseconds(8) };
        /// <summary>Retains only the diagnostic's own native top level.</summary>
        private readonly Window _window;
        /// <summary>Invalidates one scene-recording observation per requested phase.</summary>
        private readonly SceneRecordProbe _probe;
        /// <summary>Tracks the currently observed finite phase.</summary>
        private NavigationMeasurement? _phase;
        /// <summary>Retains the last native UI render-cycle timestamp.</summary>
        private double _lastFrame;
        /// <summary>Retains the last heartbeat callback timestamp.</summary>
        private double _lastBeat;
        /// <summary>Stops rescheduling callbacks after the diagnostic completes.</summary>
        private bool _running = true;
        /// <summary>Gets a wall-clock anchor for matching observer-relative navigation times to an EventPipe session.</summary>
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        /// <summary>Gets only this diagnostic process's creation time for trace alignment.</summary>
        public DateTimeOffset ProcessStartUtc { get; } = new(Process.GetCurrentProcess().StartTime.ToUniversalTime());

        /// <summary>Starts actual dispatcher and top-level frame observations.</summary>
        /// <param name="window">The owned native window.</param>
        /// <param name="probe">The layout-preserving scene-recording wrapper.</param>
        public UiObserver(Window window, SceneRecordProbe probe)
        {
            _window = window;
            _probe = probe;
            probe.Recorded = OnSceneRecorded;
            _heartbeat.Tick += OnHeartbeat;
            _heartbeat.Start();
            window.RequestAnimationFrame(OnFrame);
        }
        /// <summary>Gets elapsed monotonic milliseconds without accessing another process.</summary>
        public double ElapsedMs => _clock.Elapsed.TotalMilliseconds;
        /// <summary>Begins one phase and schedules a real scene-recording observation.</summary>
        /// <param name="name">The generated scenario label.</param>
        /// <returns>The mutable observation owned by this UI thread.</returns>
        public NavigationMeasurement Begin(string name)
        {
            _phase = new NavigationMeasurement { Name = name, StartMs = ElapsedMs };
            _probe.InvalidateVisual();
            return _phase;
        }
        /// <summary>Completes a phase and computes all callback intervals overlapping its observed window.</summary>
        /// <param name="phase">The phase which has just completed.</param>
        public void End(NavigationMeasurement phase)
        {
            var now = ElapsedMs;
            phase.TotalMs = now - phase.StartMs;
            phase.MaxFrameGapMs = MaximumGap("frame", phase.StartMs, now);
            phase.MaxHeartbeatGapMs = MaximumGap("heartbeat", phase.StartMs, now);
            _phase = null;
        }
        /// <summary>Stops only this observer without changing system timing or other windows.</summary>
        public void Stop()
        {
            _running = false;
            _heartbeat.Stop();
            _probe.Recorded = null;
        }
        /// <summary>Returns a compact summary of the complete finite observer session.</summary>
        /// <returns>The actual callback counts and maximum intervals.</returns>
        public object Summary() => new { Frames = _events.Count(item => item.Kind == "frame"), Heartbeats = _events.Count(item => item.Kind == "heartbeat"),
            MaxFrameGapMs = MaximumGap("frame", 0, ElapsedMs), MaxHeartbeatGapMs = MaximumGap("heartbeat", 0, ElapsedMs) };
        /// <summary>Exports only monotonic scheduling data to an ignored diagnostic artifact.</summary>
        /// <returns>The invariant CSV header and finite callback samples.</returns>
        public IEnumerable<string> Csv()
        {
            yield return "kind,at_ms,gap_ms";
            foreach (var item in _events)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"{item.Kind},{item.AtMs:F3},{item.GapMs:F3}");
            }
        }
        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
            _heartbeat.Tick -= OnHeartbeat;
        }
        /// <summary>Records a normal platform animation callback and requests its next real frame.</summary>
        /// <param name="elapsed">The platform's animation timestamp, retained independently from the monotonic measurement origin.</param>
        private void OnFrame(TimeSpan elapsed)
        {
            if (!_running)
            {
                return;
            }
            var now = ElapsedMs;
            if (_lastFrame > 0)
            {
                _events.Add(("frame", now, now - _lastFrame));
            }
            _lastFrame = now;
            if (_phase is { FirstFrameCallbackMs: null } phase)
            {
                phase.FirstFrameCallbackMs = now - phase.StartMs;
            }
            _window.RequestAnimationFrame(OnFrame);
        }
        /// <summary>Records an ordinary dispatcher timer callback without a busy loop or high-resolution timer changes.</summary>
        /// <param name="sender">The owned heartbeat timer.</param>
        /// <param name="args">The callback notification.</param>
        private void OnHeartbeat(object? sender, EventArgs args)
        {
            var now = ElapsedMs;
            if (_lastBeat > 0)
            {
                _events.Add(("heartbeat", now, now - _lastBeat));
            }
            _lastBeat = now;
        }
        /// <summary>Records the first scene graph draw after a navigation request.</summary>
        private void OnSceneRecorded()
        {
            if (_phase is { FirstSceneRecordMs: null } phase)
            {
                phase.FirstSceneRecordMs = ElapsedMs - phase.StartMs;
            }
        }
        /// <summary>Finds the largest measured scheduling gap which overlaps one observation window.</summary>
        /// <param name="kind">The callback family.</param>
        /// <param name="start">The observed interval start.</param>
        /// <param name="end">The observed interval end.</param>
        /// <returns>The largest measured gap, or zero without two callbacks.</returns>
        private double MaximumGap(string kind, double start, double end) => _events.Where(item => item.Kind == kind && item.AtMs >= start && item.AtMs - item.GapMs <= end)
            .Select(item => item.GapMs).DefaultIfEmpty().Max();
    }

    /// <summary>Adds a layout-neutral scene-recording observation around the actual MainView.</summary>
    internal sealed class SceneRecordProbe : Decorator
    {
        /// <summary>Gets or sets the current first-record callback without retaining old navigation targets.</summary>
        public Action? Recorded { get; set; }
        /// <inheritdoc />
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            Recorded?.Invoke();
        }
    }
}
