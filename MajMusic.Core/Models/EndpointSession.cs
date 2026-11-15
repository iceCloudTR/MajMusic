#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes the current account of an endpoint without exposing authentication secrets.</summary>
    public sealed record EndpointSession
    {
        /// <summary>Gets the saved endpoint configuration identity.</summary>
        public required string EndpointId { get; init; }

        /// <summary>Gets whether this endpoint has an active account login.</summary>
        public bool IsLoggedIn { get; init; }

        /// <summary>Gets the account name, or null for Guest.</summary>
        public string? Username { get; init; }

        /// <summary>Gets the short account label displayed by the source selector.</summary>
        public string DisplayName => IsLoggedIn ? Username ?? "已登录" : "Guest";
    }
}
