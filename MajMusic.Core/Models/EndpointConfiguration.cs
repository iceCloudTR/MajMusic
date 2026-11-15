#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes an explicitly configured source without credentials or a built-in address.</summary>
    public sealed record EndpointConfiguration
    {
        /// <summary>Gets the stable configuration identity used to isolate sessions.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the user-visible source name.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the HTTPS API root including any required path prefix.</summary>
        public required Uri BaseUri { get; init; }

        /// <summary>Gets the last explicitly discovered server identity, when available.</summary>
        public string? ServerId { get; init; }

        /// <summary>Gets saved account preferences without passwords or session cookies.</summary>
        public IReadOnlyList<SavedEndpointAccount> Accounts { get; init; } = Array.Empty<SavedEndpointAccount>();

        /// <summary>Gets the chosen saved account identity, or null when Guest is selected.</summary>
        public string? SelectedAccountId { get; init; }
    }
}
