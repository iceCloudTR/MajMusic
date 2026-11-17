#nullable enable

using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Downloads;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Owns Music's selected media directory without reading another application's storage.</summary>
    public sealed partial class MusicWorkspace
    {
        /// <summary>Accesses only a directory explicitly authorized for Music media.</summary>
        private readonly IMediaStorageAccess? _mediaStorageAccess;
        /// <summary>Retains previous native cache owners until active operations have drained.</summary>
        private readonly Dictionary<string, MediaCache> _mediaStorageCaches = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        /// <summary>Orders managed document publication against explicit file deletion.</summary>
        private readonly SemaphoreSlim _mediaWrites = new(1, 1);

        /// <summary>Gets the selected independent root or Music's private default.</summary>
        private string GetMediaStorageRoot()
        {
            lock (_stateGate)
            {
                return _settings.MediaStorageDirectory ?? DataDirectory;
            }
        }

        /// <summary>Rejects invalid destinations before saving a media-directory selection.</summary>
        /// <param name="settings">The requested independent media preferences.</param>
        private void ValidateMediaStorageLocation(AppSettings settings)
        {
            var root = settings.MediaStorageDirectory ?? DataDirectory;
            if (DocumentReference.IsContentUri(root))
            {
                return;
            }
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (OperatingSystem.IsAndroid() && !Directory.Exists(normalized))
            {
                throw new InvalidDataException("媒体目录不可访问，请重新选择目录并授予访问权限。");
            }
            if (OperatingSystem.IsWindows())
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (normalized.Equals(system, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(system + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("请选择应用的媒体储存目录。");
                }
            }
        }

        /// <summary>Gets the selected native cache or private staging cache used by a document provider.</summary>
        private MediaCache GetCurrentMediaCache()
        {
            var root = GetMediaStorageRoot();
            if (DocumentReference.IsContentUri(root) || SameMediaDirectory(root, DataDirectory))
            {
                return _defaultCache;
            }
            root = Path.GetFullPath(root);
            lock (_stateGate)
            {
                if (!_mediaStorageCaches.TryGetValue(root, out var cache))
                {
                    var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
                    cache = new MediaCache(Path.Combine(root, "Cache"), _resourceClient, _networkPolicy,
                        indexDirectory: Path.Combine(DataDirectory, "Runtime", "MajMusicCache", key), requireIndexOwnership: true);
                    _mediaStorageCaches.Add(root, cache);
                }
                return cache;
            }
        }

        /// <summary>Builds Music's immutable relative media filename from a validated digest.</summary>
        /// <param name="hash">The complete SHA-256 digest.</param>
        private static string MediaObjectRelativePath(string hash)
        {
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("A media object requires a SHA-256 digest.");
            }
            var normalized = hash.ToLowerInvariant();
            return Path.Combine("Cache", "Objects", normalized[..2], normalized);
        }

        /// <summary>Resolves only a document previously published by Music into its currently selected root.</summary>
        /// <param name="track">The source-owned music entry.</param>
        /// <param name="cover">Whether the resource is artwork.</param>
        /// <param name="token">Cancels document validation.</param>
        private async Task<string?> TryResolveMediaDocumentAsync(MusicTrack track, bool cover, CancellationToken token)
        {
            var resource = cover ? track.Cover : track.Audio;
            if (resource is null)
            {
                return null;
            }
            var reference = resource.LocalPath;
            if (!DocumentReference.IsContentUri(reference) && resource.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit))
            {
                reference = await FindOwnedDocumentAsync(hash, token).ConfigureAwait(false);
                resource = resource with { LocalPath = reference, ManagedStorageRoot = reference is null ? null : GetMediaStorageRoot() };
            }
            if (reference is null || !DocumentReference.IsContentUri(reference))
            {
                return null;
            }
            var safe = await FilterOwnedResourceAsync(resource, token).ConfigureAwait(false);
            return safe.LocalPath is not null && await MatchesMediaFileAsync(reference, resource.Sha256!, resource.Length, token).ConfigureAwait(false) ? reference : null;
        }

        /// <summary>Publishes a completed Music transfer into its selected document provider.</summary>
        /// <param name="track">The captured source revision.</param>
        /// <param name="cover">Whether the resource is artwork.</param>
        /// <param name="path">The already validated completed Music file.</param>
        /// <param name="token">Cancels provider I/O.</param>
        /// <param name="mediaVersion">Rejects a publication superseded by explicit deletion.</param>
        private async Task<string> PublishMediaAsync(MusicTrack track, bool cover, string path, CancellationToken token, long? mediaVersion = null)
        {
            var root = GetMediaStorageRoot();
            if (!RequiresManagedMedia(track) || !DocumentReference.IsContentUri(root) || DocumentReference.IsContentUri(path))
            {
                return path;
            }
            await _mediaWrites.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (mediaVersion is { } version)
                {
                    EnsureLocalMediaVersion(track, version);
                }
                var access = _mediaStorageAccess ?? throw new NotSupportedException("请重新选择媒体目录并授予读写权限。");
                await using var stream = File.OpenRead(path);
                var length = stream.Length;
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                var relative = MediaObjectRelativePath(hash);
                var existing = await access.FindAsync(root, relative, false, token).ConfigureAwait(false);
                if (existing is not null)
                {
                    // An old, unclaimed document must not silently become managed media.
                    var descriptor = cover ? track.Cover : track.Audio;
                    var owned = descriptor?.ManagedStorageRoot == root && descriptor.LocalPath == existing
                        && string.Equals(descriptor.Sha256, hash, StringComparison.OrdinalIgnoreCase);
                    if (!owned)
                    {
                        owned = await FindOwnedDocumentAsync(hash, token).ConfigureAwait(false) == existing;
                    }
                    if (!owned || !await MatchesMediaFileAsync(existing, hash, length, token).ConfigureAwait(false))
                    {
                        throw new IOException("媒体目录存在同名文件，请选择空的 Music 媒体目录。");
                    }
                    return existing;
                }
                return await access.PublishFileAsync(root, relative, path, token).ConfigureAwait(false);
            }
            finally
            {
                _mediaWrites.Release();
            }
        }

        /// <summary>Finds a document claimed by Music's own library in the selected media root.</summary>
        /// <param name="hash">The validated content identity.</param>
        /// <param name="token">Cancels the private library lookup and document traversal.</param>
        /// <returns>The claimed document reference, or null when Music has no ownership record.</returns>
        private async Task<string?> FindOwnedDocumentAsync(string hash, CancellationToken token)
        {
            var root = GetMediaStorageRoot();
            if (!DocumentReference.IsContentUri(root) || _mediaStorageAccess is not { } access)
            {
                return null;
            }
            var library = await _library.SearchAsync(null, token).ConfigureAwait(false);
            var claimed = library.Where(RequiresManagedMedia).SelectMany(item => new[] { item.Audio, item.Cover }).OfType<MediaResource>()
                .Where(item => item.ManagedStorageRoot == root && item.LocalPath is not null
                    && string.Equals(item.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.LocalPath).ToHashSet(StringComparer.Ordinal);
            if (claimed.Count == 0)
            {
                return null;
            }
            var found = await access.FindAsync(root, MediaObjectRelativePath(hash), false, token).ConfigureAwait(false);
            return found is not null && claimed.Contains(found) ? found : null;
        }

        /// <summary>Removes only Music-published documents whose content is no longer referenced by another Music entry.</summary>
        /// <param name="track">The current source-owned entry.</param>
        /// <param name="protectedHashes">Content still used by other Music entries.</param>
        /// <param name="token">Cancels provider access.</param>
        private async Task<(bool[] Removed, int Deleted)> RemoveMediaDocumentsAsync(MusicTrack track, IReadOnlySet<string> protectedHashes, CancellationToken token)
        {
            var resources = new[] { track.Audio, track.Cover };
            var removed = new bool[2];
            var deleted = 0;
            if (!DocumentReference.IsContentUri(GetMediaStorageRoot()) || _mediaStorageAccess is null)
            {
                return (removed, deleted);
            }
            await _mediaWrites.WaitAsync(token).ConfigureAwait(false);
            try
            {
                for (var index = 0; index < resources.Length; index++)
                {
                    var resource = resources[index];
                    if (resource?.Sha256 is not { } hash || resource.LocalPath is not { } reference || !DocumentReference.IsContentUri(reference))
                    {
                        continue;
                    }
                    var owned = await FilterOwnedResourceAsync(resource, token).ConfigureAwait(false);
                    if (owned.LocalPath is null || !await MatchesMediaFileAsync(reference, hash, resource.Length, token).ConfigureAwait(false))
                    {
                        continue;
                    }
                    removed[index] = true;
                    if (protectedHashes.Contains(hash))
                    {
                        continue;
                    }
                    await _mediaStorageAccess.DeleteFileAsync(resource.ManagedStorageRoot!, MediaObjectRelativePath(hash), token).ConfigureAwait(false);
                    deleted++;
                }
            }
            finally
            {
                _mediaWrites.Release();
            }
            return (removed, deleted);
        }

        /// <summary>Checks the actual bytes of one owned native or document resource.</summary>
        private async Task<bool> MatchesMediaFileAsync(string reference, string hash, long? length, CancellationToken token)
        {
            await using var stream = await OpenMediaReadAsync(reference, token).ConfigureAwait(false);
            if (length is { } expected && stream.CanSeek && stream.Length != expected)
            {
                return false;
            }
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(hash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Opens an authorized document or an already selected ordinary media file.</summary>
        private Task<Stream> OpenMediaReadAsync(string reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return DocumentReference.IsContentUri(reference)
                ? (_mediaStorageAccess ?? throw new NotSupportedException("媒体目录访问不可用。")).OpenReadAsync(reference, token)
                : Task.FromResult<Stream>(new FileStream(reference, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }

        /// <summary>Rejects a resource publication superseded by a different media-directory choice.</summary>
        /// <param name="root">The root captured before resolution.</param>
        /// <exception cref="OperationCanceledException">The selected media directory changed.</exception>
        private void EnsureMediaStorageRoot(string root)
        {
            if (!SameMediaDirectory(root, GetMediaStorageRoot()))
            {
                throw new OperationCanceledException("The Music media directory changed during resolution.");
            }
        }

        /// <summary>Compares native media roots or exact provider-owned tree references.</summary>
        private static bool SameMediaDirectory(string first, string second)
        {
            return DocumentReference.IsContentUri(first) || DocumentReference.IsContentUri(second)
                ? first == second
                : string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>Releases directory owners only after active workspace operations have drained.</summary>
        private void DisposeMediaStorage()
        {
            _defaultCache.Dispose();
            foreach (var cache in _mediaStorageCaches.Values)
            {
                cache.Dispose();
            }
            _mediaStorageCaches.Clear();
            _mediaWrites.Dispose();
            _localMediaLookups.Dispose();
        }
    }
}
