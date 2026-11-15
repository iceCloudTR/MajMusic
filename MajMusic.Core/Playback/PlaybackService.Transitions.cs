#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Playback
{
    /// <summary>Prepares audio independently of native commands and commits only the latest playback intent.</summary>
    public sealed partial class PlaybackService
    {
        /// <summary>Protects pending navigation and preparation ownership without holding a lock across I/O.</summary>
        private readonly object _transitionGate = new();
        /// <summary>Tracks preparation cleanup that the owning service must drain during shutdown.</summary>
        private readonly HashSet<Task> _preparations = new();
        /// <summary>Retains navigation belonging to the most recently committed resource.</summary>
        private PlaybackQueue.QueueState _committedQueue;
        /// <summary>Owns the latest requested audio preparation, including its cancellation lifetime.</summary>
        private Preparation? _preparation;
        /// <summary>Publishes the latest preparation independently of the successfully loaded media identity.</summary>
        private MusicTrack? _preparingTrack;

        /// <summary>Gets the current audio preparation intent, or null after completion, failure or cancellation.</summary>
        public MusicTrack? PreparingTrack => Volatile.Read(ref _preparingTrack);

        /// <summary>Plans navigation immediately and resolves media outside the native-command gate.</summary>
        /// <param name="select">Updates the pending queue intent and selects its resource.</param>
        /// <param name="stopAtBoundary">Whether an empty selection stops the loaded resource.</param>
        /// <param name="cancellationToken">Cancels this preparation and its eventual native commit.</param>
        /// <param name="expectedGeneration">The completed-resource generation for automatic navigation.</param>
        /// <returns>A task representing this preparation, including canceled or failed cleanup.</returns>
        private Task ChangeTrackAsync(Func<MusicTrack?> select, bool stopAtBoundary, CancellationToken cancellationToken, long? expectedGeneration = null)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            Preparation request;
            Preparation? obsolete;
            lock (_transitionGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (expectedGeneration is not null && (expectedGeneration != Interlocked.Read(ref _generation)
                    || _preparation is not null || _backend.Snapshot.State != PlaybackState.Completed))
                {
                    return Task.CompletedTask;
                }

                EnsureAvailable();
                var previous = Queue.CaptureState();
                MusicTrack? selected;
                try
                {
                    selected = select();
                }
                catch
                {
                    Queue.RestoreState(previous);
                    throw;
                }

                obsolete = _preparation;
                var baseline = obsolete?.Baseline ?? Snapshot;
                var baselineOverride = obsolete is null ? Volatile.Read(ref _stateOverride) : obsolete.BaselineOverride;
                request = new Preparation(Interlocked.Increment(ref _generation), selected, stopAtBoundary, baseline,
                    baselineOverride, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token));
                _preparation = request;
                _preparations.Add(request.Completion.Task);
                Volatile.Write(ref _preparingTrack, selected);
                _stateOverride = selected is null ? baselineOverride : baseline with { State = PlaybackState.Loading, Track = _loadedTrack, Error = null };
            }

            CancelPreparationRequest(obsolete);
            RaiseStateChanged();
            return RunPreparationAsync(request);
        }

        /// <summary>Downloads one resource and serializes only its final native open and playback.</summary>
        /// <param name="request">The owned navigation and cancellation intent.</param>
        /// <returns>A task representing resolution, commit and cleanup.</returns>
        private async Task RunPreparationAsync(Preparation request)
        {
            try
            {
                var path = request.Track is null ? null : await _resolveAudioPath(request.Track, request.Cancellation.Token).ConfigureAwait(false);
                EnsureCurrentPreparation(request);
                await ExecuteAsync(async token =>
                {
                    try
                    {
                        EnsureCurrentPreparation(request);
                        if (request.Track is not null)
                        {
                            lock (_transitionGate)
                            {
                                EnsureCurrentPreparation(request);
                                request.OpeningStarted = true;
                            }

                            await _backend.OpenAsync(path!, token).ConfigureAwait(false);
                            EnsureCurrentPreparation(request);
                            await _backend.PlayAsync(token).ConfigureAwait(false);
                            EnsureCurrentPreparation(request);
                        }
                        else if (request.StopAtBoundary && _loadedTrack is not null)
                        {
                            await _backend.StopAsync(token).ConfigureAwait(false);
                            EnsureCurrentPreparation(request);
                        }

                        lock (_transitionGate)
                        {
                            EnsureCurrentPreparation(request);
                            if (request.Track is not null)
                            {
                                Volatile.Write(ref _loadedTrack, request.Track);
                                _stateOverride = null;
                            }
                            else if (request.StopAtBoundary)
                            {
                                if (Queue.Current is not null)
                                {
                                    RestoreCommittedQueue();
                                }

                                _stateOverride = request.Baseline with { State = PlaybackState.Stopped, Track = _loadedTrack, Position = TimeSpan.Zero, Error = null };
                            }
                            else
                            {
                                RestoreCommittedQueue();
                                _stateOverride = request.BaselineOverride;
                            }

                            _committedQueue = Queue.CaptureState();
                            _preparation = null;
                            Volatile.Write(ref _preparingTrack, null);
                        }
                    }
                    catch
                    {
                        if (request.OpeningStarted)
                        {
                            await TryStopAfterFailureAsync().ConfigureAwait(false);
                            InvalidateNativeOwner(request);
                        }

                        throw;
                    }
                }, request.Cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var current = false;
                lock (_transitionGate)
                {
                    current = ReferenceEquals(_preparation, request);
                    if (current)
                    {
                        RestoreCommittedQueue();
                        _stateOverride = exception is OperationCanceledException
                            ? request.OpeningStarted ? new PlaybackSnapshot { State = PlaybackState.Stopped } : request.BaselineOverride
                            : request.Baseline with
                            {
                                State = PlaybackState.Failed,
                                Track = request.Baseline.Track,
                                Position = TimeSpan.Zero,
                                Error = "The audio resource could not be prepared."
                            };
                        _preparation = null;
                        Volatile.Write(ref _preparingTrack, null);
                    }
                }

                if (!current)
                {
                    throw new OperationCanceledException(request.Cancellation.Token);
                }

                if (!request.OpeningStarted && request.Track is not null && exception is not OperationCanceledException)
                {
                    try
                    {
                        await ExecuteAsync(async _ =>
                        {
                            if (IsCurrentIntent(request.Generation))
                            {
                                await TryStopAfterFailureAsync().ConfigureAwait(false);
                            }
                        }, _lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        // Shutdown owns the final native resource cleanup.
                    }
                }

                throw;
            }
            finally
            {
                request.Cancellation.Dispose();
                RaiseStateChanged();
                request.Completion.TrySetResult();
                lock (_transitionGate)
                {
                    _preparations.Remove(request.Completion.Task);
                }
            }
        }

        /// <summary>Stops an interrupted decoder before releasing native access and invalidates its previous logical owner.</summary>
        /// <param name="interrupted">The preparation whose open or play could have replaced native media.</param>
        private void InvalidateNativeOwner(Preparation interrupted)
        {
            lock (_transitionGate)
            {
                Volatile.Write(ref _loadedTrack, null);
                var empty = new PlaybackSnapshot { State = PlaybackState.Stopped };
                if (_preparation is { } active)
                {
                    if (!ReferenceEquals(active, interrupted))
                    {
                        active.Baseline = empty;
                        active.BaselineOverride = empty;
                    }

                    _stateOverride = active.Track is null ? empty : empty with { State = PlaybackState.Loading };
                }
                else
                {
                    _stateOverride = empty;
                }
            }
        }

        /// <summary>Supersedes pending preparation before a pause, resume or stop can wait for native access.</summary>
        /// <param name="cancellationToken">Rejects an already canceled user intent.</param>
        /// <returns>The generation allowed to publish this control's result.</returns>
        private long CancelPreparation(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            Preparation? obsolete;
            long generation;
            lock (_transitionGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                generation = Interlocked.Increment(ref _generation);
                obsolete = _preparation;
                if (obsolete is not null)
                {
                    RestoreCommittedQueue();
                    if (obsolete.OpeningStarted)
                    {
                        Volatile.Write(ref _loadedTrack, null);
                        obsolete.Baseline = new PlaybackSnapshot { State = PlaybackState.Stopped };
                        obsolete.BaselineOverride = obsolete.Baseline;
                    }

                    _stateOverride = obsolete.BaselineOverride;
                    _preparation = null;
                    Volatile.Write(ref _preparingTrack, null);
                }
            }

            CancelPreparationRequest(obsolete);
            RaiseStateChanged();
            return generation;
        }

        /// <summary>Restores successful navigation without discarding subsequently edited repeat or shuffle preferences.</summary>
        private void RestoreCommittedQueue()
        {
            var shuffle = Queue.Shuffle;
            var repeat = Queue.RepeatMode;
            Queue.RestoreState(_committedQueue);
            Queue.Shuffle = shuffle;
            Queue.RepeatMode = repeat;
            _committedQueue = Queue.CaptureState();
        }

        /// <summary>Checks whether a short native command still represents the newest user intent.</summary>
        /// <param name="generation">The generation assigned before waiting for native access.</param>
        /// <returns>Whether this intent remains current.</returns>
        private bool IsCurrentIntent(long generation)
        {
            return generation == Interlocked.Read(ref _generation) && Volatile.Read(ref _disposed) == 0;
        }

        /// <summary>Rejects obsolete resolution results before they can open or publish a native resource.</summary>
        /// <param name="request">The owned preparation.</param>
        /// <exception cref="OperationCanceledException">The preparation was canceled or superseded.</exception>
        private void EnsureCurrentPreparation(Preparation request)
        {
            request.Cancellation.Token.ThrowIfCancellationRequested();
            lock (_transitionGate)
            {
                if (!ReferenceEquals(_preparation, request) || !IsCurrentIntent(request.Generation))
                {
                    throw new OperationCanceledException(request.Cancellation.Token);
                }
            }
        }

        /// <summary>Cancels an obsolete owner outside the queue-state lock.</summary>
        /// <param name="request">The obsolete preparation, or null when no resolution was active.</param>
        private static void CancelPreparationRequest(Preparation? request)
        {
            try
            {
                request?.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A just-completed owner has already released its cancellation source.
            }
        }

        /// <summary>Retains one preparation's immutable intent and its final cleanup signal.</summary>
        private sealed class Preparation
        {
            /// <summary>Publishes whether native ownership may have changed before cancellation inspects this request.</summary>
            private int _openingStarted;

            /// <summary>Creates an owned preparation snapshot.</summary>
            /// <param name="generation">The current playback intent generation.</param>
            /// <param name="track">The requested resource, or null at a queue boundary.</param>
            /// <param name="stopAtBoundary">Whether an empty selection stops playback.</param>
            /// <param name="baseline">The last successfully presented media state.</param>
            /// <param name="baselineOverride">The prior explicit backend-state override.</param>
            /// <param name="cancellation">The caller, supersession and service cancellation lifetime.</param>
            public Preparation(long generation, MusicTrack? track, bool stopAtBoundary, PlaybackSnapshot baseline,
                PlaybackSnapshot? baselineOverride, CancellationTokenSource cancellation)
            {
                Generation = generation;
                Track = track;
                StopAtBoundary = stopAtBoundary;
                Baseline = baseline;
                BaselineOverride = baselineOverride;
                Cancellation = cancellation;
            }

            /// <summary>Gets the generation allowed to commit this resource.</summary>
            public long Generation { get; }
            /// <summary>Gets the selected pending resource.</summary>
            public MusicTrack? Track { get; }
            /// <summary>Gets whether an empty selection stops playback.</summary>
            public bool StopAtBoundary { get; }
            /// <summary>Gets or sets the preceding native ownership state under the transition gate.</summary>
            public PlaybackSnapshot Baseline { get; set; }
            /// <summary>Gets or sets its explicit override when an earlier native open loses its resource.</summary>
            public PlaybackSnapshot? BaselineOverride { get; set; }
            /// <summary>Gets cancellation owned by this preparation.</summary>
            public CancellationTokenSource Cancellation { get; }
            /// <summary>Gets the cleanup signal awaited by service disposal.</summary>
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets or sets whether native resource ownership may have changed.</summary>
            public bool OpeningStarted
            {
                get => Volatile.Read(ref _openingStarted) != 0;
                set => Volatile.Write(ref _openingStarted, value ? 1 : 0);
            }
        }
    }
}
