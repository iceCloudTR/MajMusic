#nullable enable

namespace MajMusic.Infrastructure.Network
{
    /// <summary>Describes a server independently of the selected reverse-proxy address.</summary>
    public sealed record RemoteServer
    {
        /// <summary>Gets the stable server identity or configured endpoint fallback.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the server's display name.</summary>
        public required string Name { get; init; }
    }

    /// <summary>Represents a read-only collection returned by a configured MajNet source.</summary>
    public sealed record RemotePlaylist
    {
        /// <summary>Gets the remote collection identity.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the collection's display name.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the collection's description.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Gets source revision hashes; these are not audio file digests.</summary>
        public IReadOnlyList<string> SourceHashes { get; init; } = Array.Empty<string>();

        /// <summary>Gets the optional source flag distinguishing ordered playlists from other chart collections.</summary>
        public bool? IsPlayList { get; init; }
    }
}
