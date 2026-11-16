#nullable enable

using System.ComponentModel;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Invalidates owned artwork and availability after an explicit local-file removal.</summary>
    public sealed partial class SongCardViewModel
    {
        /// <summary>Applies an exact resource removal without reusing a completed download or old decoded-cover lease.</summary>
        /// <param name="result">The current descriptor and resources removed by the workspace.</param>
        internal void ApplyMediaRemoval(TrackMediaRemovalResult result)
        {
            StopThumbnail();
            _track = result.UpdatedTrack;
            if (result.AudioRemoved)
            {
                _downloadState.Version++;
                _downloadState.IsAvailable = false;
                _downloadState.LocalPath = null;
                _downloadState.Progress = null;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Track)));
            NotifyDownloadState();
            if (_attachments > 0 && !result.CoverRemoved)
            {
                StartThumbnail();
            }
        }
    }
}
