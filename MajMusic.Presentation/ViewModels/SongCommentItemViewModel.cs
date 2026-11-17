#nullable enable

using System.ComponentModel;
using System.Globalization;
using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Exposes one source comment row and actions scoped to the owning details selection.</summary>
    public sealed class SongCommentItemViewModel : INotifyPropertyChanged
    {
        /// <summary>Routes every action through the current source-scoped details owner.</summary>
        private readonly MainViewModel _owner;
        /// <summary>Retains the source-confirmed comment snapshot.</summary>
        private TrackComment _comment;

        /// <summary>Creates a finite display row without loading author profiles or images.</summary>
        /// <param name="owner">The details selection which owns all row actions.</param>
        /// <param name="comment">The source-confirmed comment.</param>
        /// <param name="depth">The displayed depth within the loaded thread.</param>
        internal SongCommentItemViewModel(MainViewModel owner, TrackComment comment, int depth)
        {
            _owner = owner;
            _comment = comment;
            Depth = depth;
            ReplyCommand = owner.CreateSongSocialCommand(() =>
            {
                owner.ReplyToSongComment(this);
                return Task.CompletedTask;
            }, () => CanReply);
            DeleteCommand = owner.CreateSongSocialCommand(() => owner.DeleteSongCommentAsync(this), () => CanDelete);
            ToggleLikeCommand = owner.CreateSongSocialCommand(() => owner.ToggleSongCommentLikeAsync(this), () => CanReply);
        }

        /// <summary>Occurs when this row's vote or action availability changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>Gets the source comment identity.</summary>
        public string Id => _comment.Id;
        /// <summary>Gets the canonical author username.</summary>
        public string Author => string.IsNullOrWhiteSpace(_comment.Author) ? "—" : _comment.Author;
        /// <summary>Gets plain text or the source's deleted-placeholder label.</summary>
        public string Content => IsDeleted ? "这条评论已删除" : _comment.Body;
        /// <summary>Gets the creation time formatted in the user's local time.</summary>
        public string PostedAtText => _comment.CreatedAt == default ? string.Empty : _comment.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
        /// <summary>Gets the displayed reply depth.</summary>
        public int Depth { get; }
        /// <summary>Gets whether this is a deleted or moderated placeholder.</summary>
        public bool IsDeleted => _comment.Deleted || _comment.Removed;
        /// <summary>Gets whether the current details account may reply or vote.</summary>
        public bool CanReply => !IsDeleted && _owner.CanLikeSong;
        /// <summary>Gets stable row action visibility independently of transient request activity.</summary>
        public bool CanInteract => !IsDeleted && _owner.CanComposeSongComment;
        /// <summary>Gets whether the source confirms this account owns the current undeleted comment.</summary>
        public bool IsOwn => _comment.CanDelete;
        /// <summary>Gets whether this comment belongs to the current source account.</summary>
        public bool CanDelete => _comment.CanDelete && _owner.CanLikeSong;
        /// <summary>Gets the source-confirmed signed comment score.</summary>
        public string ScoreText => _comment.Score.ToString(CultureInfo.CurrentCulture);
        /// <summary>Gets whether the current source account upvoted this comment.</summary>
        public bool IsLiked => _comment.MyVote == 1;
        /// <summary>Gets the action targeting the current composer at this comment.</summary>
        public AsyncCommand ReplyCommand { get; }
        /// <summary>Gets the verified author deletion action.</summary>
        public AsyncCommand DeleteCommand { get; }
        /// <summary>Gets the action casting or withdrawing this comment's upvote.</summary>
        public AsyncCommand ToggleLikeCommand { get; }

        /// <summary>Applies a source-confirmed vote without replacing or reordering comment rows.</summary>
        /// <param name="vote">The confirmed vote result.</param>
        internal void ApplyVote(TrackVoteResult vote)
        {
            _comment = _comment with { Score = vote.Score, MyVote = vote.MyVote };
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScoreText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLiked)));
        }

        /// <summary>Refreshes source text and flags while preserving the row identity and scroll position.</summary>
        /// <param name="comment">The latest source snapshot for the same comment.</param>
        internal void ApplyComment(TrackComment comment)
        {
            _comment = comment;
            foreach (var name in new[] { nameof(Author), nameof(Content), nameof(PostedAtText), nameof(IsDeleted), nameof(ScoreText), nameof(IsLiked) })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
            RefreshAvailability();
        }

        /// <summary>Refreshes row action availability after source or mutation state changes.</summary>
        internal void RefreshAvailability()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReply)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDelete)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInteract)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOwn)));
            ReplyCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            ToggleLikeCommand.NotifyCanExecuteChanged();
        }
    }
}
