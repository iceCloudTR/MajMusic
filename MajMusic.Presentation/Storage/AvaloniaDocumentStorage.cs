#nullable enable

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.Storage
{
    /// <summary>Indexes granted document trees through platform streams while leaving original music read-only and in place.</summary>
    public sealed class AvaloniaDocumentStorage : IReadOnlyDocumentStorage
    {
        /// <summary>Reads the currently attached platform provider rather than retaining a destroyed mobile activity.</summary>
        private readonly Func<IStorageProvider?> _provider;
        /// <summary>Stores only small on-demand cover copies needed by ordinary image decoders.</summary>
        private readonly string _coverCacheDirectory;
        /// <summary>Matches the standard desktop track filename priority.</summary>
        private static readonly string[] AudioExtensions = [".mp3", ".ogg", ".opus", ".wav", ".aac", ".flac"];

        /// <summary>Creates a document adapter without opening a picker or requesting general storage permission.</summary>
        /// <param name="provider">Gets the current view's provider on the UI dispatcher.</param>
        /// <param name="coverCacheDirectory">The application-private artwork cache directory.</param>
        public AvaloniaDocumentStorage(Func<IStorageProvider?> provider, string coverCacheDirectory)
        {
            ArgumentNullException.ThrowIfNull(provider);
            _provider = provider;
            _coverCacheDirectory = Path.GetFullPath(coverCacheDirectory);
        }

        /// <inheritdoc />
        public bool CanHandle(string? reference)
        {
            return DocumentReference.IsContentUri(reference);
        }

        /// <inheritdoc />
        public Task<LocalLibraryScanResult> ScanAsync(string directory, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ScanCoreAsync(directory, cancellationToken), cancellationToken);
        }

        /// <inheritdoc />
        public async Task<string> ResolveAudioAsync(string reference, CancellationToken cancellationToken = default)
        {
            await using var stream = await OpenReadAsync(reference, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return reference;
        }

        /// <inheritdoc />
        public async Task<string?> ResolveCoverAsync(MediaResource resource, CancellationToken cancellationToken = default)
        {
            var reference = resource.LocalPath ?? throw new ArgumentException("The original cover document reference is missing.", nameof(resource));
            var identity = reference + "\n" + resource.CacheVersion;
            var path = Path.Combine(_coverCacheDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
            if (File.Exists(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return path;
            }

            using var file = await GetFileAsync(reference, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(_coverCacheDirectory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var source = await file.OpenReadAsync().ConfigureAwait(false))
                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    await CopyBoundedAsync(source, destination, 64L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
                return path;
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        /// <inheritdoc />
        public async Task<DocumentFileReference> GetFileReferenceAsync(string reference, CancellationToken cancellationToken = default)
        {
            using var file = await GetFileAsync(reference, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new DocumentFileReference(file.Path.AbsoluteUri, file.Name);
        }

        /// <inheritdoc />
        public async Task<Stream> OpenReadAsync(string reference, CancellationToken cancellationToken = default)
        {
            using var file = await GetFileAsync(reference, cancellationToken).ConfigureAwait(false);
            var stream = await file.OpenReadAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return stream;
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<DocumentFileReference> EnumerateCollectionFilesAsync(string directory, bool includeDirectFiles,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var root = await GetFolderAsync(directory, cancellationToken).ConfigureAwait(false);
            var direct = includeDirectFiles || root.Name.Equals("majcollection", StringComparison.OrdinalIgnoreCase);
            await foreach (var item in root.GetItemsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                using (item)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item is IStorageFile file && file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        && (direct || file.Name.Equals("MyFavorites.json", StringComparison.OrdinalIgnoreCase)))
                    {
                        yield return new DocumentFileReference(file.Path.AbsoluteUri, file.Name);
                    }
                    else if (item is IStorageFolder folder && folder.Name.Equals("majcollection", StringComparison.OrdinalIgnoreCase))
                    {
                        await foreach (var child in folder.GetItemsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
                        {
                            using (child)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (child is IStorageFile json && json.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                {
                                    yield return new DocumentFileReference(json.Path.AbsoluteUri, json.Name);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Enumerates provider folders on a worker and reads only chart text and file metadata.</summary>
        /// <param name="directory">The persisted granted tree URI.</param>
        /// <param name="token">Cancels traversal and text reads.</param>
        /// <returns>The original-file catalog and isolated read failures.</returns>
        private async Task<LocalLibraryScanResult> ScanCoreAsync(string directory, CancellationToken token)
        {
            var pending = new Stack<IStorageFolder>();
            pending.Push(await GetFolderAsync(directory, token).ConfigureAwait(false));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var tracks = new List<MusicTrack>();
            var issues = new List<LocalLibraryScanIssue>();
            var count = 0;
            try
            {
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    using var folder = pending.Pop();
                    if (!visited.Add(folder.Path.AbsoluteUri))
                    {
                        continue;
                    }

                    count++;
                    var files = new List<IStorageFile>();
                    try
                    {
                        await foreach (var item in folder.GetItemsAsync().WithCancellation(token).ConfigureAwait(false))
                        {
                            token.ThrowIfCancellationRequested();
                            if (item is IStorageFolder child)
                            {
                                pending.Push(child);
                            }
                            else if (item is IStorageFile file)
                            {
                                files.Add(file);
                            }
                            else
                            {
                                item.Dispose();
                            }
                        }

                        var audio = Find(files, "track", AudioExtensions);
                        if (audio is not null)
                        {
                            tracks.Add(await ReadTrackAsync(folder.Name, audio, files, token).ConfigureAwait(false));
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        issues.Add(new LocalLibraryScanIssue { Path = folder.Path.AbsoluteUri, Message = "The granted directory or its chart metadata could not be read." });
                    }
                    finally
                    {
                        foreach (var file in files)
                        {
                            file.Dispose();
                        }
                    }
                }
            }
            finally
            {
                foreach (var folder in pending)
                {
                    folder.Dispose();
                }
            }

            return new LocalLibraryScanResult { RootDirectory = directory, Tracks = tracks, ScannedDirectoryCount = count, Issues = issues };
        }

        /// <summary>Reads canonical chart title and portable chart identity while retaining original provider media references.</summary>
        /// <param name="folderName">The fallback title when no chart title is provided.</param>
        /// <param name="audio">The original standard track document.</param>
        /// <param name="files">The direct documents in the song folder.</param>
        /// <param name="token">Cancels text reading.</param>
        /// <returns>The indexed original document references.</returns>
        private static async Task<MusicTrack> ReadTrackAsync(string folderName, IStorageFile audio, IReadOnlyList<IStorageFile> files, CancellationToken token)
        {
            var properties = await audio.GetBasicPropertiesAsync().ConfigureAwait(false);
            if (properties.Size == 0)
            {
                throw new InvalidDataException("The original audio document is empty.");
            }

            var title = folderName;
            var artist = string.Empty;
            string? hash = null;
            var chart = files.FirstOrDefault(file => file.Name.Equals("maidata.txt", StringComparison.OrdinalIgnoreCase));
            if (chart is not null)
            {
                await using var input = await chart.OpenReadAsync().ConfigureAwait(false);
                using var bytes = new MemoryStream();
                await CopyBoundedAsync(input, bytes, 16L * 1024 * 1024, token).ConfigureAwait(false);
                hash = Convert.ToBase64String(MD5.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
                bytes.Position = 0;
                using var reader = new StreamReader(bytes, new UTF8Encoding(false, true), true);
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    var separator = line.IndexOf('=');
                    if (separator < 0)
                    {
                        continue;
                    }

                    var key = line[..separator].Trim();
                    var value = line[(separator + 1)..].Trim();
                    if (key.StartsWith("&inote_", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    if (key.Equals("&title", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                    {
                        title = value;
                    }
                    else if (key.Equals("&artist", StringComparison.OrdinalIgnoreCase))
                    {
                        artist = value;
                    }
                }
            }

            var cover = Find(files, "bg", [".png", ".jpg", ".jpeg"]);
            StorageItemProperties? coverProperties = cover is null ? null : await cover.GetBasicPropertiesAsync().ConfigureAwait(false);
            var reference = audio.Path.AbsoluteUri;
            return new MusicTrack
            {
                Id = "local-document:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reference))),
                Title = title,
                Artist = artist,
                MajChartHash = hash,
                SourceId = "local/document",
                Revision = properties.Size + ":" + properties.DateModified?.UtcTicks,
                Audio = new MediaResource { LocalPath = reference, Length = properties.Size is { } size && size <= long.MaxValue ? (long)size : null },
                Cover = cover is null ? null : new MediaResource
                {
                    LocalPath = cover.Path.AbsoluteUri,
                    CacheVersion = coverProperties?.Size + ":" + coverProperties?.DateModified?.UtcTicks
                }
            };
        }

        /// <summary>Finds a standard name with the same extension priority as desktop indexing.</summary>
        /// <param name="files">The direct folder documents.</param>
        /// <param name="basename">The standard media basename.</param>
        /// <param name="extensions">The supported ordered suffixes.</param>
        /// <returns>The matching original document.</returns>
        private static IStorageFile? Find(IReadOnlyList<IStorageFile> files, string basename, IReadOnlyList<string> extensions)
        {
            return extensions.Select(extension => files.FirstOrDefault(file => file.Name.Equals(basename + extension, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(file => file is not null);
        }

        /// <summary>Obtains the live provider on its dispatcher without retaining a previous activity.</summary>
        /// <param name="token">Cancels provider acquisition.</param>
        /// <returns>The current platform provider.</returns>
        private async Task<IStorageProvider> ProviderAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var provider = Dispatcher.UIThread.CheckAccess() ? _provider() : await Dispatcher.UIThread.InvokeAsync(_provider);
            token.ThrowIfCancellationRequested();
            return provider ?? throw new InvalidOperationException("文档选择器尚未就绪。");
        }

        /// <summary>Opens an existing granted folder without manufacturing a physical filesystem path.</summary>
        /// <param name="reference">The granted tree reference.</param>
        /// <param name="token">Cancels lookup.</param>
        /// <returns>The caller-owned folder handle.</returns>
        private async Task<IStorageFolder> GetFolderAsync(string reference, CancellationToken token)
        {
            if (!CanHandle(reference))
            {
                throw new ArgumentException("A content document-tree reference is required.", nameof(reference));
            }

            var provider = await ProviderAsync(token).ConfigureAwait(false);
            return await OpenGrantedFolderAsync(provider, reference, token).ConfigureAwait(false)
                ?? throw new DirectoryNotFoundException("所选文档目录不可访问，请重新选择目录。");
        }

        /// <summary>Reopens a tree descendant by walking the original grant so Android retains its permission-root context.</summary>
        /// <param name="provider">The currently attached platform storage provider.</param>
        /// <param name="reference">The saved tree or tree/document directory URI.</param>
        /// <param name="token">Cancels provider enumeration and releases intermediate wrappers.</param>
        /// <returns>The caller-owned actual folder, or null when it no longer exists.</returns>
        /// <exception cref="NotSupportedException">A descendant's opaque document ID cannot be safely resolved within its original grant.</exception>
        internal static async Task<IStorageFolder?> OpenGrantedFolderAsync(IStorageProvider provider, string reference, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(provider);
            token.ThrowIfCancellationRequested();
            var uri = new Uri(reference);
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!DocumentReference.IsContentUri(reference) || parts.Length != 4 || parts[0] != "tree" || parts[2] != "document")
            {
                var folder = await provider.TryGetFolderFromPathAsync(uri).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    folder?.Dispose();
                    token.ThrowIfCancellationRequested();
                }

                return folder;
            }

            var grantId = Uri.UnescapeDataString(parts[1]).TrimEnd('/');
            var documentId = Uri.UnescapeDataString(parts[3]).TrimEnd('/');
            var treeUri = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/tree/" + parts[1]);
            string[] descendants;
            if (documentId == grantId)
            {
                descendants = [];
            }
            else if (documentId.StartsWith(grantId + '/', StringComparison.Ordinal))
            {
                descendants = documentId[(grantId.Length + 1)..].Split('/');
                if (descendants.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
                {
                    throw new NotSupportedException("无法恢复所选文档子目录，请重新选择目录。");
                }
            }
            else
            {
                throw new NotSupportedException("此文档提供方无法恢复子目录，请重新选择目标目录。");
            }

            IStorageFolder? current = await provider.TryGetFolderFromPathAsync(treeUri).ConfigureAwait(false);
            try
            {
                foreach (var name in descendants)
                {
                    token.ThrowIfCancellationRequested();
                    if (current is null)
                    {
                        return null;
                    }

                    IStorageFolder? next = null;
                    await foreach (var item in current.GetItemsAsync().WithCancellation(token).ConfigureAwait(false))
                    {
                        if (item is IStorageFolder folder && item.Name.Equals(name, StringComparison.Ordinal))
                        {
                            next = folder;
                            break;
                        }

                        item.Dispose();
                    }

                    // Child wrappers retain their own Activity and original PermissionRoot; disposing a parent
                    // wrapper neither releases the persisted grant nor disposes any child document.
                    current.Dispose();
                    current = next;
                }

                token.ThrowIfCancellationRequested();
                var result = current;
                current = null;
                return result;
            }
            finally
            {
                current?.Dispose();
            }
        }

        /// <summary>Opens an existing granted document without interpreting its URI as a local path.</summary>
        /// <param name="reference">The original document URI.</param>
        /// <param name="token">Cancels lookup.</param>
        /// <returns>The caller-owned document handle.</returns>
        private async Task<IStorageFile> GetFileAsync(string reference, CancellationToken token)
        {
            if (!CanHandle(reference))
            {
                throw new ArgumentException("A content document reference is required.", nameof(reference));
            }

            var provider = await ProviderAsync(token).ConfigureAwait(false);
            return await provider.TryGetFileFromPathAsync(new Uri(reference)).ConfigureAwait(false)
                ?? throw new FileNotFoundException("所选文档不可访问，请重新选择目录。");
        }

        /// <summary>Copies only bounded chart, JSON or artwork bytes and never runs for music audio.</summary>
        /// <param name="source">The provider-owned read stream.</param>
        /// <param name="destination">The caller-owned small private buffer or cache.</param>
        /// <param name="maximum">The explicit metadata byte limit.</param>
        /// <param name="token">Cancels copying.</param>
        /// <returns>A task representing a complete bounded copy.</returns>
        private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken token)
        {
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > maximum)
                {
                    throw new InvalidDataException("The selected metadata document exceeds its supported size.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
        }
    }
}
