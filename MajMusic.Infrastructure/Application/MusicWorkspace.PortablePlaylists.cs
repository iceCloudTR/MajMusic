#nullable enable

using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Collections;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Mirrors local Maj-compatible membership into app-owned files in the selected media root.</summary>
    public sealed partial class MusicWorkspace
    {
        /// <summary>Orders mirror updates and deletions after private local membership has committed.</summary>
        private readonly SemaphoreSlim _portablePlaylistWrites = new(1, 1);

        /// <summary>Publishes the latest saved local state rather than a potentially superseded caller snapshot.</summary>
        /// <param name="playlist">The local identity whose private database commit has finished.</param>
        /// <param name="cancellationToken">Cancels export before file replacement.</param>
        /// <param name="indexedTracks">An optional shared library snapshot when preparing several mirrors.</param>
        /// <returns>A task representing the app-owned mirror update.</returns>
        private async Task MirrorPortablePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken, IReadOnlyList<MusicTrack>? indexedTracks = null)
        {
            if (playlist.IsCloud)
            {
                return;
            }
            await _portablePlaylistWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = (await _library.GetPlaylistsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Id == playlist.Id);
                if (current is null || current.IsCloud)
                {
                    return;
                }
                var tracks = indexedTracks ?? await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false);
                var retained = current.MajCollection?.Entries.Where(entry => entry.TrackId is not null)
                    .Select(entry => entry.TrackId!).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
                var portableIds = tracks.Where(track => PortableChartHash(track) is not null).Select(track => track.Id).ToHashSet(StringComparer.Ordinal);
                portableIds.UnionWith(retained);
                var portable = current with { TrackIds = current.TrackIds.Where(portableIds.Contains).ToArray() };
                var root = GetMediaStorageRoot();
                var fileName = MajCollectionService.GetPortableFileName(current.Id);
                var relative = "MaiCharts/" + fileName;
                if (DocumentReference.IsContentUri(root))
                {
                    var storage = _sharedStorageAccess as ISharedCollectionStorageAccess
                        ?? throw new NotSupportedException("请重新选择媒体目录以保存共享歌单。");
                    var staging = Path.Combine(DataDirectory, "Runtime", "PortablePlaylists");
                    Directory.CreateDirectory(staging);
                    var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".partial");
                    try
                    {
                        await WritePortablePlaylistAsync(temporary, portable, tracks, cancellationToken).ConfigureAwait(false);
                        await storage.PublishCollectionAsync(root, relative, temporary, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        File.Delete(temporary);
                    }
                    return;
                }

                var directory = Path.Combine(Path.GetFullPath(root), "MaiCharts");
                Directory.CreateDirectory(directory);
                RejectPortableLink(directory);
                var destination = Path.Combine(directory, fileName);
                if (File.Exists(destination))
                {
                    RejectPortableLink(destination);
                    await EnsurePortableOwnershipAsync(destination, current.Id, cancellationToken).ConfigureAwait(false);
                }
                var localTemporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    await WritePortablePlaylistAsync(localTemporary, portable, tracks, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(localTemporary, destination, true);
                }
                finally
                {
                    File.Delete(localTemporary);
                }
            }
            finally
            {
                _portablePlaylistWrites.Release();
            }
        }

        /// <summary>Populates mirrors when a different media root is selected or existing local lists are restored.</summary>
        /// <param name="cancellationToken">Cancels local exports.</param>
        /// <returns>A task representing all currently stored local mirrors.</returns>
        private async Task MirrorAllPortablePlaylistsAsync(CancellationToken cancellationToken)
        {
            var tracks = await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false);
            foreach (var playlist in await GetPlaylistsAsync(cancellationToken).ConfigureAwait(false))
            {
                await MirrorPortablePlaylistAsync(playlist, cancellationToken, tracks).ConfigureAwait(false);
            }
        }

        /// <summary>Removes only this app's exact mirror after the private list has actually been deleted.</summary>
        /// <param name="playlistId">The deleted local identity.</param>
        /// <param name="cancellationToken">Cancels ownership verification and deletion.</param>
        /// <returns>A task representing deletion of an app-owned mirror or an already absent file.</returns>
        private async Task DeletePortablePlaylistMirrorAsync(string playlistId, CancellationToken cancellationToken)
        {
            await _portablePlaylistWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if ((await _library.GetPlaylistsAsync(cancellationToken).ConfigureAwait(false)).Any(item => item.Id == playlistId))
                {
                    return;
                }
                var root = GetMediaStorageRoot();
                var fileName = MajCollectionService.GetPortableFileName(playlistId);
                if (DocumentReference.IsContentUri(root))
                {
                    if (_sharedStorageAccess is ISharedCollectionStorageAccess storage)
                    {
                        await storage.DeleteCollectionAsync(root, "MaiCharts/" + fileName, cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                var directory = Path.Combine(Path.GetFullPath(root), "MaiCharts");
                var destination = Path.Combine(directory, fileName);
                if (File.Exists(destination))
                {
                    RejectPortableLink(directory);
                    RejectPortableLink(destination);
                    await EnsurePortableOwnershipAsync(destination, playlistId, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(destination);
                }
            }
            finally
            {
                _portablePlaylistWrites.Release();
            }
        }

        /// <summary>Writes a complete portable document before any final filename is replaced.</summary>
        /// <param name="path">The application-owned temporary destination.</param>
        /// <param name="playlist">The portable subset of the local playlist.</param>
        /// <param name="tracks">Current library metadata used for exact chart-hash resolution.</param>
        /// <param name="token">Cancels serialization or output.</param>
        /// <returns>A task representing a closed, flushed document.</returns>
        private static async Task WritePortablePlaylistAsync(string path, MusicPlaylist playlist, IReadOnlyList<MusicTrack> tracks, CancellationToken token)
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await new MajCollectionService().WriteAsync(stream, playlist, tracks, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        /// <summary>Checks that an existing destination is the mirror of this exact local identity.</summary>
        /// <param name="path">The reserved app mirror path.</param>
        /// <param name="id">The local identity allowed to own it.</param>
        /// <param name="token">Cancels bounded local reading.</param>
        /// <returns>A task representing successful ownership verification.</returns>
        private static async Task EnsurePortableOwnershipAsync(string path, string id, CancellationToken token)
        {
            if (new FileInfo(path).Length > 16L * 1024 * 1024)
            {
                throw new InvalidDataException("共享歌单文件过大，未覆盖现有文件。");
            }
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("MajMusicPlaylistId", out var value)
                || value.ValueKind != JsonValueKind.String || value.GetString() != id)
            {
                throw new InvalidDataException("共享位置已被其他列表占用，未覆盖现有文件。");
            }
        }

        /// <summary>Rejects linked destination entries rather than redirecting a mirror outside its selected media tree.</summary>
        /// <param name="path">The existing destination directory or file.</param>
        private static void RejectPortableLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("共享歌单目标不能是目录链接。");
            }
        }

        /// <summary>Reads only actual chart-file identities accepted by Maj's SongHashs format.</summary>
        /// <param name="track">The local or online music entry.</param>
        /// <returns>The portable chart hash, or null for ordinary audio without a chart.</returns>
        private static string? PortableChartHash(MusicTrack track)
        {
            var value = track.MajChartHash ?? (track.RemoteId is not null ? track.Revision : null);
            Span<byte> hash = stackalloc byte[16];
            return value is not null && Convert.TryFromBase64String(value, hash, out var written) && written == hash.Length ? value : null;
        }
    }
}
