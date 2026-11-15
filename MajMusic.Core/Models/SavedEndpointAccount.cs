#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Stores a saved account's non-secret preferences within one configured endpoint.</summary>
    public sealed record SavedEndpointAccount
    {
        /// <summary>Gets the stable identity used to address this account in the system credential vault.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the account name supplied to the configured endpoint.</summary>
        public required string Username { get; init; }

        /// <summary>Gets whether the system credential vault should retain this account's password.</summary>
        public bool RememberPassword { get; init; }

        /// <summary>Gets whether this account may log in after online mode is explicitly enabled.</summary>
        public bool AutoLogin { get; init; }

        /// <summary>Gets whether this saved account may synchronize MajMusic collections with its endpoint.</summary>
        public bool UseCloudCollections { get; init; }
    }
}
