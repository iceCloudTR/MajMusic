#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Imports and exports compatible Maj playlists without modifying an indexed music source.</summary>
    public interface IMajCollectionExchange
    {
        /// <summary>Reads the collection files associated with a read-only music root.</summary>
        /// <param name="directory">The music root or its majcollection directory.</param>
        /// <param name="preserveExisting">Whether to retain playlists already edited in this application.</param>
        /// <param name="cancellationToken">Cancels local reads and persistence.</param>
        /// <returns>The parsed playlists and any isolated file errors.</returns>
        Task<MajCollectionImportResult> ImportDirectoryAsync(string directory, bool preserveExisting = true, CancellationToken cancellationToken = default);

        /// <summary>Reads JSON files from an explicitly configured collection-index directory.</summary>
        /// <param name="directory">The selected index directory or a Maj root containing its index directory.</param>
        /// <param name="preserveExisting">Whether to retain local edits to already imported lists.</param>
        /// <param name="cancellationToken">Cancels reading and persistence.</param>
        /// <returns>The parsed lists and isolated file errors.</returns>
        Task<MajCollectionImportResult> ImportIndexDirectoryAsync(string directory, bool preserveExisting = true, CancellationToken cancellationToken = default)
        {
            return ImportDirectoryAsync(directory, preserveExisting, cancellationToken);
        }

        /// <summary>Binds retained unavailable references against the current music index without rereading any source JSON.</summary>
        /// <param name="cancellationToken">Cancels metadata resolution and persistence.</param>
        /// <returns>The number of lists updated with newly available entries.</returns>
        Task<int> ReconcileUnavailableEntriesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        /// <summary>Explicitly imports one compatible JSON document.</summary>
        /// <param name="filePath">The read-only source document.</param>
        /// <param name="cancellationToken">Cancels reading and persistence.</param>
        /// <returns>The imported playlist.</returns>
        Task<MusicPlaylist> ImportFileAsync(string filePath, CancellationToken cancellationToken = default);

        /// <summary>Exports a compatible document to a new file outside read-only source roots.</summary>
        /// <param name="filePath">The new destination file, which must not already exist.</param>
        /// <param name="playlist">The local playlist or favorites to export.</param>
        /// <param name="cancellationToken">Cancels serialization and publication.</param>
        /// <returns>A task representing the completed export.</returns>
        Task ExportAsync(string filePath, MusicPlaylist playlist, CancellationToken cancellationToken = default);
    }
}
