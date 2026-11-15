#nullable enable

using MajMusic.Core.Models;
using MajMusic.Core.Playback;

namespace MajMusic.Tests
{
    /// <summary>Validates navigation semantics and backend ownership without platform audio devices.</summary>
    internal static class PlaybackTests
    {
        /// <summary>Runs deterministic queue, cancellation and lifecycle regression cases.</summary>
        /// <returns>A task representing the completed regression cases.</returns>
        public static async Task RunAsync()
        {
            ValidateQueueBoundaries();
            ValidateStableShuffleAndHistory();
            await ValidateUnavailableBackendAsync();
            await ValidateFailureRollbackAndClockAsync();
            await ValidateCancellationRollbackAsync();
            await ValidateCanceledOpenAsync();
            await ValidateAutomaticCompletionAsync();
            await ValidateFailureSurvivesProgressAsync();
            await ValidateResolverFailureStopsAudioAsync();
            await ValidateNativeCallbackCompletionAsync();
            await ValidateCompletionFailureRecoveryAsync();
            await ValidateCompletionDisposalAsync();
            await ValidateTrackScopedSeekAsync();
            await ValidateRetainedAudioDetailsAsync();
            await ValidateReplacementAudioDetailsAsync();
        }

        /// <summary>Checks that explicit and queue-boundary stops retain details of the successfully loaded resource.</summary>
        /// <returns>A task representing retained metadata checks.</returns>
        private static async Task ValidateRetainedAudioDetailsAsync()
        {
            var info = new AudioStreamInfo { CodecName = "PCM", SampleRateHz = 44100, Channels = 2, BitrateBitsPerSecond = 1_411_200 };
            var backend = new FakePlaybackBackend { PreparedAudioInfo = info };
            await using var service = new PlaybackService(backend);
            await service.PlayAsync(Track("details"));
            await service.StopAsync();
            TestAssert.True(ReferenceEquals(info, service.Snapshot.AudioInfo), "Explicit stop must retain the loaded audio details.");
            TestAssert.Equal(TimeSpan.FromMinutes(3), service.Snapshot.Duration, "Explicit stop must retain the known duration.");
            TestAssert.Equal("details", service.Snapshot.Track?.Id, "Retained metadata belongs to the successfully loaded entry.");
            await service.ResumeAsync();
            var stopped = ObserveStateAsync(service, snapshot => snapshot.State == PlaybackState.Stopped);
            backend.Complete();
            await stopped;
            TestAssert.True(ReferenceEquals(info, service.Snapshot.AudioInfo), "Natural queue completion must retain the final audio details.");
            TestAssert.Equal(TimeSpan.FromMinutes(3), service.Snapshot.Duration, "Natural queue completion must retain the final duration.");
        }

