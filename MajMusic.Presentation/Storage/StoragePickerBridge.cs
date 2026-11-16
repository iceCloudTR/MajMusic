#nullable enable

using Avalonia.Platform.Storage;
using MajMusic.Core.Models;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Storage
{
    /// <summary>Keeps native document pickers at the presentation boundary without treating content URIs as local paths.</summary>
    internal static class StoragePickerBridge
    {
        /// <summary>Retains a selected tree grant and returns its original reference.</summary>
        /// <param name="folder">The folder explicitly selected by the user.</param>
        /// <param name="resolver">The optional host resolver for platform-specific read-only grants.</param>
        /// <returns>The ordinary local path or persisted document-tree URI.</returns>
        public static async Task<string> GetDirectoryReferenceAsync(IStorageFolder folder, Func<IStorageFolder, Task<string>>? resolver = null)
        {
            if (resolver is not null)
            {
                return LocalDirectorySettings.NormalizePath(await resolver(folder));
            }

            if (folder.TryGetLocalPath() is { } path)
            {
                return path;
            }

            if (!DocumentReference.IsContentUri(folder.Path.AbsoluteUri) || !folder.CanBookmark
                || await folder.SaveBookmarkAsync() is null)
            {
                throw new NotSupportedException("无法保留所选目录的访问权限，请重新选择目录。");
            }

            return folder.Path.AbsoluteUri;
        }

        /// <summary>Imports through the configured document service or the unchanged desktop file exchange.</summary>
        /// <param name="owner">The selected workspace presentation.</param>
        /// <param name="file">The explicitly selected JSON document.</param>
        /// <returns>A task representing validated import.</returns>
        public static Task ImportAsync(MainViewModel owner, IStorageFile file)
        {
            return owner.ImportCollectionAsync(file.TryGetLocalPath() ?? (DocumentReference.IsContentUri(file.Path.AbsoluteUri)
                ? file.Path.AbsoluteUri : throw new NotSupportedException("无法读取所选列表文档。")));
        }

        /// <summary>Exports a validated small JSON document to the platform-selected output stream.</summary>
        /// <param name="owner">The Maj-compatible exporter and error owner.</param>
        /// <param name="file">The explicit new output document.</param>
        /// <param name="playlist">The captured context-menu list, or null for the ordinary selected-list action.</param>
        /// <returns>A task representing compatible serialization and document publication.</returns>
        public static async Task ExportAsync(MainViewModel owner, IStorageFile file, MusicPlaylist? playlist = null)
        {
            if (file.TryGetLocalPath() is { } localPath)
            {
                if (playlist is null)
                {
                    await owner.ExportCollectionAsync(localPath);
                }
                else
                {
                    await owner.ExportPlaylistFromMenuAsync(playlist, localPath);
                }

                return;
            }

            if (!DocumentReference.IsContentUri(file.Path.AbsoluteUri))
            {
                throw new NotSupportedException("无法写入所选列表文档。");
            }

            var directory = Path.Combine(owner.DataDirectory, "DocumentExports");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
            try
            {
                if (playlist is null)
                {
                    await owner.ExportCollectionAsync(temporary);
                }
                else
                {
                    await owner.ExportPlaylistFromMenuAsync(playlist, temporary);
                }

                await using var source = File.OpenRead(temporary);
                await using var destination = await file.OpenWriteAsync();
                await source.CopyToAsync(destination);
                await destination.FlushAsync();
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}
