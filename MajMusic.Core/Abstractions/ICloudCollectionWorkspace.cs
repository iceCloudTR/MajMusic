#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Persists per-account cloud-collection permission and synchronizes only explicitly authorized accounts.</summary>
    public interface ICloudCollectionWorkspace
    {
        /// <summary>Atomically changes only one saved account's cloud-collection permission.</summary>
        /// <param name="endpoint">The endpoint identity and saved API root.</param>
        /// <param name="accountId">The saved account explicitly chosen when the checkbox was edited.</param>
        /// <param name="enabled">Whether that account may synchronize collections.</param>
        /// <param name="cancellationToken">Cancels local preference persistence.</param>
        /// <returns>The current endpoint after the account-specific preference update.</returns>
        /// <exception cref="InvalidOperationException">The saved endpoint or account no longer exists.</exception>
        Task<EndpointConfiguration> SetCloudCollectionUsageAsync(EndpointConfiguration endpoint, string accountId, bool enabled,
            CancellationToken cancellationToken = default);

        /// <summary>Synchronizes compatible local collections for independently authorized saved accounts.</summary>
        /// <param name="cancellationToken">Cancels the finite set of synchronization operations.</param>
        /// <returns>Independent safe results for the accounts considered by this synchronization.</returns>
        Task<IReadOnlyList<CloudCollectionSyncResult>> SynchronizeCloudCollectionsAsync(CancellationToken cancellationToken = default);
    }
}
