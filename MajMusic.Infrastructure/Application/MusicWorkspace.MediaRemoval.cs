#nullable enable

using System.Collections.Concurrent;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Downloads;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Removes Music-owned media while retaining source metadata and playlist membership.</summary>
    public sealed partial class MusicWorkspace : ITrackMediaRemovalWorkspace, ITrackMediaRemovalBatchWorkspace
    {
        /// <summary>Rejects a resolution which finishes after its files were explicitly removed.</summary>
        private readonly ConcurrentDictionary<(string Id, string Source, string? Endpoint, string? Revision), long> _localMediaVersions = new();
        /// <summary>Reuses one library snapshot during a batch cleanup operation.</summary>
        private readonly AsyncLocal<IReadOnlyList<MusicTrack>?> _batchRemovalLibrary = new();
        /// <summary>Reuses Music-only protection sets during one cleanup batch.</summary>
        private readonly AsyncLocal<(IReadOnlySet<string> Hashes, IReadOnlySet<string> Paths)?> _batchMediaProtection = new();

        /// <inheritdoc />
        public async Task<IReadOnlyList<TrackMediaRemovalResult>> RemoveTrackMediaBatchAsync(IReadOnlyList<MusicTrack> tracks, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            var owners = tracks.Where(RequiresManagedMedia).Select(LocalMediaOwner).ToHashSet();
            var library = await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false);
            var previous = _batchRemovalLibrary.Value;
            var previousProtection = _batchMediaProtection.Value;
            _batchRemovalLibrary.Value = library.Where(track => !owners.Contains(LocalMediaOwner(track))).ToArray();
            try
            {
                _batchMediaProtection.Value = CreateMediaProtection(_cache, _batchRemovalLibrary.Value);
                var results = new List<TrackMediaRemovalResult>(tracks.Count);
                foreach (var track in tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(await RemoveTrackMediaAsync(track, cancellationToken).ConfigureAwait(false));
                }
                return results;
            }
            finally
            {
                _batchRemovalLibrary.Value = previous;
                _batchMediaProtection.Value = previousProtection;
            }
        }

        /// <inheritdoc />
        public async Task<TrackMediaRemovalResult> RemoveTrackMediaAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var stored = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false) ?? track;
                if (!RequiresManagedMedia(stored) || stored.SourceId != track.SourceId || stored.EndpointId != track.EndpointId || !SameRevision(stored, track))
                {
                    return new TrackMediaRemovalResult(stored, false, false, 0);
                }
                _localMediaVersions.AddOrUpdate(LocalMediaOwner(stored), 1, static (_, version) => version + 1);
                var current = await FilterOwnedTrackAsync(stored, cancellationToken).ConfigureAwait(false);
                var root = GetMediaStorageRoot();
                var cache = _cache;
                var resources = new List<MediaResource> { ScopeResourceForCache(current, current.Audio) with { LocalPath = null, ManagedStorageRoot = null } };
                if (current.Cover is { } cover)
                {
                    resources.Add(ScopeResourceForCache(current, cover) with { LocalPath = null, ManagedStorageRoot = null });
                }
                foreach (var preview in new[] { current.Chart, current.Video }.OfType<MediaResource>())
                {
                    resources.Add(ScopeResourceForCache(current, preview) with { LocalPath = null, ManagedStorageRoot = null });
                }
                var library = _batchRemovalLibrary.Value ?? await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false);
                var protection = _batchMediaProtection.Value ?? CreateMediaProtection(cache, library.Where(item => LocalMediaOwner(item) != LocalMediaOwner(current)));
                var hashes = protection.Hashes;
                var paths = protection.Paths;
                var documents = await RemoveMediaDocumentsAsync(current, hashes, cancellationToken).ConfigureAwait(false);
                var removed = await cache.RemoveResourcesAsync(resources, paths, cancellationToken).ConfigureAwait(false);
                var privateRemoved = ReferenceEquals(cache, _defaultCache)
                    ? (Removed: new bool[resources.Count], DeletedFiles: 0)
                    : await _defaultCache.RemoveResourcesAsync(resources.Select(resource => resource with { LocalPath = null, ManagedStorageRoot = null }).ToArray(), paths, cancellationToken).ConfigureAwait(false);
                var audioRemoved = documents.Removed[0] || removed.Removed[0] || privateRemoved.Removed[0];
                var coverRemoved = current.Cover is not null && (documents.Removed[1] || removed.Removed[1] || privateRemoved.Removed[1]);
                var updated = current with
                {
                    Audio = audioRemoved ? current.Audio with { LocalPath = null, ManagedStorageRoot = null } : current.Audio,
                    Cover = coverRemoved ? current.Cover! with { LocalPath = null, ManagedStorageRoot = null } : current.Cover,
                    Chart = null,
                    Video = null,
                    LocalMediaSavedAt = audioRemoved ? null : current.LocalMediaSavedAt,
                    AudioMetadataLength = audioRemoved ? null : current.AudioMetadataLength,
                    AudioMetadataLastWriteUtcTicks = audioRemoved ? null : current.AudioMetadataLastWriteUtcTicks
                };
                if (!SameMediaDirectory(root, GetMediaStorageRoot()))
                {
                    updated = await FilterOwnedTrackAsync(updated, cancellationToken).ConfigureAwait(false);
                }
                if (updated != stored)
                {
                    await _library.UpsertTracksAsync([updated], CancellationToken.None).ConfigureAwait(false);
                }
                return new TrackMediaRemovalResult(updated, audioRemoved, coverRemoved, removed.DeletedFiles + privateRemoved.DeletedFiles + documents.Deleted);
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        /// <summary>Protects objects still referenced by another Music library entry.</summary>
        /// <param name="cache">The selected Music cache.</param>
        /// <param name="library">Entries outside the removal selection.</param>
        /// <returns>Content hashes and canonical native paths which must remain.</returns>
        private (IReadOnlySet<string> Hashes, IReadOnlySet<string> Paths) CreateMediaProtection(MediaCache cache, IEnumerable<MusicTrack> library)
        {
            var descriptors = library.SelectMany(item => new[] { item.Audio, item.Cover, item.Chart, item.Video }).OfType<MediaResource>().ToArray();
            var hashes = descriptors.Select(item => item.Sha256).OfType<string>()
                .Where(hash => hash.Length == 64 && hash.All(Uri.IsHexDigit)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var owner in new[] { cache, _defaultCache }.Distinct())
            {
                foreach (var descriptor in descriptors)
                {
                    if (owner.IsManagedPath(descriptor.LocalPath))
                    {
                        paths.Add(Path.GetFullPath(descriptor.LocalPath!));
                    }
                }
                foreach (var hash in hashes)
                {
                    paths.Add(owner.GetManagedObjectPath(hash));
                }
            }
            return (hashes, paths);
        }

        /// <summary>Reads the removal generation of a captured source revision.</summary>
        /// <param name="track">The captured source revision.</param>
        /// <returns>The current removal generation.</returns>
        private long LocalMediaVersion(MusicTrack track)
        {
            return _localMediaVersions.GetValueOrDefault(LocalMediaOwner(track));
        }

        /// <summary>Rejects a transfer whose files were explicitly removed while it was running.</summary>
        /// <param name="track">The captured source revision.</param>
        /// <param name="version">The generation captured before resolution.</param>
        /// <exception cref="OperationCanceledException">The revision was removed during resolution.</exception>
        private void EnsureLocalMediaVersion(MusicTrack track, long version)
        {
            if (LocalMediaVersion(track) != version)
            {
                throw new OperationCanceledException("The song's local files were removed during resolution.");
            }
        }

        /// <summary>Separates cleanup ownership across sources, accounts and revisions.</summary>
        /// <param name="track">The source-owned music entry.</param>
        /// <returns>The stable media owner identity.</returns>
        private static (string, string, string?, string?) LocalMediaOwner(MusicTrack track)
        {
            return (track.Id, track.SourceId, track.EndpointId, track.Revision);
        }
    }
}
