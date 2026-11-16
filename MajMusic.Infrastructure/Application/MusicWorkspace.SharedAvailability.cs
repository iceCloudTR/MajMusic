#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Shared;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Observes media downloaded by another player without requiring a rescan, login or audio download.</summary>
    public sealed partial class MusicWorkspace : ILocalTrackMediaResolver
    {
        /// <summary>Bounds concurrent disk hashing independently of API and playback operations.</summary>
        private readonly SemaphoreSlim _localMediaLookups = new(4, 4);
        /// <summary>Coalesces changed public-catalog snapshots without rescanning manual music or rewriting playlists.</summary>
        private readonly SemaphoreSlim _sharedCatalogReads = new(1, 1);
        /// <summary>Retains the last imported public database version and configured endpoint scope.</summary>
        private string? _sharedCatalogReadStamp;

        /// <inheritdoc />
        public async Task<MusicTrack> ResolveLocalTrackAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            if (track.RemoteId is null)
            {
                return track;
            }
            var root = GetMediaStorageRoot();
            if (DocumentReference.IsContentUri(root) || !Directory.Exists(Path.Combine(root, "Cache")))
            {
                return track;
            }
            EndpointConfiguration? endpoint;
            lock (_stateGate)
            {
                endpoint = _settings.Endpoints.FirstOrDefault(item => item.Id == track.EndpointId);
            }
            if (endpoint is null)
            {
                return track;
            }
            var version = LocalMediaVersion(track);
            await _localMediaLookups.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var located = await new MajdataPlayCatalog(root).AttachResourcesAsync(track, endpoint, cancellationToken).ConfigureAwait(false);
                if (located.Audio == track.Audio && located.Cover == track.Cover && located.Chart == track.Chart)
                {
                    return track;
                }
                await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    EnsureLocalMediaVersion(track, version);
                    var current = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false);
                    if (current is null || current.SourceId != track.SourceId || current.EndpointId != track.EndpointId || !SameRevision(current, track))
                    {
                        return track;
                    }
                    if (!SharedLookupStillCurrent(root, endpoint))
                    {
                        return current;
                    }
                    located = current with
                    {
                        Audio = MergeLocatedResource(track.Audio, located.Audio, current.Audio) ?? current.Audio,
                        Cover = MergeLocatedResource(track.Cover, located.Cover, current.Cover),
                        Chart = MergeLocatedResource(track.Chart, located.Chart, current.Chart),
                    };
                    if (located.Audio == current.Audio && located.Cover == current.Cover && located.Chart == current.Chart)
                    {
                        return current;
                    }
                    await _library.UpsertTracksAsync([located], cancellationToken).ConfigureAwait(false);
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

        /// <summary>Prepares only the initial visible batch; remaining cards resolve themselves when activated.</summary>
        /// <param name="tracks">The current query results.</param>
        /// <param name="cancellationToken">Cancels this query's bounded local checks.</param>
        /// <returns>The original ordering with refreshed local paths for the initial batch.</returns>
        private async Task<IReadOnlyList<MusicTrack>> AttachInitialSharedMediaAsync(IReadOnlyList<MusicTrack> tracks, CancellationToken cancellationToken)
        {
            if (tracks.Count == 0)
            {
                return tracks;
            }
            var result = tracks.ToArray();
            var pending = new List<Task>();
            for (var index = 0; index < Math.Min(16, result.Length); index++)
            {
                var position = index;
                pending.Add(AttachAsync(position));
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
            return result;

            async Task AttachAsync(int position)
            {
                result[position] = await ResolveLocalTrackAsync(result[position], cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Imports changed public chart metadata read-only, preserving a newer Music revision already indexed.</summary>
        /// <param name="cancellationToken">Cancels public reads and private-index publication.</param>
        /// <returns>A task representing a changed-catalog merge or an unchanged snapshot.</returns>
        private async Task RefreshSharedCatalogForQueryAsync(CancellationToken cancellationToken)
        {
            var root = GetMediaStorageRoot();
            if (DocumentReference.IsContentUri(root))
            {
                return;
            }
            var database = Path.Combine(root, "Runtime", "runtime.db");
            if (!File.Exists(database))
            {
                return;
            }
            await _sharedCatalogReads.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EndpointConfiguration[] endpoints;
                lock (_stateGate)
                {
                    endpoints = _settings.Endpoints.ToArray();
                }
                var stamp = string.Join('|', Path.GetFullPath(root), FileStamp(database), FileStamp(database + "-wal"),
                    string.Join('\n', endpoints.Select(endpoint => endpoint.Id + "|" + endpoint.Name + "|" + endpoint.BaseUri.AbsoluteUri + "|" + endpoint.ServerId)));
                if (stamp == _sharedCatalogReadStamp)
                {
                    return;
                }
                var received = await new MajdataPlayCatalog(root).ReadMetadataOnlyAsync(endpoints, cancellationToken).ConfigureAwait(false);
                await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!SameMediaRoot(root, GetMediaStorageRoot()) || endpoints.Any(endpoint => !SharedLookupStillCurrent(root, endpoint)))
                    {
                        return;
                    }
                    var indexed = (await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false)).ToDictionary(track => track.Id, StringComparer.Ordinal);
                    var aliases = GetTrackIdentityAliases(indexed.Values);
                    var updates = new List<MusicTrack>();
                    foreach (var entry in received)
                    {
                        var track = RetainTrackIdentity(entry, aliases);
                        if (indexed.TryGetValue(track.Id, out var previous))
                        {
                            if (!SameRevision(track, previous))
                            {
                                continue;
                            }
                            track = previous with
                            {
                                Audio = MergeResource(track.Audio, previous.Audio),
                                Cover = track.Cover is null ? previous.Cover : MergeResource(track.Cover, previous.Cover),
                                Chart = track.Chart is null ? previous.Chart : MergeResource(track.Chart, previous.Chart),
                            };
                        }
                        updates.Add(track);
                    }
                    if (!SameMediaRoot(root, GetMediaStorageRoot()) || endpoints.Any(endpoint => !SharedLookupStillCurrent(root, endpoint)))
                    {
                        return;
                    }
                    if (updates.Count > 0)
                    {
                        await _library.UpsertTracksAsync(updates, cancellationToken).ConfigureAwait(false);
                    }
                    _sharedCatalogReadStamp = stamp;
                }
                finally
                {
                    _metadataGate.Release();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
            {
                System.Diagnostics.Trace.TraceWarning("Shared catalog refresh was unavailable: {0}", exception.GetType().Name);
            }
            finally
            {
                _sharedCatalogReads.Release();
            }
        }

        /// <summary>Applies only this lookup's actual resource change while giving a concurrent new descriptor priority.</summary>
        /// <param name="original">The resource captured before shared-file I/O.</param>
        /// <param name="located">The resource returned by this lookup.</param>
        /// <param name="current">The latest indexed resource after I/O.</param>
        /// <returns>The current resource unless this lookup alone changed it.</returns>
        private static MediaResource? MergeLocatedResource(MediaResource? original, MediaResource? located, MediaResource? current)
        {
            return located == original || current != original ? current : located;
        }

        /// <summary>Rejects local paths discovered for a root or public endpoint configuration that was replaced during I/O.</summary>
        /// <param name="root">The media root captured before the lookup.</param>
        /// <param name="endpoint">The public endpoint configuration captured before the lookup.</param>
        /// <returns>Whether this lookup still belongs to the active media selection and source.</returns>
        private bool SharedLookupStillCurrent(string root, EndpointConfiguration endpoint)
        {
            if (!SameMediaRoot(root, GetMediaStorageRoot()))
            {
                return false;
            }
            lock (_stateGate)
            {
                var current = _settings.Endpoints.FirstOrDefault(item => item.Id == endpoint.Id);
                return current is not null && current.Name == endpoint.Name && current.BaseUri == endpoint.BaseUri && current.ServerId == endpoint.ServerId;
            }
        }

        /// <summary>Compares native roots without treating a newly selected document URI as a filesystem path.</summary>
        /// <param name="first">The captured native root.</param>
        /// <param name="second">The currently selected root.</param>
        /// <returns>Whether both still identify the same native directory.</returns>
        private static bool SameMediaRoot(string first, string second)
        {
            return !DocumentReference.IsContentUri(first) && !DocumentReference.IsContentUri(second) &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>Gets a cheap public-file version without opening a potentially locked database.</summary>
        /// <param name="path">The native database or journal filename.</param>
        /// <returns>Its length and modification time, or an absent marker.</returns>
        private static string FileStamp(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing";
        }
    }
}
