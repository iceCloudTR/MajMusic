#nullable enable

using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Keeps cancellable playback intents separate from local-library operations and coalesces timeline commits.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Exposes completion of the latest observed transport intent.</summary>
        private Task _transportUpdate = Task.CompletedTask;
        /// <summary>Retains only the latest timeline position waiting behind an active seek.</summary>
        private SeekRequest? _pendingSeek;
        /// <summary>Indicates whether one tracked timeline worker owns pending commits.</summary>
        private bool _seekWorkerRunning;
        /// <summary>Identifies the worker permitted to release shared seek ownership.</summary>
        private long _seekWorkerId;
        /// <summary>Exposes the single tracked timeline worker to callers joining a newer commit.</summary>
        private Task _seekWorker = Task.CompletedTask;

        /// <summary>Gets completion of the most recently requested playback or timeline operation.</summary>
        public Task TransportUpdate => Volatile.Read(ref _transportUpdate);

        /// <summary>Creates a reentrant playback intent whose observed work does not disable other player controls.</summary>
        /// <param name="action">The transport operation that may supersede preceding preparation.</param>
        /// <param name="canExecute">The resource or backend availability check.</param>
        /// <returns>A command whose tracked operation is exposed through <see cref="TransportUpdate"/>.</returns>
        private AsyncCommand CreateTransportCommand(Func<Task> action, Func<bool>? canExecute = null)
        {
            return new AsyncCommand(() =>
            {
                CancelSeek();
                RunTransportAsync(action);
                return Task.CompletedTask;
            }, ReportError, () => !Volatile.Read(ref _isDisposing) && (canExecute?.Invoke() ?? true));
        }

        /// <summary>Registers an independent player operation and observes cancellation or failure through the view lifetime.</summary>
        /// <param name="action">The playback or timeline operation.</param>
        /// <returns>The tracked completion, including its error reporting and cleanup.</returns>
        private Task RunTransportAsync(Func<Task> action)
        {
            var operation = RunTrackedAsync(async () =>
            {
                try
                {
                    await action();
                }
                catch (OperationCanceledException)
                {
                    // A newer playback intent or the view lifetime canceled this operation.
                }
                catch (Exception exception)
                {
                    try
                    {
                        await ReconcileExpiredSessionsAsync();
                    }
                    catch (Exception)
                    {
                        // Keep the original playback failure observable if optional source reconciliation also fails.
                    }

                    ReportError(exception);
                }
            });
            Volatile.Write(ref _transportUpdate, operation);
            return operation;
        }

        /// <summary>Serializes native seeks while replacing every queued position with the newest committed gesture.</summary>
        /// <param name="workerId">The worker permitted to release timeline ownership.</param>
        /// <returns>A task representing all currently retained timeline commits.</returns>
        private async Task RunSeekWorkerAsync(long workerId)
        {
            try
            {
                while (true)
                {
                    SeekRequest? request;
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    lock (_seekGate)
                    {
                        request = _pendingSeek;
                        _pendingSeek = null;
                        if (request is null)
                        {
                            _seekWorkerRunning = false;
                            return;
                        }

                        if (_seekInvalidated || request.Version != _seekVersion || request.TrackId is null
                            || _playback.Snapshot.Track?.Id != request.TrackId || !HasSeekableMedia)
                        {
                            continue;
                        }

                        _seekCancellation = cancellation;
                    }

                    try
                    {
                        await _playback.SeekAsync(TimeSpan.FromSeconds(request.Seconds), request.TrackId!, cancellation.Token);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        // A newer gesture retains its latest position for the next worker iteration.
                    }
                    catch (Exception exception)
                    {
                        ReportError(exception);
                    }
                    finally
                    {
                        lock (_seekGate)
                        {
                            if (ReferenceEquals(_seekCancellation, cancellation))
                            {
                                _seekCancellation = null;
                            }
                        }

                        NotifyTimeline();
                    }
                }
            }
            finally
            {
                lock (_seekGate)
                {
                    if (_seekWorkerId == workerId)
                    {
                        _seekWorkerRunning = false;
                    }
                }
            }
        }

        /// <summary>Retains one complete timeline gesture without creating a queue of native operations.</summary>
        /// <param name="TrackId">The media identity captured at gesture start.</param>
        /// <param name="Seconds">The latest committed position.</param>
        /// <param name="Version">The gesture generation checked before native seeking.</param>
        private sealed record SeekRequest(string? TrackId, double Seconds, long Version);
    }
}
