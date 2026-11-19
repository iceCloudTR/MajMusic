#nullable enable

using MajMusic.Core.Abstractions;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Coalesces online-mode edits while preserving independent settings editors.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Protects ownership of the single local-preference worker.</summary>
        private readonly object _onlinePreferencesGate = new();
        /// <summary>Orders user choices without creating a worker for each toggle.</summary>
        private long _onlinePreferenceVersion;
        /// <summary>Identifies the worker that may release the current batch.</summary>
        private long _onlinePreferenceWorkerId;
        /// <summary>Indicates whether a worker owns pending saves.</summary>
        private bool _onlinePreferenceWorkerRunning;
        /// <summary>Prevents initial defaults from being saved over an existing preference.</summary>
        private bool _onlinePreferencesReady;
        /// <summary>Retains a user choice made before initial settings finish loading.</summary>
        private bool _onlinePreferencePending;
        /// <summary>Exposes completion of the current merged local-save batch.</summary>
        private Task _onlineSettingsUpdate = Task.CompletedTask;

        /// <summary>Gets completion of the latest merged online-mode preference save.</summary>
        public Task OnlineSettingsUpdate => _onlineSettingsUpdate;

        /// <summary>Drains earlier saves before reloading all application settings.</summary>
        /// <returns>A task representing readiness for restoring saved preferences.</returns>
        private async Task PrepareOnlinePreferencesAsync()
        {
            _onlinePreferencesReady = false;
            await OnlineSettingsUpdate;
        }

        /// <summary>Restores the loaded permission unless the user already operated the switch.</summary>
        /// <param name="saved">The online choice from the settings snapshot.</param>
        private void RestoreOnlinePreference(bool saved)
        {
            ApplyOnlinePermission(_onlinePreferencePending ? OnlineEnabled : saved);
        }

        /// <summary>Applies network permission before notifying the switch and its dependent actions.</summary>
        /// <param name="enabled">Whether to allow configured source requests.</param>
        /// <exception cref="ObjectDisposedException">The workspace has already closed.</exception>
        private void ApplyOnlinePermission(bool enabled)
        {
            if (OnlineEnabled != enabled || _workspace.IsOnline != enabled)
            {
                _workspace.SetOnlineEnabled(enabled);
            }

            _settings = _settings with { OnlineEnabled = enabled };
            if (SetField(ref _onlineEnabled, enabled, nameof(OnlineEnabled)))
            {
                QueueWarmBrowsePreviews();
                RefreshCommands();
                OnPropertyChanged(nameof(HasMoreRecommendations));
            }
        }

        /// <summary>Allows local writes as soon as the persisted preferences have loaded.</summary>
        private void CompleteOnlinePreferencesInitialization()
        {
            _onlinePreferencesReady = true;
            if (_onlinePreferencePending)
            {
                _onlinePreferencePending = false;
                QueueOnlinePreferenceUpdate();
            }
        }

        /// <summary>Schedules persistence for an explicit switch edit once the settings store is ready.</summary>
        private void RecordOnlinePreferenceEdit()
        {
            if (!_onlinePreferencesReady)
            {
                _onlinePreferencePending = true;
                return;
            }

            QueueOnlinePreferenceUpdate();
        }

        /// <summary>Starts one tracked worker that saves only the newest online choice.</summary>
        private void QueueOnlinePreferenceUpdate()
        {
            long workerId;
            lock (_onlinePreferencesGate)
            {
                _onlinePreferenceVersion++;
                if (_onlinePreferenceWorkerRunning || Volatile.Read(ref _isDisposing))
                {
                    return;
                }

                _onlinePreferenceWorkerRunning = true;
                workerId = ++_onlinePreferenceWorkerId;
            }

            _onlineSettingsUpdate = RunTrackedAsync(() => RunOnlinePreferenceWorkerAsync(workerId));
        }

        /// <summary>Persists merged choices and drains the final local save during view shutdown.</summary>
        /// <param name="workerId">The batch permitted to release worker ownership.</param>
        /// <returns>A task representing completed local preference persistence.</returns>
        private async Task RunOnlinePreferenceWorkerAsync(long workerId)
        {
            try
            {
                await Task.Yield();
                while (true)
                {
                    long version;
                    bool enabled;
                    lock (_onlinePreferencesGate)
                    {
                        version = _onlinePreferenceVersion;
                        enabled = OnlineEnabled;
                    }

                    if (_workspace is IOnlinePreferencesWorkspace patch)
                    {
                        await patch.UpdateOnlineEnabledAsync(enabled, CancellationToken.None);
                    }
                    else
                    {
                        var current = await _workspace.LoadSettingsAsync(CancellationToken.None);
                        await _workspace.SaveSettingsAsync(current with { OnlineEnabled = enabled }, CancellationToken.None);
                    }

                    _settings = _settings with { OnlineEnabled = OnlineEnabled };
                    lock (_onlinePreferencesGate)
                    {
                        if (version != _onlinePreferenceVersion)
                        {
                            continue;
                        }

                        _onlinePreferenceWorkerRunning = false;
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                ReportError(exception);
            }
            finally
            {
                lock (_onlinePreferencesGate)
                {
                    if (_onlinePreferenceWorkerId == workerId)
                    {
                        _onlinePreferenceWorkerRunning = false;
                    }
                }
            }
        }
    }
}
