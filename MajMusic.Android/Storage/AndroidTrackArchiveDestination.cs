#nullable enable

using System.Runtime.Versioning;
using AndroidX.Core.Content;
using global::Android.Content;
using global::Android.Provider;
using MajMusic.Presentation.Storage;
using AndroidEnvironment = global::Android.OS.Environment;

namespace MajMusic.Android.Storage
{
    /// <summary>Saves finished song ZIPs to public Downloads or shares their private staged copy.</summary>
    /// <param name="context">The application context used to reach the storage provider and Sharesheet.</param>
    internal sealed class AndroidTrackArchiveDestination(Context context) : ITrackArchiveDestination
    {
        /// <summary>Restricts the share provider to this child of the Android application cache.</summary>
        private const string ArchiveFolderName = "SongArchives";
        /// <summary>Describes the file to both MediaStore and the native chooser.</summary>
        private const string ZipMimeType = "application/zip";

        /// <inheritdoc />
        public string StagingRoot { get; } = Path.Combine(context.CacheDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android 的临时文件目录不可用。"), ArchiveFolderName);

        /// <inheritdoc />
        public Task<string?> SaveAsync(string archivePath, CancellationToken cancellationToken)
        {
            return Task.Run<string?>(async () =>
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    return await SaveWithMediaStoreAsync(archivePath, cancellationToken).ConfigureAwait(false);
                }

                using var downloads = AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDownloads);
                var destination = Path.Combine(downloads?.AbsolutePath ?? throw new IOException("找不到下载目录。"), "MajMusic");
                if (!await AndroidStorageAccess.Instance.EnsureAccessAsync(destination).WaitAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new UnauthorizedAccessException("请允许存储权限后再保存歌曲压缩包。");
                }
                return await SaveLegacyAsync(archivePath, destination, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<string> ShareAsync(string archivePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = new Java.IO.File(archivePath);
            using var uri = FileProvider.GetUriForFile(context, context.PackageName + ".songarchives", archive)
                ?? throw new IOException("无法生成歌曲压缩包的分享地址。");
            using var send = new Intent(Intent.ActionSend);
            send.SetType(ZipMimeType);
            send.PutExtra(Intent.ExtraStream, uri);
            using var clip = ClipData.NewRawUri(Path.GetFileName(archivePath), uri);
            send.ClipData = clip;
            send.AddFlags(ActivityFlags.GrantReadUriPermission);
            using var chooser = Intent.CreateChooser(send, "分享歌曲压缩包")
                ?? throw new IOException("无法打开系统分享面板。");
            chooser.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(chooser);
            return Task.FromResult("已打开系统分享面板。");
        }

        /// <summary>Inserts a pending MediaStore item and exposes it only after the complete ZIP has been copied.</summary>
        /// <param name="archivePath">The staged ZIP.</param>
        /// <param name="token">Cancels the save before publication.</param>
        /// <returns>A status containing the new file's actual display name.</returns>
        [SupportedOSPlatform("android29.0")]
        private async Task<string> SaveWithMediaStoreAsync(string archivePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var resolver = context.ContentResolver ?? throw new IOException("Android 下载目录不可用。");
            using var collection = MediaStore.Downloads.GetContentUri(MediaStore.VolumeExternalPrimary);
            var name = Path.GetFileName(archivePath);
            using var pending = new ContentValues();
            pending.Put(MediaStore.IMediaColumns.DisplayName, name);
            pending.Put(MediaStore.IMediaColumns.MimeType, ZipMimeType);
            pending.Put(MediaStore.IMediaColumns.RelativePath, AndroidEnvironment.DirectoryDownloads + "/MajMusic/");
            pending.Put(MediaStore.IMediaColumns.IsPending, 1);
            using var item = resolver.Insert(collection, pending) ?? throw new IOException("无法在下载目录创建歌曲压缩包。");
            try
            {
                await using (var output = resolver.OpenOutputStream(item, "w") ?? throw new IOException("无法写入歌曲压缩包。"))
                {
                    await using var source = File.OpenRead(archivePath);
                    await source.CopyToAsync(output, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                using var complete = new ContentValues();
                complete.Put(MediaStore.IMediaColumns.IsPending, 0);
                if (resolver.Update(item, complete, null, null) != 1)
                {
                    throw new IOException("无法提交完整的歌曲压缩包。");
                }
            }
            catch
            {
                try
                {
                    resolver.Delete(item, null, null);
                }
                catch (Java.Lang.Exception exception)
                {
                    System.Diagnostics.Trace.TraceWarning("Cannot remove an incomplete Downloads item: {0}", exception.GetType().Name);
                }
                throw;
            }

            try
            {
                using var result = resolver.Query(item, [MediaStore.IMediaColumns.DisplayName], null, null, null);
                if (result?.MoveToFirst() == true)
                {
                    name = result.GetString(0) ?? name;
                }
            }
            catch (Java.Lang.Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning("Cannot read the saved Downloads filename: {0}", exception.GetType().Name);
            }
            return $"已保存到 下载/MajMusic/{name}";
        }

        /// <summary>Publishes a ZIP without overwriting another archive on the legacy public filesystem.</summary>
        /// <param name="archivePath">The staged ZIP.</param>
        /// <param name="destination">The authorized public Downloads/MajMusic directory.</param>
        /// <param name="token">Cancels copying and name allocation before the rename.</param>
        /// <returns>A status containing the saved relative filename.</returns>
        private static async Task<string> SaveLegacyAsync(string archivePath, string destination, CancellationToken token)
        {
            Directory.CreateDirectory(destination);
            var partial = Path.Combine(destination, "." + Guid.NewGuid().ToString("N") + ".tmp");
            var title = Path.GetFileNameWithoutExtension(archivePath);
            try
            {
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    await using var source = File.OpenRead(archivePath);
                    await source.CopyToAsync(output, token).ConfigureAwait(false);
                }
                for (var index = 0; ; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var name = index == 0 ? title + ".zip" : $"{title} ({index}).zip";
                    var target = Path.Combine(destination, name);
                    try
                    {
                        File.Move(partial, target);
                        return $"已保存到 下载/MajMusic/{name}";
                    }
                    catch (IOException) when (File.Exists(target) || Directory.Exists(target))
                    {
                        // A save of the same song can place another copy next to the existing one.
                    }
                }
            }
            finally
            {
                try
                {
                    File.Delete(partial);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning("Cannot remove an incomplete legacy Downloads file: {0}", exception.GetType().Name);
                }
            }
        }
    }
}
