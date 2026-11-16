#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Exposes the conventional MajdataPlay media location and explicit local rereading.</summary>
    public interface IMajdataPlayWorkspace
    {
        /// <summary>Gets the recommended shared root independently of a user's saved media-storage choice.</summary>
        string DefaultMajdataPlayDirectory { get; }
        /// <summary>Reads the configured media root without changing playback or remote account state.</summary>
        /// <param name="cancellationToken">Cancels local traversal.</param>
        /// <returns>The local songs discovered in the selected media root.</returns>
        Task<LocalLibraryScanResult> RefreshMajdataPlayAsync(CancellationToken cancellationToken = default);
    }
}