        /// <summary>Checks that a parsed replacement which fails to start cannot lend its metadata to the previous track.</summary>
        /// <returns>A task representing metadata ownership across loading and failure.</returns>
        private static async Task ValidateReplacementAudioDetailsAsync()
        {
            var previousInfo = new AudioStreamInfo { CodecName = "Previous codec", SampleRateHz = 44100 };
            var replacementInfo = new AudioStreamInfo { CodecName = "Replacement codec", SampleRateHz = 48000 };
            var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new FakePlaybackBackend { PreparedAudioInfo = previousInfo };
            await using var service = new PlaybackService(backend, resolveAudioPath: async (track, token) =>
            {
                if (track.Id == "replacement")
                {
                    resolving.TrySetResult();
                    await release.Task.WaitAsync(token);
                }

                return track.Audio.LocalPath!;
            });
            await service.SetQueueAsync(new[] { Track("previous"), Track("replacement") });
            backend.PreparedAudioInfo = replacementInfo;
            backend.PreparedDuration = TimeSpan.FromMinutes(6);
            backend.FailPlay = true;
            var replacing = service.NextAsync();
            await resolving.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(PlaybackState.Loading, service.Snapshot.State, "Preparation must expose loading state.");
            TestAssert.Equal("previous", service.Snapshot.Track?.Id, "Loading retains the last successfully loaded entry.");
            TestAssert.True(ReferenceEquals(previousInfo, service.Snapshot.AudioInfo), "Loading cannot publish replacement details before it starts.");
            release.TrySetResult();
            await TestAssert.ThrowsAsync<IOException>(() => replacing);
            TestAssert.Equal(PlaybackState.Failed, service.Snapshot.State, "A failed replacement remains observable.");
            TestAssert.True(ReferenceEquals(replacementInfo, backend.Snapshot.AudioInfo), "The fixture must really have parsed the replacement metadata.");
            TestAssert.True(ReferenceEquals(previousInfo, service.Snapshot.AudioInfo), "Failure cannot mislabel replacement metadata as the previous track.");
            TestAssert.Equal(TimeSpan.FromMinutes(3), service.Snapshot.Duration, "Failed replacement duration belongs to the prior successful entry.");
            TestAssert.Equal("previous", service.Snapshot.Track?.Id, "Failure preserves the last successful track identity.");
            backend.FailPlay = false;
            backend.PreparedAudioInfo = previousInfo;
            backend.PreparedDuration = TimeSpan.FromMinutes(3);
            await service.PlayAsync(Track("previous"));
            TestAssert.True(ReferenceEquals(previousInfo, service.Snapshot.AudioInfo), "A subsequent successful load publishes its own details.");
            await service.SetQueueAsync(new[] { Track("previous"), Track("replacement") });
            backend.PreparedAudioInfo = replacementInfo;
            backend.PreparedDuration = TimeSpan.FromMinutes(6);
            backend.FailPlay = true;
            var automaticallyFailed = ObserveStateAsync(service, snapshot => snapshot.State == PlaybackState.Failed);
            backend.Complete();
            await automaticallyFailed;
            TestAssert.True(ReferenceEquals(previousInfo, service.Snapshot.AudioInfo), "Automatic-next failure must preserve the previous successful audio details.");
            TestAssert.Equal(TimeSpan.FromMinutes(3), service.Snapshot.Duration, "Automatic failure handling must not erase the retained duration.");
        }

        /// <summary>Checks empty queues, original-entry isolation and repeat semantics.</summary>
        private static void ValidateQueueBoundaries()
        {
            var queue = new PlaybackQueue(new Random(42));
            TestAssert.True(queue.MoveNext() is null && queue.MovePrevious() is null, "Empty navigation must terminate.");
            var tracks = new List<MusicTrack> { Track("a"), Track("b"), Track("c") };
            queue.SetTracks(tracks);
            tracks.Clear();
            TestAssert.Equal(3, queue.Tracks.Count, "The caller must not mutate queue storage.");
            queue.RepeatMode = RepeatMode.One;
            TestAssert.Equal("a", queue.MoveNext(true)?.Id, "Automatic single repeat must keep its entry.");
            TestAssert.Equal("b", queue.MoveNext()?.Id, "Manual next must escape single repeat.");
            TestAssert.Equal("c", queue.MoveNext()?.Id, "Manual next must advance.");
            TestAssert.True(queue.MoveNext() is null, "A nonwrapping queue must end.");
            TestAssert.Equal("b", queue.MovePrevious()?.Id, "Previous must follow visited entries.");
            TestAssert.Equal("c", queue.MoveNext()?.Id, "Next after previous must replay forward history.");
            queue.RepeatMode = RepeatMode.All;
            TestAssert.Equal("a", queue.MoveNext()?.Id, "All repeat must wrap the queue.");
            queue.SetTracks(new[] { Track("only") });
            queue.Shuffle = true;
            for (var index = 0; index < 5; index++)
            {
                TestAssert.Equal("only", queue.MoveNext(true)?.Id, "A single shuffled entry must never spin looking for a different entry.");
            }

            queue.SetTracks(Array.Empty<MusicTrack>());
            TestAssert.True(queue.Current is null, "Replacing with an empty queue must clear the selection.");
        }

