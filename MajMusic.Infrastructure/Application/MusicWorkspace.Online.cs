#nullable enable

using MajMusic.Core.Models;
using MajMusic.Infrastructure.Network;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Owns independent account sessions and the online-only collection boundary.</summary>
    public sealed partial class MusicWorkspace
    {
        /// <summary>Stores one active session per saved endpoint configuration, including duplicate addresses.</summary>
        private readonly Dictionary<string, EndpointConnection> _connections = new(StringComparer.Ordinal);

        /// <summary>Retains canceled sessions until the host has drained their in-flight operations.</summary>
        private readonly List<EndpointConnection> _retiredConnections = new();

        /// <summary>Orders competing account choices so an older login cannot replace a newer Guest selection.</summary>
        private readonly Dictionary<string, long> _accountChoices = new(StringComparer.Ordinal);

        /// <summary>Cancels in-flight authentication when its endpoint or account choice is replaced.</summary>
        private readonly Dictionary<string, EndpointConnection> _pendingLogins = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public IReadOnlyList<EndpointSession> GetEndpointSessions()
        {
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _settings.Endpoints.Select(endpoint =>
                {
                    _connections.TryGetValue(endpoint.Id, out var connection);
                    var current = connection?.Endpoint.BaseUri == endpoint.BaseUri && connection.Provider.IsLoggedIn;
                    return new EndpointSession
                    {
                        EndpointId = endpoint.Id,
                        IsLoggedIn = current,
                        Username = current ? connection!.Username : null
                    };
                }).ToArray();
            }
        }

        /// <inheritdoc />
        public Task LoginAsync(EndpointConfiguration endpoint, string username, string password, CancellationToken cancellationToken = default)
        {
            return LoginCoreAsync(endpoint, username, password, null, null, cancellationToken);
        }

        /// <summary>Logs in while rejecting saved-account attempts superseded during system-vault lookup.</summary>
        /// <param name="endpoint">The configured account endpoint.</param>
        /// <param name="username">The account name.</param>
        /// <param name="password">The transient password.</param>
        /// <param name="expectedChoice">The saved-account choice revision, or null for an explicit transient login.</param>
        /// <param name="expectedAccountId">The saved account that must remain selected.</param>
        /// <param name="cancellationToken">Cancels authentication.</param>
        /// <returns>A task representing the accepted login.</returns>
        private async Task LoginCoreAsync(EndpointConfiguration endpoint, string username, string password, long? expectedChoice,
            string? expectedAccountId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            EnsureConfiguredEndpoint(endpoint);
            long choice;
            EndpointConnection candidate;
            lock (_stateGate)
            {
                endpoint = _settings.Endpoints.First(item => item.Id == endpoint.Id && item.BaseUri == endpoint.BaseUri);
                if (expectedChoice is not null && (_accountChoices.GetValueOrDefault(endpoint.Id) != expectedChoice
                    || !_settings.Endpoints.Any(item => item.Id == endpoint.Id && item.BaseUri == endpoint.BaseUri
                        && item.SelectedAccountId == expectedAccountId)))
                {
                    throw new OperationCanceledException("A newer saved account choice replaced this login.");
                }

                choice = _accountChoices.GetValueOrDefault(endpoint.Id) + 1;
                _accountChoices[endpoint.Id] = choice;
                if (_pendingLogins.Remove(endpoint.Id, out var previous))
                {
                    previous.Retire();
                }

                candidate = CreateConnection(endpoint);
                _pendingLogins[endpoint.Id] = candidate;
            }

            var accepted = false;
            try
            {
                using var lifetime = candidate.CreateCancellation(cancellationToken);
                await candidate.Provider.LoginAsync(username, password, lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_accountChoices.GetValueOrDefault(endpoint.Id) != choice
                        || !_networkPolicy.IsEnabled
                        || !_settings.Endpoints.Any(item => item.Id == endpoint.Id && item.BaseUri == endpoint.BaseUri
                            && (expectedAccountId is null || item.SelectedAccountId == expectedAccountId)))
                    {
                        throw new OperationCanceledException("A newer source account choice replaced this login.");
                    }

                    candidate.Username = username;
                    ReplaceConnection(endpoint.Id, candidate);
                    accepted = true;
                }
            }
            finally
            {
                lock (_stateGate)
                {
                    if (_pendingLogins.TryGetValue(endpoint.Id, out var pending) && ReferenceEquals(pending, candidate))
                    {
                        _pendingLogins.Remove(endpoint.Id);
                    }
                }

                if (!accepted)
                {
                    candidate.Dispose();
                }
            }
        }

        /// <inheritdoc />
        public async Task UseGuestAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
        {
            await SelectAccountAsync(endpoint, null, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<MusicPlaylist>> GetCloudPlaylistsAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
        {
            var connection = GetConnection(endpoint);
            using var lifetime = connection.CreateCancellation(cancellationToken);
            using var mirrorRead = await AcquireCloudPlaylistReadAsync(endpoint, connection, lifetime.Token).ConfigureAwait(false);
            var lists = await connection.Provider.GetCloudPlaylistsAsync(lifetime.Token).ConfigureAwait(false);
            var server = await connection.Provider.GetServerAsync(lifetime.Token).ConfigureAwait(false);
            var tracks = await _library.SearchAsync(null, lifetime.Token).ConfigureAwait(false);
            var knownHashes = tracks.Where(track => track.RemoteId is not null && track.EndpointId == endpoint.Id && track.SourceId == server.Id)
                .Select(track => track.Revision).ToHashSet(StringComparer.Ordinal);
            if (lists.SelectMany(list => list.SourceHashes).Any(hash => !knownHashes.Contains(hash)))
            {
                var library = await connection.Provider.GetLibraryAsync(lifetime.Token).ConfigureAwait(false);
                await MergeOnlineTracksAsync(library, lifetime.Token).ConfigureAwait(false);
                tracks = await _library.SearchAsync(null, lifetime.Token).ConfigureAwait(false);
            }

            var byHash = tracks.Where(track => track.RemoteId is not null && track.EndpointId == endpoint.Id && track.SourceId == server.Id && !string.IsNullOrEmpty(track.Revision))
                .GroupBy(track => track.Revision!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
            var mirrors = await ReadCloudPlaylistMirrorsAsync(endpoint, connection, server.Id, tracks, lifetime.Token).ConfigureAwait(false);
            return lists.Select(list => new MusicPlaylist
            {
                Id = $"cloud:{Uri.EscapeDataString(endpoint.Id)}:{Uri.EscapeDataString(list.Id)}",
                Name = list.Name,
                RemoteId = list.Id,
                EndpointId = endpoint.Id,
                LocalMirrorId = mirrors.GetValueOrDefault(list.Id)?.LocalId,
                LocalMirrorHasPendingChanges = mirrors.GetValueOrDefault(list.Id)?.HasPendingChanges ?? false,
                TrackIds = list.SourceHashes.Where(byHash.ContainsKey).Select(hash => byHash[hash]).ToArray(),
                MajCollection = new MajCollectionDefinition
                {
                    IsPlayList = list.IsPlayList,
                    Entries = list.SourceHashes.Select(hash => new MajCollectionEntry
                    {
                        Hash = hash,
                        TrackId = byHash.GetValueOrDefault(hash)
                    }).ToArray()
                }
            }).ToArray();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<MusicTrack>> GetRecommendationsAsync(EndpointConfiguration endpoint, int page = 0, int pageSize = 50, CancellationToken cancellationToken = default)
        {
            var connection = GetConnection(endpoint);
            using var lifetime = connection.CreateCancellation(cancellationToken);
            var tracks = await connection.Provider.GetRecommendationsAsync(page, pageSize, lifetime.Token).ConfigureAwait(false);
            return await MergeOnlineTracksAsync(tracks, lifetime.Token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<MusicTrack> EnrichTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            if (track.RemoteId is null || !IsOnline)
            {
                return await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false) ?? track;
            }

            var endpoint = GetEndpointForTrack(track);
            var connection = GetConnection(endpoint);
            using var lifetime = connection.CreateCancellation(cancellationToken);
            var details = await connection.Provider.GetTrackAsync(track.RemoteId, lifetime.Token).ConfigureAwait(false);
            if (details is null || details.SourceId != track.SourceId)
            {
                return track;
            }

            details = details with
            {
                Id = track.Id,
                EndpointId = track.EndpointId,
                Revision = string.IsNullOrWhiteSpace(details.Revision) ? track.Revision : details.Revision
            };
            await UpdateTrackMetadataAsync(details, lifetime.Token).ConfigureAwait(false);
            return await _library.FindTrackAsync(track.Id, lifetime.Token).ConfigureAwait(false) ?? track;
        }

        /// <inheritdoc />
        public async Task UpdateTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false);
                if (current is null || !SameRevision(current, track)
                    || current.EndpointId != track.EndpointId
                    || track.RemoteId is null && current.Audio.LocalPath != track.Audio.LocalPath)
                {
                    return;
                }

                var merged = current with
                {
                    Duration = track.Duration ?? current.Duration,
                            LocalMediaSavedAt = current.LocalMediaSavedAt,
                            MajInfoJson = track.MajInfoJson ?? current.MajInfoJson,
                    AudioInfo = MergeStreamInfo(track.AudioInfo, current.AudioInfo),
                    AudioMetadataLength = track.AudioMetadataLength ?? current.AudioMetadataLength,
                    AudioMetadataLastWriteUtcTicks = track.AudioMetadataLastWriteUtcTicks ?? current.AudioMetadataLastWriteUtcTicks
                };
                await _library.UpsertTracksAsync(new[] { merged }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        /// <summary>Downloads through the owning account while retaining one shared cache publication gate.</summary>
        /// <param name="track">The current music descriptor.</param>
        /// <param name="cover">Whether to resolve the cover.</param>
        /// <param name="keepOffline">Whether to pin the downloaded file.</param>
        /// <param name="cancellationToken">Cancels resolution.</param>
        /// <param name="progress">Optional actual audio transfer observations; cover operations omit this reporter.</param>
        /// <returns>The complete local media path.</returns>
        private async Task<string> GetMediaAsync(MusicTrack track, bool cover, bool keepOffline, CancellationToken cancellationToken,
            IProgress<MediaTransferProgress>? progress = null)
        {
            var resource = ScopeResourceForCache(track, (cover ? track.Cover : track.Audio)!);
            var cached = await TryResolveSharedMediaAsync(track, cover, cancellationToken).ConfigureAwait(false)
                ?? await _cache.TryGetCachedAsync(resource, true, cancellationToken).ConfigureAwait(false)
                ?? await _defaultCache.TryGetCachedAsync(resource, true, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            if (track.RemoteId is null || !IsOnline)
            {
                return await _cache.GetAsync(resource, keepOffline, cancellationToken, progress: progress).ConfigureAwait(false);
            }

            var connection = GetConnection(GetEndpointForTrack(track));
            using var lifetime = connection.CreateCancellation(cancellationToken);
            var server = await connection.Provider.GetServerAsync(lifetime.Token).ConfigureAwait(false);
            if (server.Id != track.SourceId)
            {
                return await _cache.GetAsync(resource with { RemoteUri = null }, keepOffline, lifetime.Token, progress: progress).ConfigureAwait(false);
            }

            return await _cache.GetAsync(resource, true, lifetime.Token, connection.MediaClient, progress).ConfigureAwait(false);
        }

        /// <summary>Persists a recommendation page while retaining already parsed or downloaded data for its revision.</summary>
        /// <param name="tracks">The received source descriptors.</param>
        /// <param name="cancellationToken">Cancels the merge.</param>
        /// <returns>The merged source entries with their stable persisted identities.</returns>
        private async Task<IReadOnlyList<MusicTrack>> MergeOnlineTracksAsync(IReadOnlyList<MusicTrack> tracks, CancellationToken cancellationToken)
        {
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = (await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false))
                    .ToDictionary(track => track.Id, StringComparer.Ordinal);
                var aliases = GetTrackIdentityAliases(existing.Values);
                var updates = new List<MusicTrack>(tracks.Count);
                var mapped = new List<MusicTrack>(tracks.Count);
                foreach (var received in tracks)
                {
                    var track = RetainTrackIdentity(received, aliases);
                    existing.TryGetValue(track.Id, out var current);
                    if (current is not null && SameRevision(current, track))
                    {
                        var merged = track with
                        {
                            Audio = MergeResource(track.Audio, current.Audio),
                            Cover = track.Cover is null ? null : MergeResource(track.Cover, current.Cover),
                            Chart = track.Chart is null ? current.Chart : MergeResource(track.Chart, current.Chart),
                            Duration = track.Duration ?? current.Duration,
                            LocalMediaSavedAt = current.LocalMediaSavedAt,
                            MajInfoJson = track.MajInfoJson ?? current.MajInfoJson,
                            AudioInfo = MergeStreamInfo(track.AudioInfo, current.AudioInfo),
                            AudioMetadataLength = current.AudioMetadataLength,
                            AudioMetadataLastWriteUtcTicks = current.AudioMetadataLastWriteUtcTicks
                        };
                        updates.Add(merged);
                        mapped.Add(merged);
                    }
                    else
                    {
                        if (current is not null)
                        {
                            AddOfflineCopy(updates, current);
                        }

                        updates.Add(track);
                        mapped.Add(track);
                    }
                }

                await _library.UpsertTracksAsync(updates, cancellationToken).ConfigureAwait(false);
                return mapped;
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        /// <summary>Finds existing stable entry identities, assigning legacy entries only when their configured source is unique.</summary>
        /// <param name="tracks">The currently indexed entries.</param>
        /// <returns>The unambiguous owning-account, server and remote-resource identity map.</returns>
        private Dictionary<(string EndpointId, string ServerId, string RemoteId), string> GetTrackIdentityAliases(IEnumerable<MusicTrack> tracks)
        {
            EndpointConfiguration[] endpoints;
            lock (_stateGate)
            {
                endpoints = _settings.Endpoints.ToArray();
            }

            var candidates = new Dictionary<(string EndpointId, string ServerId, string RemoteId), List<MusicTrack>>();
            var legacySources = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var track in tracks)
            {
                if (track.RemoteId is null)
                {
                    continue;
                }

                var endpointId = track.EndpointId;
                if (endpointId is null)
                {
                    if (!legacySources.TryGetValue(track.SourceId, out endpointId))
                    {
                        var matches = endpoints.Where(endpoint => endpoint.Id == track.SourceId || endpoint.ServerId == track.SourceId).ToArray();
                        endpointId = matches.Length == 1 ? matches[0].Id : null;
                        legacySources[track.SourceId] = endpointId;
                    }

                    if (endpointId is null)
                    {
                        continue;
                    }
                }

                var key = (endpointId, track.SourceId, track.RemoteId);
                if (!candidates.TryGetValue(key, out var entries))
                {
                    entries = new List<MusicTrack>();
                    candidates[key] = entries;
                }

                entries.Add(track);
            }

            var aliases = new Dictionary<(string EndpointId, string ServerId, string RemoteId), string>();
            foreach (var pair in candidates)
            {
                var owned = pair.Value.Where(track => track.EndpointId is not null).ToArray();
                if (owned.Length == 1)
                {
                    aliases[pair.Key] = owned[0].Id;
                }
                else if (pair.Value.Count == 1)
                {
                    aliases[pair.Key] = pair.Value[0].Id;
                }
            }

            return aliases;
        }

        /// <summary>Keeps a preexisting entry identity so adding account scope does not duplicate songs or break local lists.</summary>
        /// <param name="track">The newly received source descriptor.</param>
        /// <param name="aliases">The unambiguous currently indexed identities.</param>
        /// <returns>The descriptor with its stable persisted identity when one exists.</returns>
        private static MusicTrack RetainTrackIdentity(MusicTrack track,
            IReadOnlyDictionary<(string EndpointId, string ServerId, string RemoteId), string> aliases)
        {
            return track.EndpointId is { } endpointId && track.RemoteId is { } remoteId
                && aliases.TryGetValue((endpointId, track.SourceId, remoteId), out var id) ? track with { Id = id } : track;
        }

        /// <summary>Combines known stream properties without erasing an already parsed value.</summary>
        /// <param name="incoming">The newly supplied properties.</param>
        /// <param name="previous">The indexed properties.</param>
        /// <returns>The combined metadata, or null when both are absent.</returns>
        private static AudioStreamInfo? MergeStreamInfo(AudioStreamInfo? incoming, AudioStreamInfo? previous)
        {
            return incoming is null ? previous : incoming with
            {
                CodecName = incoming.CodecName ?? previous?.CodecName,
                BitrateBitsPerSecond = incoming.BitrateBitsPerSecond ?? previous?.BitrateBitsPerSecond,
                IsBitrateEstimated = incoming.BitrateBitsPerSecond is null ? previous?.IsBitrateEstimated ?? false : incoming.IsBitrateEstimated,
                SampleRateHz = incoming.SampleRateHz ?? previous?.SampleRateHz,
                Channels = incoming.Channels ?? previous?.Channels,
                FileSizeBytes = incoming.FileSizeBytes ?? previous?.FileSizeBytes
            };
        }

        /// <summary>Routes an entry by its owning account slot, allowing legacy source identity only when unambiguous.</summary>
        /// <param name="track">The indexed entry.</param>
        /// <returns>The uniquely matching saved endpoint.</returns>
        /// <exception cref="InvalidOperationException">The source was removed or a legacy entry is ambiguous.</exception>
        private EndpointConfiguration GetEndpointForTrack(MusicTrack track)
        {
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var matches = _settings.Endpoints.Where(endpoint => track.EndpointId is not null
                    ? endpoint.Id == track.EndpointId : endpoint.Id == track.SourceId || endpoint.ServerId == track.SourceId).ToArray();
                return matches.Length == 1 ? matches[0]
                    : throw new InvalidOperationException("请选择该歌曲对应的接入点。原接入点已移除或存在多个账号。");
            }
        }

        /// <summary>Gets or creates a source session without starting any network activity.</summary>
        /// <param name="endpoint">The saved configuration requested by the caller.</param>
        /// <returns>The currently selected independent session.</returns>
        private EndpointConnection GetConnection(EndpointConfiguration endpoint)
        {
            EnsureConfiguredEndpoint(endpoint);
            lock (_stateGate)
            {
                if (_connections.TryGetValue(endpoint.Id, out var connection) && connection.Endpoint.BaseUri == endpoint.BaseUri)
                {
                    return connection;
                }

                connection = CreateConnection(endpoint);
                ReplaceConnection(endpoint.Id, connection);
                return connection;
            }
        }

        /// <summary>Constructs an idle session using anonymous transports until an explicit login is requested.</summary>
        /// <param name="endpoint">The saved configuration.</param>
        /// <returns>The new Guest connection.</returns>
        private EndpointConnection CreateConnection(EndpointConfiguration endpoint)
        {
            var provider = new MajNetClient(endpoint, _networkPolicy, _apiClient);
            var media = _ownsResourceClient ? provider.CreateMediaClient() : provider.CreateMediaClient(_resourceClient);
            media.Timeout = TimeSpan.FromMinutes(5);
            return new EndpointConnection(endpoint, provider, media);
        }

        /// <summary>Replaces an account choice while canceling consumers of the previous account.</summary>
        /// <param name="endpointId">The configuration being changed.</param>
        /// <param name="replacement">The new independent connection.</param>
        private void ReplaceConnection(string endpointId, EndpointConnection replacement)
        {
            if (_connections.Remove(endpointId, out var previous))
            {
                previous.Retire();
                _retiredConnections.Add(previous);
            }

            _connections[endpointId] = replacement;
        }

        /// <summary>Cancels removed or replaced configurations while the settings lock is held.</summary>
        /// <param name="previousSettings">The preferences before this load or persistence operation.</param>
        private void ReconcileConnections(AppSettings previousSettings)
        {
            foreach (var pair in _pendingLogins.ToArray())
            {
                var current = _settings.Endpoints.FirstOrDefault(endpoint => endpoint.Id == pair.Key && endpoint.BaseUri == pair.Value.Endpoint.BaseUri);
                if (current is not null && current.SelectedAccountId == pair.Value.Endpoint.SelectedAccountId
                    && SameLoginPreferences(current.Accounts.FirstOrDefault(account => account.Id == current.SelectedAccountId),
                        pair.Value.Endpoint.Accounts.FirstOrDefault(account => account.Id == pair.Value.Endpoint.SelectedAccountId)))
                {
                    continue;
                }

                _pendingLogins.Remove(pair.Key);
                pair.Value.Retire();
                _accountChoices[pair.Key] = _accountChoices.GetValueOrDefault(pair.Key) + 1;
            }

            foreach (var pair in _connections.ToArray())
            {
                var current = _settings.Endpoints.FirstOrDefault(endpoint => endpoint.Id == pair.Key && endpoint.BaseUri == pair.Value.Endpoint.BaseUri);
                var previous = previousSettings.Endpoints.FirstOrDefault(endpoint => endpoint.Id == pair.Key);
                var currentUsername = current?.Accounts.FirstOrDefault(account => account.Id == current.SelectedAccountId)?.Username;
                var previousUsername = previous?.Accounts.FirstOrDefault(account => account.Id == previous.SelectedAccountId)?.Username;
                var choiceChanged = previous?.SelectedAccountId != current?.SelectedAccountId || previousUsername != currentUsername;
                if (current is not null && (!choiceChanged || !pair.Value.Provider.IsLoggedIn || currentUsername == pair.Value.Username))
                {
                    continue;
                }

                _connections.Remove(pair.Key);
                pair.Value.Retire();
                _retiredConnections.Add(pair.Value);
                _accountChoices[pair.Key] = _accountChoices.GetValueOrDefault(pair.Key) + 1;
            }

            foreach (var endpointId in _accountChoices.Keys.ToArray())
            {
                if (!_settings.Endpoints.Any(endpoint => endpoint.Id == endpointId))
                {
                    _accountChoices[endpointId]++;
                }
            }
        }

        /// <summary>Compares authentication preferences independently of collection-upload authorization.</summary>
        /// <param name="current">The currently saved account, or null for Guest.</param>
        /// <param name="pending">The account captured by the pending login, or null for Guest.</param>
        /// <returns>Whether the pending login still uses the same account and credential choices.</returns>
        private static bool SameLoginPreferences(SavedEndpointAccount? current, SavedEndpointAccount? pending)
        {
            if (current is null || pending is null)
            {
                return ReferenceEquals(current, pending);
            }

            return current.Id == pending.Id && current.Username == pending.Username
                && current.RememberPassword == pending.RememberPassword && current.AutoLogin == pending.AutoLogin;
        }

        /// <summary>Releases isolated session transports after the host has drained workspace operations.</summary>
        private void DisposeOnlineSessions()
        {
            foreach (var pending in _pendingLogins.Values)
            {
                pending.Retire();
            }

            _pendingLogins.Clear();
            foreach (var connection in _connections.Values.Concat(_retiredConnections))
            {
                connection.Dispose();
            }

            _connections.Clear();
            _retiredConnections.Clear();
        }

        /// <summary>Owns the API, media and cancellation lifetime of one configured account choice.</summary>
        private sealed class EndpointConnection : IDisposable
        {
            /// <summary>Cancels operations that belong to a replaced account.</summary>
            private readonly CancellationTokenSource _lifetime = new();

            /// <summary>Constructs a session that initially represents Guest.</summary>
            /// <param name="endpoint">The saved source configuration.</param>
            /// <param name="provider">The isolated API client.</param>
            /// <param name="mediaClient">The isolated media client.</param>
            public EndpointConnection(EndpointConfiguration endpoint, MajNetClient provider, HttpClient mediaClient)
            {
                Endpoint = endpoint;
                Provider = provider;
                MediaClient = mediaClient;
            }

            /// <summary>Gets the configuration that owns this account session.</summary>
            public EndpointConfiguration Endpoint { get; }

            /// <summary>Gets this account's isolated API adapter.</summary>
            public MajNetClient Provider { get; }

            /// <summary>Gets this account's isolated media transport.</summary>
            public HttpClient MediaClient { get; }

            /// <summary>Gets or sets the account name after a successful login.</summary>
            public string? Username { get; set; }

            /// <summary>Combines the caller's cancellation with the selected-account lifetime.</summary>
            /// <param name="cancellationToken">The caller's cancellation.</param>
            /// <returns>The linked source owned by the caller.</returns>
            public CancellationTokenSource CreateCancellation(CancellationToken cancellationToken)
            {
                return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            }

            /// <summary>Stops consumers of this account without making remote requests.</summary>
            public void Retire()
            {
                _lifetime.Cancel();
            }

            /// <summary>Releases the account's transports and lifetime.</summary>
            public void Dispose()
            {
                _lifetime.Cancel();
                MediaClient.Dispose();
                Provider.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
