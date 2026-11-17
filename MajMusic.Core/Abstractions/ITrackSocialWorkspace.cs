#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Reads and changes real chart interactions through each track's independent endpoint session.</summary>
    public interface ITrackSocialWorkspace
    {
        /// <summary>Reads chart votes, plays and comments without changing collection membership.</summary>
        /// <param name="track">The online chart whose endpoint owns the request.</param>
        /// <param name="cancellationToken">Cancels the read and its source-session lifetime.</param>
        /// <returns>The source-confirmed interaction summary.</returns>
        Task<TrackSocialState> GetTrackSocialAsync(MusicTrack track, CancellationToken cancellationToken = default);

        /// <summary>Casts or withdraws the authenticated session's chart vote.</summary>
        /// <param name="track">The online chart whose endpoint owns the vote.</param>
        /// <param name="value">One for positive, zero for withdrawal, or minus one for negative.</param>
        /// <param name="cancellationToken">Cancels the write and its source-session lifetime.</param>
        /// <returns>The source-confirmed vote result.</returns>
        Task<TrackVoteResult> SetTrackVoteAsync(MusicTrack track, int value, CancellationToken cancellationToken = default);

        /// <summary>Reads one finite page of real comment threads without automatic pagination.</summary>
        /// <param name="track">The online chart whose endpoint owns the read.</param>
        /// <param name="page">The one-based comment page.</param>
        /// <param name="sort">Best, top, new, old or controversial.</param>
        /// <param name="cancellationToken">Cancels the read and its source-session lifetime.</param>
        /// <returns>The requested page with verified current-author ownership when available.</returns>
        Task<TrackCommentPage> GetTrackCommentsAsync(MusicTrack track, int page = 1, string sort = "best", CancellationToken cancellationToken = default);

        /// <summary>Publishes a comment or reply through the chart's authenticated endpoint session.</summary>
        /// <param name="track">The online chart which receives the comment.</param>
        /// <param name="body">The nonempty original comment text.</param>
        /// <param name="parentId">The parent comment identity, or null for a new thread.</param>
        /// <param name="cancellationToken">Cancels the write and its source-session lifetime.</param>
        /// <returns>The comment created by the source.</returns>
        Task<TrackComment> PostTrackCommentAsync(MusicTrack track, string body, string? parentId = null, CancellationToken cancellationToken = default);

        /// <summary>Deletes only a verified current-author comment belonging to this chart.</summary>
        /// <param name="track">The online chart which owns the comment.</param>
        /// <param name="commentId">The comment identity to verify and delete.</param>
        /// <param name="cancellationToken">Cancels verification and deletion.</param>
        /// <returns>The confirmed physical or placeholder deletion result.</returns>
        Task<TrackCommentDeletion> DeleteTrackCommentAsync(MusicTrack track, string commentId, CancellationToken cancellationToken = default);

        /// <summary>Casts or withdraws a comment vote after verifying the chart identity.</summary>
        /// <param name="track">The online chart which owns the comment.</param>
        /// <param name="commentId">The comment identity to verify and vote on.</param>
        /// <param name="value">One for positive, zero for withdrawal, or minus one for negative.</param>
        /// <param name="cancellationToken">Cancels verification and the vote.</param>
        /// <returns>The source-confirmed comment vote result.</returns>
        Task<TrackVoteResult> SetTrackCommentVoteAsync(MusicTrack track, string commentId, int value, CancellationToken cancellationToken = default);
    }
}
