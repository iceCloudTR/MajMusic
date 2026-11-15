#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Provides optional progress for explicit audio resolution independently of covers and metadata probes.</summary>
    public interface IAudioDownloadProgressSource
    {
        /// <summary>Gets the most recently published audio operation state, or null before an audio request.</summary>
        AudioDownloadProgress? CurrentAudioDownloadProgress { get; }
        /// <summary>Occurs when an explicit audio request prepares, receives bytes or reaches its terminal state.</summary>
        event EventHandler<AudioDownloadProgress>? AudioDownloadProgressChanged;
    }
}
