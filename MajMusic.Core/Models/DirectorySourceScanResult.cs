#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes one explicitly requested scan according to the configured directory role.</summary>
    public sealed record DirectorySourceScanResult
    {
        /// <summary>Gets the scanned source configuration.</summary>
        public required LocalDirectorySource Source { get; init; }

        /// <summary>Gets the number of music entries indexed by a music-library source.</summary>
        public int TrackCount { get; init; }

        /// <summary>Gets the number of portable lists read by a collection-index source.</summary>
        public int PlaylistCount { get; init; }

        /// <summary>Gets isolated path failures without changing any source files.</summary>
        public IReadOnlyList<LocalLibraryScanIssue> Issues { get; init; } = Array.Empty<LocalLibraryScanIssue>();
    }
}
