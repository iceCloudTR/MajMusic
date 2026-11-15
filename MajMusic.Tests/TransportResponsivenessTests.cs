#nullable enable

using System.Collections.Concurrent;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks cancellable preparation, latest navigation and responsive transport without network or native audio.</summary>
    internal static class TransportResponsivenessTests
    {
        /// <summary>Runs blocked-resolver, navigation rollback, presentation and bounded-seek regressions.</summary>
        /// <returns>A task representing all transport responsiveness cases.</returns>
        public static async Task RunAsync()
        {
            await ValidateLateResolutionAsync();
            await ValidateInterruptedNativeOwnerAsync();
            await ValidateResumeDuringNativeOpenAsync();
            await ValidateNavigationRollbackAsync();
            await ValidatePauseAndStopAsync();
            await ValidateNaturalCompletionAsync();
            await ValidatePreparationShutdownAsync();
            await ValidateViewTransportAsync();
            await ValidateLatestSeekAsync();
        }

        /// <summary>Recovers committed audio when resume or two rapid toggle actions supersede an unfinished native open.</summary>
        /// <returns>A task representing immediate resume-intent planning before native cleanup completes.</returns>
        private static async Task ValidateResumeDuringNativeOpenAsync()
        {
            await using (var fixture = new Fixture())
            {
                await fixture.Service.SetQueueAsync([Track("a"), Track("b")]);
                fixture.Backend.BlockOpenPath = "b.mp3";
                fixture.Backend.IgnoreOpenCancellation = true;
                var next = fixture.Service.NextAsync();
                await fixture.Backend.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var resume = fixture.Service.ResumeAsync();
                TestAssert.True(fixture.Service.PreparingTrack?.Id == "a" && fixture.Service.Snapshot.Track is null,
                    "Direct resume invalidates an interrupted native owner and plans committed audio before waiting for the native gate.");
                fixture.Backend.ReleaseOpen.TrySetResult();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => next);
                await resume.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(fixture.Backend.ActualPath == "a.mp3" && fixture.Service.Snapshot is { State: PlaybackState.Playing, Track.Id: "a" }
                    && fixture.Service.Queue.Current?.Id == "a", "Resume invoked during native open must reload A rather than play or label native B as A.");
                TestAssert.Equal(1, fixture.Backend.StopCount, "Interrupted-open cleanup completes before the resumed resource commits.");
            }

            await using (var fixture = new Fixture())
            {
                await using var view = await CreateViewAsync(fixture, [Track("a"), Track("b")]);
                await view.PlaySelectedAsync();
                fixture.Backend.BlockOpenPath = "b.mp3";
                fixture.Backend.IgnoreOpenCancellation = true;
                view.NextCommand.Execute(null);
                var next = view.TransportUpdate;
                await fixture.Backend.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                view.TogglePlayPauseCommand.Execute(null);
                TestAssert.True(view.TogglePlayPauseCommand.CanExecute(null) && fixture.Service.Snapshot is { State: PlaybackState.Stopped, Track: null },
                    "The first toggle can invalidate native ownership without waiting for the previous decoder call.");
                view.TogglePlayPauseCommand.Execute(null);
                TestAssert.Equal("a", fixture.Service.PreparingTrack?.Id, "A second immediate toggle requests the committed queue entry rather than the interrupted native resource.");
                fixture.Backend.ReleaseOpen.TrySetResult();
                await view.TransportUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                await next;
                TestAssert.True(fixture.Backend.ActualPath == "a.mp3" && fixture.Service.Snapshot is { State: PlaybackState.Playing, Track.Id: "a" }
                    && fixture.Service.Queue.Current?.Id == "a" && fixture.Backend.Played.SequenceEqual(["a.mp3", "a.mp3"]),
                    "Two rapid toggles recover actual A playback while the superseded B open never starts.");
            }
        }

        /// <summary>Prevents a canceled native open or play from retaining the previous track's logical identity.</summary>
        /// <returns>A task representing partially replaced native resources and recovery through resume.</returns>
        private static async Task ValidateInterruptedNativeOwnerAsync()
        {
            await using (var fixture = new Fixture())
            {
                await fixture.Service.SetQueueAsync([Track("a"), Track("b")]);
                fixture.Backend.BlockOpenPath = "b.mp3";
                fixture.Backend.IgnoreOpenCancellation = true;
                var next = fixture.Service.NextAsync();
                await fixture.Backend.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.Equal("b.mp3", fixture.Backend.ActualPath, "The fixture must replace native ownership before its open wait finishes.");
                var pause = fixture.Service.PauseAsync();
                TestAssert.True(fixture.Backend.OpenToken.IsCancellationRequested, "Pause requests cancellation before waiting for an interrupted native open.");
                fixture.Backend.ReleaseOpen.TrySetResult();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => next);
                await pause;
                TestAssert.True(fixture.Service.Snapshot is { State: PlaybackState.Stopped, Track: null }
                    && fixture.Service.Queue.Current?.Id == "a" && fixture.Backend.Snapshot.State == PlaybackState.Stopped,
                    "An interrupted native replacement clears the lost loaded owner while preserving committed navigation.");
                TestAssert.True(fixture.Backend.Played.SequenceEqual(["a.mp3"]), "An open that finishes after cancellation cannot start the superseded resource.");
                await fixture.Service.ResumeAsync();
                TestAssert.True(fixture.Backend.ActualPath == "a.mp3" && fixture.Service.Snapshot is { State: PlaybackState.Playing, Track.Id: "a" }
                    && fixture.Service.Queue.Current?.Id == "a", "Resume reloads committed audio after native ownership was invalidated instead of playing B under A's identity.");
            }

            await using (var fixture = new Fixture())
            {
                await fixture.Service.SetQueueAsync([Track("a"), Track("b")]);
                fixture.Backend.BlockPlayPath = "b.mp3";
                fixture.Backend.IgnorePlayCancellation = true;
                var next = fixture.Service.NextAsync();
                await fixture.Backend.PlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var replacement = fixture.Service.PlayAsync(Track("c"));
                fixture.Backend.ReleasePlay.TrySetResult();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => next);
                await replacement;
                TestAssert.True(fixture.Backend.ActualPath == "c.mp3" && fixture.Backend.Snapshot.State == PlaybackState.Playing
                    && fixture.Service.Snapshot is { State: PlaybackState.Playing, Track.Id: "c" } && fixture.Service.Queue.Current?.Id == "c",
                    "Cleanup of an interrupted native play finishes inside the native gate before the newer resource commits.");
                TestAssert.Equal(1, fixture.Backend.StopCount, "Old native cleanup cannot stop the resource that committed after it.");
            }
        }

        /// <summary>Prevents a resolver that ignores cancellation from opening media or clearing a newer preparation.</summary>
        /// <returns>A task representing obsolete completion isolation.</returns>
        private static async Task ValidateLateResolutionAsync()
        {
            await using var fixture = new Fixture();
            await fixture.Service.SetQueueAsync([Track("a"), Track("b")]);
            var obsolete = fixture.Resolver.Block("b", ignoreCancellation: true);
            var next = fixture.Service.NextAsync();
            await obsolete.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var latest = fixture.Resolver.Block("d");
            var replacement = fixture.Service.PlayAsync(Track("d"));
            await latest.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(obsolete.Token.IsCancellationRequested && !next.IsCompleted, "A newer intent cancels old preparation without waiting for an uncooperative resolver.");
            TestAssert.True(fixture.Service.PreparingTrack?.Id == "d" && fixture.Service.Snapshot.Track?.Id == "a",
                "The pending resource is published separately from the loaded media owner.");
            obsolete.Result.TrySetResult("b.mp3");
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => next);
            TestAssert.True(fixture.Service.PreparingTrack?.Id == "d" && fixture.Backend.Opened.SequenceEqual(["a.mp3"]),
                "Old resolver cleanup cannot open its resource or clear a newer preparation's identity.");
            latest.Result.TrySetResult("d.mp3");
            await replacement;
            TestAssert.True(fixture.Service.Snapshot.Track?.Id == "d" && fixture.Service.Queue.Current?.Id == "d"
                && fixture.Service.PreparingTrack is null, "Only the latest prepared resource can own playback and its queue.");
            TestAssert.True(fixture.Backend.Opened.SequenceEqual(["a.mp3", "d.mp3"]), "An obsolete completed download never reaches the decoder.");

            var failedOld = fixture.Resolver.Block("obsolete-error", ignoreCancellation: true);
            var failed = fixture.Service.PlayAsync(Track("obsolete-error"));
            await failedOld.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Service.PlayAsync(Track("current"));
            failedOld.Result.TrySetException(new IOException("Generated obsolete download failure."));
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => failed);
            TestAssert.True(fixture.Service.Snapshot is { State: PlaybackState.Playing, Track.Id: "current" }
                && fixture.Backend.StopCount == 0, "An obsolete request's failure cannot stop or fail a newer successful resource.");
        }

        /// <summary>Advances from the pending intent and rolls current failures back to successfully loaded navigation.</summary>
        /// <returns>A task representing rapid next, previous and failure semantics.</returns>
        private static async Task ValidateNavigationRollbackAsync()
        {
            await using var fixture = new Fixture();
            await fixture.Service.SetQueueAsync([Track("a"), Track("b"), Track("c")]);
            var pendingB = fixture.Resolver.Block("b");
            var firstNext = fixture.Service.NextAsync();
            await pendingB.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var pendingC = fixture.Resolver.Block("c");
            var secondNext = fixture.Service.NextAsync();
            await pendingC.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => firstNext);
            TestAssert.True(fixture.Service.Queue.Current?.Id == "c" && fixture.Service.PreparingTrack?.Id == "c",
                "A second next action advances beyond the already pending entry instead of retrying it.");
            pendingC.Result.TrySetException(new IOException("Generated current download failure."));
            await TestAssert.ThrowsAsync<IOException>(() => secondNext);
            TestAssert.True(fixture.Service.Queue.Current?.Id == "a" && fixture.Service.Snapshot.Track?.Id == "a"
                && fixture.Service.Snapshot.State == PlaybackState.Failed && fixture.Service.PreparingTrack is null,
                "A failed current intent restores the last committed navigation without publishing a skipped entry.");
            pendingB.Result.TrySetResult("b.mp3");
            await fixture.Service.NextAsync();
            TestAssert.Equal("b", fixture.Service.Snapshot.Track?.Id, "Navigation after rollback starts from the last successful entry.");
            await fixture.Service.PreviousAsync();
            TestAssert.Equal("a", fixture.Service.Snapshot.Track?.Id, "Previous follows the restored queue history.");
            pendingB = fixture.Resolver.Block("b");
            firstNext = fixture.Service.NextAsync();
            await pendingB.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Service.PreviousAsync();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => firstNext);
            TestAssert.True(fixture.Service.Queue.Current?.Id == "a" && fixture.Service.Snapshot.Track?.Id == "a",
                "Previous can replace a pending next intent without waiting for its download.");

            await fixture.Service.SetQueueAsync([Track("a"), Track("last")]);
            var pendingLast = fixture.Resolver.Block("last");
            var last = fixture.Service.NextAsync();
            await pendingLast.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Service.NextAsync();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => last);
            TestAssert.True(fixture.Service.Snapshot is { State: PlaybackState.Stopped, Track.Id: "a" } && fixture.Service.Queue.Current?.Id == "a",
                "Advancing beyond a pending final entry stops without committing a resource that was never loaded.");
        }

        /// <summary>Interrupts resource preparation with pause and stop while retaining the actually loaded queue entry.</summary>
        /// <returns>A task representing control responsiveness without releasing blocked downloads.</returns>
        private static async Task ValidatePauseAndStopAsync()
        {
            await using var fixture = new Fixture();
            await fixture.Service.SetQueueAsync([Track("a"), Track("b")]);
            var pending = fixture.Resolver.Block("b");
            var next = fixture.Service.NextAsync();
            await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Service.PauseAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => next);
            TestAssert.True(pending.Token.IsCancellationRequested && fixture.Service.PreparingTrack is null
                && fixture.Service.Snapshot is { State: PlaybackState.Paused, Track.Id: "a" } && fixture.Service.Queue.Current?.Id == "a",
                "Pause cancels download preparation and reaches the already loaded resource immediately.");
            await fixture.Service.ResumeAsync();
            pending = fixture.Resolver.Block("b");
            next = fixture.Service.NextAsync();
            await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => next);
            TestAssert.True(fixture.Service.Snapshot is { State: PlaybackState.Stopped, Track.Id: "a" }
                && fixture.Service.Queue.Current?.Id == "a", "Stop cancels preparation without leaving the pending queue selection committed.");

            await using var empty = new Fixture();
            var first = empty.Resolver.Block("first");
            var starting = empty.Service.PlayAsync(Track("first"));
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await empty.Service.PauseAsync();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => starting);
            TestAssert.True(empty.Service.Snapshot is { State: PlaybackState.Stopped, Track: null } && empty.Backend.PauseCount == 0,
                "Canceling the initial preparation does not attempt to pause a nonexistent native resource.");
        }

        /// <summary>Shares preparation generations between automatic completion and manual navigation.</summary>
        /// <returns>A task representing natural-completion supersession.</returns>
        private static async Task ValidateNaturalCompletionAsync()
        {
            await using var fixture = new Fixture();
            await fixture.Service.SetQueueAsync([Track("a"), Track("b"), Track("c")]);
            var automatic = fixture.Resolver.Block("b");
            fixture.Backend.Complete();
            await automatic.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Service.NextAsync();
            await automatic.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(fixture.Service.Snapshot is { State: PlaybackState.Playing, Track.Id: "c" }
                && fixture.Service.Queue.Current?.Id == "c", "A manual next can supersede automatic preparation without a late automatic failure overriding it.");
            TestAssert.True(fixture.Backend.Opened.SequenceEqual(["a.mp3", "c.mp3"]), "Canceled automatic preparation never opens its skipped resource.");
        }

        /// <summary>Keeps transport buttons and local pages usable while a playback resource waits for resolution.</summary>
        /// <returns>A task representing reentrant presentation commands and pause behavior.</returns>
        private static async Task ValidateViewTransportAsync()
        {
            await using var fixture = new Fixture();
            var tracks = new[] { Track("a"), Track("b"), Track("c") };
            var workspace = new LibraryExperienceTests.ExperienceWorkspace();
            await using var view = await CreateViewAsync(fixture, tracks, workspace);
            var first = fixture.Resolver.Block("a");
            var pending = view.PlaySelectedAsync();
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(!view.IsBusy && view.IsIdle && view.NextCommand.CanExecute(null) && view.PreviousCommand.CanExecute(null)
                && view.PauseCommand.CanExecute(null) && view.TogglePlayPauseCommand.CanExecute(null)
                && view.SearchCommand.CanExecute(null) && view.CreatePlaylistCommand.CanExecute(null),
                "Preparing playback cannot occupy the global library busy state or disable the player controls.");
            var second = fixture.Resolver.Block("b");
            view.NextCommand.Execute(null);
            await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(view.NextCommand.CanExecute(null), "The same next button can replace its still-pending previous request.");
            view.NextCommand.Execute(null);
            await view.TransportUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            await pending;
            TestAssert.True(fixture.Service.Snapshot.Track?.Id == "c" && fixture.Backend.Opened.SequenceEqual(["c.mp3"]),
                "Repeated next commands reach the latest requested song without opening stale downloads.");
            first = fixture.Resolver.Block("a");
            pending = view.PlaySelectedAsync();
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            view.TogglePlayPauseCommand.Execute(null);
            await view.TransportUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            await pending;
            TestAssert.True(fixture.Service.Snapshot is { State: PlaybackState.Paused, Track.Id: "c" } && !view.IsBusy,
                "The shared transport button cancels pending preparation and pauses the loaded song.");
            view.TogglePlayPauseCommand.Execute(null);
            await view.TransportUpdate;
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.SearchOverride = (_, token) =>
            {
                reading.TrySetResult();
                return release.Task.WaitAsync(token);
            };
            view.SearchCommand.Execute(null);
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                TestAssert.True(view.IsBusy && view.PauseCommand.CanExecute(null), "A separate busy library operation cannot disable transport for loaded audio.");
                view.PauseCommand.Execute(null);
                await view.TransportUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.Equal(PlaybackState.Paused, fixture.Service.Snapshot.State, "Pause reaches the audio owner while local-library work is still pending.");
            }
            finally
            {
                release.TrySetResult(tracks);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (view.IsBusy)
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        /// <summary>Drains canceled preparation before releasing its playback owner.</summary>
        /// <returns>A task representing service-lifetime cancellation and pending-identity cleanup.</returns>
        private static async Task ValidatePreparationShutdownAsync()
        {
            await using var fixture = new Fixture();
            await fixture.Service.SetQueueAsync([Track("a"), Track("b")]);
            var pending = fixture.Resolver.Block("b");
            var next = fixture.Service.NextAsync();
            await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => next);
            TestAssert.True(pending.Token.IsCancellationRequested && fixture.Service.PreparingTrack is null
                && fixture.Backend.Opened.SequenceEqual(["a.mp3"]), "Shutdown cancels and drains preparation before any late resource can open.");
        }

        /// <summary>Retains only the latest committed seek and cancels cooperative seeking before another control runs.</summary>
        /// <returns>A task representing bounded native seeking without resource resolution.</returns>
        private static async Task ValidateLatestSeekAsync()
        {
            await using var fixture = new Fixture();
            await using var view = await CreateViewAsync(fixture, [Track("a"), Track("b")]);
            await view.PlaySelectedAsync();
            fixture.Backend.BlockSeek = true;
            fixture.Backend.IgnoreSeekCancellation = true;
            TestAssert.True(view.BeginSeek(), "Prepared local playback must allow a timeline gesture.");
            view.PositionSeconds = 10;
            var first = view.CommitSeekAsync();
            await fixture.Backend.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var commits = new List<Task> { first };
            for (var position = 20; position <= 90; position += 10)
            {
                TestAssert.True(view.BeginSeek(), "Another gesture remains available while a native seek is in progress.");
                view.PositionSeconds = position;
                commits.Add(view.CommitSeekAsync());
            }

            TestAssert.True(fixture.Backend.Seeks.Count == 1 && fixture.Resolver.Calls.Count == 1 && !view.IsBusy,
                "Repeated gestures cannot queue native operations or resolve audio again while a seek is blocked.");
            fixture.Backend.ReleaseSeek.TrySetResult();
            await Task.WhenAll(commits).WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(fixture.Backend.Seeks.SequenceEqual([10d, 90d]) && fixture.Service.Snapshot.Position.TotalSeconds == 90,
                "A single seek worker applies only the initial in-flight position and the latest retained position.");
            TestAssert.True(fixture.Backend.Opened.Count == 1 && fixture.Resolver.Calls.Count == 1, "Seeking never reopens or downloads the loaded audio.");

            fixture.Backend.ResetSeekBlock();
            TestAssert.True(view.BeginSeek(), "A new seek can begin after the preceding worker drains.");
            view.PositionSeconds = 40;
            var blocked = view.CommitSeekAsync();
            await fixture.Backend.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            view.PauseCommand.Execute(null);
            await view.TransportUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            await blocked;
            TestAssert.Equal(PlaybackState.Paused, fixture.Service.Snapshot.State, "Pause cancels a cooperative active seek before waiting for native access.");
        }

        /// <summary>Creates an initialized local browsing view around the controlled playback service.</summary>
        /// <param name="fixture">The resolver and backend controls.</param>
        /// <param name="tracks">The locally indexed browsing entries.</param>
        /// <param name="workspace">An optional controlled library used to overlap nontransport operations.</param>
        /// <returns>The initialized view with its first library entry selected.</returns>
        private static async Task<MainViewModel> CreateViewAsync(Fixture fixture, MusicTrack[] tracks, LibraryExperienceTests.ExperienceWorkspace? workspace = null)
        {
            workspace ??= new LibraryExperienceTests.ExperienceWorkspace();
            workspace.Library.AddRange(tracks);
            var view = new MainViewModel(workspace, fixture.Service, static action => action());
            await view.InitializeAsync();
            view.ShowAllCommand.Execute(null);
            view.SelectedTrack = view.Tracks[0];
            return view;
        }

        /// <summary>Creates synthetic metadata without reading source files.</summary>
        /// <param name="id">The stable track identity.</param>
        /// <returns>The locally resolvable fixture entry.</returns>
        private static MusicTrack Track(string id)
        {
            return new MusicTrack { Id = id, Title = id, Audio = new MediaResource { LocalPath = id + ".mp3" } };
        }

        /// <summary>Owns controlled preparation and audio state for one test without creating native resources.</summary>
        private sealed class Fixture : IAsyncDisposable
        {
            /// <summary>Creates the real playback owner around controlled synthetic resources.</summary>
            public Fixture()
            {
                Service = new PlaybackService(Backend, resolveAudioPath: Resolver.ResolveAsync);
            }

            /// <summary>Gets the controlled resource resolver.</summary>
            public ControlledResolver Resolver { get; } = new();
            /// <summary>Gets the synthetic backend.</summary>
            public ControlledBackend Backend { get; } = new();
            /// <summary>Gets the real playback coordinator.</summary>
            public PlaybackService Service { get; }

            /// <inheritdoc />
            public async ValueTask DisposeAsync()
            {
                var disposal = Service.DisposeAsync().AsTask();
                Resolver.ReleaseAll();
                Backend.ReleaseSeek.TrySetResult();
                Backend.ReleaseOpen.TrySetResult();
                Backend.ReleasePlay.TrySetResult();
                await disposal;
            }
        }

        /// <summary>Controls resource completion and can emulate a resolver that ignores its cancellation token.</summary>
        private sealed class ControlledResolver
        {
            /// <summary>Retains one optional controlled resolution per library identity.</summary>
            private readonly ConcurrentDictionary<string, Resolution> _blocked = new(StringComparer.Ordinal);
            /// <summary>Gets every requested track identity without issuing HTTP requests.</summary>
            public ConcurrentQueue<string> Calls { get; } = new();

            /// <summary>Arms one controlled resource resolution.</summary>
            /// <param name="id">The resource identity to block.</param>
            /// <param name="ignoreCancellation">Whether the fixture deliberately ignores cancellation while waiting.</param>
            /// <returns>The completion controls for that resource.</returns>
            public Resolution Block(string id, bool ignoreCancellation = false)
            {
                var resolution = new Resolution(ignoreCancellation);
                _blocked[id] = resolution;
                return resolution;
            }

            /// <summary>Resolves an immediate synthetic path or waits for controlled completion.</summary>
            /// <param name="track">The requested library entry.</param>
            /// <param name="token">The preparation lifetime supplied by the service.</param>
            /// <returns>The generated resource path.</returns>
            public async Task<string> ResolveAsync(MusicTrack track, CancellationToken token)
            {
                Calls.Enqueue(track.Id);
                if (!_blocked.TryGetValue(track.Id, out var resolution))
                {
                    token.ThrowIfCancellationRequested();
                    return track.Audio.LocalPath!;
                }

                resolution.Token = token;
                resolution.Started.TrySetResult();
                try
                {
                    return resolution.IgnoreCancellation ? await resolution.Result.Task : await resolution.Result.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    resolution.Canceled.TrySetResult();
                    throw;
                }
            }

            /// <summary>Releases every outstanding synthetic operation before fixture disposal.</summary>
            public void ReleaseAll()
            {
                foreach (var pair in _blocked)
                {
                    pair.Value.Result.TrySetResult(pair.Key + ".mp3");
                }
            }
        }

        /// <summary>Retains the explicit completion boundary for one generated resource.</summary>
        private sealed class Resolution
        {
            /// <summary>Creates a controlled resolution boundary.</summary>
            /// <param name="ignoreCancellation">Whether waiting deliberately disregards the service token.</param>
            public Resolution(bool ignoreCancellation)
            {
                IgnoreCancellation = ignoreCancellation;
            }

            /// <summary>Gets whether the synthetic wait ignores cancellation.</summary>
            public bool IgnoreCancellation { get; }
            /// <summary>Gets or sets the cancellation token observed by the resolver.</summary>
            public CancellationToken Token { get; set; }
            /// <summary>Gets the signal that resolution began.</summary>
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets the signal that cooperative resolution observed cancellation.</summary>
            public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets the generated path or failure supplied by the test.</summary>
            public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Records native-command intent without decoding audio or accessing any media file.</summary>
        private sealed class ControlledBackend : IPlaybackBackend
        {
            /// <summary>Publishes immutable synthetic clock state.</summary>
            private PlaybackSnapshot _snapshot = new();
            /// <summary>Retains the resource actually selected by the synthetic native decoder.</summary>
            private string? _actualPath;
            /// <summary>Gets the generated paths explicitly opened by committed transitions.</summary>
            public ConcurrentQueue<string> Opened { get; } = new();
            /// <summary>Gets resource paths actually started by the synthetic native player.</summary>
            public ConcurrentQueue<string> Played { get; } = new();
            /// <summary>Gets the resource actually held by the synthetic native decoder.</summary>
            public string? ActualPath => Volatile.Read(ref _actualPath);
            /// <summary>Gets or sets the path whose native open pauses after replacing the resource.</summary>
            public string? BlockOpenPath { get; set; }
            /// <summary>Gets or sets whether a controlled open deliberately ignores cancellation.</summary>
            public bool IgnoreOpenCancellation { get; set; }
            /// <summary>Gets the cancellation token supplied to a controlled native open.</summary>
            public CancellationToken OpenToken { get; private set; }
            /// <summary>Gets the signal that a controlled native open already replaced its resource.</summary>
            public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the controlled native open.</summary>
            public TaskCompletionSource ReleaseOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets or sets the path whose native play pauses before reporting success.</summary>
            public string? BlockPlayPath { get; set; }
            /// <summary>Gets or sets whether a controlled play deliberately ignores cancellation.</summary>
            public bool IgnorePlayCancellation { get; set; }
            /// <summary>Gets the signal that a controlled native play began.</summary>
            public TaskCompletionSource PlayStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the controlled native play.</summary>
            public TaskCompletionSource ReleasePlay { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets positions that reached the synthetic native seek.</summary>
            public ConcurrentQueue<double> Seeks { get; } = new();
            /// <summary>Gets the number of pause operations.</summary>
            public int PauseCount { get; private set; }
            /// <summary>Gets the number of stop operations.</summary>
            public int StopCount { get; private set; }
            /// <summary>Gets or sets whether the first pending seek waits for explicit release.</summary>
            public bool BlockSeek { get; set; }
            /// <summary>Gets or sets whether a blocked synthetic native seek ignores cancellation.</summary>
            public bool IgnoreSeekCancellation { get; set; }
            /// <summary>Gets the signal that a blocked native seek started.</summary>
            public TaskCompletionSource SeekStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets the release boundary of a blocked native seek.</summary>
            public TaskCompletionSource ReleaseSeek { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <inheritdoc />
            public bool IsAvailable => true;
            /// <inheritdoc />
            public PlaybackCapabilities Capabilities => PlaybackCapabilities.Playback | PlaybackCapabilities.Seeking;
            /// <inheritdoc />
            public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);
            /// <inheritdoc />
            public event EventHandler? StateChanged;

            /// <inheritdoc />
            public async Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Opened.Enqueue(localPath);
                Volatile.Write(ref _actualPath, localPath);
                Publish(new PlaybackSnapshot { State = PlaybackState.Stopped, Duration = TimeSpan.FromMinutes(2) });
                if (localPath == BlockOpenPath)
                {
                    OpenToken = cancellationToken;
                    OpenStarted.TrySetResult();
                    if (IgnoreOpenCancellation)
                    {
                        await ReleaseOpen.Task;
                    }
                    else
                    {
                        await ReleaseOpen.Task.WaitAsync(cancellationToken);
                    }
                }

                if (!IgnoreOpenCancellation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            /// <inheritdoc />
            public async Task PlayAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ActualPath == BlockPlayPath && BlockPlayPath is not null)
                {
                    PlayStarted.TrySetResult();
                    if (IgnorePlayCancellation)
                    {
                        await ReleasePlay.Task;
                    }
                    else
                    {
                        await ReleasePlay.Task.WaitAsync(cancellationToken);
                    }
                }

                if (!IgnorePlayCancellation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Played.Enqueue(ActualPath!);
                Publish(Snapshot with { State = PlaybackState.Playing });
            }

            /// <inheritdoc />
            public Task PauseAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PauseCount++;
                Publish(Snapshot with { State = PlaybackState.Paused });
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StopCount++;
                Publish(Snapshot with { State = PlaybackState.Stopped, Position = TimeSpan.Zero });
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
            {
                Seeks.Enqueue(position.TotalSeconds);
                if (BlockSeek && !ReleaseSeek.Task.IsCompleted)
                {
                    SeekStarted.TrySetResult();
                    if (IgnoreSeekCancellation)
                    {
                        await ReleaseSeek.Task;
                    }
                    else
                    {
                        await ReleaseSeek.Task.WaitAsync(cancellationToken);
                    }
                }

                if (!IgnoreSeekCancellation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Publish(Snapshot with { Position = position });
            }

            /// <summary>Arms a cooperative seek wait after a preceding synthetic native operation has drained.</summary>
            public void ResetSeekBlock()
            {
                BlockSeek = true;
                IgnoreSeekCancellation = false;
                SeekStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ReleaseSeek = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            /// <summary>Emits a terminal backend notification for automatic-navigation validation.</summary>
            public void Complete()
            {
                Publish(Snapshot with { State = PlaybackState.Completed });
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            /// <summary>Publishes synthetic backend state to the real playback coordinator.</summary>
            /// <param name="snapshot">The new immutable clock and decoder state.</param>
            private void Publish(PlaybackSnapshot snapshot)
            {
                Volatile.Write(ref _snapshot, snapshot);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
