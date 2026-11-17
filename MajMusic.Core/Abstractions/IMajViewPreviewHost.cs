#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Prepares a separate MajView session using the selected song's resolved media.</summary>
    public interface IMajViewPreviewHost
    {
        /// <summary>Gets whether the on-demand renderer is present in application storage.</summary>
        bool IsMajViewInstalled { get; }

        /// <summary>Creates a browser preview scoped to the selected song.</summary>
        /// <param name="track">The exact song to preview.</param>
        /// <param name="includeVideo">Whether to use its PV when available.</param>
        /// <param name="allowRuntimeDownload">Whether to install the viewer on demand.</param>
        /// <param name="cancellationToken">Cancels before opening the session.</param>
        /// <returns>The prepared transient preview session.</returns>
        Task<IMajViewPreviewSession> CreateMajViewPreviewAsync(MusicTrack track, bool includeVideo,
            bool allowRuntimeDownload, CancellationToken cancellationToken = default);
    }

    /// <summary>Owns one loopback preview and its access to the chosen song.</summary>
    public interface IMajViewPreviewSession : IAsyncDisposable
    {
        /// <summary>Gets the address to open using the device browser.</summary>
        Uri Address { get; }
    }
}
