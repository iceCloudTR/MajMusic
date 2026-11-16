#nullable enable

using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.Storage
{
    /// <summary>Traverses granted trees and publishes immutable Maj media objects through provider-owned streams.</summary>
    public sealed partial class AvaloniaSharedStorageAccess : ISharedStorageAccess
    {
        /// <summary>Gets the currently attached provider without retaining a destroyed activity.</summary>
        private readonly Func<IStorageProvider?> _provider;
        /// <summary>Renames one completed temporary document using the native provider's commit primitive.</summary>
        private readonly Func<string, string, CancellationToken, Task<string>>? _rename;
        /// <summary>Prevents concurrent local publishers from racing the same immutable destination.</summary>
        private readonly SemaphoreSlim _publications = new(1, 1);

        /// <summary>Creates a shared-tree adapter without requesting permissions or touching user files.</summary>
        /// <param name="provider">Gets the currently attached native storage provider.</param>
        /// <param name="rename">Renames a completed document within its parent and returns its new URI.</param>
        public AvaloniaSharedStorageAccess(Func<IStorageProvider?> provider, Func<string, string, CancellationToken, Task<string>>? rename = null)
        {
            ArgumentNullException.ThrowIfNull(provider);
            _provider = provider;
            _rename = rename;
        }

        /// <inheritdoc />
        public bool CanHandle(string root) => DocumentReference.IsContentUri(root);

        /// <inheritdoc />
        public async Task<string?> FindAsync(string root, string relativePath, bool directory, CancellationToken cancellationToken = default)
        {
            var parts = SplitRelativePath(relativePath, directory);
            using var folder = await OpenFolderAsync(root, parts, directory ? parts.Length : parts.Length - 1, false, cancellationToken).ConfigureAwait(false);
            if (folder is null)
            {
                return null;
            }
            if (directory)
            {
                return folder.Path.AbsoluteUri;
            }

            using var file = await FindChildAsync(folder, parts[^1], cancellationToken).ConfigureAwait(false);
            return file is IStorageFile ? file.Path.AbsoluteUri : null;
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
        public async Task<string> PublishFileAsync(string root, string relativePath, string localSourcePath, CancellationToken cancellationToken = default)
        {
            var parts = SplitObjectPath(relativePath);
            if (_rename is null)
            {
                throw new NotSupportedException("此文档提供方不支持完整文件提交。");
            }
            if (!Path.IsPathFullyQualified(localSourcePath) || !File.Exists(localSourcePath))
            {
                throw new FileNotFoundException("完整的本地音频文件不存在。");
            }

            await _publications.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? temporaryReference = null;
            var published = false;
            try
            {
                using var folder = await OpenFolderAsync(root, parts, parts.Length - 1, true, cancellationToken).ConfigureAwait(false)
                    ?? throw new DirectoryNotFoundException("无法创建共享媒体目录。");
                using var existing = await FindChildAsync(folder, parts[^1], cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    throw new IOException("共享媒体对象已存在，请先校验并复用现有文件。");
                }

                using var temporary = await folder.CreateFileAsync("." + parts[^1] + "." + Guid.NewGuid().ToString("N") + ".partial").ConfigureAwait(false)
                    ?? throw new IOException("无法创建共享媒体临时文件。");
                temporaryReference = temporary.Path.AbsoluteUri;
                await using (var source = new FileStream(localSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = await temporary.OpenWriteAsync().ConfigureAwait(false))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var reference = await _rename(temporaryReference, parts[^1], cancellationToken).ConfigureAwait(false);
                temporaryReference = reference;
                using var completed = await GetFileAsync(reference, CancellationToken.None).ConfigureAwait(false);
                if (!completed.Name.Equals(parts[^1], StringComparison.Ordinal))
                {
                    throw new IOException("文档提供方更改了共享媒体文件名。");
                }
                published = true;
                return reference;
            }
            finally
            {
                if (!published && temporaryReference is not null)
                {
                    try
                    {
                        using var temporary = await GetFileAsync(temporaryReference, CancellationToken.None).ConfigureAwait(false);
                        await temporary.DeleteAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Preserve the original publication failure if the provider also refuses temporary-file cleanup.
                    }
                }
                _publications.Release();
            }
        }

        /// <inheritdoc />
        public async Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
        {
            SplitObjectPath(relativePath);
            var reference = await FindAsync(root, relativePath, false, cancellationToken).ConfigureAwait(false);
            if (reference is not null)
            {
                using var file = await GetFileAsync(reference, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await file.DeleteAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Opens only descendants reached through provider-owned child names.</summary>
        /// <param name="root">The granted root URI.</param>
        /// <param name="parts">The already validated relative path components.</param>
        /// <param name="count">The number of directory components to traverse.</param>
        /// <param name="create">Whether absent destination directories may be created.</param>
        /// <param name="token">Cancels traversal.</param>
        /// <returns>The caller-owned descendant folder, or null when absent.</returns>
        private async Task<IStorageFolder?> OpenFolderAsync(string root, string[] parts, int count, bool create, CancellationToken token)
        {
            if (!CanHandle(root))
            {
                throw new ArgumentException("A granted shared document tree is required.", nameof(root));
            }
            var provider = await ProviderAsync(token).ConfigureAwait(false);
            var current = await provider.TryGetFolderFromPathAsync(new Uri(root)).ConfigureAwait(false)
                ?? throw new DirectoryNotFoundException("共享目录不可访问，请重新浏览并选择目录。");
            try
            {
                for (var index = 0; index < count; index++)
                {
                    var child = await FindChildAsync(current, parts[index], token).ConfigureAwait(false);
                    if (child is not null && child is not IStorageFolder)
                    {
                        child.Dispose();
                        throw new IOException("共享目录路径已被同名文件占用。");
                    }
                    var next = child as IStorageFolder;
                    if (next is null && create)
                    {
                        token.ThrowIfCancellationRequested();
                        next = await current.CreateFolderAsync(parts[index]).ConfigureAwait(false);
                    }
                    current.Dispose();
                    if (next is null)
                    {
                        return null;
                    }
                    current = next;
                }
                return current;
            }
            catch
            {
                current.Dispose();
                throw;
            }
        }

        /// <summary>Finds one exact child name without interpreting URI text as an untrusted path.</summary>
        /// <param name="folder">The authorized parent.</param>
        /// <param name="name">The literal child name.</param>
        /// <param name="token">Cancels enumeration.</param>
        /// <returns>The caller-owned child or null.</returns>
        private static async Task<IStorageItem?> FindChildAsync(IStorageFolder folder, string name, CancellationToken token)
        {
            await foreach (var item in folder.GetItemsAsync().WithCancellation(token).ConfigureAwait(false))
            {
                if (item.Name.Equals(name, StringComparison.Ordinal))
                {
                    return item;
                }
                item.Dispose();
            }
            return null;
        }

        /// <summary>Opens an explicitly granted document reference.</summary>
        /// <param name="reference">The provider-owned URI.</param>
        /// <param name="token">Cancels opening.</param>
        /// <returns>The caller-owned file handle.</returns>
        private async Task<IStorageFile> GetFileAsync(string reference, CancellationToken token)
        {
            if (!CanHandle(reference))
            {
                throw new ArgumentException("A granted document reference is required.", nameof(reference));
            }
            var provider = await ProviderAsync(token).ConfigureAwait(false);
            return await provider.TryGetFileFromPathAsync(new Uri(reference)).ConfigureAwait(false)
                ?? throw new FileNotFoundException("共享媒体文件不可访问。");
        }

        /// <summary>Obtains the active provider without retaining an obsolete native activity.</summary>
        /// <param name="token">Cancels lookup.</param>
        /// <returns>The active platform storage provider.</returns>
        private async Task<IStorageProvider> ProviderAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var provider = Dispatcher.UIThread.CheckAccess() ? _provider() : await Dispatcher.UIThread.InvokeAsync(_provider);
            token.ThrowIfCancellationRequested();
            return provider ?? throw new InvalidOperationException("文档选择器尚未就绪。");
        }

        /// <summary>Rejects absolute paths, empty segments and parent traversal before any provider operation.</summary>
        /// <param name="relativePath">The caller-supplied child path.</param>
        /// <param name="allowRoot">Whether an empty relative path may select the root itself.</param>
        /// <returns>The literal safe child names.</returns>
        private static string[] SplitRelativePath(string relativePath, bool allowRoot)
        {
            ArgumentNullException.ThrowIfNull(relativePath);
            if (relativePath.Length == 0 && allowRoot)
            {
                return [];
            }
            var parts = relativePath.Replace('\\', '/').Split('/');
            if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.Contains(':') || part.Contains('\0')))
            {
                throw new ArgumentException("Shared paths must remain relative to the selected directory.", nameof(relativePath));
            }
            return parts;
        }

        /// <summary>Limits all writes and deletions to the immutable Maj SHA-256 object namespace.</summary>
        /// <param name="relativePath">The relative object path.</param>
        /// <returns>The validated cache namespace, shard and hash filename.</returns>
        private static string[] SplitObjectPath(string relativePath)
        {
            var parts = SplitRelativePath(relativePath, false);
            if (parts.Length != 4 || parts[0] != "Cache" || parts[1] != "Objects" || parts[3].Length != 64
                || parts[3].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || parts[2] != parts[3][..2])
            {
                throw new ArgumentException("Only immutable Maj SHA-256 cache objects may be published or deleted.", nameof(relativePath));
            }
            return parts;
        }
    }
}
