#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Reports an independently authorized saved account's cloud-collection synchronization result.</summary>
    public sealed record CloudCollectionSyncResult
    {
        /// <summary>Gets the endpoint that owns the synchronized account.</summary>
        public required string EndpointId { get; init; }
        /// <summary>Gets the explicitly authorized saved account.</summary>
        public required string AccountId { get; init; }
        /// <summary>Gets whether this account's collection synchronization succeeded.</summary>
        public bool Succeeded { get; init; }
        /// <summary>Gets the number of collections successfully synchronized for this account.</summary>
        public int CollectionCount { get; init; }
        /// <summary>Gets a short safe failure description without server bodies or credentials.</summary>
        public string? Error { get; init; }
    }
}
