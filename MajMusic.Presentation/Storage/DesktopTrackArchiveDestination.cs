#nullable enable

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace MajMusic.Presentation.Storage
{
    /// <summary>Uses a native Save As dialog and a real file clipboard item on desktop.</summary>
    /// <param name="owner">The originating desktop top-level window.</param>
    internal sealed class DesktopTrackArchiveDestination(TopLevel owner) : ITrackArchiveDestination
    {
        /// <inheritdoc />
        public string StagingRoot { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localData
                ? localData : Path.GetTempPath(),
            "MajMusic", "SongArchiveCache");

        /// <inheritdoc />
        public async Task<string?> SaveAsync(string archivePath, CancellationToken cancellationToken)
        {
            var storage = owner.StorageProvider;
            using var downloads = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);
            using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存歌曲压缩包",
                SuggestedStartLocation = downloads,
                SuggestedFileName = Path.GetFileName(archivePath),
                DefaultExtension = "zip",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("ZIP 压缩包") { Patterns = ["*.zip"], MimeTypes = ["application/zip"] }]
            });
            if (file is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows() && file.TryGetLocalPath() is { } target)
            {
                await SaveToWindowsPathAsync(archivePath, target, cancellationToken);
            }
            else
            {
                await using var source = File.OpenRead(archivePath);
                await using var output = await file.OpenWriteAsync();
                await source.CopyToAsync(output, cancellationToken);
                if (output.CanSeek)
                {
                    output.SetLength(output.Position);
                }
            }
            return $"已保存 {file.Name}";
        }

        /// <summary>Commits a Windows Save As selection only after a complete copy exists beside it.</summary>
        /// <param name="archivePath">The already complete staged ZIP.</param>
        /// <param name="target">The path chosen and, if necessary, confirmed in Save As.</param>
        /// <param name="token">Cancels copying before replacing the selected file.</param>
        /// <returns>A task representing the completed save.</returns>
        /// <exception cref="IOException">The destination cannot be written or replaced.</exception>
        /// <exception cref="OperationCanceledException">The operation was canceled before publication.</exception>
        internal static async Task SaveToWindowsPathAsync(string archivePath, string target, CancellationToken token)
        {
            var folder = Path.GetDirectoryName(target) ?? throw new IOException("无法访问选择的文件夹。");
            var temporary = Path.Combine(folder, ".MajMusic-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    await using var source = File.OpenRead(archivePath);
                    await source.CopyToAsync(output, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning("Cannot remove an incomplete Windows archive copy: {0}", exception.GetType().Name);
                }
            }
        }

        /// <inheritdoc />
        public async Task<string> ShareAsync(string archivePath, CancellationToken cancellationToken)
        {
            var clipboard = owner.Clipboard ?? throw new NotSupportedException("此窗口无法访问系统剪贴板。");
            var file = await owner.StorageProvider.TryGetFileFromPathAsync(new Uri(archivePath))
                ?? throw new FileNotFoundException("无法访问已生成的歌曲压缩包。");
            cancellationToken.ThrowIfCancellationRequested();
            await clipboard.SetFilesAsync([file]);
            await clipboard.FlushAsync();
            return "已复制 ZIP 文件，可粘贴到聊天窗口或文件夹。";
        }
    }
}
