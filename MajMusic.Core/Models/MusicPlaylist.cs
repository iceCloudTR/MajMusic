#nullable enable

using System.Text.Json.Serialization;

namespace MajMusic.Core.Models
{
    /// <summary>Represents an ordered playlist referencing stable library identities.</summary>
    public sealed record MusicPlaylist
    {
        /// <summary>Gets the transient source/account label assigned by the presentation catalog.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SourceName { get; init; }

        /// <summary>Gets the stable playlist identity.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the user-visible name.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the ordered track identities without taking ownership of their files.</summary>
        public IReadOnlyList<string> TrackIds { get; init; } = Array.Empty<string>();

        /// <summary>Gets the independent endpoint session owning a cloud collection, or null for a local list.</summary>
        public string? EndpointId { get; init; }

        /// <summary>Gets the source's cloud collection identifier, or null for a local list.</summary>
        public string? RemoteId { get; init; }

        /// <summary>Gets whether collection membership belongs to the remote account rather than local storage.</summary>
        [JsonIgnore]
        public bool IsCloud => RemoteId is not null;

        /// <summary>Gets the current presentation classification, recomputed from library metadata rather than persisted.</summary>
        [JsonIgnore]
        public PlaylistContentKind ContentKind { get; init; }

        /// <summary>Gets the current presentation count of unavailable references, recomputed rather than persisted.</summary>
        [JsonIgnore]
        public int UnresolvedCount { get; init; }

        /// <summary>Gets whether this is the local favorites collection.</summary>
        public bool IsFavorite { get; init; }

        /// <summary>Gets the compatible Maj document, including unresolved references retained for round trips.</summary>
        public MajCollectionDefinition? MajCollection { get; init; }

        /// <summary>Gets the last update time in UTC.</summary>
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    }
}
