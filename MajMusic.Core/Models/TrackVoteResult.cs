#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Contains a source-confirmed chart or comment vote result.</summary>
    public sealed record TrackVoteResult
    {
        /// <summary>Gets the resulting signed score.</summary>
        public long Score { get; init; }
        /// <summary>Gets the positive vote count when returned for a chart.</summary>
        public long Upvotes { get; init; }
        /// <summary>Gets the negative vote count when returned for a chart.</summary>
        public long Downvotes { get; init; }
        /// <summary>Gets the confirmed session vote: one, zero or minus one.</summary>
        public int MyVote { get; init; }
    }
}
