#nullable enable

using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Bridges only small portable JSON documents into the existing validated Maj exchange.</summary>
    public sealed partial class MajCollectionExchange
    {
        /// <summary>Reads original provider JSON while preserving configured source trees as read-only.</summary>
        /// <param name="directory">The granted root or index URI.</param>
        /// <param name="direct">Whether all direct JSON files are indexes.</param>
        /// <param name="tracks">The current portable music identities.</param>
        /// <param name="token">Cancels enumeration and imports.</param>
        /// <returns>Parsed original lists with per-document isolated failures.</returns>
        private async Task<MajCollectionImportResult> ReadDocumentDirectoryAsync(string directory, bool direct, IReadOnlyList<MusicTrack> tracks, CancellationToken token)
        {
            var documents = _documents ?? throw new NotSupportedException("此宿主未提供文档列表访问。");
            var playlists = new List<MusicPlaylist>();
            var issues = new List<MajCollectionImportIssue>();
            await foreach (var file in documents.EnumerateCollectionFilesAsync(directory, direct, token).ConfigureAwait(false))
            {
                try
                {
                    playlists.Add(await ReadDocumentAsync(file, tracks, token).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    issues.Add(new MajCollectionImportIssue { Path = file.Reference, Message = "The selected collection document could not be read." });
                }
            }

            return new MajCollectionImportResult { Playlists = playlists, Issues = issues };
        }

        /// <summary>Temporarily copies one bounded JSON document using a stable identity, never any music files.</summary>
        /// <param name="file">The original provider reference and display name.</param>
        /// <param name="tracks">The indexed portable song identities.</param>
        /// <param name="token">Cancels document reading and validation.</param>
        /// <returns>The compatible playlist retaining its original source URI.</returns>
        private async Task<MusicPlaylist> ReadDocumentAsync(DocumentFileReference file, IReadOnlyList<MusicTrack> tracks, CancellationToken token)
        {
            var documents = _documents ?? throw new NotSupportedException("此宿主未提供文档列表访问。");
            var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.Reference)));
            var directory = Path.Combine(_workspace.DataDirectory, "DocumentImports", identity);
            var name = file.Name.Equals("MyFavorites.json", StringComparison.OrdinalIgnoreCase) ? "MyFavorites.json" : "collection.json";
            var path = Path.Combine(directory, name);
            Directory.CreateDirectory(directory);
            try
            {
                await using (var source = await documents.OpenReadAsync(file.Reference, token).ConfigureAwait(false))
                await using (var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        total += count;
                        if (total > 16L * 1024 * 1024)
                        {
                            throw new InvalidDataException("The collection document is too large.");
                        }

                        await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                }

                var playlist = await _service.ReadFileAsync(path, tracks, token).ConfigureAwait(false);
                return playlist with { MajCollection = playlist.MajCollection! with { SourceFilePath = file.Reference } };
            }
            finally
            {
                File.Delete(path);
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }
    }
}
