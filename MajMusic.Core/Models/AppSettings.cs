#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Stores non-secret preferences; new installations perform no online requests.</summary>
    public sealed record AppSettings
    {
        /// <summary>Gets whether the user has explicitly enabled configured online sources.</summary>
        public bool OnlineEnabled { get; init; }

        /// <summary>Gets the configured endpoint selected for explicit online actions.</summary>
        public string? ActiveEndpointId { get; init; }

        /// <summary>Gets user-supplied endpoints; the default list is empty.</summary>
        public IReadOnlyList<EndpointConfiguration> Endpoints { get; init; } = Array.Empty<EndpointConfiguration>();

        /// <summary>Gets the explicitly selected read-only music roots used for rescanning and export protection.</summary>
        public IReadOnlyList<string> LocalMusicDirectories { get; init; } = Array.Empty<string>();

        /// <summary>Gets the independently configured music-library and collection-index directories.</summary>
        public IReadOnlyList<LocalDirectorySource> LocalDirectorySources { get; init; } = Array.Empty<LocalDirectorySource>();

        /// <summary>Gets the directory configuration version used to migrate legacy music roots exactly once.</summary>
        public int LocalDirectoryFormatVersion { get; init; }

        /// <summary>Gets the maximum size of automatically evictable cached resources in bytes.</summary>
        public long CacheLimitBytes { get; init; } = 1024L * 1024 * 1024;

        /// <summary>Gets persistent application-volume and native-device preferences.</summary>
        public AudioOutputPreferences AudioOutput { get; init; } = new();
    }
}
