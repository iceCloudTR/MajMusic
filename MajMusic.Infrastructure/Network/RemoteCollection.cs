#nullable enable

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Describes an owned cloud collection and its ordered chart identities when details were requested.</summary>
    public sealed record RemoteCollection
    {
        /// <summary>Gets the server's opaque collection identity.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the collection's display name.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the canonical username of its creator.</summary>
        public required string CreatedBy { get; init; }

        /// <summary>Gets the description retained by the server, or null when absent.</summary>
        public string? Description { get; init; }

        /// <summary>Gets the count reported by the collection metadata.</summary>
        public int Count { get; init; }

        /// <summary>Gets private visibility zero or public visibility one, or null on list entries without this field.</summary>
        public int? Visibility { get; init; }

        /// <summary>Gets ordered server chart IDs rather than portable chart hashes.</summary>
        public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    }
}
