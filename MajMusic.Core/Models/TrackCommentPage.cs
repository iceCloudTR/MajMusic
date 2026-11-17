#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Contains one finite source page of top-level comment threads.</summary>
    public sealed record TrackCommentPage
    {
        /// <summary>Gets the threads returned for this page.</summary>
        public IReadOnlyList<TrackComment> Items { get; init; } = Array.Empty<TrackComment>();
        /// <summary>Gets the source's total top-level thread count.</summary>
        public long Total { get; init; }
        /// <summary>Gets the one-based source page number.</summary>
        public int Page { get; init; } = 1;
        /// <summary>Gets the source-reported page size.</summary>
        public int PageSize { get; init; } = 20;
        /// <summary>Gets the source's applied sort order.</summary>
        public string Sort { get; init; } = "best";
        /// <summary>Gets whether the captured source session is authenticated.</summary>
        public bool CanInteract { get; init; }
        /// <summary>Gets whether the source reports another page after this one.</summary>
        public bool HasMore => (long)Page * PageSize < Total;
    }
}
