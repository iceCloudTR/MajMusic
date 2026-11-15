#nullable enable

using MajMusic.Core.Abstractions;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Coalesces local collection edits into observed, account-scoped cloud uploads.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Protects the single synchronization worker's pending generation.</summary>
        private readonly object _cloudCollectionGate = new();
        /// <summary>Serializes local playlist reads while account-private snapshots are merged only at publication.</summary>
        private readonly SemaphoreSlim _playlistReloadGate = new(1, 1);
        /// <summary>Records the newest local collection snapshot requested for synchronization.</summary>
        private long _cloudCollectionVersion;
        /// <summary>Identifies the worker permitted to release synchronization ownership.</summary>
        private long _cloudCollectionWorkerId;
        /// <summary>Indicates whether a tracked worker is processing local edits.</summary>
        private bool _cloudCollectionWorkerRunning;
        /// <summary>Exposes completion of the current coalesced synchronization batch.</summary>
        private Task _cloudCollectionsUpdate = Task.CompletedTask;

        /// <summary>Gets completion of the latest observed cloud collection synchronization.</summary>
        public Task CloudCollectionsUpdate => Volatile.Read(ref _cloudCollectionsUpdate);

        /// <summary>Schedules the latest local lists after an explicit collection or account preference change.</summary>
        internal void RequestCloudCollectionSync()
        {
            if (_workspace is not ICloudCollectionWorkspace || !OnlineEnabled || !_accountSettingsLoaded
                || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            long workerId;
            lock (_cloudCollectionGate)
            {
                _cloudCollectionVersion++;
                if (_cloudCollectionWorkerRunning)
                {
                    return;
                }

                _cloudCollectionWorkerRunning = true;
                workerId = ++_cloudCollectionWorkerId;
            }

            Volatile.Write(ref _cloudCollectionsUpdate, RunTrackedAsync(() => RunCloudCollectionSyncAsync(workerId)));
        }

        /// <summary>Uploads current local lists and refreshes only the still-selected successful account snapshots.</summary>
        /// <param name="workerId">The worker permitted to release the current batch.</param>
        /// <returns>A task representing all coalesced local edits and their visible cloud refresh.</returns>
        private async Task RunCloudCollectionSyncAsync(long workerId)
        {
            var attemptedVersion = 0L;
            try
            {
                await Task.Yield();
                while (OnlineEnabled && !Volatile.Read(ref _isDisposing))
                {
                    long version;
                    lock (_cloudCollectionGate)
                    {
                        version = _cloudCollectionVersion;
                    }

                    attemptedVersion = version;
                    var results = await ((ICloudCollectionWorkspace)_workspace).SynchronizeCloudCollectionsAsync(_lifetime.Token);
                    _lifetime.Token.ThrowIfCancellationRequested();
                    if (!OnlineEnabled)
                    {
                        return;
                    }

                    var settings = await _workspace.LoadSettingsAsync(_lifetime.Token);
                    var currentResults = results.Where(result => settings.Endpoints.Any(endpoint => endpoint.Id == result.EndpointId
                        && endpoint.SelectedAccountId == result.AccountId
                        && endpoint.Accounts.Any(account => account.Id == result.AccountId && account.UseCloudCollections))).ToArray();
                    var refreshed = false;
                    var refreshFailed = false;
                    foreach (var result in currentResults.Where(item => item.Succeeded))
                    {
                        var endpoint = settings.Endpoints.FirstOrDefault(item => item.Id == result.EndpointId
                            && item.SelectedAccountId == result.AccountId
                            && item.Accounts.Any(account => account.Id == result.AccountId && account.UseCloudCollections));
                        if (endpoint is null || !OnlineEnabled || _onlineWorkspace is null)
                        {
                            continue;
                        }

                        try
                        {
                            var lists = await _onlineWorkspace.GetCloudPlaylistsAsync(endpoint, _lifetime.Token);
                            var current = await _workspace.LoadSettingsAsync(_lifetime.Token);
                            var account = current.Endpoints.FirstOrDefault(item => item.Id == endpoint.Id && item.BaseUri == endpoint.BaseUri
                                && item.SelectedAccountId == result.AccountId)?.Accounts.FirstOrDefault(item => item.Id == result.AccountId);
                            if (OnlineEnabled && account is { UseCloudCollections: true }
                                && _onlineWorkspace.GetEndpointSessions().Any(session => session.EndpointId == endpoint.Id
                                    && session.IsLoggedIn && session.Username == account.Username))
                            {
                                _cloudPlaylists[endpoint.Id] = lists;
                                refreshed = true;
                            }
                        }
                        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                        {
                            // A replaced session cannot publish its earlier private list snapshot.
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            refreshFailed = true;
                        }
                    }

                    if (!OnlineEnabled)
                    {
                        return;
                    }

                    if (refreshFailed || currentResults.Any(item => !item.Succeeded))
                    {
                        await ReconcileExpiredSessionsAsync();
                    }

                    if (refreshed)
                    {
                        await ReloadPlaylistsAsync(false);
                    }

                    if (currentResults.Any(item => !item.Succeeded))
                    {
                        Status = "收藏夹同步未完成";
                    }
                    else if (refreshFailed)
                    {
                        Status = "收藏夹刷新未完成";
                    }
                    else if (currentResults.Any(item => item.CollectionCount > 0))
                    {
                        Status = "收藏夹已同步";
                    }

                    lock (_cloudCollectionGate)
                    {
                        if (version == _cloudCollectionVersion)
                        {
                            _cloudCollectionWorkerRunning = false;
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Offline mode, an account change or view shutdown ended the current batch.
            }
            catch (Exception exception)
            {
                ReportError(exception);
            }
            finally
            {
                var retryPending = false;
                lock (_cloudCollectionGate)
                {
                    if (_cloudCollectionWorkerId == workerId)
                    {
                        _cloudCollectionWorkerRunning = false;
                        retryPending = attemptedVersion != _cloudCollectionVersion && OnlineEnabled && !Volatile.Read(ref _isDisposing);
                    }
                }

                if (retryPending)
                {
                    RequestCloudCollectionSync();
                }
            }
        }
    }
}
