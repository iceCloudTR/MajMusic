#nullable enable

using System.Security.Cryptography;
using System.Text.Json;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Associates cloud snapshots with confirmed local backing identities without matching user-visible names.</summary>
    public sealed partial class MusicWorkspace
    {
        /// <summary>Reads only applied bindings belonging to the current authenticated account and API root.</summary>
        /// <param name="endpoint">The independently configured source being projected.</param>
        /// <param name="connection">The authenticated source connection used for the snapshot.</param>
        /// <param name="server">The discovered stable source identity.</param>
        /// <param name="tracks">The already indexed music used by the same source projection as synchronization.</param>
        /// <param name="token">Cancels a replaced account or closed operation.</param>
        /// <returns>The exact remote-to-local mirror identities, otherwise an empty mapping.</returns>
        private async Task<IReadOnlyDictionary<string, CloudPlaylistMirror>> ReadCloudPlaylistMirrorsAsync(EndpointConfiguration endpoint,
            EndpointConnection connection, string server, IReadOnlyList<MusicTrack> tracks, CancellationToken token)
        {
            var mirrors = new Dictionary<string, CloudPlaylistMirror>(StringComparer.Ordinal);
            if (!File.Exists(Path.Combine(DataDirectory, "cloud-collections.db")))
            {
                return mirrors;
            }

            var target = CaptureCloudMirrorTarget(endpoint, connection);
            if (target is null)
            {
                return mirrors;
            }

            EnsureCloudTarget(target, token);
            var username = await connection.Provider.GetCurrentUsernameAsync(token).ConfigureAwait(false);
            EnsureCloudTarget(target, token);
            var scope = CloudScope(target, server, username);
            var bindings = await GetCloudCollectionStore().ReadAsync(scope, token).ConfigureAwait(false);
            var locals = (await _library.GetPlaylistsAsync(token).ConfigureAwait(false)).ToDictionary(playlist => playlist.Id, StringComparer.Ordinal);
            var byId = tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
            EnsureCloudTarget(target, token);
            foreach (var binding in bindings)
            {
                if (binding is { RemoteId: { Length: > 0 } remote, AppliedFingerprint: { Length: > 0 }, CreationPending: false })
                {
                    if (locals.TryGetValue(binding.PlaylistId, out var local))
                    {
                        var items = local.TrackIds.Where(byId.ContainsKey).Select(id => byId[id])
                            .Where(track => track.SourceId == server && track.RemoteId is not null && Guid.TryParse(track.RemoteId, out _))
                            .Select(track => Guid.Parse(track.RemoteId!).ToString("D")).ToArray();
                        mirrors.TryAdd(remote, new CloudPlaylistMirror(binding.PlaylistId,
                            CloudCollectionFingerprint(local.Name, items) != binding.AppliedFingerprint));
                    }
                }
            }

            return mirrors;
        }

        /// <summary>Serializes an authorized mirror snapshot and its binding annotation against the same account's uploads.</summary>
        /// <param name="endpoint">The source being read.</param>
        /// <param name="connection">The current independently authenticated connection.</param>
        /// <param name="token">Cancels a replaced session or closed request.</param>
        /// <returns>The scope read lease, or null when the account has no mirror permission.</returns>
        private async Task<IDisposable?> AcquireCloudPlaylistReadAsync(EndpointConfiguration endpoint, EndpointConnection connection, CancellationToken token)
        {
            var target = CaptureCloudMirrorTarget(endpoint, connection);
            if (target is null)
            {
                return null;
            }

            await target.Slot.Gate.WaitAsync(token).ConfigureAwait(false);
            var lease = new CloudPlaylistReadLease(target.Slot.Gate);
            try
            {
                EnsureCloudTarget(target, token);
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        /// <summary>Captures only the currently selected account whose saved permission matches the exact live session.</summary>
        /// <param name="endpoint">The configured source identity and API root.</param>
        /// <param name="connection">The exact connection used by the source read.</param>
        /// <returns>The authorized account scope or null for an unrelated cloud read.</returns>
        private CloudSyncTarget? CaptureCloudMirrorTarget(EndpointConfiguration endpoint, EndpointConnection connection)
        {
            lock (_stateGate)
            {
                var current = _settings.Endpoints.FirstOrDefault(item => item.Id == endpoint.Id && item.BaseUri == endpoint.BaseUri);
                var account = current?.Accounts.FirstOrDefault(item => item.Id == current.SelectedAccountId);
                if (current is null || account is not { UseCloudCollections: true } || !connection.Provider.IsLoggedIn
                    || connection.Username != account.Username || !_connections.TryGetValue(endpoint.Id, out var selected)
                    || !ReferenceEquals(connection, selected))
                {
                    return null;
                }

                var slot = GetCloudSyncSlot(endpoint.Id, account.Id);
                return new CloudSyncTarget(current, account, connection, slot, Interlocked.Read(ref slot.Revision));
            }
        }

        /// <summary>Fingerprints the exact name and ordered chart-ID projection used by the upload engine.</summary>
        /// <param name="name">The current local playlist name.</param>
        /// <param name="items">The compatible source's ordered chart identifiers.</param>
        /// <returns>The stable applied-payload identity.</returns>
        private static string CloudCollectionFingerprint(string name, IReadOnlyList<string> items)
        {
            return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Name = name, Items = items })));
        }

        /// <summary>Retains a verified backing identity and whether its last upload still matches local edits.</summary>
        /// <param name="LocalId">The preserved local playlist identity.</param>
        /// <param name="HasPendingChanges">Whether a stale remote snapshot must not import members.</param>
        private sealed record CloudPlaylistMirror(string LocalId, bool HasPendingChanges);

        /// <summary>Releases one account's snapshot/upload serialization lease exactly once.</summary>
        /// <param name="gate">The synchronization slot held by the source snapshot.</param>
        private sealed class CloudPlaylistReadLease(SemaphoreSlim gate) : IDisposable
        {
            /// <summary>Retains the held slot until the complete source snapshot is annotated.</summary>
            private SemaphoreSlim? _gate = gate;

            /// <inheritdoc />
            public void Dispose()
            {
                var held = Interlocked.Exchange(ref _gate, null);
                try
                {
                    held?.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Workspace shutdown already canceled the source and released this slot's lifetime.
                }
            }
        }
    }
}
