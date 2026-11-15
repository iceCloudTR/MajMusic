#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Stores application audio controls independently of operating-system volume.</summary>
    public sealed record AudioOutputPreferences
    {
        /// <summary>Gets the application volume from zero to one hundred percent.</summary>
        public int Volume { get; init; } = 100;

        /// <summary>Gets whether application audio is muted.</summary>
        public bool Muted { get; init; }

        /// <summary>Gets the selected native device identity, or null for the system default route.</summary>
        public string? DeviceId { get; init; }
    }
}
