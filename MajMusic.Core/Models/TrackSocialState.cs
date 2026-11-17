#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes the source's chart interactions independently of local or cloud collection membership.</summary>
    public sealed record TrackSocialState
    {
        /// <summary>Gets the source chart identity.</summary>
        public required string ChartId { get; init; }
        /// <summary>Gets the source's recorded play count.</summary>
        public long PlayCount { get; init; }
        /// <summary>Gets the chart's signed vote score.</summary>
        public long Score { get; init; }
        /// <summary>Gets the number of positive chart votes.</summary>
        public long Upvotes { get; init; }
        /// <summary>Gets the number of negative chart votes.</summary>
        public long Downvotes { get; init; }
        /// <summary>Gets the total number of chart comments.</summary>
        public long CommentCount { get; init; }
        /// <summary>Gets the current session's vote: one, zero or minus one.</summary>
        public int MyVote { get; init; }
        /// <summary>Gets whether this chart's endpoint currently has an authenticated session.</summary>
        public bool CanInteract { get; init; }
    }
}
