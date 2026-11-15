#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Metadata
{
    /// <summary>Reuses local audio metadata by path, file length and last-write time without downloading resources.</summary>
    public sealed class CachedTrackMetadataProvider : ITrackMetadataProvider, IAsyncDisposable
    {
        /// <summary>Parses complete local audio without touching the playback owner.</summary>
        private readonly IAudioMetadataReader _reader;
        /// <summary>Contains only application-owned metadata cache files.</summary>
        private readonly string _cacheDirectory;
        /// <summary>Coalesces successive visible-library reads without an unbounded per-file lock table.</summary>
        private readonly SemaphoreSlim _reads = new(1, 1);
        /// <summary>Protects creation of the shared provider-disposal operation.</summary>
        private readonly object _disposalGate = new();
        /// <summary>Contains the single provider-disposal operation.</summary>
        private Task? _disposal;
        /// <summary>Rejects new lookups during shutdown.</summary>
        private int _disposed;

        /// <summary>Creates a cache without reading sources or creating directories.</summary>
        /// <param name="reader">The local-only parser whose lifetime is owned by this provider.</param>
        /// <param name="cacheDirectory">An application-owned metadata-cache directory.</param>
        /// <exception cref="ArgumentException">The cache location is empty.</exception>
        public CachedTrackMetadataProvider(IAudioMetadataReader reader, string cacheDirectory)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
            _reader = reader;
            _cacheDirectory = Path.GetFullPath(cacheDirectory);
        }

        /// <inheritdoc />
        public async Task<MusicTrack> EnrichAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (track.Audio.LocalPath is not { } path || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                return track;
            }

            await _reads.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var file = new FileInfo(path);
                file.Refresh();
                if (!file.Exists)
                {
                    return track;
                }

                var length = file.Length;
                var lastWrite = file.LastWriteTimeUtc.Ticks;
                if (track.AudioMetadataLength == length && track.AudioMetadataLastWriteUtcTicks == lastWrite
                    && (track.Duration is not null || track.AudioInfo is not null))
                {
                    return track;
                }

                var cachePath = GetCachePath(path);
                var cached = await ReadCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
                if (cached?.Metadata is { } cachedProperties && cached.FileLength == length && cached.LastWriteUtcTicks == lastWrite)
                {
                    return ApplyMetadata(track, cachedProperties, length, lastWrite);
                }

                var metadata = await _reader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                file.Refresh();
                if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != lastWrite)
                {
                    return track with { Duration = null, AudioInfo = null, AudioMetadataLength = null, AudioMetadataLastWriteUtcTicks = null };
                }

                await WriteCacheAsync(cachePath, new CachedMetadata
                {
                    FileLength = length,
                    LastWriteUtcTicks = lastWrite,
                    Metadata = metadata
                }, cancellationToken).ConfigureAwait(false);
                return ApplyMetadata(track, metadata, length, lastWrite);
            }
            finally
            {
                _reads.Release();
            }
        }

        /// <summary>Releases the owned parser after the host has canceled and drained visible-library work.</summary>
        /// <returns>A task representing completed parser and cache-lifetime cleanup.</returns>
        public ValueTask DisposeAsync()
        {
            lock (_disposalGate)
            {
                if (_disposal is null)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    _disposal = DisposeCoreAsync();
                }

                return new ValueTask(_disposal);
            }
        }

        /// <summary>Waits for the last cache lookup and releases the owned parser.</summary>
        /// <returns>A task representing completed provider cleanup.</returns>
        private async Task DisposeCoreAsync()
        {
            await _reads.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_reader is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (_reader is IDisposable synchronous)
                {
                    synchronous.Dispose();
                }
            }
            finally
            {
                _reads.Release();
                _reads.Dispose();
            }
        }

        /// <summary>Reads a disposable cache record, ignoring invalid data so the source can be reparsed.</summary>
        /// <param name="path">The application-owned cache path.</param>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>The cache record, or null when missing or unreadable.</returns>
        private static async Task<CachedMetadata?> ReadCacheAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return await JsonSerializer.DeserializeAsync<CachedMetadata>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>Atomically publishes a disposable cache record without changing any music source.</summary>
        /// <param name="path">The application-owned cache path.</param>
        /// <param name="record">The source fingerprint and parsed properties.</param>
        /// <param name="cancellationToken">Cancels writing.</param>
        /// <returns>A task representing the best-effort cache write.</returns>
        private static async Task WriteCacheAsync(string path, CachedMetadata record, CancellationToken cancellationToken)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, record, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Cache availability does not affect the parsed library result.
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // An externally held temporary cache file may be retried by later maintenance.
                }
            }
        }

        /// <summary>Builds a cache filename from the normalized source path without reading audio content.</summary>
        /// <param name="path">The complete local audio path.</param>
        /// <returns>A filename inside the application-owned cache.</returns>
        private string GetCachePath(string path)
        {
            var normalized = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
            {
                normalized = normalized.ToUpperInvariant();
            }

            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            return Path.Combine(_cacheDirectory, digest + ".json");
        }

        /// <summary>Retains identity, title and resource descriptors while adding validated metadata.</summary>
        /// <param name="track">The source entry.</param>
        /// <param name="metadata">The parsed or cached properties.</param>
        /// <param name="length">The source file length.</param>
        /// <param name="lastWrite">The source last-write ticks.</param>
        /// <returns>An enriched immutable entry.</returns>
        private static MusicTrack ApplyMetadata(MusicTrack track, AudioMetadata metadata, long length, long lastWrite)
        {
            return track with
            {
                Duration = metadata.Duration,
                AudioInfo = metadata.AudioInfo,
                AudioMetadataLength = length,
                AudioMetadataLastWriteUtcTicks = lastWrite
            };
        }

        /// <summary>Stores one disposable source-fingerprint cache record.</summary>
        private sealed record CachedMetadata
        {
            /// <summary>Gets the file length at parse time.</summary>
            public long FileLength { get; init; }
            /// <summary>Gets the file's UTC last-write ticks at parse time.</summary>
            public long LastWriteUtcTicks { get; init; }
            /// <summary>Gets the media properties obtained from the matching source bytes.</summary>
            public AudioMetadata Metadata { get; init; } = new();
        }
    }
}
