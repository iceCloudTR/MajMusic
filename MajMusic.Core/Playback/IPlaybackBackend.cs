#nullable enable

namespace MajMusic.Core.Playback
{
    /// <summary>Owns platform audio playback and exposes its authoritative media clock.</summary>
    /// <remarks>Mobile implementations must live in their platform media service, independently of the user interface.</remarks>
    public interface IPlaybackBackend : IAsyncDisposable
    {
        /// <summary>Gets whether an actual audio playback implementation is available.</summary>
        bool IsAvailable { get; }

        /// <summary>Gets the operations supported by the backend.</summary>
        PlaybackCapabilities Capabilities { get; }

        /// <summary>Gets the current state using the backend's media clock.</summary>
        PlaybackSnapshot Snapshot { get; }

        /// <summary>Occurs when observable media state changes, possibly on a backend thread.</summary>
        event EventHandler? StateChanged;

        /// <summary>Opens one complete local audio resource.</summary>
        /// <param name="localPath">The local file resolved by the owning resource service.</param>
        /// <param name="cancellationToken">Cancels media preparation.</param>
        /// <returns>A task representing completed preparation.</returns>
        /// <exception cref="NotSupportedException">The backend cannot play this media.</exception>
        Task OpenAsync(string localPath, CancellationToken cancellationToken = default);

        /// <summary>Starts or resumes the prepared resource.</summary>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>A task representing the completed command.</returns>
        /// <exception cref="NotSupportedException">Playback is unavailable.</exception>
        Task PlayAsync(CancellationToken cancellationToken = default);

        /// <summary>Pauses the current resource.</summary>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>A task representing the completed command.</returns>
        /// <exception cref="NotSupportedException">Playback is unavailable.</exception>
        Task PauseAsync(CancellationToken cancellationToken = default);

        /// <summary>Stops the current resource.</summary>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>A task representing the completed command.</returns>
        /// <exception cref="NotSupportedException">Playback is unavailable.</exception>
        Task StopAsync(CancellationToken cancellationToken = default);

        /// <summary>Seeks the current resource using its media timeline.</summary>
        /// <param name="position">The nonnegative target position.</param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>A task representing the completed seek.</returns>
        /// <exception cref="NotSupportedException">Seeking is unavailable.</exception>
        Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    }
}
