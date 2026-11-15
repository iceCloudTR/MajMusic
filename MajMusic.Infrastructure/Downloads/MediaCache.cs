#nullable enable

using MajMusic.Core.Models;
using MajMusic.Infrastructure.Network;
using System.Buffers;
using System.Diagnostics;
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

        /// <summary>Serializes index access, atomic publication and cleanup without covering network transfers.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Protects short reservations for per-resource transfer gates.</summary>
        private readonly object _resourceGate = new();

        /// <summary>Coalesces only requests for the same resource identity.</summary>
        private readonly Dictionary<string, ResourceTransfer> _transfers = new(StringComparer.Ordinal);

        /// <summary>Protects completed objects from cleanup while their integrity is checked outside the index gate.</summary>
        private readonly Dictionary<string, int> _validationReaders = new(StringComparer.Ordinal);

        /// <summary>Limits a stalled response read without limiting a continuously advancing transfer.</summary>
        private readonly TimeSpan _bodyIdleTimeout;

        /// <summary>Contains persisted lookup aliases and their retention decisions.</summary>
        private Dictionary<string, CacheEntry>? _index;

        /// <summary>Serializes only local cache metadata.</summary>
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        /// <summary>Creates an idle managed cache without issuing requests.</summary>
        /// <param name="root">The application's managed cache directory.</param>
        /// <param name="client">An anonymous or endpoint-isolated media client; the caller owns it.</param>
        /// <param name="policy">The shared online/offline gate.</param>
        /// <param name="bodyIdleTimeout">The maximum response-read idle interval, or null for thirty seconds.</param>
        /// <exception cref="ArgumentException">The directory is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The idle interval is not positive.</exception>
        public MediaCache(string root, HttpClient client, NetworkAccessPolicy policy, TimeSpan? bodyIdleTimeout = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(policy);
            _root = Path.GetFullPath(root);
            _client = client;
            _policy = policy;
            _bodyIdleTimeout = bodyIdleTimeout ?? TimeSpan.FromSeconds(30);
            if (_bodyIdleTimeout <= TimeSpan.Zero || _bodyIdleTimeout.TotalMilliseconds > uint.MaxValue - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(bodyIdleTimeout));
            }
        }

        /// <summary>Resolves an imported file or a validated completed object, downloading only on an online cache miss.</summary>
        /// <param name="resource">The audio or cover descriptor.</param>
        /// <param name="keepOffline">Whether to pin this object against automatic cache cleanup.</param>
        /// <param name="cancellationToken">Cancels waiting, transfer, validation and publication.</param>
        /// <param name="downloadClient">An optional endpoint-isolated transport for this cache miss; its caller retains ownership.</param>
        /// <param name="progress">An optional observer of actual response bytes, including an already running transfer for this resource.</param>
        /// <returns>The fully validated local file path.</returns>
        /// <exception cref="InvalidOperationException">An uncached resource is requested in offline mode.</exception>
        /// <exception cref="InvalidDataException">A length, digest or cache index is invalid.</exception>
        /// <exception cref="HttpRequestException">The download fails.</exception>
        /// <exception cref="TimeoutException">The response body stops supplying bytes for the configured idle interval.</exception>
        /// <exception cref="FileNotFoundException">Neither a readable local file nor a remote address is available.</exception>
        public async Task<string> GetAsync(MediaResource resource, bool keepOffline = false, CancellationToken cancellationToken = default, HttpClient? downloadClient = null,
            IProgress<MediaTransferProgress>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(resource);
            var expectedHash = NormalizeHash(resource.Sha256);
            if (resource.LocalPath is { Length: > 0 } localPath && File.Exists(localPath))
            {
                var absolutePath = Path.GetFullPath(localPath);
                var objectRoot = Path.Combine(_root, "objects") + Path.DirectorySeparatorChar;
                if (!absolutePath.StartsWith(objectRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    await ValidateFileAsync(absolutePath, expectedHash, resource.Length, cancellationToken).ConfigureAwait(false);
                    return absolutePath;
                }

                var fileName = Path.GetFileName(absolutePath);
                var objectHash = expectedHash ?? (fileName.Length == 64 && fileName.All(Uri.IsHexDigit) ? fileName.ToLowerInvariant() : null);
                if (objectHash is null || !string.Equals(absolutePath, GetObjectPath(objectHash),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    await ValidateFileAsync(absolutePath, expectedHash, resource.Length, cancellationToken).ConfigureAwait(false);
                    return absolutePath;
                }

                // Canonical objects are validated once under a short cleanup lease in the normal cache-hit path.
                expectedHash = objectHash;
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

            var reservation = ReserveTransfer(key, progress);
            var transfer = reservation.Transfer;
            var entered = false;
            try
            {
                await transfer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                var cached = await TryGetCachedPathAsync(key, uriKey, expectedHash, resource.Length, keepOffline, cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    return cached;
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

                    MediaTransferProgress completed;
                    await using (var source = await response.Content.ReadAsStreamAsync(lifetime.Token).ConfigureAwait(false))
                    await using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    {
                        completed = await CopyResponseAsync(source, destination, response.Content.Headers.ContentLength ?? resource.Length,
                            transfer, lifetime.Token).ConfigureAwait(false);
                    }

                    var length = await ValidateFileAsync(partialPath, expectedHash, resource.Length, lifetime.Token).ConfigureAwait(false);
                    var contentHash = expectedHash ?? await HashFileAsync(partialPath, lifetime.Token).ConfigureAwait(false);
                    var finalPath = GetObjectPath(contentHash);
                    await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        var index = await LoadIndexAsync(lifetime.Token).ConfigureAwait(false);
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
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    ReportProgress(transfer, completed with { IsCompleted = true });
                    return finalPath;
                }
                finally
                {
                    File.Delete(partialPath);
                }
            }
            finally
            {
                ReleaseTransfer(key, transfer, reservation.Subscription, entered);
            }
        }

        /// <summary>Validates a completed object without keeping unrelated requests behind its disk read.</summary>
        /// <param name="key">The authoritative digest or revision-scoped lookup alias.</param>
        /// <param name="uriKey">The optional address alias to publish after validation.</param>
        /// <param name="expectedHash">The authoritative expected digest, when supplied.</param>
        /// <param name="expectedLength">The expected byte count, when supplied.</param>
        /// <param name="keepOffline">Whether to retain the completed object against cleanup.</param>
        /// <param name="cancellationToken">Cancels validation and publication.</param>
        /// <returns>The validated object path, or null for an unavailable or damaged object.</returns>
        private async Task<string?> TryGetCachedPathAsync(string key, string? uriKey, string? expectedHash, long? expectedLength,
            bool keepOffline, CancellationToken cancellationToken)
        {
            string? objectHash;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
                objectHash = expectedHash ?? index.GetValueOrDefault(key)?.ContentHash;
                if (objectHash is null || !File.Exists(GetObjectPath(objectHash)))
                {
                    return null;
                }

                _validationReaders[objectHash] = _validationReaders.GetValueOrDefault(objectHash) + 1;
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                var path = GetObjectPath(objectHash);
                long length;
                try
                {
                    length = await ValidateFileAsync(path, objectHash, expectedLength, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
                {
                    return null;
                }

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var latest = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
                    await PublishAliasesAsync(latest, uriKey, objectHash, length, keepOffline, cancellationToken).ConfigureAwait(false);
                    return path;
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var readers = _validationReaders[objectHash] - 1;
                    if (readers == 0)
                    {
                        _validationReaders.Remove(objectHash);
                    }
                    else
                    {
                        _validationReaders[objectHash] = readers;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        /// <summary>Receives response bytes with a per-read idle deadline and throttled progress.</summary>
        /// <param name="source">The response body owned by the current request.</param>
        /// <param name="destination">The private, unpublished temporary file.</param>
        /// <param name="totalBytes">The expected body length, when known.</param>
        /// <param name="transfer">The resource-specific progress subscribers.</param>
        /// <param name="cancellationToken">Cancels the current online request.</param>
        /// <returns>The final received-byte snapshot, ready for a successful publication report.</returns>
        private async Task<MediaTransferProgress> CopyResponseAsync(Stream source, Stream destination, long? totalBytes,
            ResourceTransfer transfer, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long received = 0;
            ReportProgress(transfer, new MediaTransferProgress { TotalBytes = totalBytes });
            try
            {
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(_bodyIdleTimeout);
                    int count;
                    try
                    {
                        count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && idle.IsCancellationRequested)
                    {
                        throw new TimeoutException("The media source stopped sending response bytes.");
                    }

                    if (count == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    received += count;
                    if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(100))
                    {
                        lastReport = clock.Elapsed;
                        ReportProgress(transfer, TransferSnapshot(received, totalBytes, clock.Elapsed));
                    }
                }

                if (totalBytes is { } expected && received != expected)
                {
                    throw new InvalidDataException("The response body length does not match its descriptor.");
                }

                return TransferSnapshot(received, totalBytes, clock.Elapsed);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Creates an immutable measured transfer snapshot.</summary>
        /// <param name="received">The number of bytes written successfully.</param>
        /// <param name="total">The expected response length, when known.</param>
        /// <param name="elapsed">The elapsed receive time.</param>
        /// <returns>The current byte count and average transfer rate.</returns>
        private static MediaTransferProgress TransferSnapshot(long received, long? total, TimeSpan elapsed)
        {
            return new MediaTransferProgress
            {
                ReceivedBytes = received,
                TotalBytes = total,
                BytesPerSecond = elapsed.TotalSeconds > 0 ? received / elapsed.TotalSeconds : null
            };
        }

        /// <summary>Reserves a resource gate and subscribes to its current and subsequent transfer snapshots.</summary>
        /// <param name="key">The authoritative resource lookup identity.</param>
        /// <param name="progress">The optional observer joining this request.</param>
        /// <returns>The reserved gate and optional subscription retained until this caller completes or cancels.</returns>
        private (ResourceTransfer Transfer, ProgressSubscription? Subscription) ReserveTransfer(string key, IProgress<MediaTransferProgress>? progress)
        {
            ResourceTransfer transfer;
            lock (_resourceGate)
            {
                if (!_transfers.TryGetValue(key, out transfer!))
                {
                    transfer = new ResourceTransfer();
                    _transfers.Add(key, transfer);
                }

                transfer.Users++;
            }

            ProgressSubscription? subscription = null;
            if (progress is not null)
            {
                subscription = new ProgressSubscription(progress);
                MediaTransferProgress? latest;
                long version;
                lock (transfer.ProgressGate)
                {
                    transfer.Observers.Add(subscription);
                    latest = transfer.Latest;
                    version = transfer.ProgressVersion;
                }

                if (latest is not null)
                {
                    NotifyProgress(subscription, latest, version);
                }
            }

            return (transfer, subscription);
        }

        /// <summary>Releases one subscription and removes unused resource gates without retaining an unbounded map.</summary>
        /// <param name="key">The resource identity originally reserved.</param>
        /// <param name="transfer">The exact reserved gate.</param>
        /// <param name="subscription">The optional observer leaving this request.</param>
        /// <param name="entered">Whether this caller acquired the resource semaphore.</param>
        private void ReleaseTransfer(string key, ResourceTransfer transfer, ProgressSubscription? subscription, bool entered)
        {
            if (subscription is not null)
            {
                lock (transfer.ProgressGate)
                {
                    transfer.Observers.Remove(subscription);
                }

                lock (subscription.Gate)
                {
                    subscription.IsDisposed = true;
                    subscription.Pending = null;
                }
            }

            if (entered)
            {
                transfer.Gate.Release();
            }

            lock (_resourceGate)
            {
                transfer.Users--;
                if (transfer.Users == 0)
                {
                    _transfers.Remove(key);
                    transfer.Gate.Dispose();
                }
            }
        }

        /// <summary>Serializes snapshots so a joining observer never receives an older snapshot after a newer one.</summary>
        /// <param name="transfer">The resource-specific subscribers.</param>
        /// <param name="snapshot">The measured transfer state.</param>
        private static void ReportProgress(ResourceTransfer transfer, MediaTransferProgress snapshot)
        {
            ProgressSubscription[] observers;
            long version;
            lock (transfer.ProgressGate)
            {
                transfer.Latest = snapshot;
                version = ++transfer.ProgressVersion;
                observers = transfer.Observers.ToArray();
            }

            foreach (var observer in observers)
            {
                NotifyProgress(observer, snapshot, version);
            }
        }

        /// <summary>Keeps an observer failure from aborting a validated media transfer shared by other callers.</summary>
        /// <param name="subscription">The progress receiver and its ordered pending state.</param>
        /// <param name="snapshot">The measured state to deliver.</param>
        /// <param name="version">The snapshot's monotonic transfer version.</param>
        private static void NotifyProgress(ProgressSubscription subscription, MediaTransferProgress snapshot, long version)
        {
            lock (subscription.Gate)
            {
                if (subscription.IsDisposed || version <= subscription.LatestVersion)
                {
                    return;
                }

                subscription.LatestVersion = version;
                subscription.Pending = snapshot;
                if (subscription.IsReporting)
                {
                    return;
                }

                subscription.IsReporting = true;
            }

            while (true)
            {
                MediaTransferProgress? pending;
                lock (subscription.Gate)
                {
                    pending = subscription.IsDisposed ? null : subscription.Pending;
                    subscription.Pending = null;
                    if (pending is null)
                    {
                        subscription.IsReporting = false;
                        return;
                    }
                }

                try
                {
                    subscription.Observer.Report(pending);
                }
                catch (Exception)
                {
                    // Progress observers do not own the transfer or its cache publication.
                }
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
                    if (_validationReaders.ContainsKey(item.Hash))
                    {
                        continue;
                    }

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
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }

        /// <summary>Tracks only callers currently resolving the same resource identity.</summary>
        private sealed class ResourceTransfer
        {
            /// <summary>Serializes transfer preparation for this resource alone.</summary>
            public SemaphoreSlim Gate { get; } = new(1, 1);
            /// <summary>Serializes observer registration and snapshot delivery.</summary>
            public object ProgressGate { get; } = new();
            /// <summary>Stores live observers retained by their own request lifetimes.</summary>
            public List<ProgressSubscription> Observers { get; } = new();
            /// <summary>Stores the latest actual transfer snapshot for a joining caller.</summary>
            public MediaTransferProgress? Latest { get; set; }
            /// <summary>Orders snapshots against subscription replay without holding locks during observer callbacks.</summary>
            public long ProgressVersion { get; set; }
            /// <summary>Counts owners and waiters under the cache's resource-map lock.</summary>
            public int Users { get; set; }
        }

        /// <summary>Delivers ordered, coalesced progress to one caller without invoking its code under cache locks.</summary>
        private sealed class ProgressSubscription
        {
            /// <summary>Creates one request-scoped progress receiver.</summary>
            /// <param name="observer">The caller's non-owning progress receiver.</param>
            public ProgressSubscription(IProgress<MediaTransferProgress> observer)
            {
                Observer = observer;
            }

            /// <summary>Protects only pending notification state.</summary>
            public object Gate { get; } = new();
            /// <summary>Gets the receiver owned by the requesting caller.</summary>
            public IProgress<MediaTransferProgress> Observer { get; }
            /// <summary>Stores the newest pending snapshot while another callback is reporting.</summary>
            public MediaTransferProgress? Pending { get; set; }
            /// <summary>Rejects an older replay that arrives after a newer snapshot.</summary>
            public long LatestVersion { get; set; } = -1;
            /// <summary>Allows only one callback drainer per subscriber.</summary>
            public bool IsReporting { get; set; }
            /// <summary>Rejects pending delivery after the caller has left the resource gate.</summary>
            public bool IsDisposed { get; set; }
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
