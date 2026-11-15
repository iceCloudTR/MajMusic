#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Playback
{
    /// <summary>Serializes queue and platform-audio commands without depending on a user-interface lifecycle.</summary>
    /// <remarks>The host owns this service and disposes it when its desktop process or mobile media service ends.</remarks>
    public sealed class PlaybackService : IAsyncDisposable
    {
        /// <summary>Owns the actual audio resource and media clock.</summary>
        private readonly IPlaybackBackend _backend;

        /// <summary>Resolves a track to a complete local audio resource.</summary>
        private readonly Func<MusicTrack, CancellationToken, Task<string>> _resolveAudioPath;

        /// <summary>Serializes commands and natural-completion handling.</summary>
        private readonly SemaphoreSlim _commands = new(1, 1);

        /// <summary>Cancels queued and in-flight commands when the owning host ends.</summary>
        private readonly CancellationTokenSource _lifetime = new();

        /// <summary>Retains the last successfully loaded track.</summary>
        private MusicTrack? _loadedTrack;

        /// <summary>Overrides backend state during loading, failure or explicit queue boundaries.</summary>
        private PlaybackSnapshot? _stateOverride;

        /// <summary>Rejects completion events belonging to a previous command.</summary>
        private long _generation;

        /// <summary>Suppresses backend events while an explicit command updates several pieces of state.</summary>
        private int _suppressEvents;

        /// <summary>Coalesces backend notifications into one bounded pending wake-up.</summary>
        private readonly SemaphoreSlim _notifications = new(0, 1);

        /// <summary>Processes backend notifications outside native callback stacks.</summary>
        private readonly Task _notificationWorker;

        /// <summary>Avoids releasing an already pending bounded worker signal.</summary>
        private int _notificationPending;

        /// <summary>Ensures the service is disposed only once.</summary>
        private int _disposed;

        /// <summary>Creates a playback owner around one platform audio backend.</summary>
        /// <param name="backend">The backend owned and disposed by this service.</param>
        /// <param name="queue">An optional queue, exclusively mutated through this service while playback is active.</param>
        /// <param name="resolveAudioPath">An optional resource resolver; the default requires a track's local audio path.</param>
        /// <exception cref="ArgumentNullException"><paramref name="backend"/> is null.</exception>
        public PlaybackService(
            IPlaybackBackend backend,
            PlaybackQueue? queue = null,
            Func<MusicTrack, CancellationToken, Task<string>>? resolveAudioPath = null)
        {
            ArgumentNullException.ThrowIfNull(backend);
            _backend = backend;
            Queue = queue ?? new PlaybackQueue();
            _resolveAudioPath = resolveAudioPath ?? ResolveLocalPathAsync;
            _backend.StateChanged += OnBackendStateChanged;
            _notificationWorker = Task.Run(ProcessBackendNotificationsAsync);
        }

        /// <summary>Gets the queue for read-only observation; use service commands for mutations during playback.</summary>
        public PlaybackQueue Queue { get; }

        /// <summary>Gets whether the host has connected a real audio playback implementation.</summary>
        public bool IsAvailable => _backend.IsAvailable;

        /// <summary>Gets optional application-volume and output-route controls exposed by the owned backend.</summary>
        public IAudioOutputControl? AudioOutput => _backend as IAudioOutputControl;

        /// <summary>Gets the host backend's audio capabilities.</summary>
        public PlaybackCapabilities Capabilities => _backend.Capabilities;

        /// <summary>Gets the authoritative media state, enriched with the loaded library entry.</summary>
        public PlaybackSnapshot Snapshot => Volatile.Read(ref _stateOverride) ?? _backend.Snapshot with { Track = _loadedTrack };

        /// <summary>Occurs when playback state changes, potentially on a backend thread.</summary>
        public event EventHandler? StateChanged;

        /// <summary>Replaces the queue with one entry and starts it.</summary>
        /// <param name="track">The entry to play.</param>
        /// <param name="cancellationToken">Cancels preparation or the playback command.</param>
        /// <returns>A task representing started playback.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="track"/> is null.</exception>
        /// <exception cref="NotSupportedException">No platform audio backend is available.</exception>
        public Task PlayAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            return SetQueueAsync(new[] { track }, 0, cancellationToken);
        }

        /// <summary>Replaces the queue and starts its selected entry.</summary>
        /// <param name="tracks">The entries to play.</param>
        /// <param name="startIndex">The original entry index to start.</param>
        /// <param name="cancellationToken">Cancels preparation or playback.</param>
        /// <returns>A task representing started playback, or stopped playback for an empty queue.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="tracks"/> is null.</exception>
        /// <exception cref="NotSupportedException">No platform audio backend is available.</exception>
        public Task SetQueueAsync(IEnumerable<MusicTrack> tracks, int startIndex = 0, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            return ExecuteAsync(token => ChangeTrackAsync(() =>
            {
                Queue.SetTracks(tracks, startIndex);
                return Queue.Current;
            }, true, token), cancellationToken);
        }

        /// <summary>Advances manually, independently of single-track repeat.</summary>
        /// <param name="cancellationToken">Cancels preparation or playback.</param>
        /// <returns>A task representing the completed transition.</returns>
        public Task NextAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(token => ChangeTrackAsync(() => Queue.MoveNext(), true, token), cancellationToken);
        }

        /// <summary>Returns to an earlier entry without stopping when no previous entry exists.</summary>
        /// <param name="cancellationToken">Cancels preparation or playback.</param>
        /// <returns>A task representing the completed transition.</returns>
        public Task PreviousAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(token => ChangeTrackAsync(Queue.MovePrevious, false, token), cancellationToken);
        }

        /// <summary>Pauses the loaded audio.</summary>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task representing the completed pause.</returns>
        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(async token =>
            {
                EnsureAvailable();
                await _backend.PauseAsync(token).ConfigureAwait(false);
                _stateOverride = null;
            }, cancellationToken);
        }

        /// <summary>Resumes the loaded audio.</summary>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task representing resumed playback.</returns>
        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(async token =>
            {
                EnsureAvailable();
                if (_loadedTrack is null)
                {
                    throw new InvalidOperationException("No audio resource is loaded.");
                }

                await _backend.PlayAsync(token).ConfigureAwait(false);
                _stateOverride = null;
            }, cancellationToken);
        }

        /// <summary>Stops audio while retaining the queue and its current selection.</summary>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task representing the completed stop.</returns>
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(async token =>
            {
                var previousSnapshot = Snapshot;
                Interlocked.Increment(ref _generation);
                if (_loadedTrack is not null)
                {
                    await _backend.StopAsync(token).ConfigureAwait(false);
                }

                _stateOverride = previousSnapshot with
                {
                    State = PlaybackState.Stopped,
                    Track = _loadedTrack,
                    Position = TimeSpan.Zero,
                    Error = null
                };
            }, cancellationToken);
        }

        /// <summary>Seeks the loaded resource using the backend timeline.</summary>
        /// <param name="position">The requested nonnegative position.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task representing the completed seek.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The requested position is negative or exceeds a known duration.</exception>
        /// <exception cref="NotSupportedException">The backend does not support seeking.</exception>
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            return SeekCoreAsync(position, null, cancellationToken);
        }

        /// <summary>Seeks only if the same music entry is still loaded when the serialized command executes.</summary>
        /// <param name="position">The requested nonnegative position.</param>
        /// <param name="expectedTrackId">The identity captured when the seek gesture began.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task representing the seek, or no operation when the loaded entry changed.</returns>
        /// <exception cref="ArgumentException">The expected identity is blank.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The position is negative or exceeds a known duration.</exception>
        /// <exception cref="NotSupportedException">The backend does not support seeking.</exception>
        public Task SeekAsync(TimeSpan position, string expectedTrackId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedTrackId);
            return SeekCoreAsync(position, expectedTrackId, cancellationToken);
        }

        /// <summary>Serializes seeking and optionally rejects a gesture belonging to a replaced entry.</summary>
        /// <param name="position">The requested media position.</param>
        /// <param name="expectedTrackId">The gesture's entry identity, or null for an unconditional seek.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task representing the completed or obsolete command.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The position is outside the known media timeline.</exception>
        /// <exception cref="NotSupportedException">The backend does not support seeking.</exception>
        private Task SeekCoreAsync(TimeSpan position, string? expectedTrackId, CancellationToken cancellationToken)
        {
            if (position < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            return ExecuteAsync(async token =>
            {
                if (expectedTrackId is not null && _loadedTrack?.Id != expectedTrackId)
                {
                    return;
                }

                EnsureAvailable();
                if (!Capabilities.HasFlag(PlaybackCapabilities.Seeking))
                {
                    throw new NotSupportedException("The platform audio backend does not support seeking.");
                }

                if (_backend.Snapshot.Duration is { } duration && position > duration)
                {
                    throw new ArgumentOutOfRangeException(nameof(position));
                }

                await _backend.SeekAsync(position, token).ConfigureAwait(false);
                _stateOverride = null;
            }, cancellationToken);
        }

        /// <summary>Changes shuffle without replacing the current resource.</summary>
        /// <param name="enabled">Whether future navigation uses a stable shuffled order.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task representing the completed preference update.</returns>
        public Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(_ =>
            {
                Queue.Shuffle = enabled;
                return Task.CompletedTask;
            }, cancellationToken);
        }

        /// <summary>Changes automatic repeat without replacing the current resource.</summary>
        /// <param name="mode">The requested repeat behavior.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task representing the completed preference update.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The repeat value is not defined.</exception>
        public Task SetRepeatModeAsync(RepeatMode mode, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            return ExecuteAsync(_ =>
            {
                Queue.RepeatMode = mode;
                return Task.CompletedTask;
            }, cancellationToken);
        }

        /// <summary>Cancels work and disposes the owned platform backend exactly once.</summary>
        /// <returns>A task representing completed resource release.</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _backend.StateChanged -= OnBackendStateChanged;
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _notificationWorker.ConfigureAwait(false);
            await _commands.WaitAsync().ConfigureAwait(false);
            try
            {
                await _backend.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _commands.Release();
                _notifications.Dispose();
                _lifetime.Dispose();
            }
        }

        /// <summary>Serializes a command and links it to the media-service lifetime.</summary>
        /// <param name="action">The command executed while holding the owner gate.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A task representing the completed command.</returns>
        /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
        private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _suppressEvents);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                await action(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _suppressEvents);
                _commands.Release();
                RaiseStateChanged();
                SignalBackendNotification();
            }
        }

        /// <summary>Transitions one queue entry and rolls back navigation if preparation fails.</summary>
        /// <param name="select">Selects the next requested queue entry.</param>
        /// <param name="stopAtBoundary">Whether a null selection stops playback.</param>
        /// <param name="cancellationToken">Cancels resolution, preparation or playback.</param>
        /// <returns>A task representing the completed transition.</returns>
        private async Task ChangeTrackAsync(Func<MusicTrack?> select, bool stopAtBoundary, CancellationToken cancellationToken)
        {
            EnsureAvailable();
            var previousQueue = Queue.CaptureState();
            var previousOverride = Volatile.Read(ref _stateOverride);
            var previousSnapshot = Snapshot;
            var openingStarted = false;
            var resolutionStarted = false;
            Interlocked.Increment(ref _generation);
            try
            {
                var selected = select();
                if (selected is null)
                {
                    if (stopAtBoundary)
                    {
                        if (_loadedTrack is not null)
                        {
                            await _backend.StopAsync(cancellationToken).ConfigureAwait(false);
                        }

                        _stateOverride = previousSnapshot with
                        {
                            State = PlaybackState.Stopped,
                            Track = _loadedTrack,
                            Position = TimeSpan.Zero,
                            Error = null
                        };
                    }

                    return;
                }

                _stateOverride = previousSnapshot with { State = PlaybackState.Loading, Track = _loadedTrack, Error = null };
                RaiseStateChanged();
                resolutionStarted = true;
                var path = await _resolveAudioPath(selected, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                openingStarted = true;
                await _backend.OpenAsync(path, cancellationToken).ConfigureAwait(false);
                await _backend.PlayAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _loadedTrack = selected;
                _stateOverride = null;
            }
            catch (Exception exception)
            {
                Queue.RestoreState(previousQueue);
                if (openingStarted || (resolutionStarted && exception is not OperationCanceledException))
                {
                    await TryStopAfterFailureAsync().ConfigureAwait(false);
                }

                _stateOverride = exception is OperationCanceledException && !openingStarted
                    ? previousOverride
                    : previousSnapshot with
                    {
                        State = exception is OperationCanceledException ? PlaybackState.Stopped : PlaybackState.Failed,
                        Track = _loadedTrack,
                        Position = TimeSpan.Zero,
                        Error = exception is OperationCanceledException ? null : "The audio resource could not be prepared."
                    };
                throw;
            }
        }

        /// <summary>Stops a partially prepared resource without hiding the original failure.</summary>
        /// <returns>A task representing cleanup.</returns>
        private async Task TryStopAfterFailureAsync()
        {
            try
            {
                await _backend.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The original command failure remains observable by its caller.
            }
        }

        /// <summary>Signals managed notification processing without calling audio APIs on a native callback stack.</summary>
        /// <param name="sender">The notifying backend.</param>
        /// <param name="eventArgs">The state-change notification.</param>
        private void OnBackendStateChanged(object? sender, EventArgs eventArgs)
        {
            SignalBackendNotification();
        }

        /// <summary>Coalesces any number of progress or terminal notifications into one worker wake-up.</summary>
        private void SignalBackendNotification()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (Interlocked.Exchange(ref _notificationPending, 1) != 0)
            {
                return;
            }

            try
            {
                _notifications.Release();
            }
            catch (SemaphoreFullException)
            {
                // The worker will read the newest authoritative backend state when it wakes.
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                // A callback already in flight may arrive after the owner unsubscribed.
            }
        }

        /// <summary>Observes backend notifications and automatic navigation on a managed worker.</summary>
        /// <returns>A task that ends after the owning service cancels and drains its notification worker.</returns>
        private async Task ProcessBackendNotificationsAsync()
        {
            try
            {
                while (true)
                {
                    await _notifications.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _notificationPending, 0);
                    try
                    {
                        if (Volatile.Read(ref _suppressEvents) != 0)
                        {
                            // Every command signals again after publishing its final state.
                            continue;
                        }

                        var generation = Interlocked.Read(ref _generation);
                        if (_backend.Snapshot.State == PlaybackState.Completed
                            && Volatile.Read(ref _stateOverride)?.State is not (PlaybackState.Failed or PlaybackState.Stopped))
                        {
                            await ExecuteAsync(async token =>
                            {
                                if (generation == Interlocked.Read(ref _generation) && _backend.Snapshot.State == PlaybackState.Completed)
                                {
                                    await ChangeTrackAsync(() => Queue.MoveNext(true), true, token).ConfigureAwait(false);
                                }
                            }, _lifetime.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            // Progress must not erase a failed command or a deliberate queue-boundary state.
                            RaiseStateChanged();
                        }
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        _stateOverride = Snapshot with
                        {
                            State = PlaybackState.Failed,
                            Track = _loadedTrack,
                            Error = "The next audio resource could not be prepared."
                        };
                        RaiseStateChanged();
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Owner shutdown cancels natural-completion transitions.
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                // A late backend event cannot restart a disposed media service.
            }
        }

        /// <summary>Checks that commands will reach an actual platform audio implementation.</summary>
        /// <exception cref="NotSupportedException">No platform audio backend is connected.</exception>
        private void EnsureAvailable()
        {
            if (!_backend.IsAvailable)
            {
                throw new NotSupportedException("A platform audio backend has not been connected.");
            }
        }

        /// <summary>Raises a state notification for the host to marshal to its user interface.</summary>
        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Resolves the local path without initiating any network access.</summary>
        /// <param name="track">The requested library entry.</param>
        /// <param name="cancellationToken">Cancels resolution.</param>
        /// <returns>The complete local audio path.</returns>
        /// <exception cref="InvalidOperationException">The entry has no local audio resource.</exception>
        private static Task<string> ResolveLocalPathAsync(MusicTrack track, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPath = track.Audio.LocalPath;
            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new InvalidOperationException("The music entry has no local audio resource.");
            }

            return Task.FromResult(localPath);
        }
    }
}
