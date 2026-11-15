#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Reports one independently configured account's automatic-login attempt without authentication secrets.</summary>
    public sealed record AutoLoginResult
    {
        /// <summary>Gets the configured endpoint identity.</summary>
        public required string EndpointId { get; init; }

        /// <summary>Gets the saved account identity selected for this attempt.</summary>
        public required string AccountId { get; init; }

        /// <summary>Gets whether this account completed login.</summary>
        public bool Succeeded { get; init; }

        /// <summary>Gets a safe user-facing error, or null when login succeeded.</summary>
        public string? Error { get; init; }
    }
}
