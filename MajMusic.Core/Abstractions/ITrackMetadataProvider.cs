#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Enriches a music entry with cached properties without changing its title or ownership.</summary>
    public interface ITrackMetadataProvider
    {
        /// <summary>Obtains available audio properties without starting playback or downloading online music.</summary>
        /// <param name="track">The entry whose existing local audio may be parsed.</param>
        /// <param name="cancellationToken">Cancels lookup and parsing.</param>
        /// <returns>The original entry with known properties, or the unchanged entry when no local file exists.</returns>
        Task<MusicTrack> EnrichAsync(MusicTrack track, CancellationToken cancellationToken = default);
    }
}
