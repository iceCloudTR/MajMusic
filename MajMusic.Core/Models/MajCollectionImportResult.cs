#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Contains portable collections read from one selected local directory without writing to it.</summary>
    public sealed record MajCollectionImportResult
    {
        /// <summary>Gets the successfully read favorites and playlist snapshots.</summary>
        public IReadOnlyList<MusicPlaylist> Playlists { get; init; } = Array.Empty<MusicPlaylist>();

        /// <summary>Gets per-file failures that did not prevent reading other collection files.</summary>
        public IReadOnlyList<MajCollectionImportIssue> Issues { get; init; } = Array.Empty<MajCollectionImportIssue>();
    }

    /// <summary>Describes a portable collection file that could not be read.</summary>
    public sealed record MajCollectionImportIssue
    {
        /// <summary>Gets the absolute source path, used only for locating the reported failure.</summary>
        public required string Path { get; init; }

        /// <summary>Gets the short failure description.</summary>
        public required string Message { get; init; }
    }
}