        /// <summary>Checks that one shuffle cycle is a permutation and previous follows actual playback.</summary>
        private static void ValidateStableShuffleAndHistory()
        {
            var queue = new PlaybackQueue(new Random(13)) { Shuffle = true };
            queue.SetTracks(Enumerable.Range(0, 24).Select(index => Track(index.ToString())), 8);
            var visited = new List<string> { queue.Current!.Id };
            MusicTrack? selected;
            while ((selected = queue.MoveNext()) is not null)
            {
                visited.Add(selected.Id);
            }

            TestAssert.Equal(24, visited.Count, "A shuffle cycle must visit every queue slot once.");
            TestAssert.Equal(24, visited.Distinct().Count(), "A shuffle cycle must not repeat entries.");
            for (var index = visited.Count - 2; index >= 0; index--)
            {
                TestAssert.Equal(visited[index], queue.MovePrevious()?.Id, "Previous must retrace the actual shuffled order.");
            }

            TestAssert.True(queue.MovePrevious() is null, "Nonrepeating history must stop at its beginning.");
            queue.Shuffle = false;
            TestAssert.Equal("8", queue.Current?.Id, "Changing shuffle must preserve the selected entry.");
            TestAssert.Equal("9", queue.MoveNext()?.Id, "Disabling shuffle must resume original playlist order.");
        }

        /// <summary>Checks that an unconnected platform backend does not pretend to play audio.</summary>
        /// <returns>A task representing the completed case.</returns>
        private static async Task ValidateUnavailableBackendAsync()
        {
            await using var service = new PlaybackService(new UnavailablePlaybackBackend());
            TestAssert.True(!service.IsAvailable, "An unconnected backend must report unavailable.");
            await TestAssert.ThrowsAsync<NotSupportedException>(() => service.PlayAsync(Track("local")));
            TestAssert.True(service.Queue.Current is null, "Unavailable playback must not mutate the queue.");
            TestAssert.Equal(PlaybackState.Empty, service.Snapshot.State, "Unavailable playback must not simulate a playing state.");
            await using var explained = new UnavailablePlaybackBackend("Install the desktop audio engine.");
            TestAssert.Equal("Install the desktop audio engine.", explained.Snapshot.Error, "Unavailable adapters must retain actionable startup details.");
        }

        /// <summary>Checks that stopped or clock notifications cannot hide an observed command failure.</summary>
        /// <returns>A task representing persistent failure and clock recovery checks.</returns>
        private static async Task ValidateFailureSurvivesProgressAsync()
        {
            var backend = new FakePlaybackBackend { FailingPath = "bad.mp3" };
            await using var service = new PlaybackService(backend);
            await service.SetQueueAsync(new[] { Track("good"), Track("bad") });
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => service.NextAsync());
            var failed = ObserveStateAsync(service, snapshot => snapshot.State == PlaybackState.Failed);
            backend.SetPosition(TimeSpan.FromSeconds(9));
            await failed;
            TestAssert.Equal(PlaybackState.Failed, service.Snapshot.State, "A backend clock update cannot erase a failed command.");
            TestAssert.True(service.Snapshot.Error is not null, "The failure remains observable until a successful command.");
            await service.StopAsync();
            TestAssert.Equal(PlaybackState.Stopped, service.Snapshot.State, "Explicit stop clears the failed command state.");
            await service.PlayAsync(Track("good"));
            var progress = ObserveStateAsync(service, snapshot => snapshot.Position == TimeSpan.FromSeconds(23));
            backend.SetPosition(TimeSpan.FromSeconds(23));
            await progress;
            TestAssert.Equal(TimeSpan.FromSeconds(23), service.Snapshot.Position, "Recovered playback continues exposing the backend clock.");
            TestAssert.Equal(TimeSpan.FromMinutes(3), service.Snapshot.Duration, "The backend duration survives progress updates.");
        }

