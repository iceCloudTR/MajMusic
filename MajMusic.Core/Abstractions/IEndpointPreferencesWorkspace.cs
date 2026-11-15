#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Updates independent endpoint addresses without replacing unrelated preferences.</summary>
    public interface IEndpointPreferencesWorkspace
    {
        /// <summary>Saves one endpoint's identity, name and address while retaining its current account choices.</summary>
        /// <param name="endpoint">The source identity, display name and API address.</param>
        /// <param name="cancellationToken">Cancels validation and persistence.</param>
        /// <returns>The saved configuration including its current account preferences.</returns>
        Task<EndpointConfiguration> SaveEndpointAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default);

        /// <summary>Removes one endpoint, its sessions and its remembered credentials.</summary>
        /// <param name="endpointId">The saved endpoint identity.</param>
        /// <param name="cancellationToken">Cancels credential cleanup and persistence.</param>
        /// <returns>A task representing completed removal.</returns>
        Task RemoveEndpointAsync(string endpointId, CancellationToken cancellationToken = default);
    }
}
