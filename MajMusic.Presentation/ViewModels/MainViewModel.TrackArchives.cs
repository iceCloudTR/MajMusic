#nullable enable

using System.Text;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Presentation.Storage;

namespace MajMusic.Presentation.ViewModels
{
    public sealed partial class MainViewModel
    {
        /// <summary>Gets or sets the native archive destination supplied by the mobile host.</summary>
        public ITrackArchiveDestination? TrackArchiveDestination { get; set; }

        /// <summary>Gets whether a foreground song action can currently prepare a portable archive.</summary>
        internal bool CanPrepareTrackArchive => IsIdle && _workspace is ITrackArchiveWorkspace;

        /// <summary>Stages the captured song before offering it to a destination and observes file and network errors.</summary>
        /// <param name="track">The exact song underneath the gesture.</param>
        /// <param name="destination">The platform action and temporary-file location.</param>
        /// <param name="share">Whether to share the archive or save a public copy.</param>
        /// <returns>A task representing the complete action or dismissal.</returns>
        internal Task ExportTrackFromMenuAsync(MusicTrack track, ITrackArchiveDestination destination, bool share)
        {
            if (_workspace is not ITrackArchiveWorkspace archiveWorkspace)
            {
                return Task.CompletedTask;
            }

            return RunBusyAsync(async () =>
            {
                var token = _lifetime.Token;
                Status = "正在准备歌曲压缩包…";
                var folder = Path.Combine(destination.StagingRoot, Guid.NewGuid().ToString("N"));
                var archivePath = Path.Combine(folder, TrackArchiveFileName(track.Title));
                var retainForShare = false;
                try
                {
                    await Task.Run(async () =>
                    {
                        Directory.CreateDirectory(folder);
                        PruneTrackArchives(destination.StagingRoot, folder);
                        await using var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await archiveWorkspace.WriteTrackArchiveAsync(track, stream, token).ConfigureAwait(false);
                    }, token);

                    if (share)
                    {
                        Status = await destination.ShareAsync(archivePath, token);
                        retainForShare = true;
                    }
                    else
                    {
                        Status = await destination.SaveAsync(archivePath, token) ?? "已取消保存。";
                    }
                }
                finally
                {
                    if (!retainForShare)
                    {
                        TryRemoveTrackArchive(folder, archivePath);
                    }
                }
            });
        }

        /// <summary>Builds a portable bounded filename while preserving ordinary song title characters.</summary>
        /// <param name="title">The untrusted track title.</param>
        /// <returns>A safe basename including the ZIP extension.</returns>
        internal static string TrackArchiveFileName(string title)
        {
            var builder = new StringBuilder();
            foreach (var rune in title.EnumerateRunes().Take(60))
            {
                if (Rune.IsControl(rune) || (rune.IsAscii && "<>:\"/\\|?*".Contains((char)rune.Value)))
                {
                    builder.Append('_');
                }
                else
                {
                    builder.Append(rune.ToString());
                }
            }

            var name = builder.ToString().Trim().Trim('.', ' ');
            if (name.Length == 0)
            {
                name = "MajMusic";
            }
            var firstComponent = name.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
            if (firstComponent is "CON" or "NUL" or "AUX" or "PRN" or "CONIN$" or "CONOUT$"
                || (firstComponent.StartsWith("COM", StringComparison.Ordinal) || firstComponent.StartsWith("LPT", StringComparison.Ordinal))
                    && firstComponent.Length == 4 && "0123456789¹²³".Contains(firstComponent[3]))
            {
                name = "_" + name;
            }

            return name + ".zip";
        }

        /// <summary>Removes only expired archive files from staging subdirectories created by this feature.</summary>
        /// <param name="root">The platform's dedicated temporary archive root.</param>
        /// <param name="currentFolder">The folder leased to the current operation.</param>
        private static void PruneTrackArchives(string root, string currentFolder)
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            try
            {
                foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
                {
                    if (folder.FullName == currentFolder || !Guid.TryParseExact(folder.Name, "N", out _)
                        || folder.LastWriteTimeUtc >= cutoff || folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    foreach (var file in folder.EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly))
                    {
                        TryRemoveTrackArchive(folder.FullName, file.FullName);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Could not prune temporary song archives: {0}", exception.GetType().Name);
            }
        }

        /// <summary>Best-effort cleanup of an owned temporary archive and its now-empty staging folder.</summary>
        /// <param name="folder">The known staging folder.</param>
        /// <param name="archive">The exact archive file to delete.</param>
        private static void TryRemoveTrackArchive(string folder, string archive)
        {
            try
            {
                File.Delete(archive);
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Could not release a temporary song archive: {0}", exception.GetType().Name);
            }
        }
    }
}
