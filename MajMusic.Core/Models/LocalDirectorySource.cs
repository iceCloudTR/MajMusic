#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Defines the independent responsibility of a configured local directory.</summary>
    public enum LocalDirectoryKind
    {
        /// <summary>Indexes audio and song metadata from a directory tree.</summary>
        MusicLibrary,

        /// <summary>Reads portable favorites and playlist JSON without discovering audio.</summary>
        CollectionIndex
    }

    /// <summary>Identifies one explicit read-only local source and its role.</summary>
    public sealed record LocalDirectorySource
    {
        /// <summary>Gets the stable configuration identity.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the absolute directory path.</summary>
        public required string Path { get; init; }

        /// <summary>Gets the source role selected independently of its filesystem name.</summary>
        public LocalDirectoryKind Kind { get; init; }

        /// <summary>Gets an optional user-assigned display name.</summary>
        public string? Name { get; init; }
    }
}
