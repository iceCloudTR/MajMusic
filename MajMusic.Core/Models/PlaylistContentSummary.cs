#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Counts playlist members by origin without treating an unavailable reference as a known source.</summary>
    public sealed record PlaylistContentSummary
    {
        /// <summary>Gets the classification of resolved members, or empty/unresolved when none are available.</summary>
        public PlaylistContentKind Kind { get; init; }

        /// <summary>Gets the number of local-origin entries, including repeated playlist occurrences.</summary>
        public int LocalCount { get; init; }

        /// <summary>Gets the number of online-origin entries, including downloaded and repeated occurrences.</summary>
        public int OnlineCount { get; init; }

        /// <summary>Gets the number of missing library entries and unresolved portable references.</summary>
        public int UnresolvedCount { get; init; }
    }
}
