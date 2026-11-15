#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Separates read-only audio discovery from explicitly configured collection-index reads.</summary>
    public sealed class LocalDirectoryService : ILocalDirectoryService
    {
        /// <summary>Indexes original audio references and persists local metadata.</summary>
        private readonly IMusicWorkspace _workspace;
        /// <summary>Reads portable collection documents and rebinds their unavailable references.</summary>
        private readonly IMajCollectionExchange _collections;

        /// <summary>Creates a source scanner without reading any directory.</summary>
        /// <param name="workspace">The application metadata workspace.</param>
        /// <param name="collections">The compatible list exchange.</param>
        public LocalDirectoryService(IMusicWorkspace workspace, IMajCollectionExchange collections)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            ArgumentNullException.ThrowIfNull(collections);
            _workspace = workspace;
            _collections = collections;
        }

        /// <inheritdoc />
        /// <exception cref="InvalidDataException">The source role, identity or path is invalid.</exception>
        public async Task<DirectorySourceScanResult> ScanAsync(LocalDirectorySource source, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = LocalDirectorySettings.Normalize(new AppSettings { LocalDirectorySources = [source] }).LocalDirectorySources.Single();
            try
            {
                if (normalized.Kind == LocalDirectoryKind.MusicLibrary)
                {
                    var music = await _workspace.IndexDirectoryAsync(normalized.Path, cancellationToken).ConfigureAwait(false);
                    await _collections.ReconcileUnavailableEntriesAsync(cancellationToken).ConfigureAwait(false);
                    return new DirectorySourceScanResult { Source = normalized, TrackCount = music.Tracks.Count, Issues = music.Issues };
                }

                var lists = await _collections.ImportIndexDirectoryAsync(normalized.Path, true, cancellationToken).ConfigureAwait(false);
                return new DirectorySourceScanResult
                {
                    Source = normalized,
                    PlaylistCount = lists.Playlists.Count,
                    Issues = lists.Issues.Select(issue => new LocalLibraryScanIssue { Path = issue.Path, Message = issue.Message }).ToArray()
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new DirectorySourceScanResult
                {
                    Source = normalized,
                    Issues = [new LocalLibraryScanIssue { Path = normalized.Path, Message = "The configured directory could not be read." }]
                };
            }
        }
    }
}
