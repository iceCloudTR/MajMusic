#nullable enable

using System.Collections.Concurrent;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks details ownership against deliberately late social reads and writes that ignore cancellation.</summary>
    internal static class SocialPresentationTests
    {
        /// <summary>Runs chart, account and composer ownership regressions without files, native audio or network requests.</summary>
        /// <returns>A task representing all details presentation boundaries.</returns>
        public static async Task RunAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ValidateLateSelectionAsync(timeout.Token);
            await ValidateLateAccountAsync(timeout.Token);
            await ValidateCapturedComposerAsync(timeout.Token);
            await ValidateFailedComposerAsync(timeout.Token);
            await ValidateCommentPaginationAsync(timeout.Token);
        }

        /// <summary>Discards an old chart's comments while the newly inspected chart and playing queue remain independent.</summary>
        /// <param name="token">Cancels a missing controlled completion.</param>
        /// <returns>A task representing selection replacement and old-result refusal.</returns>
        private static async Task ValidateLateSelectionAsync(CancellationToken token)
        {
            var workspace = new SocialWorkspace();
            var backend = new LibraryExperienceTests.ExperienceBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: (track, lifetime) => workspace.ResolveAudioAsync(track, false, lifetime));
            await using var model = new MainViewModel(workspace, playback, static action => action());
            await PrepareAsync(model, token);
            var first = workspace.Library[0];
            var second = workspace.Library[1];
            model.SelectedTrack = first;
            await model.PlaySelectedAsync().WaitAsync(token);
            var started = Completion();
            var release = new TaskCompletionSource<TrackCommentPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.CommentsOverride = (track, account, page, _) =>
            {
                if (track.Id == first.Id)
                {
                    started.TrySetResult();
                    return release.Task;
                }
                return Task.FromResult(SocialWorkspace.Page(track, account, page));
            };
            model.IsSongDetailsExpanded = true;
            var oldRead = model.SongSocialUpdate;
            try
            {
                await started.Task.WaitAsync(token);
                TestAssert.True(model.IsSongSocialLoading && model.IsIdle && model.NextCommand.CanExecute(null),
                    "A blocked comment read must not occupy global busy state or disable the playing queue's transport.");
                model.SongCommentDraft = "Draft for first chart";
                model.SelectedTrack = second;
                await model.SongSocialUpdate.WaitAsync(token);
                TestAssert.True(model.SongCommentDraft.Length == 0 && !model.IsSongSocialLoading
                    && model.SongComments.Single().Content == "Generated second / Alice",
                    "Selecting another chart clears the previous composer and immediately publishes only the new chart's comments.");
                release.TrySetResult(SocialWorkspace.Page(first, "Alice", 1));
                await oldRead.WaitAsync(token);
                TestAssert.True(model.SelectedTrack?.Id == second.Id && model.SongComments.Single().Content == "Generated second / Alice"
                    && model.SongLikeText == "22" && !model.IsSongSocialLoading && model.CanLikeSong,
                    "An old resolver that ignores cancellation cannot overwrite the new chart's rows, counts or loading state.");
                TestAssert.True(playback.Snapshot.Track?.Id == first.Id && playback.Queue.Current?.Id == first.Id
                    && backend.OpenCount == 1 && playback.Snapshot.State == PlaybackState.Playing,
                    "Inspecting another song's social details cannot reopen audio, replace the playing song or change its queue.");
            }
            finally
            {
                release.TrySetResult(SocialWorkspace.Page(first, "Alice", 1));
                await oldRead.WaitAsync(token);
            }
        }

        /// <summary>Clears drafts and refuses a late private response when the same endpoint changes its account.</summary>
        /// <param name="token">Cancels a missing controlled completion.</param>
        /// <returns>A task representing account replacement without chart replacement.</returns>
        private static async Task ValidateLateAccountAsync(CancellationToken token)
        {
            var workspace = new SocialWorkspace();
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await PrepareAsync(model, token);
            var first = workspace.Library[0];
            model.SelectedTrack = first;
            var started = Completion();
            var release = new TaskCompletionSource<TrackCommentPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.CommentsOverride = (track, account, page, _) =>
            {
                if (account == "Alice")
                {
                    started.TrySetResult();
                    return release.Task;
                }
                return Task.FromResult(SocialWorkspace.Page(track, account, page));
            };
            model.IsSongDetailsExpanded = true;
            var oldRead = model.SongSocialUpdate;
            try
            {
                await started.Task.WaitAsync(token);
                model.SongCommentDraft = "Private old-account draft";
                model.SelectedEndpointProfile = model.EndpointProfiles.Single(profile => profile.Configuration.Id == "alice");
                model.LoginUsername = "Bob";
                model.LoginPassword = "Generated presentation fixture only";
                await ExecuteBusyCommandAsync(model, model.LoginCommand, token);
                await model.SongSocialUpdate.WaitAsync(token);
                TestAssert.True(model.SelectedTrack?.Id == first.Id && model.SongSocialAccountText == "Bob"
                    && model.SongCommentDraft.Length == 0 && model.SongComments.Single().Content == "Generated first / Bob",
                    "A new account on the same chart must own its own comments and an empty composer.");
                release.TrySetResult(SocialWorkspace.Page(first, "Alice", 1));
                await oldRead.WaitAsync(token);
                TestAssert.True(model.SongSocialAccountText == "Bob" && model.SongComments.Single().Author == "Bob" && model.CanLikeSong,
                    "An old account's late private response cannot replace the new account's rows or interaction permissions.");
                model.SongCommentDraft = "Bob draft";
                await TestAssert.ExecuteAsync(model.GuestCommand).WaitAsync(token);
                await model.SongSocialUpdate.WaitAsync(token);
                TestAssert.True(model.SelectedTrack?.Id == first.Id && model.IsSongDetailsExpanded,
                    "Clearing an account's background recommendations must leave the current library song open.");
                TestAssert.True(model.SongCommentDraft.Length == 0 && !model.CanLikeSong && !model.PostSongCommentCommand.CanExecute(null)
                    && model.SongComments is [{ Author: "Guest", CanDelete: false }],
                    "Returning this endpoint to Guest must clear unsent account text and every write capability while retaining public comments.");
            }
            finally
            {
                release.TrySetResult(SocialWorkspace.Page(first, "Alice", 1));
                await oldRead.WaitAsync(token);
            }
        }

        /// <summary>Captures a submitted draft and reply owner while preserving a newer chart's draft after late completion.</summary>
        /// <param name="token">Cancels a missing controlled completion.</param>
        /// <returns>A task representing safe in-flight comment submission.</returns>
        private static async Task ValidateCapturedComposerAsync(CancellationToken token)
        {
            var workspace = new SocialWorkspace();
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await PrepareAsync(model, token);
            var first = workspace.Library[0];
            var second = workspace.Library[1];
            model.SelectedTrack = first;
            model.IsSongDetailsExpanded = true;
            await model.SongSocialUpdate.WaitAsync(token);
            var oldRow = model.SongComments.Single();
            oldRow.ReplyCommand.Execute(null);
            TestAssert.True(model.HasSongReplyTarget, "A displayed reply action must bind the current chart's explicit parent.");
            model.SongCommentDraft = "Generated original submission";
            var started = Completion();
            var release = new TaskCompletionSource<TrackComment>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.PostOverride = (_, _) =>
            {
                started.TrySetResult();
                return release.Task;
            };
            var sending = TestAssert.ExecuteAsync(model.PostSongCommentCommand);
            try
            {
                await started.Task.WaitAsync(token);
                TestAssert.True(model.IsSongCommentSending && model.IsIdle, "Sending a source comment must leave the rest of the app available.");
                model.SelectedTrack = second;
                await model.SongSocialUpdate.WaitAsync(token);
                model.SongCommentDraft = "New chart's unsent draft";
                oldRow.ReplyCommand.Execute(null);
                TestAssert.True(!model.HasSongReplyTarget,
                    "An old displayed row cannot attach its reply target to a different chart.");
                var sent = workspace.Posts.Single();
                TestAssert.True(sent.Track.Id == first.Id && sent.Account == "Alice" && sent.Body == "Generated original submission" && sent.ParentId == oldRow.Id,
                    "The in-flight request must retain its exact original chart, account, text and parent identity.");
                release.TrySetResult(SocialWorkspace.Comment(first, "Alice"));
                await sending.WaitAsync(token);
                TestAssert.True(model.SelectedTrack?.Id == second.Id && model.SongCommentDraft == "New chart's unsent draft"
                    && !model.HasSongReplyTarget && model.SongComments.Single().Content == "Generated second / Alice" && workspace.Posts.Count == 1,
                    "Late success cannot erase the new chart's draft, publish the previous comment or resubmit it to the new chart.");
                model.IsSongDetailsExpanded = false;
                TestAssert.True(!model.IsSongSocialLoading && !model.IsSongCommentSending,
                    "Closing details must release local loading indicators without touching playback.");
            }
            finally
            {
                release.TrySetResult(SocialWorkspace.Comment(first, "Alice"));
                await sending.WaitAsync(token);
            }
        }

        /// <summary>Keeps a rejected submission editable and source errors local to details, then removes write permission offline.</summary>
        /// <param name="token">Cancels model initialization and observed commands.</param>
        /// <returns>A task representing failed-post and offline presentation boundaries.</returns>
        private static async Task ValidateFailedComposerAsync(CancellationToken token)
        {
            var workspace = new SocialWorkspace();
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await PrepareAsync(model, token);
            model.SelectedTrack = workspace.Library[0];
            model.IsSongDetailsExpanded = true;
            await model.SongSocialUpdate.WaitAsync(token);
            var status = model.Status;
            var row = model.SongComments.Single();
            row.ReplyCommand.Execute(null);
            model.SongCommentDraft = "Generated draft retained on failure";
            workspace.PostOverride = (_, _) => Task.FromException<TrackComment>(new HttpRequestException("Generated source denial", null, System.Net.HttpStatusCode.Forbidden));
            await TestAssert.ExecuteAsync(model.PostSongCommentCommand).WaitAsync(token);
            TestAssert.True(model.SongCommentDraft == "Generated draft retained on failure" && model.HasSongReplyTarget
                && model.SongComments.Single() == row && model.CanPostSongComment && !model.IsSongCommentSending
                && model.SongSocialStatus == "当前账号无法执行此操作" && model.Status == status && model.IsIdle,
                "A rejected source post must keep its draft and reply target without inserting a fake row or replacing global player status.");
            model.OnlineEnabled = false;
            await model.OnlineSettingsUpdate.WaitAsync(token);
            TestAssert.True(!model.IsSongSocialVisible && !model.CanLikeSong && !model.CanPostSongComment
                && model.SongCommentDraft.Length == 0 && model.SongComments.Count == 0 && workspace.Posts.Count == 1,
                "Disabling online mode clears private composer state and cannot send a retained draft implicitly.");
        }

        /// <summary>Appends another server page without recreating earlier rows or reverting a confirmed comment vote.</summary>
        /// <param name="token">Cancels source loading and command observation.</param>
        /// <returns>A task representing stable row identity, ordering and source-scoped pagination.</returns>
        private static async Task ValidateCommentPaginationAsync(CancellationToken token)
        {
            var workspace = new SocialWorkspace();
            var pages = new List<(string TrackId, int Page)>();
            workspace.CommentsOverride = (requestedTrack, account, page, _) =>
            {
                pages.Add((requestedTrack.Id, page));
                var comment = SocialWorkspace.Comment(requestedTrack, account);
                if (page == 2)
                {
                    comment = comment with { Id = comment.Id + "-page-two", Body = "Generated second thread", Author = "Other author", IsOwn = false };
                }
                return Task.FromResult(new TrackCommentPage { Items = [comment], Page = page, PageSize = 1, Total = 2, Sort = "best", CanInteract = true });
            };
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await PrepareAsync(model, token);
            var track = workspace.Library[0];
            model.SelectedTrack = track;
            model.IsSongDetailsExpanded = true;
            await model.SongSocialUpdate.WaitAsync(token);
            var first = model.SongComments.Single();
            TestAssert.True(model.HasMoreSongComments && !first.IsLiked && first.ScoreText == "0", "The fixture must start with one unvoted source row and another finite page.");
            await TestAssert.ExecuteAsync(first.ToggleLikeCommand).WaitAsync(token);
            TestAssert.True(first.IsLiked && first.ScoreText == "1", "A confirmed comment upvote must appear before the next page is loaded.");
            var reset = false;
            var started = false;
            var loaded = Completion();
            void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
            {
                reset |= args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset;
            }
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainViewModel.IsSongSocialLoading))
                {
                    started |= model.IsSongSocialLoading;
                    if (started && !model.IsSongSocialLoading)
                    {
                        loaded.TrySetResult();
                    }
                }
            }
            model.SongComments.CollectionChanged += OnCollectionChanged;
            model.PropertyChanged += OnChanged;
            try
            {
                TestAssert.True(model.LoadMoreSongCommentsCommand.CanExecute(null), "The next finite page must be explicitly available.");
                model.LoadMoreSongCommentsCommand.Execute(null);
                await loaded.Task.WaitAsync(token);
                TestAssert.True(model.SongComments.Count == 2 && ReferenceEquals(first, model.SongComments[0])
                    && model.SongComments[1].Id == first.Id + "-page-two" && first.IsLiked && first.ScoreText == "1" && !reset,
                    "Appending comments must retain the existing first row, order and confirmed vote without resetting the bound list.");
                var vote = workspace.CommentVotes.Single();
                TestAssert.True(!model.HasMoreSongComments && pages.SequenceEqual(new[] { (track.Id, 1), (track.Id, 2) })
                    && vote.Track.Id == track.Id && vote.CommentId == first.Id && vote.Value == 1,
                    "Both requested pages and the persisted vote must remain scoped to the original chart, without loading or voting on another song.");
            }
            finally
            {
                model.SongComments.CollectionChanged -= OnCollectionChanged;
                model.PropertyChanged -= OnChanged;
            }
        }

        /// <summary>Initializes the existing library fixture and awaits its finite asynchronous browse publication.</summary>
        /// <param name="model">The actual view model under test.</param>
        /// <param name="token">Cancels initialization.</param>
        /// <returns>A task representing ready local browsing.</returns>
        private static async Task PrepareAsync(MainViewModel model, CancellationToken token)
        {
            await model.InitializeAsync().WaitAsync(token);
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate.WaitAsync(token);
        }

        /// <summary>Observes a finite global command whose success intentionally clears an input required for its availability.</summary>
        /// <param name="model">The global busy-state owner.</param>
        /// <param name="command">The explicitly available login action.</param>
        /// <param name="token">Cancels a missing busy-state completion.</param>
        /// <returns>A task representing completion without polling or assuming the command stays enabled.</returns>
        private static async Task ExecuteBusyCommandAsync(MainViewModel model, AsyncCommand command, CancellationToken token)
        {
            TestAssert.True(command.CanExecute(null), "The requested login must be available before execution.");
            var finished = Completion();
            var started = false;
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainViewModel.IsBusy))
                {
                    started |= model.IsBusy;
                    if (started && !model.IsBusy)
                    {
                        finished.TrySetResult();
                    }
                }
            }
            model.PropertyChanged += OnChanged;
            try
            {
                command.Execute(null);
                await finished.Task.WaitAsync(token);
            }
            finally
            {
                model.PropertyChanged -= OnChanged;
            }
        }

        /// <summary>Creates a non-inline continuation barrier for deliberately late source operations.</summary>
        /// <returns>The controlled operation signal.</returns>
        private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Captures an immutable generated post target before any asynchronous completion.</summary>
        /// <param name="Track">The inspected source-owned track.</param>
        /// <param name="Account">The current source account marker.</param>
        /// <param name="Body">The captured original draft.</param>
        /// <param name="ParentId">The captured reply target.</param>
        internal sealed record Submission(MusicTrack Track, string? Account, string Body, string? ParentId);

        /// <summary>Extends the existing in-memory library with finite controllable social responses.</summary>
        internal class SocialWorkspace : LibraryExperienceTests.ExperienceWorkspace, ITrackSocialWorkspace
        {
            /// <summary>Creates two source-owned songs without native files or HTTP dependencies.</summary>
            public SocialWorkspace()
            {
                Settings = new AppSettings { OnlineEnabled = true, ActiveEndpointId = "alice", Endpoints = [SocialNetworkTests.Fixture.Endpoint("alice"), SocialNetworkTests.Fixture.Endpoint("bob")] };
                SetOnlineEnabled(true);
                Sessions["alice"] = new EndpointSession { EndpointId = "alice", Username = "Alice", IsLoggedIn = true };
                Sessions["bob"] = new EndpointSession { EndpointId = "bob", Username = "Other account", IsLoggedIn = true };
                Library.AddRange(new[] { "first", "second" }.Select(id => SocialNetworkTests.Fixture.Track("alice") with
                {
                    Id = "presentation-" + id, RemoteId = id, Title = "Generated " + id,
                    Audio = new MediaResource { LocalPath = Path.Combine(Path.GetTempPath(), "generated-social-" + id + ".mp3") }, Duration = TimeSpan.FromMinutes(2)
                }));
            }
            /// <summary>Gets or sets a response barrier that deliberately need not cooperate with cancellation.</summary>
            public Func<MusicTrack, string?, int, CancellationToken, Task<TrackCommentPage>>? CommentsOverride { get; set; }
            /// <summary>Gets or sets an explicitly controlled post completion.</summary>
            public Func<Submission, CancellationToken, Task<TrackComment>>? PostOverride { get; set; }
            /// <summary>Gets the exact captured synthetic submissions.</summary>
            public ConcurrentQueue<Submission> Posts { get; } = new();
            /// <summary>Gets source-scoped comment votes sent by real row commands.</summary>
            public ConcurrentQueue<(MusicTrack Track, string CommentId, int Value)> CommentVotes { get; } = new();
            /// <inheritdoc />
            public Task<TrackSocialState> GetTrackSocialAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new TrackSocialState { ChartId = track.RemoteId!, Upvotes = track.RemoteId == "first" ? 11 : 22,
                    CommentCount = 1, CanInteract = Sessions[track.EndpointId!].IsLoggedIn });
            }
            /// <inheritdoc />
            public Task<TrackCommentPage> GetTrackCommentsAsync(MusicTrack track, int page = 1, string sort = "best", CancellationToken cancellationToken = default)
            {
                var session = Sessions[track.EndpointId!];
                return CommentsOverride?.Invoke(track, session.Username, page, cancellationToken) ?? Task.FromResult(Page(track, session.Username, page));
            }
            /// <inheritdoc />
            public Task<TrackComment> PostTrackCommentAsync(MusicTrack track, string body, string? parentId = null, CancellationToken cancellationToken = default)
            {
                var sent = new Submission(track, Sessions[track.EndpointId!].Username, body, parentId);
                Posts.Enqueue(sent);
                return PostOverride?.Invoke(sent, cancellationToken) ?? Task.FromResult(Comment(track, sent.Account));
            }
            /// <inheritdoc />
            public Task<TrackVoteResult> SetTrackVoteAsync(MusicTrack track, int value, CancellationToken cancellationToken = default)
                => Task.FromResult(new TrackVoteResult { MyVote = value, Upvotes = 12, Score = 12 });
            /// <inheritdoc />
            public Task<TrackCommentDeletion> DeleteTrackCommentAsync(MusicTrack track, string commentId, CancellationToken cancellationToken = default)
                => Task.FromResult(new TrackCommentDeletion { Succeeded = true, Purged = true });
            /// <inheritdoc />
            public Task<TrackVoteResult> SetTrackCommentVoteAsync(MusicTrack track, string commentId, int value, CancellationToken cancellationToken = default)
            {
                CommentVotes.Enqueue((track, commentId, value));
                return Task.FromResult(new TrackVoteResult { MyVote = value, Score = value });
            }
            /// <summary>Creates a unique visible comment scoped to a captured track and account.</summary>
            /// <param name="track">The captured chart.</param>
            /// <param name="account">The captured account, or Guest.</param>
            /// <returns>The generated source row.</returns>
            public static TrackComment Comment(MusicTrack track, string? account) => new()
            {
                Id = track.RemoteId + "-" + (account ?? "Guest"), ChartId = track.RemoteId, Author = account ?? "Guest",
                Body = "Generated " + track.RemoteId + " / " + (account ?? "Guest"), IsOwn = account is not null,
                CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
            };
            /// <summary>Creates one finite page containing only its captured chart and author.</summary>
            /// <param name="track">The captured chart.</param>
            /// <param name="account">The captured account, or Guest.</param>
            /// <param name="page">The requested source page.</param>
            /// <returns>The generated finite response.</returns>
            public static TrackCommentPage Page(MusicTrack track, string? account, int page) => new()
            {
                Items = [Comment(track, account)], Page = page, PageSize = 20, Total = 1, Sort = "best", CanInteract = account is not null
            };
        }
    }
}
