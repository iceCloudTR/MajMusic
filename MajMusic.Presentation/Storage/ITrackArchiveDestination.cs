#nullable enable

namespace MajMusic.Presentation.Storage
{
    /// <summary>Connects a completed portable song archive to the current host's file and share actions.</summary>
    public interface ITrackArchiveDestination
    {
        /// <summary>Gets the private staging root used for ZIP creation and deferred reads by share recipients.</summary>
        string StagingRoot { get; }

        /// <summary>Saves the completed archive to a public or explicitly chosen location.</summary>
        /// <param name="archivePath">The finished staged ZIP, with its portable display name.</param>
        /// <param name="cancellationToken">Cancels copying before publication.</param>
        /// <returns>The success message, or null when the destination was canceled.</returns>
        Task<string?> SaveAsync(string archivePath, CancellationToken cancellationToken);

        /// <summary>Hands the completed archive to the host's share mechanism.</summary>
        /// <param name="archivePath">The finished staged ZIP retained for the recipient to read.</param>
        /// <param name="cancellationToken">Cancels before handing off the ZIP.</param>
        /// <returns>The success message for the initiated handoff.</returns>
        Task<string> ShareAsync(string archivePath, CancellationToken cancellationToken);
    }
}
