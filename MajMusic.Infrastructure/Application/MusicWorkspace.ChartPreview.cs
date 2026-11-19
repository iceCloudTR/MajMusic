#nullable enable

using System.Buffers;
using System.Net;
using System.Text;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    public sealed partial class MusicWorkspace : IChartPreviewWorkspace
    {
        private const int MaximumChartBytes = 16 * 1024 * 1024;
        private const int MaximumVideoBytes = 256 * 1024 * 1024;

        /// <inheritdoc />
        public Task<IReadOnlyList<ChartPreviewDifficulty>> GetChartPreviewDifficultiesAsync(MusicTrack track,
            CancellationToken cancellationToken = default)
        {
            return WithChartPreviewSourceAsync(track, false, async (source, token) =>
            {
                var parsed = await ReadPlayableChartAsync(source.ChartPath!, token).ConfigureAwait(false);
                await ValidatePreviewSourceAsync(source, token).ConfigureAwait(false);
                return parsed.Difficulties;
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ChartPreviewAssets> ResolveChartPreviewAsync(MusicTrack track, bool includeVideo,
            CancellationToken cancellationToken = default)
        {
            return WithChartPreviewSourceAsync(track, includeVideo, CompleteChartPreviewAsync, cancellationToken);
        }

        private async Task<TResult> WithChartPreviewSourceAsync<TResult>(MusicTrack track, bool includeVideo,
            Func<ChartPreviewSource, CancellationToken, Task<TResult>> use, CancellationToken cancellationToken, bool allowMissingChart = false)
        {
            ArgumentNullException.ThrowIfNull(track);
            cancellationToken.ThrowIfCancellationRequested();
            var indexed = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false);
            if (indexed is not null && (indexed.SourceId, indexed.EndpointId, indexed.RemoteId) != (track.SourceId, track.EndpointId, track.RemoteId))
            {
                throw new InvalidOperationException("歌曲来源已更改，请重新选择歌曲。");
            }

            track = indexed ?? track;
            var mediaVersion = LocalMediaVersion(track);
            var mediaRoot = GetMediaStorageRoot();
            var cache = _cache;
            string? chartPath;
            string? videoPath = null;
            if (RequiresManagedMedia(track))
            {
                chartPath = await ReadPreviewCacheAsync(track, track.Chart, cancellationToken).ConfigureAwait(false);
                if (includeVideo)
                {
                    videoPath = await ReadPreviewCacheAsync(track, track.Video, cancellationToken).ConfigureAwait(false);
                }

                if (IsOnline && track.RemoteId is { } remoteId && (chartPath is null || includeVideo && videoPath is null))
                {
                    var connection = GetConnection(GetEndpointForTrack(track));
                    using var lifetime = connection.CreateCancellation(cancellationToken);
                    var token = lifetime.Token;
                    var server = await connection.Provider.GetServerAsync(token).ConfigureAwait(false);
                    if (server.Id != track.SourceId)
                    {
                        throw new InvalidOperationException("接入点的音乐来源已更改，请重新同步音乐库。");
                    }

                    var remote = await connection.Provider.GetChartPreviewResourcesAsync(remoteId, includeVideo && videoPath is null, token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(track.Revision) && remote.Revision is { } revision && revision != track.Revision)
                    {
                        throw new InvalidOperationException("谱面已更新，请同步曲库后再预览。");
                    }

                    try
                    {
                        chartPath ??= await cache.GetPreviewAsync(ScopeResourceForCache(track, remote.Chart.Descriptor),
                            remote.Chart.RequestAddress, MaximumChartBytes, connection.MediaClient, token).ConfigureAwait(false);
                    }
                    catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new FileNotFoundException("接入点没有提供这首歌的谱面。", exception);
                    }
                    if (includeVideo && videoPath is null && remote.Video is { } video)
                    {
                        try
                        {
                            videoPath = await cache.GetPreviewAsync(ScopeResourceForCache(track, video.Descriptor),
                                video.RequestAddress, MaximumVideoBytes, connection.MediaClient, token).ConfigureAwait(false);
                        }
                        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                        {
                            videoPath = null;
                        }
                    }

                    var chart = DescribeCachedPreview(chartPath, remote.Chart.Descriptor);
                    var cachedVideo = videoPath is not null && remote.Video is { } loadedVideo
                        ? DescribeCachedPreview(videoPath, loadedVideo.Descriptor) : null;
                    if (track.Chart != chart || cachedVideo is not null && cachedVideo != track.Video)
                    {
                        await RememberChartPreviewAsync(track, chart, cachedVideo, mediaVersion, mediaRoot, token).ConfigureAwait(false);
                    }
                    var source = new ChartPreviewSource(track, chartPath, videoPath, mediaVersion, mediaRoot, !remote.DirectRoutes);
                    return await use(source, token).ConfigureAwait(false);
                }
            }
            else
            {
                chartPath = FindLocalPreviewAsset(track.Chart, track.Audio.LocalPath, "maidata.txt");
                if (includeVideo)
                {
                    videoPath = FindLocalPreviewAsset(track.Video, track.Audio.LocalPath, "pv.mp4", "mv.mp4", "bg.mp4");
                }
            }

            if (chartPath is null && !allowMissingChart)
            {
                throw new FileNotFoundException("没有可预览的谱面文件。");
            }

            return await use(new ChartPreviewSource(track, chartPath, videoPath, mediaVersion, mediaRoot, true),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<ChartPreviewAssets> CompleteChartPreviewAsync(ChartPreviewSource source, CancellationToken cancellationToken)
        {
            var track = source.Track;
            var (chartText, difficulties) = await ReadPlayableChartAsync(source.ChartPath!, cancellationToken).ConfigureAwait(false);
            var audioPath = await ResolvePreviewAudioAsync(source, cancellationToken).ConfigureAwait(false);

            string? coverPath;
            try
            {
                coverPath = await ResolveCoverAsync(track, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException or TimeoutException)
            {
                coverPath = null;
            }

            await ValidatePreviewSourceAsync(source, cancellationToken).ConfigureAwait(false);
            return new ChartPreviewAssets(chartText, audioPath, coverPath, source.VideoPath, difficulties);
        }

        /// <summary>Resolves the audio for a chart asset set, including older sources with direct routes only.</summary>
        /// <param name="source">The source and its supported resource routes.</param>
        /// <param name="cancellationToken">Cancels resolution.</param>
        /// <returns>The original playable file or document reference.</returns>
        private async Task<string> ResolvePreviewAudioAsync(ChartPreviewSource source, CancellationToken cancellationToken)
        {
            try
            {
                return await ResolveAudioCoreAsync(source.Track, false, source.ResolveOnlineAudioDetails, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (source.ResolveOnlineAudioDetails && exception.StatusCode is
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                return await ResolveAudioCoreAsync(source.Track, false, false, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<(string Text, IReadOnlyList<ChartPreviewDifficulty> Difficulties)> ReadPlayableChartAsync(string chartPath,
            CancellationToken cancellationToken)
        {
            var chartText = await ReadPreviewChartTextAsync(chartPath, cancellationToken).ConfigureAwait(false);
            var difficulties = FindChartDifficulties(chartText, cancellationToken);
            if (difficulties.Count == 0)
            {
                throw new InvalidDataException("谱面没有可播放的难度。");
            }

            return (chartText, difficulties);
        }

        private async Task ValidatePreviewSourceAsync(ChartPreviewSource source, CancellationToken cancellationToken)
        {
            var track = source.Track;
            if (RequiresManagedMedia(track))
            {
                EnsureLocalMediaVersion(track, source.MediaVersion);
                EnsureMediaStorageRoot(source.MediaRoot);
                var current = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false);
                if (current is not null && LocalMediaOwner(current) != LocalMediaOwner(track))
                {
                    throw new InvalidOperationException("谱面已更新，请重新选择歌曲。");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        private sealed record ChartPreviewSource(MusicTrack Track, string? ChartPath, string? VideoPath, long MediaVersion,
            string MediaRoot, bool ResolveOnlineAudioDetails);

        private async Task<string?> ReadPreviewCacheAsync(MusicTrack track, MediaResource? resource, CancellationToken token)
        {
            if (resource is null)
            {
                return null;
            }

            var scoped = ScopeResourceForCache(track, resource);
            return await _cache.TryGetCachedAsync(scoped, cancellationToken: token).ConfigureAwait(false)
                ?? await _defaultCache.TryGetCachedAsync(scoped, cancellationToken: token).ConfigureAwait(false);
        }

        private async Task RememberChartPreviewAsync(MusicTrack track, MediaResource chart, MediaResource? video, long version,
            string mediaRoot, CancellationToken token)
        {
            await _metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureLocalMediaVersion(track, version);
                EnsureMediaStorageRoot(mediaRoot);
                var current = await _library.FindTrackAsync(track.Id, token).ConfigureAwait(false);
                if (current is not null && LocalMediaOwner(current) == LocalMediaOwner(track))
                {
                    await _library.UpsertTracksAsync([current with
                    {
                        Chart = chart,
                        Video = video ?? current.Video
                    }], token).ConfigureAwait(false);
                }
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        private static MediaResource DescribeCachedPreview(string path, MediaResource descriptor) => descriptor with
        {
            LocalPath = path,
            Sha256 = Path.GetFileName(path),
            Length = new FileInfo(path).Length
        };

        private static string? FindLocalPreviewAsset(MediaResource? resource, string? audioPath, params string[] names)
        {
            if (resource?.LocalPath is { } reference && (DocumentReference.IsContentUri(reference) || IsOrdinaryFile(reference)))
            {
                return reference;
            }

            if (audioPath is null || DocumentReference.IsContentUri(audioPath) || !Path.IsPathFullyQualified(audioPath)
                || Path.GetDirectoryName(audioPath) is not { } folder || !Directory.Exists(folder))
            {
                return null;
            }

            var files = Directory.EnumerateFiles(folder).Where(IsOrdinaryFile).ToArray();
            foreach (var name in names)
            {
                var match = files.FirstOrDefault(file => Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }

        private static bool IsOrdinaryFile(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

        private async Task<string> ReadPreviewChartTextAsync(string path, CancellationToken token)
        {
            await using var stream = DocumentReference.IsContentUri(path)
                ? await (_documentStorage ?? throw new NotSupportedException("此宿主未提供文档谱面访问。")).OpenReadAsync(path, token).ConfigureAwait(false)
                : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.CanSeek && stream.Length > MaximumChartBytes)
            {
                throw new InvalidDataException("谱面文件过大。");
            }

            using var data = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    if (count > MaximumChartBytes - data.Length)
                    {
                        throw new InvalidDataException("谱面文件过大。");
                    }

                    data.Write(buffer, 0, count);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            data.Position = 0;
            using var reader = new StreamReader(data, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }

        private static IReadOnlyList<ChartPreviewDifficulty> FindChartDifficulties(string chart, CancellationToken token)
        {
            var levels = new string?[7];
            var hasNotes = new bool[7];
            var current = -1;
            var closingTimingMarker = '\0';
            using var reader = new StringReader(chart);
            while (reader.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();
                var part = line.AsSpan().Trim();
                var comment = part.IndexOf("||", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    part = part[..comment].TrimEnd();
                }

                if (part.IsEmpty)
                {
                    continue;
                }

                if (part[0] == '&')
                {
                    current = -1;
                    closingTimingMarker = '\0';
                    var separator = part.IndexOf('=');
                    if (separator < 0)
                    {
                        continue;
                    }

                    var key = part[..separator].TrimEnd();
                    part = part[(separator + 1)..].TrimStart();
                    if (key.Length == 5 && key.StartsWith("&lv_", StringComparison.OrdinalIgnoreCase) && key[^1] is >= '1' and <= '7')
                    {
                        levels[key[^1] - '1'] = part.ToString();
                    }
                    else if (key.Length == 8 && key.StartsWith("&inote_", StringComparison.OrdinalIgnoreCase) && key[^1] is >= '1' and <= '7')
                    {
                        current = key[^1] - '1';
                    }
                }

                if (current >= 0 && !hasNotes[current])
                {
                    hasNotes[current] = HasPreviewNote(part, ref closingTimingMarker);
                }
            }

            return Enumerable.Range(0, 7).Where(index => hasNotes[index])
                .Select(index => new ChartPreviewDifficulty(index + 1, levels[index])).ToArray();
        }

        private static bool HasPreviewNote(ReadOnlySpan<char> content, ref char closingTimingMarker)
        {
            foreach (var character in content)
            {
                if (closingTimingMarker != '\0')
                {
                    if (character == closingTimingMarker)
                    {
                        closingTimingMarker = '\0';
                    }
                    continue;
                }

                if (character is >= '0' and <= '9' or >= 'A' and <= 'D' or >= 'a' and <= 'd')
                {
                    return true;
                }

                closingTimingMarker = character switch
                {
                    '(' => ')',
                    '{' => '}',
                    '<' => '>',
                    '[' => ']',
                    _ => '\0'
                };
            }

            return false;
        }
    }
}
