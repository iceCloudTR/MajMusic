#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Parses metadata from complete local audio files without using the playback device.</summary>
    public interface IAudioMetadataReader
    {
        /// <summary>Reads properties without starting audio playback or accessing a network resource.</summary>
        /// <param name="localPath">The fully qualified path of a complete local file.</param>
        /// <param name="cancellationToken">Cancels parsing.</param>
        /// <returns>The known duration and audio stream properties.</returns>
        /// <exception cref="IOException">The file cannot be read or does not contain supported audio.</exception>
        /// <exception cref="NotSupportedException">The native metadata reader is unavailable.</exception>
        Task<AudioMetadata> ReadAsync(string localPath, CancellationToken cancellationToken = default);
    }
}
