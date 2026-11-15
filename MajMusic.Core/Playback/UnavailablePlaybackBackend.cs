#nullable enable

namespace MajMusic.Core.Playback
{
    /// <summary>Explicitly represents an application host whose platform audio backend has not been connected.</summary>
    public sealed class UnavailablePlaybackBackend : IPlaybackBackend
    {
        /// <summary>Creates an unavailable backend with an optional actionable startup failure.</summary>
        /// <param name="reason">The non-secret reason playback could not be initialized.</param>
        public UnavailablePlaybackBackend(string? reason = null)
        {
            Snapshot = new PlaybackSnapshot { Error = reason };
        }

        /// <summary>Gets false because this implementation never decodes or plays audio.</summary>
        public bool IsAvailable => false;

        /// <summary>Gets an empty capability set.</summary>
        public PlaybackCapabilities Capabilities => PlaybackCapabilities.None;

        /// <summary>Gets an empty playback state without a simulated media clock.</summary>
        public PlaybackSnapshot Snapshot { get; }

        /// <summary>Provides the interface event without retaining subscribers because state never changes.</summary>
        public event EventHandler? StateChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        /// <summary>Reports that an actual platform audio backend is required.</summary>
        /// <param name="localPath">The requested audio file.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A faulted task rather than simulated playback.</returns>
        /// <exception cref="NotSupportedException">A platform audio backend has not been connected.</exception>
        public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
        {
            return Unavailable(cancellationToken);
        }

        /// <summary>Reports that playback is unavailable.</summary>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A faulted task.</returns>
        /// <exception cref="NotSupportedException">A platform audio backend has not been connected.</exception>
        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            return Unavailable(cancellationToken);
        }

        /// <summary>Reports that pausing is unavailable.</summary>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A faulted task.</returns>
        /// <exception cref="NotSupportedException">A platform audio backend has not been connected.</exception>
        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            return Unavailable(cancellationToken);
        }

        /// <summary>Reports that stopping is unavailable.</summary>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A faulted task.</returns>
        /// <exception cref="NotSupportedException">A platform audio backend has not been connected.</exception>
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Unavailable(cancellationToken);
        }

        /// <summary>Reports that seeking is unavailable.</summary>
        /// <param name="position">The requested position.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>A faulted task.</returns>
        /// <exception cref="NotSupportedException">A platform audio backend has not been connected.</exception>
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            return Unavailable(cancellationToken);
        }

        /// <summary>Completes without allocating platform resources.</summary>
        /// <returns>A completed disposal operation.</returns>
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        /// <summary>Creates a canceled or explicitly unsupported operation.</summary>
        /// <param name="cancellationToken">Cancels the command before capability validation.</param>
        /// <returns>A canceled or faulted task.</returns>
        private static Task Unavailable(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            return Task.FromException(new NotSupportedException("A platform audio backend has not been connected."));
        }
    }
}
