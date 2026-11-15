#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Persists endpoint-specific account choices and coordinates explicitly enabled automatic login.</summary>
    public interface IAccountPreferencesWorkspace
    {
        /// <summary>Saves and selects one account while keeping its optional password in the system vault.</summary>
        /// <param name="endpoint">The saved endpoint whose account is being edited.</param>
        /// <param name="username">The account name.</param>
        /// <param name="password">A new password, or null to retain an existing remembered password.</param>
        /// <param name="rememberPassword">Whether to retain the password in the system vault.</param>
        /// <param name="autoLogin">Whether to automatically log in when this account is selected and online mode is enabled.</param>
        /// <param name="cancellationToken">Cancels persistence.</param>
        /// <returns>The updated saved endpoint configuration.</returns>
        Task<EndpointConfiguration> SaveAccountAsync(EndpointConfiguration endpoint, string username, string? password,
            bool rememberPassword, bool autoLogin, CancellationToken cancellationToken = default);

        /// <summary>Persists the chosen saved account or Guest without making a network request.</summary>
        /// <param name="endpoint">The saved endpoint to update.</param>
        /// <param name="accountId">The chosen saved account identity, or null for Guest.</param>
        /// <param name="cancellationToken">Cancels persistence.</param>
        /// <returns>The updated saved endpoint configuration.</returns>
        Task<EndpointConfiguration> SelectAccountAsync(EndpointConfiguration endpoint, string? accountId,
            CancellationToken cancellationToken = default);

        /// <summary>Removes one saved account and its system-vault password.</summary>
        /// <param name="endpoint">The saved endpoint to update.</param>
        /// <param name="accountId">The account identity to remove.</param>
        /// <param name="cancellationToken">Cancels removal.</param>
        /// <returns>The updated saved endpoint configuration.</returns>
        Task<EndpointConfiguration> RemoveAccountAsync(EndpointConfiguration endpoint, string accountId,
            CancellationToken cancellationToken = default);

        /// <summary>Logs into the selected saved account using its system-vault password.</summary>
        /// <param name="endpoint">The saved endpoint to log into.</param>
        /// <param name="accountId">The selected account identity.</param>
        /// <param name="cancellationToken">Cancels password lookup or login.</param>
        /// <returns>A task representing the completed login.</returns>
        Task LoginSavedAccountAsync(EndpointConfiguration endpoint, string accountId, CancellationToken cancellationToken = default);

        /// <summary>Attempts the selected automatic accounts only while online mode is enabled.</summary>
        /// <param name="cancellationToken">Cancels the finite set of login attempts.</param>
        /// <returns>Independent safe results for each eligible saved account.</returns>
        Task<IReadOnlyList<AutoLoginResult>> AutoLoginAsync(CancellationToken cancellationToken = default);
    }
}
