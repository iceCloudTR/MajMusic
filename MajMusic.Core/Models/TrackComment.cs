#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Represents a real source comment or a deleted placeholder retaining its replies.</summary>
    public sealed record TrackComment
    {
        /// <summary>Gets the source comment identity.</summary>
        public required string Id { get; init; }
        /// <summary>Gets the source chart identity when supplied.</summary>
        public string? ChartId { get; init; }
        /// <summary>Gets the parent comment identity, or null for a top-level comment.</summary>
        public string? ParentId { get; init; }
        /// <summary>Gets the source's thread depth.</summary>
        public int Depth { get; init; }
        /// <summary>Gets the author's account identity when present.</summary>
        public string? AuthorId { get; init; }
        /// <summary>Gets the author's canonical username, or an empty string for a placeholder.</summary>
        public string Author { get; init; } = string.Empty;
        /// <summary>Gets the optional author nickname supplied with the comment.</summary>
        public string? AuthorNickname { get; init; }
        /// <summary>Gets the optional author avatar identity without making profile or image requests.</summary>
        public string? AuthorAvatarId { get; init; }
        /// <summary>Gets the original comment text, or an empty string for a placeholder.</summary>
        public string Body { get; init; } = string.Empty;
        /// <summary>Gets the source creation timestamp.</summary>
        public DateTimeOffset CreatedAt { get; init; }
        /// <summary>Gets the last editing timestamp when supplied.</summary>
        public DateTimeOffset? EditedAt { get; init; }
        /// <summary>Gets whether the author deleted this comment.</summary>
        public bool Deleted { get; init; }
        /// <summary>Gets whether source moderation removed this comment.</summary>
        public bool Removed { get; init; }
        /// <summary>Gets the comment's signed vote score.</summary>
        public long Score { get; init; }
        /// <summary>Gets the current session's comment vote.</summary>
        public int MyVote { get; init; }
        /// <summary>Gets the source's reply count.</summary>
        public int ReplyCount { get; init; }
        /// <summary>Gets the replies included in this source thread snapshot.</summary>
        public IReadOnlyList<TrackComment> Replies { get; init; } = Array.Empty<TrackComment>();
        /// <summary>Gets whether the active session's verified canonical identity owns this comment.</summary>
        public bool IsOwn { get; init; }
        /// <summary>Gets whether the active session may request author deletion of this comment.</summary>
        public bool CanDelete => IsOwn && !Deleted && !Removed;
    }
}
