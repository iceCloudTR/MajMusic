#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Platform.Storage;
using MajMusic.Core.Abstractions;

namespace MajMusic.Presentation.Storage
{
    /// <summary>Commits explicitly app-owned collection mirrors separately from immutable cache objects.</summary>
    public sealed partial class AvaloniaSharedStorageAccess : ISharedCollectionStorageAccess
    {
        /// <inheritdoc />
        public async Task<string> PublishCollectionAsync(string root, string relativePath, string localSourcePath, CancellationToken cancellationToken = default)
        {
            var parts = SplitCollectionPath(relativePath);
            if (_rename is null)
            {
                throw new NotSupportedException("此文档提供方不支持完整歌单提交。");
            }
            await using (var source = File.OpenRead(localSourcePath))
            {
                await VerifyCollectionOwnerAsync(source, parts[^1], cancellationToken).ConfigureAwait(false);
            }
            await _publications.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? temporaryReference = null;
            string? backupReference = null;
            var published = false;
            try
            {
                using var folder = await OpenFolderAsync(root, parts, 1, true, cancellationToken).ConfigureAwait(false)
                    ?? throw new DirectoryNotFoundException("无法创建共享歌单目录。");
                using var current = await FindChildAsync(folder, parts[^1], cancellationToken).ConfigureAwait(false);
                if (current is not null && current is not IStorageFile)
                {
                    throw new IOException("共享歌单位置已被同名目录占用。");
                }
                if (current is IStorageFile existing)
                {
                    await using var input = await existing.OpenReadAsync().ConfigureAwait(false);
                    await VerifyCollectionOwnerAsync(input, parts[^1], cancellationToken).ConfigureAwait(false);
                }
                using var temporary = await folder.CreateFileAsync("." + parts[^1] + "." + Guid.NewGuid().ToString("N") + ".partial").ConfigureAwait(false)
                    ?? throw new IOException("无法创建共享歌单临时文件。");
                temporaryReference = temporary.Path.AbsoluteUri;
                await using (var source = File.OpenRead(localSourcePath))
                await using (var output = await temporary.OpenWriteAsync().ConfigureAwait(false))
                {
                    await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (current is IStorageFile old)
                {
                    backupReference = await _rename(old.Path.AbsoluteUri, parts[^1] + ".backup-" + Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
                }
                var reference = await _rename(temporaryReference, parts[^1], cancellationToken).ConfigureAwait(false);
                temporaryReference = reference;
                using var completed = await GetFileAsync(reference, CancellationToken.None).ConfigureAwait(false);
                if (completed.Name != parts[^1])
                {
                    throw new IOException("文档提供方更改了共享歌单文件名。");
                }
                published = true;
                return reference;
            }
            finally
            {
                if (!published)
                {
                    await DeletePublicationRemainderAsync(temporaryReference).ConfigureAwait(false);
                    if (backupReference is not null)
                    {
                        try
                        {
                            await _rename(backupReference, parts[^1], CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The original complete mirror remains in its backup document if the provider refuses restoration.
                        }
                    }
                }
                else
                {
                    await DeletePublicationRemainderAsync(backupReference).ConfigureAwait(false);
                }
                _publications.Release();
            }
        }

        /// <inheritdoc />
        public async Task DeleteCollectionAsync(string root, string relativePath, CancellationToken cancellationToken = default)
        {
            var parts = SplitCollectionPath(relativePath);
            await _publications.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var reference = await FindAsync(root, relativePath, false, cancellationToken).ConfigureAwait(false);
                if (reference is null)
                {
                    return;
                }
                using var file = await GetFileAsync(reference, cancellationToken).ConfigureAwait(false);
                await using (var source = await file.OpenReadAsync().ConfigureAwait(false))
                {
                    await VerifyCollectionOwnerAsync(source, parts[^1], cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await file.DeleteAsync().ConfigureAwait(false);
            }
            finally
            {
                _publications.Release();
            }
        }

        /// <summary>Removes only a temporary or backup document created by the current publication.</summary>
        /// <param name="reference">The exact current operation-owned reference.</param>
        /// <returns>A task representing best-effort cleanup.</returns>
        private async Task DeletePublicationRemainderAsync(string? reference)
        {
            if (reference is null)
            {
                return;
            }
            try
            {
                using var file = await GetFileAsync(reference, CancellationToken.None).ConfigureAwait(false);
                await file.DeleteAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Do not replace the original provider error with an optional temporary-file cleanup error.
            }
        }

        /// <summary>Checks the embedded identity against the reserved mirror filename before modifying an existing document.</summary>
        /// <param name="source">The original or staged complete JSON stream.</param>
        /// <param name="fileName">The expected application-owned final filename.</param>
        /// <param name="token">Cancels bounded validation.</param>
        /// <returns>A task representing verified mirror ownership.</returns>
        private static async Task VerifyCollectionOwnerAsync(Stream source, string fileName, CancellationToken token)
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > 16L * 1024 * 1024)
                {
                    throw new InvalidDataException("共享歌单文件过大。");
                }
                bytes.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length));
            if (!document.RootElement.TryGetProperty("MajMusicPlaylistId", out var value) || value.ValueKind != JsonValueKind.String
                || value.GetString() is not { Length: > 0 and <= 512 } id || id.StartsWith("cloud:", StringComparison.OrdinalIgnoreCase)
                || id.Any(char.IsControl)
                || fileName != "MajMusic-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json")
            {
                throw new InvalidDataException("共享位置不是此应用的歌单镜像，未修改现有文件。");
            }
        }

        /// <summary>Limits writable collection documents to reserved direct MaiCharts mirrors.</summary>
        /// <param name="relativePath">The requested app-owned JSON path.</param>
        /// <returns>The exact directory and generated mirror filename.</returns>
        private static string[] SplitCollectionPath(string relativePath)
        {
            var parts = SplitRelativePath(relativePath, false);
            if (parts.Length != 2 || parts[0] != "MaiCharts" || parts[1].Length != 78
                || !parts[1].StartsWith("MajMusic-", StringComparison.Ordinal) || !parts[1].EndsWith(".json", StringComparison.Ordinal)
                || parts[1].AsSpan(9, 64).IndexOfAnyExcept("0123456789abcdef".AsSpan()) >= 0)
            {
                throw new ArgumentException("Only app-owned MaiCharts collection mirrors may be changed.", nameof(relativePath));
            }
            return parts;
        }
    }
}
