#nullable enable

using System.Net;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Network;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Routes chart interactions through the exact configured account session which owns each online track.</summary>
    public sealed partial class MusicWorkspace : ITrackSocialWorkspace
    {
        /// <inheritdoc />
        public Task<TrackSocialState> GetTrackSocialAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            return RunTrackSocialAsync(track, false, (provider, id, token) => provider.GetTrackSocialAsync(id, token), cancellationToken);
        }

        /// <inheritdoc />
        public Task<TrackVoteResult> SetTrackVoteAsync(MusicTrack track, int value, CancellationToken cancellationToken = default)
        {
            return RunTrackSocialAsync(track, true, (provider, id, token) => provider.SetTrackVoteAsync(id, value, token), cancellationToken);
        }

        /// <inheritdoc />
        public Task<TrackCommentPage> GetTrackCommentsAsync(MusicTrack track, int page = 1, string sort = "best", CancellationToken cancellationToken = default)
        {
            return RunTrackSocialAsync(track, false, async (provider, id, token) =>
            {
                var comments = await provider.GetTrackCommentsAsync(id, page, sort, token).ConfigureAwait(false);
                var username = await GetSocialUsernameAsync(provider, token).ConfigureAwait(false);
                return comments with
                {
                    Items = comments.Items.Select(comment => ApplySocialOwnership(comment, id, username)).ToArray(),
                    CanInteract = provider.IsLoggedIn
                };
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<TrackComment> PostTrackCommentAsync(MusicTrack track, string body, string? parentId = null, CancellationToken cancellationToken = default)
        {
            return RunTrackSocialAsync(track, true, async (provider, id, token) =>
            {
                if (parentId is not null)
                {
                    var parent = await provider.GetTrackCommentAsync(parentId, token).ConfigureAwait(false);
                    EnsureSocialCommentChart(parent, id);
                }
                var created = await provider.PostTrackCommentAsync(id, body, parentId, token).ConfigureAwait(false);
                return created with { IsOwn = !created.Deleted && !created.Removed && created.ChartId == id };
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<TrackCommentDeletion> DeleteTrackCommentAsync(MusicTrack track, string commentId, CancellationToken cancellationToken = default)
        {
            return RunTrackSocialAsync(track, true, async (provider, id, token) =>
            {
                var comment = await provider.GetTrackCommentAsync(commentId, token).ConfigureAwait(false);
                EnsureSocialCommentChart(comment, id);
                var username = await provider.GetCurrentUsernameAsync(token).ConfigureAwait(false);
                if (!ApplySocialOwnership(comment, id, username).CanDelete)
                {
                    throw new InvalidOperationException("只能删除自己的评论。");
                }
                return await provider.DeleteTrackCommentAsync(commentId, token).ConfigureAwait(false);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<TrackVoteResult> SetTrackCommentVoteAsync(MusicTrack track, string commentId, int value, CancellationToken cancellationToken = default)
        {
            return RunTrackSocialAsync(track, true, async (provider, id, token) =>
            {
                var comment = await provider.GetTrackCommentAsync(commentId, token).ConfigureAwait(false);
                EnsureSocialCommentChart(comment, id);
                return await provider.SetTrackCommentVoteAsync(commentId, value, token).ConfigureAwait(false);
            }, cancellationToken);
        }

        /// <summary>Captures the chart's exact endpoint session and rejects results after replacement or offline selection.</summary>
        /// <typeparam name="T">The typed source result.</typeparam>
        /// <param name="track">The captured online chart identity.</param>
        /// <param name="requiresLogin">Whether the operation changes source data.</param>
        /// <param name="operation">The single source operation bound to the captured session.</param>
        /// <param name="cancellationToken">Cancels discovery and the complete operation.</param>
        /// <returns>The result only while its original source and account lifetime remain active.</returns>
        /// <exception cref="InvalidOperationException">The track is local, offline, ambiguous, or its required account is not logged in.</exception>
        /// <exception cref="OperationCanceledException">The source session or online choice changes during the operation.</exception>
        private async Task<T> RunTrackSocialAsync<T>(MusicTrack track, bool requiresLogin,
            Func<MajNetClient, string, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(track);
            cancellationToken.ThrowIfCancellationRequested();
            if (track.RemoteId is not { Length: > 0 } id)
            {
                throw new InvalidOperationException("本地音乐没有在线互动。");
            }
            var connection = GetConnection(GetEndpointForTrack(track));
            using var sessionLifetime = connection.CreateCancellation(cancellationToken);
            using var onlineLifetime = _networkPolicy.CreateRequestCancellation(sessionLifetime.Token);
            var token = onlineLifetime.Token;
            token.ThrowIfCancellationRequested();
            if (requiresLogin && !connection.Provider.IsLoggedIn)
            {
                throw new InvalidOperationException("请登录该接入点的账号。");
            }
            var server = await connection.Provider.GetServerAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (server.Id != track.SourceId)
            {
                throw new InvalidOperationException("接入点的音乐来源已更改，请重新同步音乐库。");
            }
            var result = await operation(connection.Provider, id, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }

        /// <summary>Obtains optional canonical ownership without discarding an otherwise valid public comment page.</summary>
        /// <param name="provider">The captured source session.</param>
        /// <param name="token">Cancels canonical identity discovery.</param>
        /// <returns>The verified canonical username, or null for Guest or unavailable optional profile metadata.</returns>
        private static async Task<string?> GetSocialUsernameAsync(MajNetClient provider, CancellationToken token)
        {
            if (!provider.IsLoggedIn)
            {
                return null;
            }
            try
            {
                return await provider.GetCurrentUsernameAsync(token).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (exception.StatusCode != HttpStatusCode.Unauthorized)
            {
                token.ThrowIfCancellationRequested();
                return null;
            }
            catch (JsonException)
            {
                token.ThrowIfCancellationRequested();
                return null;
            }
        }

        /// <summary>Marks only exact canonical authorship within the requested chart, preserving every reply and placeholder.</summary>
        /// <param name="comment">The source comment or placeholder.</param>
        /// <param name="chartId">The requested source chart identity.</param>
        /// <param name="username">The verified current-session username, or null.</param>
        /// <returns>The thread with source-scoped author action flags.</returns>
        private static TrackComment ApplySocialOwnership(TrackComment comment, string chartId, string? username)
        {
            return comment with
            {
                IsOwn = username is not null && comment.ChartId == chartId && string.Equals(comment.Author, username, StringComparison.Ordinal),
                Replies = comment.Replies.Select(reply => ApplySocialOwnership(reply, chartId, username)).ToArray()
            };
        }

        /// <summary>Prevents a stale or unrelated comment identity from being written through this chart's controls.</summary>
        /// <param name="comment">The source-verified comment thread root.</param>
        /// <param name="chartId">The chart selected by the caller.</param>
        /// <exception cref="InvalidOperationException">The comment does not identify the selected chart.</exception>
        private static void EnsureSocialCommentChart(TrackComment comment, string chartId)
        {
            if (comment.ChartId != chartId)
            {
                throw new InvalidOperationException("这条评论不属于当前歌曲。");
            }
        }
    }
}
