#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Contains audio properties obtained by parsing a file without playing it.</summary>
    public sealed record AudioMetadata
    {
        /// <summary>Gets the actual media duration, or null when the format does not provide one.</summary>
        public TimeSpan? Duration { get; init; }

        /// <summary>Gets the parsed audio stream properties.</summary>
        public AudioStreamInfo? AudioInfo { get; init; }
    }
}
