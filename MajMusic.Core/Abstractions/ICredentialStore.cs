#nullable enable

namespace MajMusic.Core.Abstractions
{
    /// <summary>Stores application credentials in the current operating-system user's protected vault.</summary>
    public interface ICredentialStore
    {
        /// <summary>Reads one credential without enumerating other application or user secrets.</summary>
        /// <param name="key">The application-owned identity including endpoint, account and API origin.</param>
        /// <param name="cancellationToken">Cancels the operation before publication of its result.</param>
        /// <returns>The stored secret, or null when the identity has no saved credential.</returns>
        /// <exception cref="InvalidOperationException">The system vault cannot be accessed.</exception>
        Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>Creates or updates one protected credential.</summary>
        /// <param name="key">The application-owned identity including endpoint, account and API origin.</param>
        /// <param name="secret">The credential to persist; it must never be logged or written into settings.</param>
        /// <param name="cancellationToken">Cancels before the protected write begins.</param>
        /// <returns>A task representing the completed protected write.</returns>
        /// <exception cref="InvalidOperationException">The system vault cannot store the credential.</exception>
        Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default);

        /// <summary>Removes one application-owned credential, succeeding when it is already absent.</summary>
        /// <param name="key">The exact application-owned credential identity.</param>
        /// <param name="cancellationToken">Cancels before removal begins.</param>
        /// <returns>A task representing the completed removal.</returns>
        /// <exception cref="InvalidOperationException">The system vault cannot remove the credential.</exception>
        Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    }
}
