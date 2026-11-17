#nullable enable

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Network;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Owns expanded song details and source-scoped interaction requests independently of playback.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Stores whether the inspected song uses the expanded details presentation.</summary>
        private bool _isSongDetailsExpanded;
        /// <summary>Retains the explicitly requested chart pane independently of discussion loading.</summary>
        private bool _isSongChartPreviewSelected;
        /// <summary>Identifies the exact chart, endpoint and account displayed by interaction controls.</summary>
        private SongSocialOwner? _songSocialOwner;
        /// <summary>Cancels requests when the inspected chart or its account changes.</summary>
        private CancellationTokenSource? _songSocialCancellation;
        /// <summary>Rejects late reads and writes from a previous details selection.</summary>
        private long _songSocialVersion;
        /// <summary>Retains the last source-confirmed interaction summary for this selection.</summary>
        private TrackSocialState? _songSocialState;
        /// <summary>Retains the last finite comment page and its continuation.</summary>
        private TrackCommentPage? _songCommentPage;
        /// <summary>Retains loaded top-level comment threads before flattening them for virtualization.</summary>
        private readonly List<TrackComment> _songCommentThreads = new();
        /// <summary>Stores the unsent text for the currently displayed chart and account.</summary>
        private string _songCommentDraft = string.Empty;
        /// <summary>Stores the explicit reply target in the current chart.</summary>
        private SongCommentItemViewModel? _songReplyTarget;
        /// <summary>Stores a details-local request failure or action result.</summary>
        private string _songSocialStatus = string.Empty;
        /// <summary>Indicates that an interaction summary or comment page is loading.</summary>
        private bool _isSongSocialLoading;
        /// <summary>Prevents overlapping mutations within the currently displayed source scope.</summary>
        private bool _isSongSocialMutating;
        /// <summary>Indicates that the current composer is submitting its captured text.</summary>
        private bool _isSongCommentSending;
        /// <summary>Retains an observed automatic read for deterministic shutdown and regression checks.</summary>
        private Task _songSocialUpdate = Task.CompletedTask;

        /// <summary>Gets or sets whether the selected song fills the details presentation.</summary>
        public bool IsSongDetailsExpanded
        {
            get => _isSongDetailsExpanded;
            set
            {
                if (SetField(ref _isSongDetailsExpanded, value))
                {
                    RefreshSongSocialCommands();
                    if (value)
                    {
                        QueueSongSocialRefresh();
                    }
                    else
                    {
                        IsSongChartPreviewSelected = false;
                        CancelSongSocialRequests();
                        NotifySongSocialBindings();
                    }
                }
            }
        }

        /// <summary>Gets the action opening the selected song's expanded details.</summary>
        public AsyncCommand OpenExpandedSongDetailsCommand { get; private set; } = null!;
        /// <summary>Gets the action returning to the compact details presentation.</summary>
        public AsyncCommand CloseExpandedSongDetailsCommand { get; private set; } = null!;
        /// <summary>Gets the action opening the chart preview controls for the inspected song.</summary>
        public AsyncCommand OpenSongChartPreviewCommand { get; private set; } = null!;
        /// <summary>Gets the action returning the expanded center pane to source comments.</summary>
        public AsyncCommand ShowSongCommentsCommand { get; private set; } = null!;
        /// <summary>Gets or sets whether expanded details display the chart viewport.</summary>
        public bool IsSongChartPreviewSelected
        {
            get => _isSongChartPreviewSelected;
            set
            {
                if (SetField(ref _isSongChartPreviewSelected, value) && !value)
                {
                    CancelMajView();
                    MajViewStatus = string.Empty;
                }
            }
        }
        /// <summary>Gets the independent chart upvote action, separate from favorites.</summary>
        public AsyncCommand ToggleSongLikeCommand { get; private set; } = null!;
        /// <summary>Gets the action reloading a finite source summary and first comment page.</summary>
        public AsyncCommand RefreshSongSocialCommand { get; private set; } = null!;
        /// <summary>Gets the next finite comment page action.</summary>
        public AsyncCommand LoadMoreSongCommentsCommand { get; private set; } = null!;
        /// <summary>Gets the action posting the captured composer text to its original chart.</summary>
        public AsyncCommand PostSongCommentCommand { get; private set; } = null!;
        /// <summary>Gets the action clearing the explicit reply target.</summary>
        public AsyncCommand CancelSongReplyCommand { get; private set; } = null!;
        /// <summary>Gets the flat source-confirmed comment rows used by virtualized views.</summary>
        public ObservableCollection<SongCommentItemViewModel> SongComments { get; } = new();
        /// <summary>Gets completion of the current automatically requested details read.</summary>
        public Task SongSocialUpdate => _songSocialUpdate;
        /// <summary>Gets whether the inspected song can expose live source interactions.</summary>
        public bool IsSongSocialVisible => OnlineEnabled && SelectedTrack?.RemoteId is not null && _workspace is ITrackSocialWorkspace;
        /// <summary>Gets whether this source session can change chart or comment votes.</summary>
        public bool CanLikeSong => IsSongSocialVisible && !_isSongSocialLoading && !_isSongSocialMutating && !_isDisposing
            && _songSocialState is { CanInteract: true } && CurrentSongSocialSession()?.IsLoggedIn == true;
        /// <summary>Gets whether the current source account has upvoted this chart.</summary>
        public bool IsSongLiked => _songSocialState?.MyVote == 1;
        /// <summary>Gets the positive vote label without fabricating a count before the first successful read.</summary>
        public string SongLikeText => _songSocialState is { } state ? state.Upvotes.ToString(CultureInfo.CurrentCulture) : "—";
        /// <summary>Gets the source comment-count label.</summary>
        public string SongCommentCountText => (_songSocialState?.CommentCount.ToString(CultureInfo.CurrentCulture) ?? "—") + " 条评论";
        /// <summary>Gets the account used by this chart's source, independently of the library source picker.</summary>
        public string SongSocialAccountText => CurrentSongSocialSession()?.DisplayName ?? "Guest";
        /// <summary>Gets whether source comments or summary are currently loading.</summary>
        public bool IsSongSocialLoading => _isSongSocialLoading;
        /// <summary>Gets whether the composer is currently submitting its captured comment.</summary>
        public bool IsSongCommentSending => _isSongCommentSending;
        /// <summary>Gets whether the server reports another finite page after the displayed one.</summary>
        public bool HasMoreSongComments => IsSongSocialVisible && _songCommentPage?.HasMore == true;
        /// <summary>Gets or sets the current chart's unsent plain text.</summary>
        public string SongCommentDraft
        {
            get => _songCommentDraft;
            set
            {
                if (SetField(ref _songCommentDraft, value ?? string.Empty))
                {
                    NotifySongSocialBindings();
                }
            }
        }
        /// <summary>Gets whether the current composer can publish within the source's Unicode text limit.</summary>
        public bool CanPostSongComment => CanLikeSong && !string.IsNullOrWhiteSpace(SongCommentDraft)
            && SongCommentDraft.Trim().EnumerateRunes().Count() <= 10000;
        /// <summary>Gets stable composer visibility independently of draft text and transient request activity.</summary>
        public bool CanComposeSongComment => IsSongSocialVisible && !_isDisposing
            && _songSocialState is { CanInteract: true } && CurrentSongSocialSession()?.IsLoggedIn == true;
        /// <summary>Gets the current reply-target label.</summary>
        public string ReplyingToText => _songReplyTarget is { } target ? "回复 " + target.Author : string.Empty;
        /// <summary>Gets whether the composer targets a specific comment.</summary>
        public bool HasSongReplyTarget => _songReplyTarget is not null;
        /// <summary>Gets a brief result or error local to song details.</summary>
        public string SongSocialStatus => _songSocialStatus;
        /// <summary>Gets whether details have a brief source result to display.</summary>
        public bool HasSongSocialStatus => !string.IsNullOrWhiteSpace(_songSocialStatus);

        /// <summary>Creates details commands without occupying the application's playback or navigation busy state.</summary>
        private void InitializeSongSocialCommands()
        {
            OpenExpandedSongDetailsCommand = CreateSongSocialCommand(() =>
            {
                IsSongChartPreviewSelected = false;
                IsSongDetailsExpanded = true;
                return Task.CompletedTask;
            }, () => SelectedTrack is not null);
            CloseExpandedSongDetailsCommand = CreateSongSocialCommand(() =>
            {
                IsSongDetailsExpanded = false;
                return Task.CompletedTask;
            });
            OpenSongChartPreviewCommand = CreateSongSocialCommand(() =>
            {
                IsSongChartPreviewSelected = true;
                IsSongDetailsExpanded = true;
                return Task.CompletedTask;
            }, () => SelectedTrack is not null);
            ShowSongCommentsCommand = CreateSongSocialCommand(() =>
            {
                IsSongChartPreviewSelected = false;
                return Task.CompletedTask;
            }, () => SelectedTrack is not null);
            ToggleSongLikeCommand = CreateSongSocialCommand(ToggleSongLikeAsync, () => CanLikeSong);
            RefreshSongSocialCommand = CreateSongSocialCommand(() => LoadSongSocialAsync(false), () => IsSongSocialVisible && !_isSongSocialLoading && !_isSongSocialMutating);
            LoadMoreSongCommentsCommand = CreateSongSocialCommand(() => LoadSongSocialAsync(true), () => HasMoreSongComments && !_isSongSocialLoading && !_isSongSocialMutating);
            PostSongCommentCommand = CreateSongSocialCommand(PostSongCommentAsync, () => CanPostSongComment);
            CancelSongReplyCommand = CreateSongSocialCommand(() =>
            {
                _songReplyTarget = null;
                NotifySongSocialBindings();
                return Task.CompletedTask;
            }, () => HasSongReplyTarget && !_isSongCommentSending);
        }

        /// <summary>Builds a source-scoped operation observed by the owning view lifetime.</summary>
        /// <param name="action">The action to register and await.</param>
        /// <param name="canExecute">An optional source availability check.</param>
        /// <returns>The observed asynchronous command.</returns>
        internal AsyncCommand CreateSongSocialCommand(Func<Task> action, Func<bool>? canExecute = null)
        {
            return new AsyncCommand(() => RunTrackedAsync(action), ReportSongSocialError,
                () => !_isDisposing && (canExecute?.Invoke() ?? true));
        }

        /// <summary>Refreshes source ownership when selection, online permission or an account changes.</summary>
        private void RefreshSongSocialCommands()
        {
            if (SelectedTrack is null && _isSongDetailsExpanded)
            {
                _isSongDetailsExpanded = false;
                OnPropertyChanged(nameof(IsSongDetailsExpanded));
            }
            var owner = CaptureSongSocialOwner();
            if (owner != _songSocialOwner)
            {
                CancelSongSocialRequests();
                _songSocialOwner = owner;
                _songSocialState = null;
                _songCommentPage = null;
                _songCommentThreads.Clear();
                SongComments.Clear();
                _songCommentDraft = string.Empty;
                _songReplyTarget = null;
                _songSocialStatus = string.Empty;
                if (IsSongDetailsExpanded && owner is not null && !_isDisposing)
                {
                    QueueSongSocialRefresh();
                }
            }
            NotifySongSocialBindings();
        }

        /// <summary>Captures the selected chart's saved source and current account without issuing a request.</summary>
        /// <returns>The current interaction scope, or null for local or offline details.</returns>
        private SongSocialOwner? CaptureSongSocialOwner()
        {
            if (!IsSongSocialVisible || SelectedTrack is not { } track)
            {
                return null;
            }
            var endpoint = _settings.Endpoints.FirstOrDefault(item => item.Id == track.EndpointId);
            var session = CurrentSongSocialSession();
            return new SongSocialOwner(track.Id, track.SourceId, track.EndpointId!, track.RemoteId!, endpoint?.BaseUri.AbsoluteUri,
                session?.IsLoggedIn == true, session?.Username);
        }

        /// <summary>Reads the session belonging to the inspected chart rather than the active library filter.</summary>
        /// <returns>The chart's current endpoint session, when available.</returns>
        private EndpointSession? CurrentSongSocialSession()
        {
            return _onlineWorkspace?.GetEndpointSessions().FirstOrDefault(item => item.EndpointId == SelectedTrack?.EndpointId);
        }

        /// <summary>Schedules one observed automatic read after opening or changing expanded details.</summary>
        private void QueueSongSocialRefresh()
        {
            if (!IsSongDetailsExpanded || !IsSongSocialVisible || _isDisposing || _isSongSocialLoading)
            {
                return;
            }
            _songSocialUpdate = RunTrackedAsync(async () =>
            {
                try
                {
                    await LoadSongSocialAsync(false);
                }
                catch (OperationCanceledException)
                {
                    // A closed details view or a different source owns subsequent results.
                }
                catch (Exception exception)
                {
                    ReportSongSocialError(exception);
                }
            });
        }

        /// <summary>Loads one source summary and comment page, publishing only to their original selection.</summary>
        /// <param name="append">Whether to append the next finite comment page.</param>
        private async Task LoadSongSocialAsync(bool append)
        {
            if (_workspace is not ITrackSocialWorkspace social || SelectedTrack is not { } track || !IsSongSocialVisible || _isSongSocialLoading)
            {
                return;
            }
            var owner = _songSocialOwner;
            var version = _songSocialVersion;
            var token = SongSocialToken();
            var page = append ? (_songCommentPage?.Page ?? 0) + 1 : 1;
            _isSongSocialLoading = true;
            _songSocialStatus = string.Empty;
            NotifySongSocialBindings();
            try
            {
                var summaryRead = social.GetTrackSocialAsync(track, token);
                var commentsRead = social.GetTrackCommentsAsync(track, page, "best", token);
                await Task.WhenAll(summaryRead, commentsRead);
                if (!OwnsSongSocialResult(owner, version))
                {
                    return;
                }
                _songSocialState = await summaryRead;
                _songCommentPage = await commentsRead;
                if (!append)
                {
                    _songCommentThreads.Clear();
                }
                foreach (var comment in _songCommentPage.Items)
                {
                    var index = _songCommentThreads.FindIndex(item => item.Id == comment.Id);
                    if (index >= 0)
                    {
                        _songCommentThreads[index] = comment;
                    }
                    else
                    {
                        _songCommentThreads.Add(comment);
                    }
                }
                PublishSongComments();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (OwnsSongSocialResult(owner, version))
                {
                    ReportSongSocialError(exception);
                }
            }
            finally
            {
                if (version == _songSocialVersion && owner == _songSocialOwner)
                {
                    _isSongSocialLoading = false;
                    RefreshSongSocialCommands();
                }
            }
        }

        /// <summary>Returns a cancellation token bound to the currently displayed chart and account.</summary>
        /// <returns>The token canceled by closing or changing the current details scope.</returns>
        private CancellationToken SongSocialToken()
        {
            _songSocialCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            return _songSocialCancellation.Token;
        }

        /// <summary>Rejects late results after account, source, selection or details lifetime changes.</summary>
        private bool OwnsSongSocialResult(SongSocialOwner? owner, long version)
        {
            return !_isDisposing && version == _songSocialVersion && owner == _songSocialOwner && owner == CaptureSongSocialOwner();
        }

        /// <summary>Invalidates in-flight reads and writes without blocking playback.</summary>
        private void CancelSongSocialRequests()
        {
            _songSocialVersion++;
            _songSocialCancellation?.Cancel();
            _songSocialCancellation?.Dispose();
            _songSocialCancellation = null;
            _isSongSocialLoading = false;
            _isSongSocialMutating = false;
            _isSongCommentSending = false;
        }

        /// <summary>Casts or withdraws one chart upvote using source-confirmed counts.</summary>
        private Task ToggleSongLikeAsync()
        {
            return MutateSongSocialAsync(async (social, track, token) =>
            {
                var result = await social.SetTrackVoteAsync(track, IsSongLiked ? 0 : 1, token);
                return () =>
                {
                    if (_songSocialState is { } state)
                    {
                        _songSocialState = state with { Score = result.Score, Upvotes = result.Upvotes, Downvotes = result.Downvotes, MyVote = result.MyVote };
                    }
                };
            });
        }

        /// <summary>Publishes only the captured draft and reply target, clearing them after confirmed success.</summary>
        private Task PostSongCommentAsync()
        {
            var text = SongCommentDraft.Trim();
            var parent = _songReplyTarget?.Id;
            return MutateSongSocialAsync(async (social, track, token) =>
            {
                await social.PostTrackCommentAsync(track, text, parent, token);
                return () =>
                {
                    if (SongCommentDraft.Trim() == text && _songReplyTarget?.Id == parent)
                    {
                        _songCommentDraft = string.Empty;
                        _songReplyTarget = null;
                    }
                    _songSocialStatus = "评论已发表";
                };
            }, refreshComments: true, sendingComment: true);
        }

        /// <summary>Targets the composer at a current displayed comment without copying its text.</summary>
        /// <param name="comment">The currently displayed reply target.</param>
        internal void ReplyToSongComment(SongCommentItemViewModel comment)
        {
            if (CanLikeSong && SongComments.Contains(comment) && !comment.IsDeleted)
            {
                _songReplyTarget = comment;
                NotifySongSocialBindings();
            }
        }

        /// <summary>Deletes a verified author-owned comment through its chart's account.</summary>
        /// <param name="comment">The currently displayed author-owned comment.</param>
        internal Task DeleteSongCommentAsync(SongCommentItemViewModel comment)
        {
            if (!SongComments.Contains(comment) || !comment.CanDelete)
            {
                return Task.CompletedTask;
            }
            return MutateSongSocialAsync(async (social, track, token) =>
            {
                var result = await social.DeleteTrackCommentAsync(track, comment.Id, token);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException("评论未删除");
                }
                return () => _songSocialStatus = "评论已删除";
            }, refreshComments: true);
        }

        /// <summary>Casts or withdraws an upvote on one current comment.</summary>
        /// <param name="comment">The displayed source comment.</param>
        internal Task ToggleSongCommentLikeAsync(SongCommentItemViewModel comment)
        {
            if (!SongComments.Contains(comment) || comment.IsDeleted)
            {
                return Task.CompletedTask;
            }
            return MutateSongSocialAsync(async (social, track, token) =>
            {
                var result = await social.SetTrackCommentVoteAsync(track, comment.Id, comment.IsLiked ? 0 : 1, token);
                return () =>
                {
                    comment.ApplyVote(result);
                    for (var index = 0; index < _songCommentThreads.Count; index++)
                    {
                        _songCommentThreads[index] = UpdateVote(_songCommentThreads[index]);
                    }
                };
                TrackComment UpdateVote(TrackComment source)
                {
                    return source.Id == comment.Id ? source with { Score = result.Score, MyVote = result.MyVote }
                        : source with { Replies = source.Replies.Select(UpdateVote).ToArray() };
                }
            });
        }

        /// <summary>Serializes one source mutation and applies its result only to its captured chart and account.</summary>
        /// <param name="mutation">Performs a source action and returns its deferred presentation update.</param>
        /// <param name="refreshComments">Whether successful completion reloads the first comment page.</param>
        /// <param name="sendingComment">Whether this action owns the composer sending indicator.</param>
        private async Task MutateSongSocialAsync(Func<ITrackSocialWorkspace, MusicTrack, CancellationToken, Task<Action>> mutation,
            bool refreshComments = false, bool sendingComment = false)
        {
            if (!CanLikeSong || _workspace is not ITrackSocialWorkspace social || SelectedTrack is not { } track)
            {
                return;
            }
            var owner = _songSocialOwner;
            var version = _songSocialVersion;
            var token = SongSocialToken();
            _isSongSocialMutating = true;
            _isSongCommentSending = sendingComment;
            _songSocialStatus = string.Empty;
            NotifySongSocialBindings();
            try
            {
                var publish = await mutation(social, track, token);
                if (OwnsSongSocialResult(owner, version))
                {
                    publish();
                    if (refreshComments)
                    {
                        var status = _songSocialStatus;
                        await LoadSongSocialAsync(false);
                        if (OwnsSongSocialResult(owner, version) && string.IsNullOrEmpty(_songSocialStatus))
                        {
                            _songSocialStatus = status;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (OwnsSongSocialResult(owner, version))
                {
                    ReportSongSocialError(exception);
                }
            }
            finally
            {
                if (version == _songSocialVersion && owner == _songSocialOwner)
                {
                    _isSongSocialMutating = false;
                    _isSongCommentSending = false;
                    RefreshSongSocialCommands();
                }
            }
        }

        /// <summary>Flattens finite source threads while retaining reply depth and deleted placeholders.</summary>
        private void PublishSongComments()
        {
            var existing = SongComments.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var rows = new List<SongCommentItemViewModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var comment in _songCommentThreads)
            {
                Append(comment, 0);
            }
            for (var index = 0; index < rows.Count; index++)
            {
                if (index < SongComments.Count && ReferenceEquals(SongComments[index], rows[index]))
                {
                    continue;
                }
                var previous = SongComments.IndexOf(rows[index]);
                if (previous >= 0)
                {
                    SongComments.Move(previous, index);
                }
                else
                {
                    SongComments.Insert(index, rows[index]);
                }
            }
            while (SongComments.Count > rows.Count)
            {
                SongComments.RemoveAt(SongComments.Count - 1);
            }
            void Append(TrackComment comment, int depth)
            {
                if (depth > 16 || !seen.Add(comment.Id))
                {
                    return;
                }
                if (existing.TryGetValue(comment.Id, out var row) && row.Depth == depth)
                {
                    row.ApplyComment(comment);
                }
                else
                {
                    row = new SongCommentItemViewModel(this, comment, depth);
                }
                rows.Add(row);
                foreach (var reply in comment.Replies)
                {
                    Append(reply, depth + 1);
                }
            }
        }

        /// <summary>Publishes source state and availability changes without rebuilding the main music library.</summary>
        private void NotifySongSocialBindings()
        {
            foreach (var name in new[] { nameof(IsSongSocialVisible), nameof(CanLikeSong), nameof(IsSongLiked), nameof(SongLikeText),
                nameof(SongCommentCountText), nameof(SongSocialAccountText), nameof(IsSongSocialLoading), nameof(IsSongCommentSending),
                nameof(CanPostSongComment), nameof(CanComposeSongComment), nameof(HasMoreSongComments), nameof(SongCommentDraft), nameof(ReplyingToText),
                nameof(HasSongReplyTarget), nameof(SongSocialStatus), nameof(HasSongSocialStatus) })
            {
                OnPropertyChanged(name);
            }
            foreach (var command in new[] { OpenExpandedSongDetailsCommand, CloseExpandedSongDetailsCommand, OpenSongChartPreviewCommand, ShowSongCommentsCommand, ToggleSongLikeCommand,
                RefreshSongSocialCommand, LoadMoreSongCommentsCommand, PostSongCommentCommand, CancelSongReplyCommand })
            {
                command?.NotifyCanExecuteChanged();
            }
            foreach (var comment in SongComments)
            {
                comment.RefreshAvailability();
            }
        }

        /// <summary>Maps social request errors into the details pane without replacing player or library state.</summary>
        /// <param name="exception">The observed source failure.</param>
        private void ReportSongSocialError(Exception exception)
        {
            if (_isDisposing || exception is OperationCanceledException)
            {
                return;
            }
            _songSocialStatus = exception is BrowserVerificationRequiredException ? "接入点需要浏览器验证"
                : exception is HttpRequestException request ? request.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "请登录后重试",
                System.Net.HttpStatusCode.Forbidden => "当前账号无法执行此操作",
                System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotImplemented => "接入点暂未提供此功能",
                System.Net.HttpStatusCode.TooManyRequests => "操作太频繁，请稍后重试",
                _ => "暂时无法连接接入点，点击刷新重试"
            } : exception is InvalidOperationException ? exception.Message : "加载未完成，点击刷新重试";
            NotifySongSocialBindings();
        }

        /// <summary>Identifies one selected chart and the source account used for its interaction requests.</summary>
        private sealed record SongSocialOwner(string TrackId, string SourceId, string EndpointId, string ChartId, string? BaseUri, bool LoggedIn, string? Username);
    }
}
