#nullable enable

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Network;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Mirrors compatible local playlist projections into explicitly authorized application-owned collections.</summary>
    public sealed partial class MusicWorkspace
    {
        /// <summary>Protects source/account synchronization slots and lazy local store creation.</summary>
        private readonly object _cloudCollectionGate = new();
        /// <summary>Serializes only competing batches for the same saved source and account.</summary>
        private readonly Dictionary<(string Endpoint, string Account), CloudSyncSlot> _cloudCollectionSlots = new();
        /// <summary>Cancels active account targets before the workspace releases their services.</summary>
        private readonly CancellationTokenSource _cloudCollectionLifetime = new();
        /// <summary>Stores durable ownership only after an eligible synchronization requests it.</summary>
        private CloudCollectionSyncStore? _cloudCollectionStore;

        /// <inheritdoc />
        public async Task<IReadOnlyList<CloudCollectionSyncResult>> SynchronizeCloudCollectionsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = new List<CloudSyncTarget>();
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!IsOnline)
                {
                    return Array.Empty<CloudCollectionSyncResult>();
                }

                foreach (var endpoint in _settings.Endpoints)
                {
                    var account = endpoint.Accounts.FirstOrDefault(item => item.Id == endpoint.SelectedAccountId);
                    if (account is not { UseCloudCollections: true } || !_connections.TryGetValue(endpoint.Id, out var connection)
                        || connection.Endpoint.BaseUri != endpoint.BaseUri || !connection.Provider.IsLoggedIn || connection.Username != account.Username)
                    {
                        continue;
                    }

                    var slot = GetCloudSyncSlot(endpoint.Id, account.Id);
                    targets.Add(new CloudSyncTarget(endpoint, account, connection, slot, Interlocked.Read(ref slot.Revision)));
                }
            }

            return await Task.WhenAll(targets.Select(target => SynchronizeCloudTargetAsync(target, cancellationToken))).ConfigureAwait(false);
        }

        /// <summary>Invalidates a permission generation before a checkbox edit can publish or replace it.</summary>
        /// <param name="endpointId">The edited source identity.</param>
        /// <param name="accountId">The edited saved account identity.</param>
        internal void InvalidateCloudCollectionSync(string endpointId, string accountId)
        {
            var slot = GetCloudSyncSlot(endpointId, accountId);
            Interlocked.Increment(ref slot.Revision);
            CancellationTokenSource? active;
            lock (_cloudCollectionGate)
            {
                active = slot.Active;
            }

            try
            {
                active?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A completed target already released this permission generation.
            }
        }

        /// <summary>Processes one authenticated account without holding global preference or metadata gates during HTTP.</summary>
        /// <param name="target">The selected account, isolated connection and permission generation.</param>
        /// <param name="callerToken">Cancels the enclosing finite batch.</param>
        /// <returns>The independent safe synchronization result.</returns>
        private async Task<CloudCollectionSyncResult> SynchronizeCloudTargetAsync(CloudSyncTarget target, CancellationToken callerToken)
        {
            var entered = false;
            var count = 0;
            CancellationTokenSource? active = null;
            try
            {
                await target.Slot.Gate.WaitAsync(callerToken).ConfigureAwait(false);
                entered = true;
                using var session = target.Connection.CreateCancellation(callerToken);
                using var permission = _networkPolicy.CreateRequestCancellation(session.Token);
                active = CancellationTokenSource.CreateLinkedTokenSource(permission.Token, _cloudCollectionLifetime.Token);
                lock (_cloudCollectionGate)
                {
                    target.Slot.Active = active;
                }

                var token = active.Token;
                EnsureCloudTarget(target, token);
                IReadOnlyList<MusicPlaylist> playlists;
                IReadOnlyList<MusicTrack> tracks;
                await _metadataGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    playlists = (await _library.GetPlaylistsAsync(token).ConfigureAwait(false)).Where(playlist => !playlist.IsCloud).ToArray();
                    tracks = await _library.SearchAsync(null, token).ConfigureAwait(false);
                }
                finally
                {
                    _metadataGate.Release();
                }

                var byId = tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
                if (!File.Exists(Path.Combine(DataDirectory, "cloud-collections.db"))
                    && !playlists.SelectMany(playlist => playlist.TrackIds).Any(id => byId.TryGetValue(id, out var track)
                        && track.RemoteId is not null && Guid.TryParse(track.RemoteId, out _)))
                {
                    return CloudResult(target, true, 0, null);
                }

                var provider = target.Connection.Provider;
                EnsureCloudTarget(target, token);
                var server = await provider.GetServerAsync(token).ConfigureAwait(false);
                EnsureCloudTarget(target, token);
                var username = await provider.GetCurrentUsernameAsync(token).ConfigureAwait(false);
                EnsureCloudTarget(target, token);
                var scope = CloudScope(target, server.Id, username);
                var store = GetCloudCollectionStore();
                var bindings = (await store.ReadAsync(scope, token).ConfigureAwait(false)).ToDictionary(binding => binding.PlaylistId, StringComparer.Ordinal);
                var owned = await ReadOwnedCloudCollectionsAsync(target, username, token).ConfigureAwait(false);
                EnsureCloudTarget(target, token);
                var favorites = (await provider.GetCloudPlaylistsAsync(token).ConfigureAwait(false)).Select(list => list.Id).ToHashSet(StringComparer.Ordinal);
                EnsureCloudTarget(target, token);
                foreach (var playlist in playlists)
                {
                    var items = playlist.TrackIds.Where(byId.ContainsKey).Select(id => byId[id])
                        .Where(track => track.SourceId == server.Id && track.RemoteId is not null && Guid.TryParse(track.RemoteId, out _))
                        .Select(track => Guid.Parse(track.RemoteId!).ToString("D")).ToArray();
                    bindings.TryGetValue(playlist.Id, out var binding);
                    if (items.Length == 0 && binding is null)
                    {
                        continue;
                    }

                    binding ??= new CloudCollectionBinding { Scope = scope, PlaylistId = playlist.Id, Marker = Guid.NewGuid().ToString("N") };
                    await store.SaveAsync(binding, token).ConfigureAwait(false);
                    var resolved = await ResolveCloudBindingAsync(target, username, playlist.Name, binding, owned, token).ConfigureAwait(false);
                    binding = resolved.Binding;
                    owned = resolved.Owned;
                    EnsureCloudTarget(target, token);
                    var details = await provider.GetCollectionSongListAsync(binding.RemoteId!, token).ConfigureAwait(false);
                    EnsureCloudTarget(target, token);
                    VerifyCloudOwnership(details, username, binding);
                    if (details!.Name != playlist.Name || !details.Items.Select(NormalizeCloudChartId).SequenceEqual(items, StringComparer.Ordinal))
                    {
                        EnsureCloudTarget(target, token);
                        await provider.UpdateCollectionAsync(details.Id, playlist.Name, CloudMarker(binding), items, token).ConfigureAwait(false);
                        EnsureCloudTarget(target, token);
                    }

                    if (!favorites.Contains(details.Id))
                    {
                        EnsureCloudTarget(target, token);
                        await provider.SetCollectionFavoriteAsync(details.Id, true, token).ConfigureAwait(false);
                        EnsureCloudTarget(target, token);
                        favorites.Add(details.Id);
                    }

                    var fingerprint = CloudCollectionFingerprint(playlist.Name, items);
                    await store.SaveAsync(binding with { AppliedFingerprint = fingerprint }, token).ConfigureAwait(false);
                    bindings.Remove(playlist.Id);
                    count++;
                }

                foreach (var deleted in bindings.Values)
                {
                    EnsureCloudTarget(target, token);
                    var remoteIds = owned.Where(item => item.CreatedBy == username && item.Description == CloudMarker(deleted))
                        .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                    if (deleted.RemoteId is { } boundId)
                    {
                        remoteIds.Add(boundId);
                    }

                    if (remoteIds.Count == 0)
                    {
                        if (!deleted.CreationPending)
                        {
                            await store.DeleteAsync(deleted, token).ConfigureAwait(false);
                        }

                        continue;
                    }

                    foreach (var remoteId in remoteIds)
                    {
                        var details = await provider.GetCollectionSongListAsync(remoteId, token).ConfigureAwait(false);
                        EnsureCloudTarget(target, token);
                        if (details is not null)
                        {
                            VerifyCloudOwnership(details, username, deleted);
                            if (favorites.Contains(remoteId))
                            {
                                EnsureCloudTarget(target, token);
                                await provider.SetCollectionFavoriteAsync(remoteId, false, token).ConfigureAwait(false);
                                EnsureCloudTarget(target, token);
                            }

                            await provider.DeleteCollectionAsync(remoteId, token).ConfigureAwait(false);
                            EnsureCloudTarget(target, token);
                        }
                    }

                    await store.DeleteAsync(deleted, token).ConfigureAwait(false);
                }

                return CloudResult(target, true, count, null);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                return CloudResult(target, false, count, "账号状态已更改");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return CloudResult(target, false, count, exception is CloudSyncException safe ? safe.Message : "云端同步未完成");
            }
            finally
            {
                lock (_cloudCollectionGate)
                {
                    if (ReferenceEquals(target.Slot.Active, active))
                    {
                        target.Slot.Active = null;
                    }
                }

                active?.Dispose();
                if (entered)
                {
                    target.Slot.Gate.Release();
                }
            }
        }

        /// <summary>Creates or recovers only a collection carrying this binding's precommitted random marker.</summary>
        /// <param name="target">The current authorized account.</param>
        /// <param name="username">The canonical server account identity.</param>
        /// <param name="name">The local playlist name.</param>
        /// <param name="binding">The locally committed ownership journal.</param>
        /// <param name="owned">The complete current owned collection inventory.</param>
        /// <param name="token">Cancels the account generation.</param>
        /// <returns>The recovered binding and refreshed owned inventory.</returns>
        private async Task<(CloudCollectionBinding Binding, IReadOnlyList<RemoteCollection> Owned)> ResolveCloudBindingAsync(
            CloudSyncTarget target, string username, string name, CloudCollectionBinding binding, IReadOnlyList<RemoteCollection> owned, CancellationToken token)
        {
            var found = FindOwnedCloudCollection(owned, username, binding);
            if (found is null)
            {
                if (binding.RemoteId is not null)
                {
                    EnsureCloudTarget(target, token);
                    var previous = await target.Connection.Provider.GetCollectionSongListAsync(binding.RemoteId, token).ConfigureAwait(false);
                    EnsureCloudTarget(target, token);
                    if (previous is not null)
                    {
                        return (binding, owned);
                    }

                    binding = binding with { RemoteId = null, CreationPending = false };
                    await GetCloudCollectionStore().SaveAsync(binding, token).ConfigureAwait(false);
                }

                binding = binding with { CreationPending = true };
                await GetCloudCollectionStore().SaveAsync(binding, token).ConfigureAwait(false);
                try
                {
                    EnsureCloudTarget(target, token);
                }
                catch (OperationCanceledException)
                {
                    await GetCloudCollectionStore().SaveAsync(binding with { CreationPending = false }, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                try
                {
                    await target.Connection.Provider.CreateCollectionAsync(name, CloudMarker(binding), 0, token).ConfigureAwait(false);
                }
                catch (HttpRequestException exception) when (exception.StatusCode is { } status && (int)status is >= 400 and < 500
                    && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict))
                {
                    await GetCloudCollectionStore().SaveAsync(binding with { CreationPending = false }, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                EnsureCloudTarget(target, token);
                owned = await ReadOwnedCloudCollectionsAsync(target, username, token).ConfigureAwait(false);
                found = FindOwnedCloudCollection(owned, username, binding) ?? throw new CloudSyncException("等待确认云端歌单");
            }

            var duplicates = owned.Where(item => item.CreatedBy == username && item.Description == CloudMarker(binding) && item.Id != found.Id).ToArray();
            if (duplicates.Length > 0)
            {
                EnsureCloudTarget(target, token);
                VerifyCloudOwnership(await target.Connection.Provider.GetCollectionSongListAsync(found.Id, token).ConfigureAwait(false), username, binding);
                foreach (var duplicate in duplicates)
                {
                    EnsureCloudTarget(target, token);
                    var details = await target.Connection.Provider.GetCollectionSongListAsync(duplicate.Id, token).ConfigureAwait(false);
                    EnsureCloudTarget(target, token);
                    VerifyCloudOwnership(details, username, binding);
                    await target.Connection.Provider.SetCollectionFavoriteAsync(duplicate.Id, false, token).ConfigureAwait(false);
                    EnsureCloudTarget(target, token);
                    await target.Connection.Provider.DeleteCollectionAsync(duplicate.Id, token).ConfigureAwait(false);
                    EnsureCloudTarget(target, token);
                }

                var removed = duplicates.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                owned = owned.Where(item => !removed.Contains(item.Id)).ToArray();
            }

            binding = binding with { RemoteId = found.Id, CreationPending = false };
            await GetCloudCollectionStore().SaveAsync(binding, token).ConfigureAwait(false);
            return (binding, owned);
        }

        /// <summary>Reads complete owned pages without treating a partial inventory as proof that a marker is absent.</summary>
        /// <param name="target">The current authorized account.</param>
        /// <param name="username">The canonical creator identity.</param>
        /// <param name="token">Cancels the account generation.</param>
        /// <returns>All owned collection descriptors returned by finite distinct pages.</returns>
        private async Task<IReadOnlyList<RemoteCollection>> ReadOwnedCloudCollectionsAsync(CloudSyncTarget target, string username, CancellationToken token)
        {
            var collections = new Dictionary<string, RemoteCollection>(StringComparer.Ordinal);
            for (var page = 0; page < 10000; page++)
            {
                EnsureCloudTarget(target, token);
                var items = await target.Connection.Provider.GetCollectionPageAsync(username, page, 100, token).ConfigureAwait(false);
                EnsureCloudTarget(target, token);
                var added = 0;
                foreach (var item in items)
                {
                    if (collections.TryAdd(item.Id, item))
                    {
                        added++;
                    }
                }

                if (items.Count < 100)
                {
                    return collections.Values.ToArray();
                }

                if (added == 0)
                {
                    throw new CloudSyncException("云端列表读取未完成");
                }
            }

            throw new CloudSyncException("云端列表读取未完成");
        }

        /// <summary>Verifies current permission, selected account and exact live session before and after every remote action.</summary>
        /// <param name="target">The immutable authorization snapshot.</param>
        /// <param name="token">The source, permission and caller lifetime.</param>
        /// <exception cref="OperationCanceledException">The source account or permission is no longer current.</exception>
        private void EnsureCloudTarget(CloudSyncTarget target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_stateGate)
            {
                var endpoint = _settings.Endpoints.FirstOrDefault(item => item.Id == target.Endpoint.Id && item.BaseUri == target.Endpoint.BaseUri);
                var account = endpoint?.Accounts.FirstOrDefault(item => item.Id == endpoint.SelectedAccountId);
                if (_disposed || !IsOnline || account is not { UseCloudCollections: true } || account.Id != target.Account.Id
                    || account.Username != target.Account.Username || target.Connection.Username != account.Username
                    || !target.Connection.Provider.IsLoggedIn || !_connections.TryGetValue(target.Endpoint.Id, out var current)
                    || !ReferenceEquals(current, target.Connection) || Interlocked.Read(ref target.Slot.Revision) != target.Revision)
                {
                    throw new OperationCanceledException(token);
                }
            }
        }

        /// <summary>Finds an exact application marker owned by the canonical account, never by display-name similarity.</summary>
        /// <param name="owned">The complete owned inventory.</param>
        /// <param name="username">The canonical account owner.</param>
        /// <param name="binding">The local ownership journal.</param>
        /// <returns>The existing binding or stable first marked collection, or null when absent.</returns>
        private static RemoteCollection? FindOwnedCloudCollection(IReadOnlyList<RemoteCollection> owned, string username, CloudCollectionBinding binding)
        {
            var matches = owned.Where(item => item.CreatedBy == username && item.Description == CloudMarker(binding)).ToArray();
            return matches.FirstOrDefault(item => item.Id == binding.RemoteId) ?? matches.OrderBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>Rejects modification or deletion after a remote collection loses its expected owner marker.</summary>
        /// <param name="details">The freshly read remote collection.</param>
        /// <param name="username">The canonical authenticated owner.</param>
        /// <param name="binding">The persisted application binding.</param>
        private static void VerifyCloudOwnership(RemoteCollection? details, string username, CloudCollectionBinding binding)
        {
            if (details is null || details.CreatedBy != username || details.Description != CloudMarker(binding) || details.Visibility != 0)
            {
                throw new CloudSyncException("云端歌单归属已更改");
            }
        }

        /// <summary>Builds an opaque full-source and account scope without persisting credentials or account names.</summary>
        /// <param name="target">The saved source and account identity.</param>
        /// <param name="server">The current server identity.</param>
        /// <param name="username">The canonical authenticated principal.</param>
        /// <returns>The SHA-256 scope digest.</returns>
        private static string CloudScope(CloudSyncTarget target, string server, string username)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
            {
                target.Endpoint.Id, target.Endpoint.BaseUri.AbsoluteUri.TrimEnd('/') + "/", server, target.Account.Id, username
            }))));
        }

        /// <summary>Formats the non-secret ownership description used by the application's private mirrors.</summary>
        /// <param name="binding">The persisted random ownership marker.</param>
        /// <returns>The exact cloud collection description.</returns>
        private static string CloudMarker(CloudCollectionBinding binding)
        {
            return "MajMusic · " + binding.Marker;
        }

        /// <summary>Normalizes GUID spellings for comparison without converting portable hashes into chart identities.</summary>
        /// <param name="id">The remote chart identifier.</param>
        /// <returns>The canonical GUID spelling, or the original invalid remote value.</returns>
        private static string NormalizeCloudChartId(string id)
        {
            return Guid.TryParse(id, out var chart) ? chart.ToString("D") : id;
        }

        /// <summary>Creates one safe per-account completion result.</summary>
        /// <param name="target">The explicitly authorized account scope.</param>
        /// <param name="succeeded">Whether the target completed.</param>
        /// <param name="count">The successfully reconciled local mirrors.</param>
        /// <param name="error">A caller-safe short failure description.</param>
        /// <returns>The target result without server response details.</returns>
        private static CloudCollectionSyncResult CloudResult(CloudSyncTarget target, bool succeeded, int count, string? error)
        {
            return new CloudCollectionSyncResult { EndpointId = target.Endpoint.Id, AccountId = target.Account.Id, Succeeded = succeeded, CollectionCount = count, Error = error };
        }

        /// <summary>Finds or creates only the in-memory serialization slot for one saved source/account.</summary>
        /// <param name="endpoint">The source identity.</param>
        /// <param name="account">The saved account identity.</param>
        /// <returns>The independent synchronization slot.</returns>
        private CloudSyncSlot GetCloudSyncSlot(string endpoint, string account)
        {
            lock (_cloudCollectionGate)
            {
                if (!_cloudCollectionSlots.TryGetValue((endpoint, account), out var slot))
                {
                    slot = new CloudSyncSlot();
                    _cloudCollectionSlots[(endpoint, account)] = slot;
                }

                return slot;
            }
        }

        /// <summary>Creates the local ownership store without opening its database until a binding is accessed.</summary>
        /// <returns>The shared dedicated binding store.</returns>
        private CloudCollectionSyncStore GetCloudCollectionStore()
        {
            lock (_cloudCollectionGate)
            {
                return _cloudCollectionStore ??= new CloudCollectionSyncStore(Path.Combine(DataDirectory, "cloud-collections.db"));
            }
        }

        /// <summary>Cancels synchronization after its tracked host operations have drained.</summary>
        private void DisposeCloudCollectionSync()
        {
            _cloudCollectionLifetime.Cancel();
            _cloudCollectionLifetime.Dispose();
            _cloudCollectionStore?.Dispose();
            foreach (var slot in _cloudCollectionSlots.Values)
            {
                slot.Gate.Dispose();
            }
        }

        /// <summary>Retains one selected account's immutable live authorization scope.</summary>
        /// <param name="Endpoint">The saved source and full API root.</param>
        /// <param name="Account">The selected saved account and explicit cloud permission.</param>
        /// <param name="Connection">The exact authenticated connection isolated from other source accounts.</param>
        /// <param name="Slot">The account-specific synchronization serialization slot.</param>
        /// <param name="Revision">The checkbox permission generation.</param>
        private sealed record CloudSyncTarget(EndpointConfiguration Endpoint, SavedEndpointAccount Account, EndpointConnection Connection, CloudSyncSlot Slot, long Revision);

        /// <summary>Owns only one saved account's serialization and cancellable permission generation.</summary>
        private sealed class CloudSyncSlot
        {
            /// <summary>Serializes complete reconciliation batches for this saved account.</summary>
            public SemaphoreSlim Gate { get; } = new(1, 1);
            /// <summary>Rejects targets captured before a cloud permission edit.</summary>
            public long Revision;
            /// <summary>Retains the current target cancellation source under the collection gate.</summary>
            public CancellationTokenSource? Active;
        }

        /// <summary>Carries only a short safe synchronization outcome for presentation.</summary>
        private sealed class CloudSyncException : Exception
        {
            /// <summary>Creates a safe synchronization outcome.</summary>
            /// <param name="message">The short caller-safe outcome text.</param>
            public CloudSyncException(string message) : base(message)
            {
            }
        }
    }
}
