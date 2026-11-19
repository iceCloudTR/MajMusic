#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Prepares a separate MajView session using the selected song's resolved media.</summary>
    public interface IMajViewPreviewHost
    {
        /// <summary>Creates a browser preview scoped to the selected song.</summary>
        /// <param name="track">The exact song to preview.</param>
        /// <param name="includeVideo">Whether to use its PV when available.</param>
        /// <param name="cancellationToken">Cancels before opening the session.</param>
        /// <returns>The prepared transient preview session.</returns>
        Task<IMajViewPreviewSession> CreateMajViewPreviewAsync(MusicTrack track, bool includeVideo,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Owns one loopback preview and its access to the chosen song.</summary>
    public interface IMajViewPreviewSession : IAsyncDisposable
    {
        /// <summary>Gets the address to open using the device browser.</summary>
        Uri Address { get; }

        /// <summary>Publishes renderer feedback from the session thread.</summary>
        event Action<ChartFrame>? FrameReceived;

        /// <summary>Replaces any unconsumed transport request with the latest native intent.</summary>
        void SetIntent(ChartIntent intent);
    }
}
