#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes a completed read-only scan of a local music directory tree.</summary>
    public sealed record LocalLibraryScanResult
    {
        /// <summary>Gets the normalized absolute directory chosen by the user.</summary>
        public required string RootDirectory { get; init; }

        /// <summary>Gets the discovered music entries referencing their original files.</summary>
        public IReadOnlyList<MusicTrack> Tracks { get; init; } = Array.Empty<MusicTrack>();

        /// <summary>Gets the number of ordinary directories inspected, including the root.</summary>
        public int ScannedDirectoryCount { get; init; }

        /// <summary>Gets skipped paths and readable explanations without aborting other directories.</summary>
        public IReadOnlyList<LocalLibraryScanIssue> Issues { get; init; } = Array.Empty<LocalLibraryScanIssue>();
    }

    /// <summary>Explains why one part of a local directory scan was skipped.</summary>
    public sealed record LocalLibraryScanIssue
    {
        /// <summary>Gets the original path that could not be indexed safely.</summary>
        public required string Path { get; init; }

        /// <summary>Gets a short description of the skipped item.</summary>
        public required string Message { get; init; }
    }
}
