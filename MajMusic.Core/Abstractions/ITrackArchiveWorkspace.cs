#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Writes a portable ZIP of an explicitly selected song and its available Maj assets.</summary>
    public interface ITrackArchiveWorkspace
    {
        /// <summary>Writes a ZIP without taking ownership of the destination stream.</summary>
        /// <param name="track">The song captured by the save or share action.</param>
        /// <param name="destination">A fresh writable stream that the caller will publish only on success.</param>
        /// <param name="cancellationToken">Cancels resource resolution and archive writing.</param>
        /// <returns>A task that completes after the full archive is written.</returns>
        /// <exception cref="IOException">A required source file or the destination is unavailable.</exception>
        /// <exception cref="InvalidOperationException">A required online resource is unavailable in the current mode.</exception>
        /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
        Task WriteTrackArchiveAsync(MusicTrack track, Stream destination, CancellationToken cancellationToken = default);
    }
}
