#nullable enable

using System.Text.Json;

namespace MajMusic.Core.Models
{
    /// <summary>Retains portable Maj collection references and fields that the music player does not interpret.</summary>
    public sealed record MajCollectionDefinition
    {
        /// <summary>Gets the original file name, without making it an export destination.</summary>
        public string? SourceFileName { get; init; }

        /// <summary>Gets the original read-only file path for local protection and diagnostics, never portable export.</summary>
        public string? SourceFilePath { get; init; }

        /// <summary>Gets the original playlist flag, or null when the source omitted it.</summary>
        public bool? IsPlayList { get; init; }

        /// <summary>Gets the original ordered references, including duplicates and unavailable entries.</summary>
        public IReadOnlyList<MajCollectionEntry> Entries { get; init; } = Array.Empty<MajCollectionEntry>();

        /// <summary>Gets unknown portable fields preserved for a later Maj-compatible export.</summary>
        public IReadOnlyDictionary<string, JsonElement> AdditionalProperties { get; init; } = new Dictionary<string, JsonElement>();
    }

    /// <summary>Associates one original chart-file hash with an optional current music-library entry.</summary>
    public sealed record MajCollectionEntry
    {
        /// <summary>Gets the portable chart identity, normally the Base64 MD5 of original maidata bytes.</summary>
        public required string Hash { get; init; }

        /// <summary>Gets the resolved local library identity, or null when the referenced chart is unavailable.</summary>
        public string? TrackId { get; init; }
    }
}
