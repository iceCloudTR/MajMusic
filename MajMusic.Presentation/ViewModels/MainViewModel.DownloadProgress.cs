#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Routes audio transfer notifications to stable song-card presentations.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Observes explicit audio transfers independently of cover and metadata requests.</summary>
        private IAudioDownloadProgressSource? _audioDownloadSource;

        /// <summary>Subscribes to optional transfer reporting without starting a resource request.</summary>
        private void InitializeAudioDownloadProgress()
        {
            _audioDownloadSource = _workspace as IAudioDownloadProgressSource;
            if (_audioDownloadSource is not null)
            {
                _audioDownloadSource.AudioDownloadProgressChanged += OnAudioDownloadProgressChanged;
            }
        }

        /// <summary>Releases the transfer subscription before workspace shutdown.</summary>
        private void DisposeAudioDownloadProgress()
        {
            if (_audioDownloadSource is not null)
            {
                _audioDownloadSource.AudioDownloadProgressChanged -= OnAudioDownloadProgressChanged;
                _audioDownloadSource = null;
            }
        }

        /// <summary>Publishes each track's audio transfer on the presentation dispatcher.</summary>
        /// <param name="sender">The reporting audio workspace.</param>
        /// <param name="progress">The source-scoped transfer operation.</param>
        private void OnAudioDownloadProgressChanged(object? sender, AudioDownloadProgress progress)
        {
            _dispatch(() =>
            {
                if (!Volatile.Read(ref _isDisposing))
                {
                    ApplyCardDownloadProgress(progress);
                }
            });
        }
    }
}