        /// <summary>Checks that a failed local path lookup cannot leave audible playback behind a failed UI state.</summary>
        /// <returns>A task representing resource-resolution failure consistency.</returns>
        private static async Task ValidateResolverFailureStopsAudioAsync()
        {
            var backend = new FakePlaybackBackend();
            await using var service = new PlaybackService(backend, resolveAudioPath: (track, _) =>
            {
                if (track.Id == "missing")
                {
                    throw new InvalidOperationException("Synthetic missing local file.");
                }

                return Task.FromResult(track.Audio.LocalPath!);
            });
            await service.SetQueueAsync(new[] { Track("active"), Track("missing") });
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => service.NextAsync());
            TestAssert.Equal(1, backend.StopCount, "A failed local lookup must stop the previous audible resource.");
            TestAssert.Equal(PlaybackState.Stopped, backend.Snapshot.State, "A failed music transition must not leave audio advancing.");
            TestAssert.Equal(PlaybackState.Failed, service.Snapshot.State, "The failed lookup remains visible to the UI.");
            TestAssert.Equal("active", service.Queue.Current?.Id, "A failed lookup restores the prior queue selection.");
        }

        /// <summary>Checks that duplicate native completion callbacks never reenter audio APIs or skip an entry.</summary>
        /// <returns>A task representing native-callback isolation checks.</returns>
        private static async Task ValidateNativeCallbackCompletionAsync()
        {
            var backend = new FakePlaybackBackend { RejectCallbackReentry = true };
            await using var service = new PlaybackService(backend);
            await service.SetQueueAsync(new[] { Track("first"), Track("second"), Track("third") });
            var advanced = ObserveStateAsync(service, snapshot => snapshot.State == PlaybackState.Playing && snapshot.Track?.Id == "second");
            backend.Complete(nativeCallback: true, notificationCount: 3);
            await advanced;
            await service.SetShuffleAsync(false);
            TestAssert.Equal(0, backend.CallbackReentryCount, "Audio commands must not execute on the native callback stack.");
            TestAssert.Equal(2, backend.OpenCount, "Duplicate end notifications must advance exactly once.");
            TestAssert.Equal("second", service.Queue.Current?.Id, "Duplicate notifications cannot skip the next music entry.");
        }

        /// <summary>Checks that a failed automatic resource resolution neither loops nor disables later notifications.</summary>
        /// <returns>A task representing automatic failure recovery.</returns>
        private static async Task ValidateCompletionFailureRecoveryAsync()
        {
            var backend = new FakePlaybackBackend();
            var rejectedResolutions = 0;
            await using var service = new PlaybackService(backend, resolveAudioPath: (track, _) =>
            {
                if (track.Id == "missing")
                {
                    Interlocked.Increment(ref rejectedResolutions);
                    throw new InvalidOperationException("Synthetic missing file.");
                }

                return Task.FromResult(track.Audio.LocalPath!);
            });
            await service.SetQueueAsync(new[] { Track("first"), Track("missing") });
            var failed = ObserveStateAsync(service, snapshot => snapshot.State == PlaybackState.Failed);
            backend.Complete(nativeCallback: true, notificationCount: 3);
            await failed;
            await service.SetShuffleAsync(false);
            TestAssert.Equal(1, rejectedResolutions, "A failed end transition cannot spin retrying the same resource.");
            await service.SetQueueAsync(new[] { Track("recovered"), Track("last") });
            var advanced = ObserveStateAsync(service, snapshot => snapshot.State == PlaybackState.Playing && snapshot.Track?.Id == "last");
            backend.Complete();
            await advanced;
        }

        /// <summary>Checks that disposing the owner cancels and drains an automatic media preparation.</summary>
        /// <returns>A task representing shutdown during automatic navigation.</returns>
        private static async Task ValidateCompletionDisposalAsync()
        {
            var backend = new FakePlaybackBackend { BlockingPath = "blocked.mp3" };
            var service = new PlaybackService(backend);
            await service.SetQueueAsync(new[] { Track("first"), Track("blocked") });
            backend.Complete(nativeCallback: true);
            await backend.BlockingOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(1, backend.DisposalCount, "Shutdown must await completion handling before disposing its audio owner.");
            backend.Complete(nativeCallback: true);
            TestAssert.Equal(1, backend.OpenCount, "Late completion notifications cannot restart a disposed owner.");
        }

        /// <summary>Checks that a seek queued behind a track switch cannot move the replacement track.</summary>
        /// <returns>A task representing track-scoped seek validation.</returns>
        private static async Task ValidateTrackScopedSeekAsync()
        {
            var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new FakePlaybackBackend();
            await using var service = new PlaybackService(backend, resolveAudioPath: async (track, token) =>
            {
                if (track.Id == "second")
                {
                    resolving.TrySetResult();
                    await release.Task.WaitAsync(token);
                }

                return track.Audio.LocalPath!;
            });
            await service.SetQueueAsync(new[] { Track("first"), Track("second") });
            var transition = service.NextAsync();
            await resolving.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var staleSeek = service.SeekAsync(TimeSpan.FromSeconds(17), "first");
            release.TrySetResult();
            await Task.WhenAll(transition, staleSeek);
            TestAssert.Equal(0, backend.SeekCount, "An obsolete seek must not reach the replacement media.");
            await service.SeekAsync(TimeSpan.FromSeconds(19), "second");
            TestAssert.Equal(1, backend.SeekCount, "A seek matching the loaded entry remains available.");
            TestAssert.Equal(TimeSpan.FromSeconds(19), service.Snapshot.Position, "Track-scoped seek uses the backend timeline.");
            await service.SeekAsync(TimeSpan.FromSeconds(20));
            TestAssert.Equal(2, backend.SeekCount, "The existing unconditional seek API remains available.");
        }

        /// <summary>Checks backend-clock state and transactional navigation after a decoding failure.</summary>
        /// <returns>A task representing the completed case.</returns>
        private static async Task ValidateFailureRollbackAndClockAsync()
        {
            var backend = new FakePlaybackBackend { FailingPath = "bad.mp3" };
            var service = new PlaybackService(backend);
            await service.SetQueueAsync(new[] { Track("good"), Track("bad") });
            backend.SetPosition(TimeSpan.FromSeconds(17));
            TestAssert.Equal(TimeSpan.FromSeconds(17), service.Snapshot.Position, "The media position must come from the backend.");
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => service.NextAsync());
            TestAssert.Equal("good", service.Queue.Current?.Id, "A failed open must roll navigation back.");
            TestAssert.Equal("good", service.Snapshot.Track?.Id, "A failed open must not publish the failed track as loaded.");
            TestAssert.Equal(PlaybackState.Failed, service.Snapshot.State, "A decoding failure must remain observable.");
            await service.DisposeAsync();
            await service.DisposeAsync();
            TestAssert.Equal(1, backend.DisposalCount, "The owning service must dispose its backend exactly once.");
            await TestAssert.ThrowsAsync<ObjectDisposedException>(() => service.NextAsync());
        }

        /// <summary>Checks that cancellation during resource resolution preserves the active track and queue.</summary>
        /// <returns>A task representing the completed case.</returns>
        private static async Task ValidateCancellationRollbackAsync()
        {
            var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new FakePlaybackBackend();
            await using var service = new PlaybackService(backend, resolveAudioPath: async (track, token) =>
            {
                if (track.Id == "pending")
                {
                    resolving.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }

                return track.Audio.LocalPath!;
            });
            await service.SetQueueAsync(new[] { Track("active"), Track("pending") });
            using var cancellation = new CancellationTokenSource();
            var next = service.NextAsync(cancellation.Token);
            await resolving.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var queuedCancellation = new CancellationTokenSource();
            var queuedNext = service.NextAsync(queuedCancellation.Token);
            queuedCancellation.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => queuedNext);
            cancellation.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => next);
            TestAssert.Equal("active", service.Queue.Current?.Id, "Canceled preparation must restore the original queue position.");
            TestAssert.Equal("active", service.Snapshot.Track?.Id, "Canceled resolution must preserve the loaded track.");
            TestAssert.Equal(PlaybackState.Playing, service.Snapshot.State, "Canceling before opening must preserve already playing audio.");
            TestAssert.Equal(0, backend.StopCount, "Canceling resource resolution must not stop the original audio.");
            backend.SetPosition(TimeSpan.FromSeconds(31));
            TestAssert.Equal(TimeSpan.FromSeconds(31), service.Snapshot.Position, "Canceled resolution must not freeze the original backend clock.");
        }

        /// <summary>Checks cleanup and queue restoration when cancellation interrupts the decoder itself.</summary>
        /// <returns>A task representing the completed case.</returns>
        private static async Task ValidateCanceledOpenAsync()
        {
            var backend = new FakePlaybackBackend { BlockingPath = "blocked.mp3" };
            await using var service = new PlaybackService(backend);
            await service.SetQueueAsync(new[] { Track("initial"), Track("blocked") });
            using var cancellation = new CancellationTokenSource();
            var next = service.NextAsync(cancellation.Token);
            await backend.BlockingOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => next);
            TestAssert.Equal("initial", service.Queue.Current?.Id, "Canceling an active decoder must not advance navigation.");
            TestAssert.Equal(PlaybackState.Stopped, service.Snapshot.State, "A canceled partial resource must be stopped.");
            TestAssert.Equal(PlaybackState.Stopped, backend.Snapshot.State, "The decoder must receive cleanup after cancellation.");
        }

        /// <summary>Checks automatic repeat, queue advancement and stopping at the end.</summary>
        /// <returns>A task representing the completed case.</returns>
        private static async Task ValidateAutomaticCompletionAsync()
        {
            var backend = new FakePlaybackBackend();
            await using var service = new PlaybackService(backend);
            await service.SetQueueAsync(new[] { Track("first"), Track("second") });
            await service.SetRepeatModeAsync(RepeatMode.One);
            var repeated = ObserveStateAsync(service, snapshot => snapshot.State == PlaybackState.Playing && backend.OpenCount == 2);
            backend.Complete();
            await repeated;
            TestAssert.Equal("first", service.Queue.Current?.Id, "Natural completion must obey single repeat.");
            await service.SetRepeatModeAsync(RepeatMode.None);
            var advanced = ObserveStateAsync(service, snapshot => snapshot.State == PlaybackState.Playing && snapshot.Track?.Id == "second");
            backend.Complete();
            await advanced;
            var stopped = ObserveStateAsync(service, snapshot => snapshot.State == PlaybackState.Stopped);
            backend.Complete();
            await stopped;
            TestAssert.Equal("second", service.Queue.Current?.Id, "Stopping at the end must retain the final selection.");
        }

        /// <summary>Waits for a specified service state without polling or arbitrary sleeps.</summary>
        /// <param name="service">The service under test.</param>
        /// <param name="predicate">The state condition to observe.</param>
        /// <returns>A task completing when the expected notification arrives.</returns>
        private static async Task ObserveStateAsync(PlaybackService service, Func<PlaybackSnapshot, bool> predicate)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStateChanged(object? sender, EventArgs eventArgs)
            {
                if (predicate(service.Snapshot))
                {
                    completion.TrySetResult();
                }
            }

            service.StateChanged += OnStateChanged;
            try
            {
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                service.StateChanged -= OnStateChanged;
            }
        }

        /// <summary>Creates a local metadata entry for deterministic playback cases.</summary>
        /// <param name="id">The entry identity and local file stem.</param>
        /// <returns>The synthetic music metadata.</returns>
        private static MusicTrack Track(string id)
        {
            return new MusicTrack
            {
                Id = id,
                Title = id,
                Audio = new MediaResource { LocalPath = id + ".mp3" }
            };
        }

        /// <summary>Provides a controllable audio backend without decoding media or reading files.</summary>
        private sealed class FakePlaybackBackend : IPlaybackBackend
        {
            /// <summary>Marks the current synchronous native-callback stack independently for each thread.</summary>
            [ThreadStatic]
            private static bool _insideCallback;

            /// <summary>Gets whether audio API calls should reject native callback reentry.</summary>
            public bool RejectCallbackReentry { get; init; }

            /// <summary>Gets the number of attempted native callback reentries.</summary>
            public int CallbackReentryCount { get; private set; }

            /// <summary>Gets the number of commands that reached the synthetic seek implementation.</summary>
            public int SeekCount { get; private set; }

            /// <summary>Gets the number of commands that stopped the synthetic audio resource.</summary>
            public int StopCount { get; private set; }

            /// <summary>Gets or sets the path that fails during preparation.</summary>
            public string? FailingPath { get; init; }

            /// <summary>Gets or sets the synthetic resource whose preparation blocks until canceled.</summary>
            public string? BlockingPath { get; init; }

            /// <summary>Signals that cancelable decoder preparation has started.</summary>
            public TaskCompletionSource BlockingOpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Gets the number of opened resources.</summary>
            public int OpenCount { get; private set; }

            /// <summary>Gets the number of disposal requests.</summary>
            public int DisposalCount { get; private set; }

            /// <summary>Gets or sets the metadata published by the next synthetic preparation.</summary>
            public AudioStreamInfo? PreparedAudioInfo { get; set; }

            /// <summary>Gets or sets the duration published by the next synthetic preparation.</summary>
            public TimeSpan PreparedDuration { get; set; } = TimeSpan.FromMinutes(3);

            /// <summary>Gets or sets whether starting an already parsed synthetic resource fails.</summary>
            public bool FailPlay { get; set; }

            /// <summary>Gets true for this controllable test backend.</summary>
            public bool IsAvailable => true;

            /// <summary>Gets playback and seeking support for regression cases.</summary>
            public PlaybackCapabilities Capabilities => PlaybackCapabilities.Playback | PlaybackCapabilities.Seeking;

            /// <summary>Gets the manually controlled backend media state.</summary>
            public PlaybackSnapshot Snapshot { get; private set; } = new();

            /// <summary>Reports changes to the playback owner.</summary>
            public event EventHandler? StateChanged;

            /// <summary>Prepares a synthetic resource or reports a requested failure.</summary>
            /// <param name="localPath">The synthetic resource path.</param>
            /// <param name="cancellationToken">Cancels the command.</param>
            /// <returns>A completed preparation operation.</returns>
            public async Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
            {
                if (RejectCallbackReentry && _insideCallback)
                {
                    CallbackReentryCount++;
                    throw new InvalidOperationException("Audio commands cannot run within a native callback.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (localPath == FailingPath)
                {
                    throw new InvalidOperationException("Synthetic decoder failure.");
                }

                if (localPath == BlockingPath)
                {
                    Snapshot = new PlaybackSnapshot { State = PlaybackState.Loading };
                    BlockingOpenStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                OpenCount++;
                Snapshot = new PlaybackSnapshot { State = PlaybackState.Paused, Duration = PreparedDuration, AudioInfo = PreparedAudioInfo };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            /// <summary>Marks synthetic audio as playing.</summary>
            /// <param name="cancellationToken">Cancels the command.</param>
            /// <returns>A completed command.</returns>
            public Task PlayAsync(CancellationToken cancellationToken = default)
            {
                if (FailPlay)
                {
                    throw new IOException("The parsed synthetic resource could not start.");
                }

                return ChangeStateAsync(PlaybackState.Playing, cancellationToken);
            }

            /// <summary>Marks synthetic audio as paused.</summary>
            /// <param name="cancellationToken">Cancels the command.</param>
            /// <returns>A completed command.</returns>
            public Task PauseAsync(CancellationToken cancellationToken = default)
            {
                return ChangeStateAsync(PlaybackState.Paused, cancellationToken);
            }

            /// <summary>Marks synthetic audio as stopped.</summary>
            /// <param name="cancellationToken">Cancels the command.</param>
            /// <returns>A completed command.</returns>
            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StopCount++;
                return ChangeStateAsync(PlaybackState.Stopped, cancellationToken);
            }

            /// <summary>Changes the synthetic backend media clock.</summary>
            /// <param name="position">The target position.</param>
            /// <param name="cancellationToken">Cancels the command.</param>
            /// <returns>A completed command.</returns>
            public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SeekCount++;
                SetPosition(position);
                return Task.CompletedTask;
            }

            /// <summary>Records disposal without native resources.</summary>
            /// <returns>A completed disposal operation.</returns>
            public ValueTask DisposeAsync()
            {
                DisposalCount++;
                return ValueTask.CompletedTask;
            }

            /// <summary>Updates the authoritative synthetic position.</summary>
            /// <param name="position">The media position to report.</param>
            public void SetPosition(TimeSpan position)
            {
                Snapshot = Snapshot with { Position = position };
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            /// <summary>Signals the natural end of the current resource.</summary>
            /// <param name="nativeCallback">Whether notifications execute inside a synthetic native callback.</param>
            /// <param name="notificationCount">The number of duplicate notifications to raise for the same terminal state.</param>
            public void Complete(bool nativeCallback = false, int notificationCount = 1)
            {
                Snapshot = Snapshot with { State = PlaybackState.Completed };
                var previous = _insideCallback;
                _insideCallback = nativeCallback;
                try
                {
                    for (var index = 0; index < notificationCount; index++)
                    {
                        StateChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                finally
                {
                    _insideCallback = previous;
                }
            }

            /// <summary>Publishes a lifecycle state for a test command.</summary>
            /// <param name="state">The requested lifecycle state.</param>
            /// <param name="cancellationToken">Cancels the command.</param>
            /// <returns>A completed command.</returns>
            private Task ChangeStateAsync(PlaybackState state, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Snapshot = Snapshot with { State = state };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }
        }
    }
}
