#nullable enable

using MajMusic.Core.Models;
using MajMusic.Infrastructure.Network;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MajMusic.Infrastructure.Downloads
{
    /// <summary>Publishes validated audio and covers atomically, separating evictable cache entries from pinned downloads.</summary>
    public sealed class MediaCache : IDisposable
    {
        /// <summary>Stores managed objects and an index containing no resource addresses or credentials.</summary>
        private readonly string _root;

        /// <summary>Transports explicit downloads; its ownership stays with the composition root.</summary>
        private readonly HttpClient _client;

        /// <summary>Gates cache misses without preventing offline cache hits.</summary>
        private readonly NetworkAccessPolicy _policy;

        /// <summary>Serializes publication and cleanup, including requests for the same object.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Contains persisted lookup aliases and their retention decisions.</summary>
        private Dictionary<string, CacheEntry>? _index;

        /// <summary>Serializes only local cache metadata.</summary>
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        /// <summary>Creates an idle managed cache without issuing requests.</summary>
        /// <param name="root">The application's managed cache directory.</param>
        /// <param name="client">An anonymous or endpoint-isolated media client; the caller owns it.</param>
        /// <param name="policy">The shared online/offline gate.</param>
        /// <exception cref="ArgumentException">The directory is empty.</exception>
        public MediaCache(string root, HttpClient client, NetworkAccessPolicy policy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(policy);
            _root = Path.GetFullPath(root);
            _client = client;
            _policy = policy;
        }

        /// <summary>Resolves an imported file or a validated completed object, downloading only on an online cache miss.</summary>
        /// <param name="resource">The audio or cover descriptor.</param>
        /// <param name="keepOffline">Whether to pin this object against automatic cache cleanup.</param>
        /// <param name="cancellationToken">Cancels waiting, transfer, validation and publication.</param>
        /// <param name="downloadClient">An optional endpoint-isolated transport for this cache miss; its caller retains ownership.</param>
        /// <returns>The fully validated local file path.</returns>
        /// <exception cref="InvalidOperationException">An uncached resource is requested in offline mode.</exception>
        /// <exception cref="InvalidDataException">A length, digest or cache index is invalid.</exception>
        /// <exception cref="HttpRequestException">The download fails.</exception>
        /// <exception cref="FileNotFoundException">Neither a readable local file nor a remote address is available.</exception>
        public async Task<string> GetAsync(MediaResource resource, bool keepOffline = false, CancellationToken cancellationToken = default, HttpClient? downloadClient = null)
        {
            ArgumentNullException.ThrowIfNull(resource);
            var expectedHash = NormalizeHash(resource.Sha256);
            if (resource.LocalPath is { Length: > 0 } localPath && File.Exists(localPath))
            {
                await ValidateFileAsync(localPath, expectedHash, resource.Length, cancellationToken).ConfigureAwait(false);
                var absolutePath = Path.GetFullPath(localPath);
                var objectRoot = Path.Combine(_root, "objects") + Path.DirectorySeparatorChar;
                if (!absolutePath.StartsWith(objectRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return absolutePath;
                }

                expectedHash ??= await HashFileAsync(absolutePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(absolutePath, GetObjectPath(expectedHash), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return absolutePath;
                }
            }

            var address = resource.RemoteUri;
            if (address is not null)
            {
                MajNetClient.ValidateAddress(address);
            }

            var aliasContext = address is null ? null : string.IsNullOrEmpty(resource.CacheVersion)
                ? address.AbsoluteUri
                : string.Concat(address.AbsoluteUri, "\0", resource.CacheVersion);
            var uriKey = aliasContext is null ? null : $"uri:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(aliasContext))).ToLowerInvariant()}";
            var key = expectedHash is not null ? $"sha256:{expectedHash}" : uriKey
                ?? throw new FileNotFoundException("The resource has no readable local file or remote address.");

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
                var existing = index.GetValueOrDefault(key);
                // An explicit digest is authoritative; a newer revision must never fall back to an older URI alias.
                var objectHash = expectedHash ?? existing?.ContentHash;
                if (objectHash is not null)
                {
                    var objectPath = GetObjectPath(objectHash);
                    if (File.Exists(objectPath))
                    {
                        try
                        {
                            var length = await ValidateFileAsync(objectPath, objectHash, resource.Length, cancellationToken).ConfigureAwait(false);
                            await PublishAliasesAsync(index, uriKey, objectHash, length, keepOffline, cancellationToken).ConfigureAwait(false);
                            return objectPath;
                        }
                        catch (InvalidDataException)
                        {
                            // A damaged object is never returned as a completed download.
                        }
                    }
                }

                if (address is null)
                {
                    throw new FileNotFoundException("The cached resource is unavailable and has no remote address.");
                }

                using var lifetime = _policy.CreateRequestCancellation(cancellationToken);
                Directory.CreateDirectory(Path.Combine(_root, "partial"));
                var partialPath = Path.Combine(_root, "partial", Guid.NewGuid().ToString("N"));
                try
                {
                    using var response = await (downloadClient ?? _client).GetAsync(address, HttpCompletionOption.ResponseHeadersRead, lifetime.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"The media source returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
                    }

                    await using (var source = await response.Content.ReadAsStreamAsync(lifetime.Token).ConfigureAwait(false))
                    await using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    {
                        await source.CopyToAsync(destination, lifetime.Token).ConfigureAwait(false);
                    }

                    var length = await ValidateFileAsync(partialPath, expectedHash, resource.Length, lifetime.Token).ConfigureAwait(false);
                    var contentHash = expectedHash ?? await HashFileAsync(partialPath, lifetime.Token).ConfigureAwait(false);
                    var finalPath = GetObjectPath(contentHash);
                    lifetime.Token.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                    var objectAlreadyExisted = File.Exists(finalPath);
                    File.Move(partialPath, finalPath, true);
                    try
                    {
                        await PublishAliasesAsync(index, uriKey, contentHash, length, keepOffline, lifetime.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (!objectAlreadyExisted && !index.Values.Any(entry => entry.ContentHash == contentHash))
                        {
                            File.Delete(finalPath);
                        }

                        throw;
                    }

                    return finalPath;
                }
                finally
                {
                    File.Delete(partialPath);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Removes evictable aliases and files while preserving pinned downloads and imported files.</summary>
        /// <param name="cancellationToken">Cancels cleanup.</param>
        /// <returns>The number of physical objects removed.</returns>
        public async Task<int> ClearCacheAsync(CancellationToken cancellationToken = default)
        {
            return await EnforceLimitAsync(0, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Evicts least-recently-used unpinned objects to meet an automatic-cache byte budget.</summary>
        /// <param name="maxBytes">The allowed number of unpinned bytes, excluding pinned downloads.</param>
        /// <param name="cancellationToken">Cancels cleanup.</param>
        /// <returns>The number of physical objects removed.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The budget is negative.</exception>
        public async Task<int> EnforceLimitAsync(long maxBytes, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
                var objects = index.Values.GroupBy(entry => entry.ContentHash, StringComparer.Ordinal)
                    .Where(group => !group.Any(entry => entry.KeepOffline))
                    .Select(group => new { Hash = group.Key, Length = group.Max(entry => entry.Length), LastUse = group.Max(entry => entry.LastAccessUtc) })
                    .OrderBy(item => item.LastUse).ToArray();
                var total = objects.Sum(item => item.Length);
                var candidates = new Dictionary<string, CacheEntry>(index, StringComparer.Ordinal);
                var removedObjects = new List<string>();
                foreach (var item in objects)
                {
                    if (maxBytes != 0 && total <= maxBytes)
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    removedObjects.Add(item.Hash);
                    foreach (var key in candidates.Where(pair => pair.Value.ContentHash == item.Hash).Select(pair => pair.Key).ToArray())
                    {
                        candidates.Remove(key);
                    }

                    total -= item.Length;
                }

                // Keep the previous index and every file intact if persisting the cleanup decision fails.
                await SaveIndexAsync(candidates, cancellationToken).ConfigureAwait(false);
                var removed = 0;
                foreach (var hash in removedObjects)
                {
                    var path = GetObjectPath(hash);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        removed++;
                    }
                }

                return removed;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Releases synchronization resources; the caller retains ownership of the HTTP client.</summary>
        public void Dispose()
        {
            _gate.Dispose();
        }

        /// <summary>Loads and validates persisted cache metadata without issuing network requests.</summary>
        /// <param name="cancellationToken">Cancels the local read.</param>
        /// <returns>The mutable in-memory index protected by the cache gate.</returns>
        /// <exception cref="InvalidDataException">The local cache index is malformed.</exception>
        private async Task<Dictionary<string, CacheEntry>> LoadIndexAsync(CancellationToken cancellationToken)
        {
            if (_index is not null)
            {
                return _index;
            }

            var indexPath = Path.Combine(_root, "index.json");
            if (!File.Exists(indexPath))
            {
                _index = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
                return _index;
            }

            try
            {
                await using var stream = File.OpenRead(indexPath);
                _index = await JsonSerializer.DeserializeAsync<Dictionary<string, CacheEntry>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The cache index is empty.");
                foreach (var entry in _index.Values)
                {
                    if (entry is null || NormalizeHash(entry.ContentHash) != entry.ContentHash || entry.Length < 0)
                    {
                        throw new InvalidDataException("The cache index contains an invalid object descriptor.");
                    }
                }

                // Upgrade indexes created before canonical hash aliases were stored, preserving every existing pin.
                foreach (var group in _index.Values.GroupBy(entry => entry.ContentHash, StringComparer.Ordinal).ToArray())
                {
                    _index[$"sha256:{group.Key}"] = new CacheEntry
                    {
                        ContentHash = group.Key,
                        Length = group.Max(entry => entry.Length),
                        KeepOffline = group.Any(entry => entry.KeepOffline),
                        LastAccessUtc = group.Max(entry => entry.LastAccessUtc)
                    };
                }

                return _index;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
            {
                _index = null;
                throw new InvalidDataException("The cache index is invalid; existing files have been preserved.", exception);
            }
        }

        /// <summary>Commits verified URI and hash aliases without mutating the previous index before persistence succeeds.</summary>
        /// <param name="index">The previous validated index.</param>
        /// <param name="uriKey">The optional hashed address alias.</param>
        /// <param name="contentHash">The validated content digest.</param>
        /// <param name="length">The completed file length.</param>
        /// <param name="keepOffline">Whether this operation explicitly pins the object.</param>
        /// <param name="cancellationToken">Cancels publication before the atomic commit.</param>
        /// <returns>A task representing the durable alias update.</returns>
        private async Task PublishAliasesAsync(Dictionary<string, CacheEntry> index, string? uriKey, string contentHash, long length, bool keepOffline, CancellationToken cancellationToken)
        {
            var updated = new Dictionary<string, CacheEntry>(index, StringComparer.Ordinal);
            var entry = new CacheEntry
            {
                ContentHash = contentHash,
                Length = length,
                KeepOffline = keepOffline || index.Values.Any(value => value.ContentHash == contentHash && value.KeepOffline),
                LastAccessUtc = DateTimeOffset.UtcNow
            };
            updated[$"sha256:{contentHash}"] = entry;
            if (uriKey is not null)
            {
                // The URI alias is only the latest verified content; it does not override an explicit expected digest.
                updated[uriKey] = entry;
            }

            await SaveIndexAsync(updated, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Publishes a candidate index by replacing it only after a successful complete write.</summary>
        /// <param name="candidate">The complete index to commit.</param>
        /// <param name="cancellationToken">Cancels serialization before publication.</param>
        /// <returns>A task representing the completed index publication.</returns>
        private async Task SaveIndexAsync(Dictionary<string, CacheEntry> candidate, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_root);
            var temporary = Path.Combine(_root, $"index.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, candidate, JsonOptions, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, Path.Combine(_root, "index.json"), true);
                _index = candidate;
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        /// <summary>Builds a managed path only from a validated content digest.</summary>
        /// <param name="hash">The lowercase SHA-256 digest.</param>
        /// <returns>The content-addressed object path.</returns>
        private string GetObjectPath(string hash)
        {
            var normalized = NormalizeHash(hash) ?? throw new InvalidDataException("A cache object requires a SHA-256 digest.");
            return Path.Combine(_root, "objects", normalized[..2], normalized);
        }

        /// <summary>Validates a digest without allowing it to become a filesystem path fragment.</summary>
        /// <param name="hash">The optional expected digest.</param>
        /// <returns>The lowercase digest, or null when unknown.</returns>
        /// <exception cref="ArgumentException">The digest is not SHA-256 hexadecimal.</exception>
        private static string? NormalizeHash(string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return null;
            }

            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("A resource SHA-256 digest must contain 64 hexadecimal characters.", nameof(hash));
            }

            return hash.ToLowerInvariant();
        }

        /// <summary>Checks a completed file against known metadata before it becomes playable.</summary>
        /// <param name="path">The file to validate.</param>
        /// <param name="expectedHash">The optional expected digest.</param>
        /// <param name="expectedLength">The optional expected byte length.</param>
        /// <param name="cancellationToken">Cancels hashing.</param>
        /// <returns>The observed byte length.</returns>
        /// <exception cref="InvalidDataException">The file does not match its descriptor.</exception>
        private static async Task<long> ValidateFileAsync(string path, string? expectedHash, long? expectedLength, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = new FileInfo(path).Length;
            if (expectedLength is not null && expectedLength != length)
            {
                throw new InvalidDataException("The media file length does not match its descriptor.");
            }

            var normalized = NormalizeHash(expectedHash);
            if (normalized is not null && await HashFileAsync(path, cancellationToken).ConfigureAwait(false) != normalized)
            {
                throw new InvalidDataException("The media file SHA-256 digest does not match its descriptor.");
            }

            return length;
        }

        /// <summary>Hashes file contents without loading the entire audio file into memory.</summary>
        /// <param name="path">The completed file path.</param>
        /// <param name="cancellationToken">Cancels reading and hashing.</param>
        /// <returns>The lowercase content digest.</returns>
        private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }

        /// <summary>Stores one local lookup alias and its offline-retention decision.</summary>
        private sealed record CacheEntry
        {
            /// <summary>Gets the validated SHA-256 object identity.</summary>
            public required string ContentHash { get; init; }

            /// <summary>Gets the validated completed length in bytes.</summary>
            public long Length { get; init; }

            /// <summary>Gets whether automatic cleanup must preserve this object.</summary>
            public bool KeepOffline { get; init; }

            /// <summary>Gets the latest successful access time for eviction ordering.</summary>
            public DateTimeOffset LastAccessUtc { get; init; }
        }
    }
}
