#nullable enable

using System.IO.Compression;
using System.Net;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    public sealed partial class MusicWorkspace : ITrackArchiveWorkspace
    {
        /// <summary>Caps a full-size artwork response before it can be added to an archive.</summary>
        private const int MaximumArchiveCoverBytes = 32 * 1024 * 1024;

        /// <inheritdoc />
        public async Task WriteTrackArchiveAsync(MusicTrack track, Stream destination, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            ArgumentNullException.ThrowIfNull(destination);
            await WithChartPreviewSourceAsync(track, true, async (source, token) =>
            {
                string audio;
                try
                {
                    audio = await ResolvePreviewAudioAsync(source, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException
                    && !IsOnline && source.Track.RemoteId is not null)
                {
                    throw new InvalidOperationException("这首歌尚未下载，请开启在线模式后再试。", exception);
                }

                var cover = await ResolveArchiveCoverAsync(source.Track, token).ConfigureAwait(false);
                await using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await WriteArchiveEntryAsync(archive, "track", audio, token).ConfigureAwait(false);
                    await WriteArchiveEntryAsync(archive, "maidata", source.ChartPath, token).ConfigureAwait(false);
                    await WriteArchiveEntryAsync(archive, "bg", cover, token).ConfigureAwait(false);
                    await WriteArchiveEntryAsync(archive, "pv", source.VideoPath, token).ConfigureAwait(false);
                }

                await ValidatePreviewSourceAsync(source, token).ConfigureAwait(false);
                return true;
            }, cancellationToken, allowMissingChart: track.RemoteId is null || !IsOnline).ConfigureAwait(false);
        }

        /// <summary>Prefers original artwork from the current endpoint and falls back when the route is unavailable.</summary>
        /// <param name="track">The source-scoped chart being packaged.</param>
        /// <param name="token">Cancels cover resolution.</param>
        /// <returns>The original artwork, an available cached cover, or null.</returns>
        private async Task<string?> ResolveArchiveCoverAsync(MusicTrack track, CancellationToken token)
        {
            if (IsOnline && track.RemoteId is { } remoteId)
            {
                var connection = GetConnection(GetEndpointForTrack(track));
                using var lifetime = connection.CreateCancellation(token);
                var server = await connection.Provider.GetServerAsync(lifetime.Token).ConfigureAwait(false);
                if (server.Id != track.SourceId)
                {
                    throw new InvalidOperationException("接入点的音乐来源已更改，请重新同步音乐库。");
                }

                var address = connection.Provider.GetFullCoverAddress(remoteId);
                try
                {
                    return await _cache.GetPreviewAsync(ScopeResourceForCache(track, new MediaResource { RemoteUri = address }),
                        address, MaximumArchiveCoverBytes, connection.MediaClient, lifetime.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException exception) when (IsMissingArchiveAsset(exception))
                {
                }
            }

            try
            {
                return await ResolveCoverAsync(track, token).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (HttpRequestException exception) when (IsMissingArchiveAsset(exception))
            {
                return null;
            }
        }

        /// <summary>Checks whether this source does not provide an optional asset route.</summary>
        /// <param name="exception">The asset request failure.</param>
        /// <returns>Whether the request should be treated as absence.</returns>
        private static bool IsMissingArchiveAsset(HttpRequestException exception)
        {
            return exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;
        }

        /// <summary>Copies one available source into a conventional entry without re-encoding its bytes.</summary>
        /// <param name="archive">The staged ZIP receiving the entry.</param>
        /// <param name="stem">The fixed Maj filename stem.</param>
        /// <param name="reference">The local file or granted document URI, or null for an absent sidecar.</param>
        /// <param name="token">Cancels opening and copying the stream.</param>
        /// <returns>A task that completes when the entry is closed.</returns>
        private async Task WriteArchiveEntryAsync(ZipArchive archive, string stem, string? reference, CancellationToken token)
        {
            if (reference is null)
            {
                return;
            }

            await using var source = await OpenArchiveAssetAsync(reference, token).ConfigureAwait(false);
            var prefix = new byte[12];
            var size = await source.ReadAtLeastAsync(prefix, prefix.Length, false, token).ConfigureAwait(false);
            if (size == 0)
            {
                throw new InvalidDataException("歌曲包包含空文件。");
            }

            var name = reference;
            if (stem == "track" && _documentStorage?.CanHandle(reference) == true)
            {
                name = (await _documentStorage.GetFileReferenceAsync(reference, token).ConfigureAwait(false)).Name;
            }
            var extension = ArchiveAssetExtension(stem, name, prefix.AsSpan(0, size));
            var compression = stem == "maidata" ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
            var entry = archive.CreateEntry(stem + extension, compression);
            await using var output = await entry.OpenAsync(token).ConfigureAwait(false);
            await output.WriteAsync(prefix.AsMemory(0, size), token).ConfigureAwait(false);
            await source.CopyToAsync(output, token).ConfigureAwait(false);
        }

        /// <summary>Reads an original document through its grant, or a regular file through the filesystem.</summary>
        /// <param name="reference">The source file or content URI.</param>
        /// <param name="token">Cancels opening the source.</param>
        /// <returns>The caller-owned read stream.</returns>
        private async Task<Stream> OpenArchiveAssetAsync(string reference, CancellationToken token)
        {
            if (!DocumentReference.IsContentUri(reference))
            {
                return new FileStream(reference, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }

            if (_documentStorage?.CanHandle(reference) == true)
            {
                return await _documentStorage.OpenReadAsync(reference, token).ConfigureAwait(false);
            }

            return await (_mediaStorageAccess ?? throw new NotSupportedException("无法读取此歌曲文件。"))
                .OpenReadAsync(reference, token).ConfigureAwait(false);
        }

        /// <summary>Identifies common stored formats even when the cache or document URI omits an extension.</summary>
        /// <param name="stem">The archive's conventional entry stem.</param>
        /// <param name="name">The original local path, document display name, or opaque reference.</param>
        /// <param name="prefix">The leading bytes already read from the file.</param>
        /// <returns>The extension for the portable entry.</returns>
        private static string ArchiveAssetExtension(string stem, string name, ReadOnlySpan<byte> prefix)
        {
            if (stem == "maidata")
            {
                return ".txt";
            }
            if (stem == "pv")
            {
                return ".mp4";
            }

            var extension = DocumentReference.IsContentUri(name) ? string.Empty : Path.GetExtension(name).ToLowerInvariant();
            if (stem == "bg")
            {
                if (prefix.Length >= 4 && prefix[0] == 137 && prefix[1..].StartsWith("PNG"u8))
                {
                    return ".png";
                }
                if (prefix.Length >= 12 && prefix[8..].StartsWith("WEBP"u8))
                {
                    return ".webp";
                }
                return extension is ".jpeg" or ".png" or ".webp" ? extension : ".jpg";
            }

            if (prefix.StartsWith("OggS"u8))
            {
                return ".ogg";
            }
            if (prefix.StartsWith("fLaC"u8))
            {
                return ".flac";
            }
            if (prefix.Length >= 12 && prefix[8..].StartsWith("WAVE"u8))
            {
                return ".wav";
            }
            if (prefix.Length >= 8 && prefix[4..].StartsWith("ftyp"u8))
            {
                return ".m4a";
            }
            if (prefix.Length >= 2 && prefix[0] == 255 && (prefix[1] & 0xf6) == 0xf0)
            {
                return ".aac";
            }
            return extension is ".ogg" or ".opus" or ".wav" or ".aac" or ".flac" or ".m4a" ? extension : ".mp3";
        }
    }
}
