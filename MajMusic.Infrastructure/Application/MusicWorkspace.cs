#nullable enable

using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Downloads;
using MajMusic.Infrastructure.Credentials;
using MajMusic.Infrastructure.Import;
using MajMusic.Infrastructure.Network;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Composes local persistence and optional sources without contacting them at startup.</summary>
    public sealed partial class MusicWorkspace : IMusicWorkspace, IOnlineMusicWorkspace, IAccountPreferencesWorkspace, IDisposable
    {
        /// <summary>Stores library metadata and playlists.</summary>
        private readonly SqliteLibraryRepository _library;
        /// <summary>Persists non-secret preferences.</summary>
        private readonly ISettingsStore _settingsStore;
        /// <summary>Copies imports without changing their original directory.</summary>
        private readonly LocalMusicImporter _importer;
        /// <summary>Indexes user-selected directory trees without copying their source files.</summary>
        private readonly LocalMusicScanner _scanner = new();
        /// <summary>Controls all explicitly requested network operations.</summary>
        private readonly NetworkAccessPolicy _networkPolicy = new();
        /// <summary>Downloads public resources without account cookies.</summary>
        private readonly HttpClient _resourceClient;
        /// <summary>Contains an optional caller-owned isolated API transport.</summary>
        private readonly HttpClient? _apiClient;
        /// <summary>Records whether this workspace owns the media transport.</summary>
        private readonly bool _ownsResourceClient;
        /// <summary>Stores validated resources separately from local imports.</summary>
        private readonly MediaCache _cache;
        /// <summary>Protects current settings and explicit network choices.</summary>
        private readonly object _stateGate = new();
        /// <summary>Serializes settings operations without blocking immediate offline cancellation.</summary>
        private readonly SemaphoreSlim _settingsGate = new(1, 1);
        /// <summary>Serializes metadata upgrades and synchronization merges.</summary>
        private readonly SemaphoreSlim _metadataGate = new(1, 1);
        /// <summary>Stores the currently loaded non-secret preferences.</summary>
        private AppSettings _settings = new();
        /// <summary>Orders permission choices so older writes cannot restore them.</summary>
        private long _permissionRevision;
        /// <summary>Records whether initial preferences have been loaded.</summary>
        private bool _settingsLoaded;
        /// <summary>Records whether owned services have been released.</summary>
        private bool _disposed;

        /// <summary>Initializes local services without making network requests.</summary>
        /// <param name="dataDirectory">An explicit data directory, or null for the platform application data location.</param>
        /// <param name="apiClient">An optional caller-owned isolated API transport without default credentials or redirects.</param>
        /// <param name="resourceClient">An optional caller-owned anonymous audio and cover transport.</param>
        /// <param name="settingsStore">An optional host-supplied non-secret preference store.</param>
        /// <param name="credentialStore">An optional system-vault adapter; tests may inject an isolated store.</param>
        public MusicWorkspace(string? dataDirectory = null, HttpClient? apiClient = null,
            HttpClient? resourceClient = null, ISettingsStore? settingsStore = null, ICredentialStore? credentialStore = null)
        {
            DataDirectory = Path.GetFullPath(dataDirectory ?? GetDefaultDataDirectory());
            _library = new SqliteLibraryRepository(Path.Combine(DataDirectory, "library.db"));
            _settingsStore = settingsStore ?? new JsonSettingsStore(Path.Combine(DataDirectory, "settings.json"));
            _credentialStore = credentialStore ?? CredentialStoreFactory.Create();
            _importer = new LocalMusicImporter(Path.Combine(DataDirectory, "Library"));
            _apiClient = apiClient;
            _ownsResourceClient = resourceClient is null;
            _resourceClient = resourceClient ?? SourceHttpTransportFactory.CreateAnonymousClient();
            if (_ownsResourceClient)
            {
                _resourceClient.Timeout = TimeSpan.FromMinutes(5);
            }
            _cache = new MediaCache(Path.Combine(DataDirectory, "Cache"), _resourceClient, _networkPolicy);
        }

        /// <inheritdoc />
        public bool IsOnline => _networkPolicy.IsEnabled;

        /// <inheritdoc />
        public string DataDirectory { get; }

        /// <inheritdoc />
        public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
        {
            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var loaded = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var firstLoad = !_settingsLoaded && _permissionRevision == 0;
                    var previousSettings = _settings;
                    _settings = loaded with { OnlineEnabled = firstLoad ? loaded.OnlineEnabled : _settings.OnlineEnabled };
                    ReconcileConnections(previousSettings);
                    _settingsLoaded = true;
                    if (firstLoad)
                    {
                        _networkPolicy.SetEnabled(_settings.OnlineEnabled);
                    }

                    return _settings;
                }
            }
            finally
            {
                _settingsGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            long permissionRevision;
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                permissionRevision = ++_permissionRevision;
            }

            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                JsonSettingsStore.Validate(settings);
                settings = NormalizeAccountAddressChanges(settings);
                JsonSettingsStore.Validate(settings);
                await RemoveUnusedCredentialsAsync(settings, cancellationToken).ConfigureAwait(false);
                await PersistSettingsAsync(settings, permissionRevision, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _settingsGate.Release();
            }
        }

        /// <inheritdoc />
        public void SetOnlineEnabled(bool enabled)
        {
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _permissionRevision++;
                _settings = _settings with { OnlineEnabled = enabled };
                _networkPolicy.SetEnabled(enabled);
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
        {
            return _library.SearchAsync(query, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
        {
            var track = await _importer.ImportDirectoryAsync(directory, cancellationToken);
            await _library.UpsertTracksAsync(new[] { track }, cancellationToken);
            return track;
        }

        /// <inheritdoc />
        public async Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
        {
            var result = await _scanner.ScanAsync(directory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = (await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false))
                    .ToDictionary(track => track.Id, StringComparer.Ordinal);
                var preserved = new List<MusicTrack>(result.Tracks.Count);
                foreach (var track in result.Tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    existing.TryGetValue(track.Id, out var previous);
                    if (previous?.AudioMetadataLength is { } length && previous.AudioMetadataLastWriteUtcTicks is { } ticks
                        && track.Audio.LocalPath is { } path)
                    {
                        try
                        {
                            var file = new FileInfo(path);
                            if (file.Exists && file.Length == length && file.LastWriteTimeUtc.Ticks == ticks)
                            {
                                preserved.Add(track with
                                {
                                    Duration = previous.Duration,
                                    AudioInfo = previous.AudioInfo,
                                    AudioMetadataLength = length,
                                    AudioMetadataLastWriteUtcTicks = ticks
                                });
                                continue;
                            }
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            // A changed source file can be reprobed later without invalidating the completed text scan.
                        }
                    }

                    preserved.Add(track);
                }

                await _library.UpsertTracksAsync(preserved, cancellationToken).ConfigureAwait(false);
                result = result with { Tracks = preserved };
            }
            finally
            {
                _metadataGate.Release();
            }

            return result;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
        {
            return _library.GetPlaylistsAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            if (playlist.IsCloud)
            {
                throw new InvalidOperationException("云端列表不能保存为本地列表。请先创建本地播放列表。");
            }

            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _library.SavePlaylistAsync(playlist, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
        {
            EnsureConfiguredEndpoint(endpoint);
            var connection = GetConnection(endpoint);
            using var sessionLifetime = connection.CreateCancellation(cancellationToken);
            cancellationToken = sessionLifetime.Token;
            var tracks = await connection.Provider.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = (await _library.SearchAsync(null, cancellationToken).ConfigureAwait(false))
                    .ToDictionary(track => track.Id, StringComparer.Ordinal);
                var aliases = GetTrackIdentityAliases(existing.Values);
                var updates = new List<MusicTrack>(tracks.Count);
                foreach (var received in tracks)
                {
                    var track = RetainTrackIdentity(received, aliases);
                    if (existing.TryGetValue(track.Id, out var previous))
                    {
                        if (SameRevision(track, previous))
                        {
                            updates.Add(track with
                            {
                                Audio = MergeResource(track.Audio, previous.Audio),
                                Cover = track.Cover is null ? null : MergeResource(track.Cover, previous.Cover),
                                Duration = track.Duration ?? previous.Duration,
                                AudioInfo = MergeStreamInfo(track.AudioInfo, previous.AudioInfo),
                                AudioMetadataLength = previous.AudioMetadataLength,
                                AudioMetadataLastWriteUtcTicks = previous.AudioMetadataLastWriteUtcTicks
                            });
                            continue;
                        }

                        AddOfflineCopy(updates, previous);
                    }

                    updates.Add(track);
                }

                await _library.UpsertTracksAsync(updates, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _metadataGate.Release();
            }

            if (tracks.Count > 0)
            {
                await SaveServerIdentityAsync(endpoint, tracks[0].SourceId, cancellationToken).ConfigureAwait(false);
            }

            return tracks.Count;
        }

        /// <inheritdoc />
        public async Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            var resolved = await ResolveMetadataAsync(track, true, cancellationToken).ConfigureAwait(false);
            if (resolved.Cover is null)
            {
                return null;
            }

            try
            {
                var keepOffline = await _cache.IsKeptOfflineAsync(ScopeResourceForCache(resolved, resolved.Audio), cancellationToken).ConfigureAwait(false);
                var path = await GetMediaAsync(resolved, true, keepOffline, cancellationToken).ConfigureAwait(false);
                await RememberLocalResourceAsync(resolved, path, true, cancellationToken).ConfigureAwait(false);
                return path;
            }
            catch (InvalidOperationException) when (!IsOnline)
            {
                return null;
            }
            catch (FileNotFoundException) when (!IsOnline)
            {
                return null;
            }
        }

        /// <inheritdoc />
        public async Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            var operation = BeginAudioDownload(track);
            try
            {
                var resolved = await ResolveMetadataAsync(track, true, cancellationToken).ConfigureAwait(false);
                var path = await _cache.TryGetCachedAsync(ScopeResourceForCache(resolved, resolved.Audio), keepOffline, cancellationToken).ConfigureAwait(false);
                if (path is null)
                {
                    resolved = await ResolveMetadataAsync(track, false, cancellationToken).ConfigureAwait(false);
                    if (!IsOnline && resolved.RemoteId is not null && resolved.Audio.Sha256 is null
                        && !File.Exists(resolved.Audio.LocalPath))
                    {
                        throw new InvalidOperationException("This source revision has not been downloaded for offline playback.");
                    }

                    path = await GetMediaAsync(resolved, false, keepOffline, cancellationToken, operation).ConfigureAwait(false);
                }

                await RememberLocalResourceAsync(resolved, path, false, cancellationToken).ConfigureAwait(false);
                if (keepOffline)
                {
                    await PreserveOfflineCoverAsync(resolved, cancellationToken).ConfigureAwait(false);
                }

                operation.Finish(AudioDownloadState.Completed, path);
                return path;
            }
            catch (OperationCanceledException)
            {
                operation.Finish(AudioDownloadState.Cancelled);
                throw;
            }
            catch (Exception)
            {
                operation.Finish(AudioDownloadState.Failed);
                throw;
            }
        }

        /// <inheritdoc />
        public Task ClearCacheAsync(CancellationToken cancellationToken = default)
        {
            return _cache.ClearCacheAsync(cancellationToken);
        }

        /// <summary>Releases services after the host has canceled and drained their active operations.</summary>
        public void Dispose()
        {
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _networkPolicy.Dispose();
            }

            DisposeCloudCollectionSync();
            DisposeOnlineSessions();
            _cache.Dispose();
            if (_ownsResourceClient)
            {
                _resourceClient.Dispose();
            }

            _settingsGate.Dispose();
            _metadataGate.Dispose();
            _autoLoginGate.Dispose();
        }

        /// <summary>Persists under the settings gate while respecting newer immediate permission decisions.</summary>
        /// <param name="settings">The metadata and requested permission snapshot.</param>
        /// <param name="permissionRevision">The explicit save decision, or null for metadata-only persistence.</param>
        /// <param name="cancellationToken">Cancels persistence.</param>
        /// <returns>A task representing a stable persisted snapshot.</returns>
        private async Task PersistSettingsAsync(AppSettings settings, long? permissionRevision, CancellationToken cancellationToken)
        {
            while (true)
            {
                AppSettings candidate;
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    candidate = settings with
                    {
                        OnlineEnabled = permissionRevision == _permissionRevision ? settings.OnlineEnabled : _settings.OnlineEnabled
                    };
                }

                await _settingsStore.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var online = permissionRevision == _permissionRevision ? candidate.OnlineEnabled : _settings.OnlineEnabled;
                    var previousSettings = _settings;
                    _settings = candidate with { OnlineEnabled = online };
                    ReconcileConnections(previousSettings);
                    _settingsLoaded = true;
                    if (permissionRevision == _permissionRevision)
                    {
                        _networkPolicy.SetEnabled(online);
                    }

                    if (candidate.OnlineEnabled == online)
                    {
                        return;
                    }

                    // Correct disk after an immediate permission change without replaying the old choice.
                    settings = _settings;
                    permissionRevision = null;
                }
            }
        }

        /// <summary>Updates discovery metadata without treating it as permission to reconnect.</summary>
        /// <param name="endpoint">The originally synchronized endpoint.</param>
        /// <param name="serverId">The discovered server identity.</param>
        /// <param name="cancellationToken">Cancels persistence.</param>
        /// <returns>A task representing the current configuration update.</returns>
        private async Task SaveServerIdentityAsync(EndpointConfiguration endpoint, string serverId, CancellationToken cancellationToken)
        {
            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AppSettings settings;
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    settings = _settings with
                    {
                        Endpoints = _settings.Endpoints.Select(item => item.Id == endpoint.Id && item.BaseUri == endpoint.BaseUri
                            ? item with { ServerId = serverId } : item).ToArray()
                    };
                }

                await PersistSettingsAsync(settings, null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _settingsGate.Release();
            }
        }

        /// <summary>Reads current descriptors instead of trusting stale view or queue objects.</summary>
        /// <param name="requested">The entry retained by a view or queue.</param>
        /// <param name="cover">Whether only stored cover metadata is required, without resolving audio details.</param>
        /// <param name="cancellationToken">Cancels lookup and optional explicitly enabled resolution.</param>
        /// <returns>The current metadata or a retained offline version.</returns>
        private async Task<MusicTrack> ResolveMetadataAsync(MusicTrack requested, bool cover, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(requested);
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var resolved = await _library.FindTrackAsync(requested.Id, cancellationToken).ConfigureAwait(false) ?? requested;
                if (!IsOnline && !SameRevision(resolved, requested))
                {
                    resolved = await _library.FindTrackAsync(GetOfflineCopyId(requested), cancellationToken).ConfigureAwait(false) ?? resolved;
                }

                if (!cover && IsOnline && resolved.RemoteId is not null && resolved.Audio.Sha256 is null)
                {
                    var endpoint = GetEndpointForTrack(resolved);
                    var connection = GetConnection(endpoint);
                    using var sessionLifetime = connection.CreateCancellation(cancellationToken);
                    var detailed = await connection.Provider.GetTrackAsync(resolved.RemoteId, sessionLifetime.Token).ConfigureAwait(false)
                        ?? throw new FileNotFoundException("The source does not provide this audio file.");
                    if (detailed.SourceId != resolved.SourceId)
                    {
                        throw new InvalidOperationException("接入点的音乐来源已更改，请重新同步音乐库。");
                    }

                    detailed = detailed with
                    {
                        Id = resolved.Id,
                        EndpointId = resolved.EndpointId,
                        Revision = string.IsNullOrWhiteSpace(detailed.Revision) ? resolved.Revision : detailed.Revision,
                        Cover = SameRevision(detailed, resolved) || string.IsNullOrWhiteSpace(detailed.Revision)
                            ? detailed.Cover is { } detailedCover ? MergeResource(detailedCover, resolved.Cover) : resolved.Cover
                            : detailed.Cover,
                        Duration = detailed.Duration ?? (SameRevision(detailed, resolved) || string.IsNullOrWhiteSpace(detailed.Revision) ? resolved.Duration : null),
                        AudioInfo = detailed.AudioInfo ?? (SameRevision(detailed, resolved) || string.IsNullOrWhiteSpace(detailed.Revision) ? resolved.AudioInfo : null)
                    };
                    var updates = new List<MusicTrack>();
                    if (!SameRevision(detailed, resolved))
                    {
                        AddOfflineCopy(updates, resolved);
                    }

                    updates.Add(detailed);
                    await _library.UpsertTracksAsync(updates, sessionLifetime.Token).ConfigureAwait(false);
                    resolved = detailed;
                }

                return resolved;
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        /// <summary>Remembers completed local paths so lazy metadata cannot make retained content unreachable.</summary>
        /// <param name="track">The descriptor used for the transfer.</param>
        /// <param name="path">The validated completed local file.</param>
        /// <param name="cover">Whether the resource is a cover rather than audio.</param>
        /// <param name="cancellationToken">Cancels hashing or persistence.</param>
        /// <returns>A task representing the metadata update.</returns>
        private async Task RememberLocalResourceAsync(MusicTrack track, string path, bool cover, CancellationToken cancellationToken)
        {
            var resource = (cover ? track.Cover : track.Audio)!;
            var digest = resource.Sha256;
            if (digest is null)
            {
                await using var stream = File.OpenRead(path);
                digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            var local = resource with { LocalPath = path, Sha256 = digest };
            var completed = cover ? track with { Cover = local } : track with { Audio = local };
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = await _library.FindTrackAsync(track.Id, cancellationToken).ConfigureAwait(false);
                var updates = new List<MusicTrack>();
                if (current is not null && !SameRevision(current, completed))
                {
                    AddOfflineCopy(updates, completed);
                }
                else
                {
                    var metadata = current ?? completed;
                    updates.Add(cover ? metadata with { Cover = local } : metadata with { Audio = local });
                }

                await _library.UpsertTracksAsync(updates, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _metadataGate.Release();
            }
        }

        /// <summary>Preserves resolved file data when metadata describes the same source revision.</summary>
        /// <param name="incoming">The newly synchronized descriptor.</param>
        /// <param name="previous">The previously resolved descriptor, if any.</param>
        /// <returns>The new route with retained hash, length and local path.</returns>
        private static MediaResource MergeResource(MediaResource incoming, MediaResource? previous)
        {
            return previous is null ? incoming : incoming with
            {
                Sha256 = incoming.Sha256 ?? previous.Sha256,
                Length = incoming.Length ?? previous.Length,
                LocalPath = incoming.LocalPath ?? previous.LocalPath
            };
        }

        /// <summary>Retains only a real downloaded audio version before its source revision changes.</summary>
        /// <param name="updates">The pending transaction entries.</param>
        /// <param name="previous">The replaced source version.</param>
        private static void AddOfflineCopy(List<MusicTrack> updates, MusicTrack previous)
        {
            if (previous.Audio.Sha256 is null || previous.Audio.LocalPath is not { } path || !File.Exists(path))
            {
                return;
            }

            updates.Add(previous with
            {
                Id = GetOfflineCopyId(previous),
                Title = previous.Title + "（离线副本）",
                SourceId = "local/archive",
                EndpointId = null,
                SourceName = "本地",
                RemoteId = null,
                Audio = previous.Audio with { RemoteUri = null },
                Cover = previous.Cover?.LocalPath is { } coverPath && File.Exists(coverPath)
                    ? previous.Cover with { RemoteUri = null } : null
            });
        }

        /// <summary>Builds a stable retained-version identity independent of expiring addresses.</summary>
        /// <param name="track">The previous version or a stale reference to it.</param>
        /// <returns>The deterministic offline-copy identity.</returns>
        private static string GetOfflineCopyId(MusicTrack track)
        {
            var version = string.IsNullOrWhiteSpace(track.Revision) ? track.Audio.Sha256 ?? string.Empty : track.Revision;
            return track.Id + ":offline:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version)));
        }

        /// <summary>Compares source revisions separately from audio content identity.</summary>
        /// <param name="first">The first entry.</param>
        /// <param name="second">The second entry.</param>
        /// <returns>Whether both entries describe the same revision.</returns>
        private static bool SameRevision(MusicTrack first, MusicTrack second)
        {
            return string.Equals(first.Revision ?? string.Empty, second.Revision ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>Checks that a network action references a saved configuration.</summary>
        /// <param name="endpoint">The source requested by the application.</param>
        /// <exception cref="InvalidOperationException">The source is not in the saved configuration.</exception>
        private void EnsureConfiguredEndpoint(EndpointConfiguration endpoint)
        {
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_settings.Endpoints.Any(item => item.Id == endpoint.Id && item.BaseUri == endpoint.BaseUri))
                {
                    throw new InvalidOperationException("Save the source configuration before synchronizing.");
                }
            }
        }

        /// <summary>Prepares portable desktop storage or the platform's mobile application-data directory.</summary>
        /// <returns>The directory owning application settings, library and managed media.</returns>
        private static string GetDefaultDataDirectory()
        {
            return DesktopDataDirectory.PrepareDefaultDirectory();
        }
    }
}
