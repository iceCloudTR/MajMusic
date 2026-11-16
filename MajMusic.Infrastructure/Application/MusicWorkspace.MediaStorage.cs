#nullable enable

using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Downloads;
using MajMusic.Infrastructure.Shared;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Reuses MajdataPlay's public media hierarchy while retaining MajMusic's own settings and indexes.</summary>
    public sealed partial class MusicWorkspace : IMajdataPlayWorkspace
    {
        /// <summary>Accesses only an explicitly granted writable document tree.</summary>
        private readonly ISharedStorageAccess? _sharedStorageAccess;
        /// <summary>Keeps caches belonging to previously selected roots alive until workspace shutdown.</summary>
        private readonly Dictionary<string, MediaCache> _mediaStorageCaches = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        /// <summary>Serializes explicit rescans without blocking unrelated media resolution.</summary>
        private readonly SemaphoreSlim _mediaStorageRefresh = new(1, 1);
        /// <summary>Orders shared-file publication against explicit deletion of completed objects.</summary>
        private readonly SemaphoreSlim _sharedMediaWrites = new(1, 1);

        /// <inheritdoc />
        public string DefaultMajdataPlayDirectory => OperatingSystem.IsAndroid()
            ? "/storage/emulated/0/Documents/MajdataPlay"
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Majdata Hub", "game")
                : DataDirectory;

        /// <summary>Gets the selected independent or shared root without moving essential application data.</summary>
        /// <returns>The ordinary filesystem path or explicitly granted document-tree URI.</returns>
        private string GetMediaStorageRoot()
        {
            lock (_stateGate)
            {
                return _settings.UseMajdataPlayStorage
                    ? _settings.MajdataPlayDirectory ?? DefaultMajdataPlayDirectory
                    : _settings.MediaStorageDirectory ?? DataDirectory;
            }
        }

        /// <summary>Validates a new selection before persisting a location which the host cannot use.</summary>
        /// <param name="settings">The requested independent or shared media selection.</param>
        /// <exception cref="InvalidDataException">A system directory or ungranted Android public path was selected.</exception>
        private void ValidateMediaStorageLocation(AppSettings settings)
        {
            var root = settings.UseMajdataPlayStorage
                ? settings.MajdataPlayDirectory ?? DefaultMajdataPlayDirectory
                : settings.MediaStorageDirectory ?? DataDirectory;
            if (DocumentReference.IsContentUri(root))
            {
                return;
            }
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (OperatingSystem.IsAndroid()
                && normalized != DataDirectory && !normalized.StartsWith(DataDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException("请通过目录选择器选择媒体储存位置。");
            }
            if (OperatingSystem.IsWindows())
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (normalized.Equals(system, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(system + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("请选择应用或游戏的储存目录。");
                }
            }
        }

        /// <summary>Gets an instance whose immutable objects belong to the selected filesystem root.</summary>
        /// <returns>The cache for the selected root, or the private transfer staging cache for document trees.</returns>
        private MediaCache GetCurrentMediaCache()
        {
            var root = GetMediaStorageRoot();
            if (DocumentReference.IsContentUri(root))
            {
                return _defaultCache;
            }
            root = Path.GetFullPath(root);
            if (string.Equals(root, DataDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return _defaultCache;
            }
            lock (_stateGate)
            {
                if (!_mediaStorageCaches.TryGetValue(root, out var cache))
                {
                    var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
                    cache = new MediaCache(Path.Combine(root, "Cache"), _resourceClient, _networkPolicy,
                        indexDirectory: Path.Combine(DataDirectory, "Runtime", "MajMusicCache", key));
                    _mediaStorageCaches.Add(root, cache);
                }
                return cache;
            }
        }

        /// <inheritdoc />
        public async Task<LocalLibraryScanResult> RefreshMajdataPlayAsync(CancellationToken cancellationToken = default)
        {
            await _mediaStorageRefresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var root = GetMediaStorageRoot();
                var tracks = new List<MusicTrack>();
                var issues = new List<LocalLibraryScanIssue>();
                var charts = await FindStorageItemAsync(root, "MaiCharts", true, cancellationToken).ConfigureAwait(false);
                if (charts is not null)
                {
                    var scanned = await IndexDirectoryAsync(charts, cancellationToken).ConfigureAwait(false);
                    tracks.AddRange(scanned.Tracks);
                    issues.AddRange(scanned.Issues);
                    var collections = new MajCollectionExchange(this, _documentStorage);
                    await collections.ImportIndexDirectoryAsync(charts, true, cancellationToken).ConfigureAwait(false);
                }
                if (!DocumentReference.IsContentUri(root) && Directory.Exists(root))
                {
                    EndpointConfiguration[] endpoints;
                    lock (_stateGate)
                    {
                        endpoints = _settings.Endpoints.ToArray();
                    }
                    var online = await new MajdataPlayCatalog(root).ReadAsync(endpoints, cancellationToken).ConfigureAwait(false);
                    if (online.Count > 0)
                    {
                        tracks.AddRange(await MergeOnlineTracksAsync(online, cancellationToken).ConfigureAwait(false));
                    }
                    var legacy = Directory.Exists(Path.Combine(root, "Cache", "Net"));
                    var catalog = new MajdataPlayCatalog(root);
                    var attachments = new List<MusicTrack>();
                    foreach (var track in await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false))
                    {
                        var endpoint = endpoints.FirstOrDefault(item => item.Id == track.EndpointId);
                        if (track.RemoteId is null || endpoint is null || !legacy && track.Audio.Sha256 is null && track.Cover?.Sha256 is null)
                        {
                            continue;
                        }
                        var attached = await catalog.AttachResourcesAsync(track, endpoint, cancellationToken).ConfigureAwait(false);
                        if (attached.Audio.LocalPath != track.Audio.LocalPath || attached.Cover?.LocalPath != track.Cover?.LocalPath)
                        {
                            attachments.Add(attached);
                        }
                    }
                    if (attachments.Count > 0)
                    {
                        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await _library.UpsertTracksAsync(attachments, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            _metadataGate.Release();
                        }
                    }
                }
                await MirrorAllPortablePlaylistsAsync(cancellationToken).ConfigureAwait(false);
                return new LocalLibraryScanResult { RootDirectory = root, Tracks = tracks, Issues = issues };
            }
            finally
            {
                _mediaStorageRefresh.Release();
            }
        }

        /// <summary>Finds only an exact child of the configured root, retaining document-provider references.</summary>
        /// <param name="root">The chosen root.</param>
        /// <param name="relativePath">A trusted application-relative child path.</param>
        /// <param name="directory">Whether the child is a directory.</param>
        /// <param name="cancellationToken">Cancels provider access.</param>
        /// <returns>The original path or document URI when available.</returns>
        private Task<string?> FindStorageItemAsync(string root, string relativePath, bool directory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DocumentReference.IsContentUri(root))
            {
                return _sharedStorageAccess?.FindAsync(root, relativePath, directory, cancellationToken)
                    ?? Task.FromResult<string?>(null);
            }
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return Task.FromResult<string?>((directory ? Directory.Exists(path) : File.Exists(path)) ? path : null);
        }

        /// <summary>Reuses only content whose bytes agree with the source digest; online identity remains unchanged.</summary>
        /// <param name="track">The original online descriptor.</param>
        /// <param name="cover">Whether to resolve artwork.</param>
        /// <param name="cancellationToken">Cancels hash validation.</param>
        /// <returns>The original matching file or document URI, or null on a miss.</returns>
        private async Task<string?> TryResolveSharedMediaAsync(MusicTrack track, bool cover, CancellationToken cancellationToken)
        {
            if (track.RemoteId is null)
            {
                return null;
            }
            var resource = cover ? track.Cover : track.Audio;
            var root = GetMediaStorageRoot();
            if (resource?.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit))
            {
                var path = await FindStorageItemAsync(root, MajdataPlayCatalog.GetObjectRelativePath(hash), false, cancellationToken).ConfigureAwait(false);
                if (path is not null && await MatchesSharedFileAsync(path, hash, resource.Length, cancellationToken).ConfigureAwait(false))
                {
                    return path;
                }
            }
            if (!DocumentReference.IsContentUri(root) && Directory.Exists(root))
            {
                EndpointConfiguration? endpoint;
                lock (_stateGate)
                {
                    endpoint = _settings.Endpoints.FirstOrDefault(item => item.Id == track.EndpointId);
                }
                if (endpoint is not null)
                {
                    var attached = await new MajdataPlayCatalog(root).AttachResourcesAsync(track, endpoint, cancellationToken).ConfigureAwait(false);
                    var candidate = cover ? attached.Cover : attached.Audio;
                    if (candidate?.LocalPath is { } path && File.Exists(path) && candidate.Sha256 is { } digest
                        && await MatchesSharedFileAsync(path, digest, candidate.Length, cancellationToken).ConfigureAwait(false))
                    {
                        return path;
                    }
                }
            }
            return null;
        }

        /// <summary>Publishes a completed private transfer to an authorized shared tree using Maj's immutable object name.</summary>
        /// <param name="track">The owning online entry.</param>
        /// <param name="cover">Whether the completed file is artwork.</param>
        /// <param name="path">The validated completed filesystem object.</param>
        /// <param name="cancellationToken">Cancels hashing and publication.</param>
        /// <param name="mediaVersion">The original removal generation, when resolution began before this publication.</param>
        /// <returns>The shared document URI, or the original filesystem path.</returns>
        private async Task<string> PublishSharedMediaAsync(MusicTrack track, bool cover, string path, CancellationToken cancellationToken, long? mediaVersion = null)
        {
            var version = mediaVersion ?? LocalMediaVersion(track);
            await _sharedMediaWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureLocalMediaVersion(track, version);
                return await PublishSharedMediaCoreAsync(track, cover, path, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sharedMediaWrites.Release();
            }
        }

        /// <summary>Publishes an immutable object while shared publication and removal are exclusively owned.</summary>
        /// <param name="track">The source-scoped owner.</param>
        /// <param name="cover">Whether this is artwork.</param>
        /// <param name="path">The complete validated source file.</param>
        /// <param name="cancellationToken">Cancels the transfer and alias cleanup.</param>
        /// <returns>The completed original file or shared document reference.</returns>
        private async Task<string> PublishSharedMediaCoreAsync(MusicTrack track, bool cover, string path, CancellationToken cancellationToken)
        {
            var root = GetMediaStorageRoot();
            if (track.RemoteId is null || DocumentReference.IsContentUri(path))
            {
                return path;
            }
            if (!DocumentReference.IsContentUri(root))
            {
                return await PublishNativeMediaAsync(root, path, cancellationToken).ConfigureAwait(false);
            }
            var storage = _sharedStorageAccess ?? throw new NotSupportedException("请重新选择媒体储存目录并授予读写权限。");
            string hash;
            long length;
            await using (var stream = File.OpenRead(path))
            {
                length = stream.Length;
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }
            var relative = MajdataPlayCatalog.GetObjectRelativePath(hash);
            var existing = await storage.FindAsync(root, relative, false, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!await MatchesSharedFileAsync(existing, hash, length, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("共享目录中的文件校验失败，请删除该本地文件后重试。");
                }
                return existing;
            }
            var published = await storage.PublishFileAsync(root, relative, path, cancellationToken).ConfigureAwait(false);
            return published;
        }

        /// <summary>Removes the private transfer staging copy after its complete bytes are committed to a granted shared tree.</summary>
        /// <param name="track">The source-scoped owner of this completed transfer.</param>
        /// <param name="cover">Whether its resource is artwork.</param>
        /// <param name="path">The private completed staging object.</param>
        /// <param name="hash">Its verified SHA-256.</param>
        /// <param name="cancellationToken">Cancels alias cleanup.</param>
        /// <returns>A task representing staging cleanup without touching imported source files.</returns>
        private async Task ReleasePrivateTransferAsync(MusicTrack track, bool cover, string path, string? hash, CancellationToken cancellationToken)
        {
            if (!_defaultCache.IsManagedPath(path))
            {
                return;
            }
            var descriptor = (cover ? track.Cover : track.Audio)! with { LocalPath = path, Sha256 = hash ?? Path.GetFileName(path) };
            try
            {
                await _defaultCache.RemoveResourcesAsync([ScopeResourceForCache(track, descriptor)], new HashSet<string>(), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // An existing reader can finish with the old private copy; the shared file and metadata are already committed.
            }
        }

        /// <summary>Moves managed playback into the selected native hierarchy using verified immutable publication.</summary>
        /// <param name="root">The selected native media root.</param>
        /// <param name="source">The already validated completed file.</param>
        /// <param name="cancellationToken">Cancels hashing and copying before publication.</param>
        /// <returns>The reusable original legacy file or the selected root's canonical object.</returns>
        private async Task<string> PublishNativeMediaAsync(string root, string source, CancellationToken cancellationToken)
        {
            var normalizedRoot = Path.GetFullPath(root);
            var absolute = Path.GetFullPath(source);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var relativeSource = Path.GetRelativePath(normalizedRoot, absolute).Replace('\\', '/');
            if (relativeSource.StartsWith("Cache/Objects/", StringComparison.OrdinalIgnoreCase) || relativeSource.StartsWith("Cache/Net/", comparison))
            {
                return absolute;
            }
            string hash;
            long length;
            await using (var input = File.OpenRead(absolute))
            {
                length = input.Length;
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            }
            var destination = Path.Combine(normalizedRoot, MajdataPlayCatalog.GetObjectRelativePath(hash));
            if (File.Exists(destination))
            {
                if (!await MatchesSharedFileAsync(destination, hash, length, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("媒体目录中的本地文件校验失败。");
                }
                return destination;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var input = File.OpenRead(absolute))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    if (!await MatchesSharedFileAsync(destination, hash, length, cancellationToken).ConfigureAwait(false))
                    {
                        throw;
                    }
                }
                return destination;
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        /// <summary>Deletes verified shared objects and compatible legacy Net files while excluding every MaiCharts source.</summary>
        /// <param name="track">The captured online owner.</param>
        /// <param name="protectedHashes">Objects still referenced by other music or game entries.</param>
        /// <param name="cancellationToken">Cancels validation and provider access.</param>
        /// <returns>Detached audio, cover and chart flags plus the number of removed physical files.</returns>
        private async Task<(bool[] Removed, int Deleted)> RemoveSharedMediaAsync(MusicTrack track, IReadOnlySet<string> protectedHashes, CancellationToken cancellationToken)
        {
            await _sharedMediaWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RemoveSharedMediaCoreAsync(track, protectedHashes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sharedMediaWrites.Release();
            }
        }

        /// <summary>Removes only verified owned resources while holding the shared-publication gate.</summary>
        /// <param name="track">The captured online owner.</param>
        /// <param name="protectedHashes">The complete protected public-resource digest set.</param>
        /// <param name="cancellationToken">Cancels provider reads and deletion.</param>
        /// <returns>Per-resource removal flags and the physical file count.</returns>
        private async Task<(bool[] Removed, int Deleted)> RemoveSharedMediaCoreAsync(MusicTrack track, IReadOnlySet<string> protectedHashes, CancellationToken cancellationToken)
        {
            var root = GetMediaStorageRoot();
            var descriptors = new[] { track.Audio, track.Cover, track.Chart };
            var removed = new bool[3];
            var deleted = 0;
            for (var index = 0; index < descriptors.Length; index++)
            {
                var resource = descriptors[index];
                if (resource?.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
                {
                    continue;
                }
                if (protectedHashes.Contains(hash))
                {
                    continue;
                }
                var relative = MajdataPlayCatalog.GetObjectRelativePath(hash);
                var reference = await FindStorageItemAsync(root, relative, false, cancellationToken).ConfigureAwait(false);
                if (reference is null && !DocumentReference.IsContentUri(root) && resource.LocalPath is { } legacy && File.Exists(legacy))
                {
                    var candidate = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(legacy)).Replace('\\', '/');
                    if (candidate.StartsWith("Cache/Net/", StringComparison.Ordinal)
                        && !candidate.Split('/').Any(segment => segment is "." or "..")
                        && Path.GetFileName(candidate) is "track.mp3" or "track.ogg" or "track.opus" or "track.aac" or "track.wav" or "bg.jpg" or "bg.png" or "bg_fullSize.jpg" or "maidata.txt")
                    {
                        reference = legacy;
                    }
                }
                if (reference is null || !await MatchesSharedFileAsync(reference, hash, resource.Length, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                if (DocumentReference.IsContentUri(root))
                {
                    await (_sharedStorageAccess ?? throw new NotSupportedException("共享储存不可用。"))
                        .DeleteFileAsync(root, relative, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    File.Delete(reference);
                    if (reference.Replace('\\', '/').Contains("/Cache/Net/", StringComparison.Ordinal))
                    {
                        File.Delete(reference + ".sha256");
                    }
                }
                removed[index] = true;
                deleted++;
            }
            return (removed, deleted);
        }

        /// <summary>Checks the actual bytes of a public immutable object before playback or reuse.</summary>
        /// <param name="reference">The original file path or granted document URI.</param>
        /// <param name="hash">The expected SHA-256.</param>
        /// <param name="length">The expected size when supplied.</param>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>Whether both integrity checks succeed.</returns>
        private async Task<bool> MatchesSharedFileAsync(string reference, string hash, long? length, CancellationToken cancellationToken)
        {
            await using var stream = await OpenMediaReadAsync(reference, cancellationToken).ConfigureAwait(false);
            if (length is { } expected && stream.CanSeek && stream.Length != expected)
            {
                return false;
            }
            var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(actual).Equals(hash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Opens original media without translating document URIs into local paths.</summary>
        /// <param name="reference">The original readable file or document.</param>
        /// <param name="cancellationToken">Cancels provider access.</param>
        /// <returns>A caller-owned readable stream.</returns>
        private Task<Stream> OpenMediaReadAsync(string reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DocumentReference.IsContentUri(reference)
                ? (_sharedStorageAccess?.OpenReadAsync(reference, cancellationToken)
                    ?? (_documentStorage ?? throw new NotSupportedException("文档储存不可用。")).OpenReadAsync(reference, cancellationToken))
                : Task.FromResult<Stream>(File.OpenRead(reference));
        }

        /// <summary>Predownloads the original chart text alongside an explicit music download without enabling chart preview.</summary>
        /// <param name="track">The descriptor containing the source's original maidata resource.</param>
        /// <param name="cancellationToken">Cancels this explicit predownload.</param>
        /// <returns>A task representing chart publication and its local metadata update.</returns>
        private async Task PreserveSharedChartAsync(MusicTrack track, CancellationToken cancellationToken)
        {
            if (track.RemoteId is null || track.Chart is not { } chart)
            {
                return;
            }
            var version = LocalMediaVersion(track);
            var source = track with { Audio = chart };
            var original = await GetMediaAsync(source, false, true, cancellationToken).ConfigureAwait(false);
            var path = await PublishSharedMediaAsync(source, false, original, cancellationToken, version).ConfigureAwait(false);
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false);
                EnsureLocalMediaVersion(track, version);
                if (current is not null && SameRevision(current, track))
                {
                    await _library.UpsertTracksAsync([current with { Chart = chart with { LocalPath = path } }], cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _metadataGate.Release();
            }
            if (path != original)
            {
                await ReleasePrivateTransferAsync(source, false, original, chart.Sha256, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Registers verified media with MajdataPlay so its normal startup scan retains these referenced objects.</summary>
        /// <param name="track">The completed online entry.</param>
        /// <param name="cancellationToken">Cancels the public metadata transaction.</param>
        /// <returns>A task representing source-scoped registration without accessing account tables.</returns>
        private async Task<bool> RegisterSharedTrackAsync(MusicTrack track, CancellationToken cancellationToken)
        {
            var root = GetMediaStorageRoot();
            if (track.RemoteId is null || DocumentReference.IsContentUri(root))
            {
                return true;
            }
            EndpointConfiguration? endpoint;
            lock (_stateGate)
            {
                endpoint = _settings.Endpoints.FirstOrDefault(item => item.Id == track.EndpointId);
            }
            if (endpoint is null)
            {
                return false;
            }
            var current = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false) ?? track;
            try
            {
                if (!await new MajdataPlayCatalog(root).RegisterResourcesAsync(current, endpoint, cancellationToken).ConfigureAwait(false))
                {
                    System.Diagnostics.Trace.TraceWarning("MajdataPlay resource registration kept a different source revision.");
                    return false;
                }
                return true;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                System.Diagnostics.Trace.TraceWarning("MajdataPlay resource registration could not complete: {0}", exception.GetType().Name);
                return false;
            }
        }

        /// <summary>Releases all media-root owners after active operations have drained.</summary>
        private void DisposeMediaStorage()
        {
            _defaultCache.Dispose();
            foreach (var cache in _mediaStorageCaches.Values)
            {
                cache.Dispose();
            }
            _mediaStorageCaches.Clear();
            _mediaStorageRefresh.Dispose();
            _portablePlaylistWrites.Dispose();
            _sharedMediaWrites.Dispose();
        }
    }
}
