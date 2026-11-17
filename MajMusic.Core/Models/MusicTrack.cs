#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Represents a playable music entry with optional chart-preview resources.</summary>
    public sealed record MusicTrack
    {
        /// <summary>Gets the stable local identity including its source scope.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the music title.</summary>
        public required string Title { get; init; }

        /// <summary>Gets the credited artist.</summary>
        public string Artist { get; init; } = string.Empty;

        /// <summary>Gets the optional descriptive text.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Gets the search tags supplied by the source.</summary>
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

        /// <summary>Gets the local source or remote server identity.</summary>
        public string SourceId { get; init; } = "local";

        /// <summary>Gets the configured endpoint session used to retrieve this entry's resources.</summary>
        public string? EndpointId { get; init; }

        /// <summary>Gets the configured source display name, when available.</summary>
        public string? SourceName { get; init; }

        /// <summary>Gets the actual audio duration supplied by a source or local media parser.</summary>
        public TimeSpan? Duration { get; init; }

        /// <summary>Gets known audio stream properties without requiring playback.</summary>
        public AudioStreamInfo? AudioInfo { get; init; }

        /// <summary>Gets the complete local file length associated with the cached audio metadata.</summary>
        public long? AudioMetadataLength { get; init; }

        /// <summary>Gets the UTC last-write ticks associated with the cached local audio metadata.</summary>
        public long? AudioMetadataLastWriteUtcTicks { get; init; }

        /// <summary>Gets when this source revision's complete audio first became locally available.</summary>
        public DateTimeOffset? LocalMediaSavedAt { get; init; }

        /// <summary>Gets the remote resource identifier, independent of the audio digest.</summary>
        public string? RemoteId { get; init; }

        /// <summary>Gets the source revision used to refresh resource metadata.</summary>
        public string? Revision { get; init; }

        /// <summary>Gets the Base64 MD5 of the original maidata bytes used by Maj collection references.</summary>
        /// <remarks>This compatibility identity is independent of the audio content and the local path identity.</remarks>
        public string? MajChartHash { get; init; }

        /// <summary>Gets the playable audio resource.</summary>
        public required MediaResource Audio { get; init; }

        /// <summary>Gets the optional cover resource.</summary>
        public MediaResource? Cover { get; init; }

        /// <summary>Gets the original maidata reference, when indexed locally or cached after preview.</summary>
        public MediaResource? Chart { get; init; }

        /// <summary>Gets the optional preview background video reference.</summary>
        public MediaResource? Video { get; init; }

    }
}
