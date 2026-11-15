#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes one audio route reported by a native output backend.</summary>
    public sealed record AudioOutputDevice
    {
        /// <summary>Gets the native device identity, or null for the system default route.</summary>
        public string? Id { get; init; }

        /// <summary>Gets the user-visible device name.</summary>
        public required string Name { get; init; }
    }
}
