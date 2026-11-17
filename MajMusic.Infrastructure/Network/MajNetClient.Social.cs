#nullable enable

using System.Globalization;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Uses MajNet's chart-ID JSON voting and threaded-comment protocol.</summary>
    public sealed partial class MajNetClient
    {
        /// <summary>Reads public chart interactions, including this session's vote when authenticated.</summary>
        /// <param name="remoteId">The source chart identity.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>The source's chart interaction summary.</returns>
        /// <exception cref="JsonException">The source returns an invalid summary.</exception>
        public async Task<TrackSocialState> GetTrackSocialAsync(string remoteId, CancellationToken cancellationToken = default)
        {
            var id = EscapeIdentifier(remoteId);
            using var response = await SendAsync(HttpMethod.Get, $"maichart/{id}/interactsum", null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var chartId = RequiredCollectionText(root, "chartId");
            if (chartId != remoteId)
            {
                throw new JsonException("The interaction summary belongs to another chart.");
            }
            return new TrackSocialState
            {
                ChartId = chartId,
                PlayCount = SocialCount(root, "plays"),
                Score = SocialInteger(root, "score"),
                Upvotes = SocialCount(root, "upvotes"),
                Downvotes = SocialCount(root, "downvotes"),
                CommentCount = SocialCount(root, "comments"),
                MyVote = SocialVote(root),
                CanInteract = IsLoggedIn
            };
        }

        /// <summary>Sets the authenticated session's chart vote without affecting favorites.</summary>
        /// <param name="remoteId">The chart identity.</param>
        /// <param name="value">One, zero or minus one.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>The source-confirmed score and vote.</returns>
        public async Task<TrackVoteResult> SetTrackVoteAsync(string remoteId, int value, CancellationToken cancellationToken = default)
        {
            RequireSocialAccount(cancellationToken);
            ValidateSocialVote(value);
            var id = EscapeIdentifier(remoteId);
            using var content = CollectionJson(new { value });
            using var response = await SendAsync(HttpMethod.Put, $"maichart/{id}/vote", content, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseSocialVoteResult(document.RootElement, true);
        }

        /// <summary>Reads one explicitly requested page of public comment threads.</summary>
        /// <param name="remoteId">The chart identity.</param>
        /// <param name="page">The one-based source page.</param>
        /// <param name="sort">The documented thread ordering.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The finite source page.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The page is not positive.</exception>
        /// <exception cref="ArgumentException">The sort is unsupported.</exception>
        /// <exception cref="JsonException">The source page is malformed.</exception>
        public async Task<TrackCommentPage> GetTrackCommentsAsync(string remoteId, int page = 1, string sort = "best", CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
            ValidateSocialSort(sort);
            var id = EscapeIdentifier(remoteId);
            var route = string.Create(CultureInfo.InvariantCulture, $"maichart/{id}/comments?sort={sort}&page={page}");
            using var response = await SendAsync(HttpMethod.Get, route, null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var receivedPage = SocialInt32(root, "page");
            var pageSize = SocialInt32(root, "pageSize");
            var appliedSort = RequiredCollectionText(root, "sort");
            if (receivedPage != page || pageSize is < 1 or > 100 || appliedSort != sort
                || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > pageSize)
            {
                throw new JsonException("The source returned an invalid comment page.");
            }
            return new TrackCommentPage
            {
                Items = items.EnumerateArray().Select(item => ParseSocialComment(item, remoteId)).ToArray(),
                Total = SocialCount(root, "total"),
                Page = receivedPage,
                PageSize = pageSize,
                Sort = appliedSort,
                CanInteract = IsLoggedIn
            };
        }

        /// <summary>Creates a comment or reply once, without retrying an ambiguous write.</summary>
        /// <param name="remoteId">The chart identity.</param>
        /// <param name="body">The original nonempty text, up to ten thousand Unicode scalars.</param>
        /// <param name="parentId">The parent identity, or null for a top-level thread.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>The actual source-created comment.</returns>
        /// <exception cref="ArgumentException">The text or parent identity is invalid.</exception>
        public async Task<TrackComment> PostTrackCommentAsync(string remoteId, string body, string? parentId = null, CancellationToken cancellationToken = default)
        {
            RequireSocialAccount(cancellationToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(body);
            if (body.Trim().EnumerateRunes().Count() > 10000)
            {
                throw new ArgumentException("评论最多包含10000个字符。", nameof(body));
            }
            var id = EscapeIdentifier(remoteId);
            var data = new Dictionary<string, string> { ["body"] = body };
            if (parentId is not null)
            {
                EscapeIdentifier(parentId);
                data["parentId"] = parentId;
            }
            using var content = CollectionJson(data);
            using var response = await SendAsync(HttpMethod.Post, $"maichart/{id}/comments", content, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseSocialComment(document.RootElement, remoteId);
        }

        /// <summary>Reads a comment and its returned replies for chart and ownership verification.</summary>
        /// <param name="commentId">The source comment identity.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The source comment thread root.</returns>
        public async Task<TrackComment> GetTrackCommentAsync(string commentId, CancellationToken cancellationToken = default)
        {
            var id = EscapeIdentifier(commentId);
            using var response = await SendAsync(HttpMethod.Get, $"comment/{id}?sort=best", null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("item", out var item))
            {
                throw new JsonException("The source did not return a comment thread.");
            }
            var comment = ParseSocialComment(item, null);
            if (comment.Id != commentId)
            {
                throw new JsonException("The source returned another comment thread.");
            }
            return comment;
        }

        /// <summary>Requests deletion after the workspace verifies current-author ownership.</summary>
        /// <param name="commentId">The verified comment identity.</param>
        /// <param name="cancellationToken">Cancels deletion.</param>
        /// <returns>The confirmed deletion mode.</returns>
        public async Task<TrackCommentDeletion> DeleteTrackCommentAsync(string commentId, CancellationToken cancellationToken = default)
        {
            RequireSocialAccount(cancellationToken);
            var id = EscapeIdentifier(commentId);
            using var response = await SendAsync(HttpMethod.Delete, $"comment/{id}", null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return new TrackCommentDeletion { Succeeded = SocialBoolean(document.RootElement, "ok"), Purged = SocialBoolean(document.RootElement, "purged") };
        }

        /// <summary>Sets a comment vote after the workspace verifies the chart identity.</summary>
        /// <param name="commentId">The verified comment identity.</param>
        /// <param name="value">One, zero or minus one.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>The confirmed comment score and vote.</returns>
        public async Task<TrackVoteResult> SetTrackCommentVoteAsync(string commentId, int value, CancellationToken cancellationToken = default)
        {
            RequireSocialAccount(cancellationToken);
            ValidateSocialVote(value);
            var id = EscapeIdentifier(commentId);
            using var content = CollectionJson(new { value });
            using var response = await SendAsync(HttpMethod.Put, $"comment/{id}/vote", content, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseSocialVoteResult(document.RootElement, false);
        }

        /// <summary>Rejects account-only writes before constructing a network request.</summary>
        /// <param name="token">The caller's cancellation.</param>
        private void RequireSocialAccount(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RequireLogin();
        }

        /// <summary>Validates the three source-defined vote values.</summary>
        /// <param name="value">The requested vote.</param>
        /// <exception cref="ArgumentOutOfRangeException">The vote is undefined.</exception>
        private static void ValidateSocialVote(int value)
        {
            if (value is not (-1 or 0 or 1))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        /// <summary>Validates a source-defined thread ordering without constructing arbitrary query parameters.</summary>
        /// <param name="sort">The requested ordering.</param>
        /// <exception cref="ArgumentException">The ordering is unsupported.</exception>
        private static void ValidateSocialSort(string sort)
        {
            if (sort is not ("best" or "top" or "new" or "old" or "controversial"))
            {
                throw new ArgumentException("The comment sort is unsupported.", nameof(sort));
            }
        }

        /// <summary>Parses the exact confirmed vote shape used by chart and comment endpoints.</summary>
        /// <param name="root">The source result.</param>
        /// <param name="chart">Whether positive and negative chart totals are required.</param>
        /// <returns>The typed confirmed result.</returns>
        private static TrackVoteResult ParseSocialVoteResult(JsonElement root, bool chart)
        {
            return new TrackVoteResult
            {
                Score = SocialInteger(root, "score"),
                MyVote = SocialVote(root),
                Upvotes = chart ? SocialCount(root, "upvotes") : 0,
                Downvotes = chart ? SocialCount(root, "downvotes") : 0
            };
        }

        /// <summary>Parses real comment data and preserves deleted placeholders without inferring account ownership.</summary>
        /// <param name="item">The source comment object.</param>
        /// <param name="expectedChart">The requested chart identity, or null for standalone verification.</param>
        /// <returns>The comment and included replies.</returns>
        /// <exception cref="JsonException">A comment identity, timestamp or chart scope is invalid.</exception>
        private static TrackComment ParseSocialComment(JsonElement item, string? expectedChart)
        {
            var chart = SocialOptionalText(item, "chartId");
            if (chart is not null && expectedChart is not null && chart != expectedChart)
            {
                throw new JsonException("The source returned comments belonging to another chart.");
            }
            var author = SocialOptionalText(item, "author") ?? string.Empty;
            string? nickname = null;
            string? avatar = null;
            if (item.TryGetProperty("authorProfile", out var profile) && profile.ValueKind == JsonValueKind.Object)
            {
                nickname = SocialOptionalText(profile, "nickname");
                avatar = SocialOptionalText(profile, "avatarId");
            }
            var replies = Array.Empty<TrackComment>();
            if (item.TryGetProperty("replies", out var children) && children.ValueKind != JsonValueKind.Null)
            {
                if (children.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("The source returned invalid comment replies.");
                }
                replies = children.EnumerateArray().Select(child => ParseSocialComment(child, expectedChart ?? chart)).ToArray();
            }
            return new TrackComment
            {
                Id = RequiredCollectionText(item, "id"),
                ChartId = chart,
                ParentId = SocialOptionalText(item, "parentId"),
                Depth = SocialInt32(item, "depth"),
                AuthorId = SocialOptionalText(item, "authorId"),
                Author = author,
                AuthorNickname = nickname,
                AuthorAvatarId = avatar,
                Body = SocialOptionalText(item, "body") ?? string.Empty,
                CreatedAt = SocialTimestamp(item, "createdAt") ?? throw new JsonException("The comment has no creation timestamp."),
                EditedAt = SocialTimestamp(item, "editedAt"),
                Deleted = SocialBoolean(item, "deleted"),
                Removed = SocialBoolean(item, "removed"),
                Score = SocialInteger(item, "score"),
                MyVote = SocialVote(item),
                ReplyCount = SocialInt32(item, "replyCount"),
                Replies = replies
            };
        }

        /// <summary>Reads a required signed JSON integer.</summary>
        /// <param name="item">The containing source object.</param>
        /// <param name="name">The required property name.</param>
        /// <returns>The exact integer value.</returns>
        private static long SocialInteger(JsonElement item, string name)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            {
                throw new JsonException("The source returned an invalid interaction number.");
            }
            return result;
        }

        /// <summary>Reads a required nonnegative JSON count.</summary>
        /// <param name="item">The containing source object.</param>
        /// <param name="name">The required property name.</param>
        /// <returns>The nonnegative count.</returns>
        private static long SocialCount(JsonElement item, string name)
        {
            var count = SocialInteger(item, name);
            return count >= 0 ? count : throw new JsonException("The source returned a negative interaction count.");
        }

        /// <summary>Reads a nonnegative count which fits a finite client collection.</summary>
        /// <param name="item">The containing source object.</param>
        /// <param name="name">The required property name.</param>
        /// <returns>The nonnegative thirty-two-bit count.</returns>
        private static int SocialInt32(JsonElement item, string name)
        {
            var count = SocialCount(item, name);
            return count <= int.MaxValue ? (int)count : throw new JsonException("The source returned an oversized interaction count.");
        }

        /// <summary>Reads a source-confirmed session vote.</summary>
        /// <param name="item">The containing source result.</param>
        /// <returns>One, zero or minus one.</returns>
        private static int SocialVote(JsonElement item)
        {
            var vote = SocialInteger(item, "myVote");
            return vote is -1 or 0 or 1 ? (int)vote : throw new JsonException("The source returned an invalid session vote.");
        }

        /// <summary>Reads a required JSON boolean without coercing other value kinds.</summary>
        /// <param name="item">The containing source object.</param>
        /// <param name="name">The required property name.</param>
        /// <returns>The source boolean.</returns>
        private static bool SocialBoolean(JsonElement item, string name)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JsonException("The source returned an invalid interaction flag.");
            }
            return value.GetBoolean();
        }

        /// <summary>Reads nullable source text without coercing numeric identities.</summary>
        /// <param name="item">The containing source object.</param>
        /// <param name="name">The optional property name.</param>
        /// <returns>The source text or null.</returns>
        private static string? SocialOptionalText(JsonElement item, string name)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException("The source returned invalid interaction text.");
        }

        /// <summary>Reads an optional ISO source timestamp.</summary>
        /// <param name="item">The containing comment object.</param>
        /// <param name="name">The timestamp property name.</param>
        /// <returns>The timestamp, or null when absent.</returns>
        private static DateTimeOffset? SocialTimestamp(JsonElement item, string name)
        {
            var text = SocialOptionalText(item, name);
            if (text is null)
            {
                return null;
            }
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                ? value : throw new JsonException("The source returned an invalid comment timestamp.");
        }
    }
}
