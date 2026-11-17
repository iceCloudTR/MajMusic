#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Resolves only media owned by Music while preserving explicit local imports.</summary>
    public sealed partial class MusicWorkspace : ILocalTrackMediaResolver
    {
        /// <summary>Bounds concurrent cache validation independently of network and transport commands.</summary>
        private readonly SemaphoreSlim _localMediaLookups = new(4, 4);

        /// <inheritdoc />
        public async Task<MusicTrack> ResolveLocalTrackAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            if (!RequiresManagedMedia(track))
            {
                return track;
            }
            var root = GetMediaStorageRoot();
            var version = LocalMediaVersion(track);
            await _localMediaLookups.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var original = await FilterOwnedTrackAsync(track, cancellationToken).ConfigureAwait(false);
                var audio = await FindOwnedResourceAsync(original, original.Audio, cancellationToken).ConfigureAwait(false);
                var cover = original.Cover is { } image ? await FindOwnedResourceAsync(original, image, cancellationToken).ConfigureAwait(false) : null;
                await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    EnsureLocalMediaVersion(track, version);
                    var current = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false) ?? track;
                    var safe = await FilterOwnedTrackAsync(current, cancellationToken).ConfigureAwait(false);
                    if (!SameMediaDirectory(root, GetMediaStorageRoot()) || current.SourceId != track.SourceId || current.EndpointId != track.EndpointId || !SameRevision(current, track))
                    {
                        return await FilterOwnedTrackAsync(track, cancellationToken).ConfigureAwait(false);
                    }
                    var located = safe with
                    {
                        Audio = safe.Audio == original.Audio ? audio : safe.Audio,
                        Cover = safe.Cover == original.Cover ? cover : safe.Cover,
                    };
                    if (located != current)
                    {
                        await _library.UpsertTracksAsync([located], cancellationToken).ConfigureAwait(false);
                    }
                    return located;
                }
                finally
                {
                    _metadataGate.Release();
                }
            }
            finally
            {
                _localMediaLookups.Release();
            }
        }

        /// <summary>Filters every returned descriptor before any view can use an old external online path.</summary>
        private async Task<IReadOnlyList<MusicTrack>> PrepareMusicMediaAsync(IReadOnlyList<MusicTrack> tracks, CancellationToken token)
        {
            var result = new MusicTrack[tracks.Count];
            for (var index = 0; index < tracks.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                result[index] = await FilterOwnedTrackAsync(tracks[index], token).ConfigureAwait(false);
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Min(16, result.Length)).Select(async index =>
            {
                result[index] = await ResolveLocalTrackAsync(result[index], token).ConfigureAwait(false);
            })).ConfigureAwait(false);
            return result;
        }

        /// <summary>Identifies online entries and application-created offline archives which require cache ownership.</summary>
        private static bool RequiresManagedMedia(MusicTrack track)
        {
            return track.RemoteId is not null || track.SourceId == "local/archive";
        }

        /// <summary>Clears unsupported media references without changing files, identities or manual imports.</summary>
        private async Task<MusicTrack> FilterOwnedTrackAsync(MusicTrack track, CancellationToken token)
        {
            if (!RequiresManagedMedia(track))
            {
                return track;
            }
            var audio = await FilterOwnedResourceAsync(track.Audio, token).ConfigureAwait(false);
            var cover = track.Cover is { } image ? await FilterOwnedResourceAsync(image, token).ConfigureAwait(false) : null;
            return track with { Audio = audio, Cover = cover, LocalMediaSavedAt = audio.LocalPath is null ? null : track.LocalMediaSavedAt };
        }

        /// <summary>Accepts only Music-indexed native objects or newly Music-published documents in the active root.</summary>
        private async Task<MediaResource> FilterOwnedResourceAsync(MediaResource resource, CancellationToken token)
        {
            if (resource.LocalPath is not { Length: > 0 } path)
            {
                return resource with { LocalPath = null, ManagedStorageRoot = null };
            }
            if (DocumentReference.IsContentUri(path))
            {
                var root = GetMediaStorageRoot();
                if (resource.ManagedStorageRoot == root && DocumentReference.IsContentUri(root) && resource.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit) && _mediaStorageAccess is { } access)
                {
                    var found = await access.FindAsync(root, MediaObjectRelativePath(hash), false, token).ConfigureAwait(false);
                    if (found == path)
                    {
                        return resource;
                    }
                }
                return resource with { LocalPath = null, ManagedStorageRoot = null };
            }
            var cache = _cache;
            if (cache.IsManagedPath(path) && await cache.IsOwnedResourceAsync(resource, token).ConfigureAwait(false) && File.Exists(path) ||
                _defaultCache.IsManagedPath(path) && await _defaultCache.IsOwnedResourceAsync(resource, token).ConfigureAwait(false) && File.Exists(path))
            {
                return resource;
            }
            return resource with { LocalPath = null, ManagedStorageRoot = null };
        }

        /// <summary>Uses the normal private alias index for local-only cache lookup without opening external paths.</summary>
        private async Task<MediaResource> FindOwnedResourceAsync(MusicTrack track, MediaResource resource, CancellationToken token)
        {
            if (DocumentReference.IsContentUri(resource.LocalPath))
            {
                return resource;
            }
            if (resource.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit)
                && await FindOwnedDocumentAsync(hash, token).ConfigureAwait(false) is { } document)
            {
                return resource with { LocalPath = document, ManagedStorageRoot = GetMediaStorageRoot() };
            }
            var scoped = ScopeResourceForCache(track, resource);
            var path = await _cache.TryGetCachedAsync(scoped, true, token).ConfigureAwait(false)
                ?? await _defaultCache.TryGetCachedAsync(scoped, true, token).ConfigureAwait(false);
            return resource with { LocalPath = path, ManagedStorageRoot = null };
        }
    }
}
