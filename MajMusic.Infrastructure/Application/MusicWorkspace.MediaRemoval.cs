#nullable enable

using System.Collections.Concurrent;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Downloads;
using MajMusic.Infrastructure.Shared;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Removes only managed media while retaining the captured source revision and every playlist reference.</summary>
    public sealed partial class MusicWorkspace : ITrackMediaRemovalWorkspace, ITrackMediaRemovalBatchWorkspace
    {
        /// <summary>Rejects older resolutions that finish publishing after this revision's local files were removed.</summary>
        private readonly ConcurrentDictionary<(string Id, string Source, string? Endpoint, string? Revision), long> _localMediaVersions = new();

        /// <summary>Reuses one protection snapshot only within the current asynchronous cleanup operation.</summary>
        private readonly AsyncLocal<IReadOnlyList<MusicTrack>?> _batchRemovalLibrary = new();
        /// <summary>Reuses one complete public-library and game-reference protection set for a cleanup batch.</summary>
        private readonly AsyncLocal<MediaRemovalProtection?> _batchRemovalProtection = new();

        /// <inheritdoc />
        public async Task<IReadOnlyList<TrackMediaRemovalResult>> RemoveTrackMediaBatchAsync(IReadOnlyList<MusicTrack> tracks, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            var owners = tracks.Select(LocalMediaOwner).ToHashSet();
            var library = await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false);
            var previous = _batchRemovalLibrary.Value;
            var previousProtection = _batchRemovalProtection.Value;
            _batchRemovalLibrary.Value = library.Where(track => !owners.Contains(LocalMediaOwner(track))).ToArray();
            try
            {
                var removalOwners = tracks.Where(track => track.RemoteId is not null).Select(track => (track.SourceId, track.RemoteId!)).ToHashSet();
                _batchRemovalProtection.Value = await CreateMediaRemovalProtectionAsync(_cache, _batchRemovalLibrary.Value, removalOwners, cancellationToken).ConfigureAwait(false);
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
                _batchRemovalProtection.Value = previousProtection;
            }
        }

        /// <inheritdoc />
        public async Task<TrackMediaRemovalResult> RemoveTrackMediaAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false) ?? track;
                if (current.RemoteId is null || current.SourceId != track.SourceId || current.EndpointId != track.EndpointId || !SameRevision(current, track))
                {
                    return new TrackMediaRemovalResult(current, false, false, 0);
                }

                _localMediaVersions.AddOrUpdate(LocalMediaOwner(current), 1, static (_, version) => version + 1);
                var cache = _cache;
                var resources = new List<MediaResource> { ScopeResourceForCache(current, current.Audio) };
                if (current.Cover is { } cover)
                {
                    resources.Add(ScopeResourceForCache(current, cover));
                }
                if (current.Chart is { } chart)
                {
                    resources.Add(ScopeResourceForCache(current, chart));
                }

                var library = _batchRemovalLibrary.Value ?? await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false);
                var protection = _batchRemovalProtection.Value ?? await CreateMediaRemovalProtectionAsync(cache,
                    library.Where(item => LocalMediaOwner(item) != LocalMediaOwner(current)).ToArray(),
                    new HashSet<(string, string)> { (current.SourceId, current.RemoteId) }, cancellationToken).ConfigureAwait(false);
                var removed = await cache.RemoveResourcesAsync(resources, protection.Paths, cancellationToken).ConfigureAwait(false);
                var shared = await RemoveSharedMediaAsync(current, protection.Hashes, cancellationToken).ConfigureAwait(false);
                var audioRemoved = removed.Removed[0] || shared.Removed[0];
                var coverRemoved = current.Cover is not null && (removed.Removed.Length > 1 && removed.Removed[1] || shared.Removed[1]);
                var chartRemoved = shared.Removed[2] || current.Chart is not null && removed.Removed[^1];
                if (!audioRemoved && !coverRemoved && !chartRemoved)
                {
                    return new TrackMediaRemovalResult(current, false, false, 0);
                }

                var updated = current with
                {
                    Audio = audioRemoved ? current.Audio with { LocalPath = null, Sha256 = null } : current.Audio,
                    Cover = coverRemoved ? current.Cover! with { LocalPath = null, Sha256 = null } : current.Cover,
                    Chart = chartRemoved ? current.Chart! with { LocalPath = null } : current.Chart,
                    LocalMediaSavedAt = audioRemoved ? null : current.LocalMediaSavedAt,
                    AudioMetadataLength = audioRemoved ? null : current.AudioMetadataLength,
                    AudioMetadataLastWriteUtcTicks = audioRemoved ? null : current.AudioMetadataLastWriteUtcTicks
                };
                await _library.UpsertTracksAsync([updated], CancellationToken.None).ConfigureAwait(false);
                return new TrackMediaRemovalResult(updated, audioRemoved, coverRemoved, removed.DeletedFiles + shared.Deleted);
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        /// <summary>Reads this revision's local-removal generation before resolving any media.</summary>
        /// <param name="track">The captured source revision.</param>
        /// <returns>The current generation used to reject a late resolution.</returns>
        private long LocalMediaVersion(MusicTrack track)
        {
            return _localMediaVersions.GetValueOrDefault(LocalMediaOwner(track));
        }

        /// <summary>Protects resources referenced by other music or game entries before any physical deletion begins.</summary>
        /// <param name="cache">The selected filesystem object owner.</param>
        /// <param name="library">Entries outside the explicit cleanup selection.</param>
        /// <param name="removalOwners">The game server and chart identities explicitly selected for removal.</param>
        /// <param name="cancellationToken">Cancels public reference reads.</param>
        /// <returns>Hash and native-path sets shared by both removal stages.</returns>
        private async Task<MediaRemovalProtection> CreateMediaRemovalProtectionAsync(MediaCache cache, IReadOnlyList<MusicTrack> library,
            IReadOnlySet<(string Server, string ChartId)> removalOwners, CancellationToken cancellationToken)
        {
            var descriptors = library.SelectMany(track => new[] { track.Audio, track.Cover, track.Chart }).OfType<MediaResource>().ToArray();
            var hashes = descriptors.Select(resource => resource.Sha256).OfType<string>()
                .Where(hash => hash.Length == 64 && hash.All(Uri.IsHexDigit)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var root = GetMediaStorageRoot();
            if (!DocumentReference.IsContentUri(root))
            {
                hashes.UnionWith(await new MajdataPlayCatalog(root).GetProtectedResourceHashesAsync(removalOwners, cancellationToken).ConfigureAwait(false));
            }
            var paths = descriptors.Select(resource => resource.LocalPath).OfType<string>().Where(cache.IsManagedPath).Select(Path.GetFullPath)
                .Concat(hashes.Select(cache.GetManagedObjectPath)).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            return new MediaRemovalProtection(hashes, paths);
        }

        /// <summary>Retains the complete immutable-file protection sets for one explicit cleanup selection.</summary>
        /// <param name="Hashes">Content still referenced outside the selection.</param>
        /// <param name="Paths">Canonical native paths which the cache must preserve.</param>
        private sealed record MediaRemovalProtection(IReadOnlySet<string> Hashes, IReadOnlySet<string> Paths);

        /// <summary>Stops an obsolete resolution from returning or persisting files removed by an explicit user action.</summary>
        /// <param name="track">The original captured revision.</param>
        /// <param name="version">The generation captured before resolution.</param>
        /// <exception cref="OperationCanceledException">This revision's local media was explicitly removed in the meantime.</exception>
        private void EnsureLocalMediaVersion(MusicTrack track, long version)
        {
            if (LocalMediaVersion(track) != version)
            {
                throw new OperationCanceledException("The song's local files were removed during resolution.");
            }
        }

        /// <summary>Separates removal ownership across servers, account slots and source revisions.</summary>
        /// <param name="track">The source-owned music entry.</param>
        /// <returns>The complete stable local-media owner.</returns>
        private static (string, string, string?, string?) LocalMediaOwner(MusicTrack track)
        {
            return (track.Id, track.SourceId, track.EndpointId, track.Revision);
        }
    }
}
