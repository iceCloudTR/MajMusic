#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes the source's confirmed author-deletion result.</summary>
    public sealed record TrackCommentDeletion
    {
        /// <summary>Gets whether the source accepted deletion.</summary>
        public bool Succeeded { get; init; }
        /// <summary>Gets whether the comment was removed physically instead of becoming a thread placeholder.</summary>
        public bool Purged { get; init; }
    }
}
