#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes an audio or cover file independently of its library entries.</summary>
    public sealed record MediaResource
    {
        /// <summary>Gets the remote file address, when supplied by a configured source.</summary>
        public Uri? RemoteUri { get; init; }

        /// <summary>Gets the opaque source revision used to scope address aliases while a content digest is unknown.</summary>
        /// <remarks>This value changes cache lookup context without changing the HTTP address or content-hash identity.</remarks>
        public string? CacheVersion { get; init; }

        /// <summary>Gets the managed or externally referenced local file path.</summary>
        public string? LocalPath { get; init; }

        /// <summary>Gets the selected Music media root that published a managed document resource.</summary>
        public string? ManagedStorageRoot { get; init; }

        /// <summary>Gets the SHA-256 digest of the file, when known.</summary>
        public string? Sha256 { get; init; }

        /// <summary>Gets the expected file length in bytes, when known.</summary>
        public long? Length { get; init; }
    }
}
