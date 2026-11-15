#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Applies immediate appearance choices and observes their independent local persistence.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Protects the current merged preference worker's ownership.</summary>
        private readonly object _appearanceGate = new();
        /// <summary>Stores the toolkit-independent selected appearance.</summary>
        private AppearancePreference _themePreference = AppearancePreference.Dark;
        /// <summary>Distinguishes restored preferences from default construction state.</summary>
        private bool _appearanceInitialized;
        /// <summary>Records an explicit choice made while startup preferences are loading.</summary>
        private bool _appearancePendingBeforeLoad;
        /// <summary>Orders explicit user choices while one local write is pending.</summary>
        private long _appearanceVersion;
        /// <summary>Identifies the worker that may release current save ownership.</summary>
        private long _appearanceWorkerId;
        /// <summary>Ensures one appearance save worker coalesces rapid choices.</summary>
        private bool _appearanceWorkerRunning;
        /// <summary>Exposes the latest observed appearance-save batch.</summary>
        private Task _appearanceSettingsUpdate = Task.CompletedTask;

        /// <summary>Gets the three supported appearance choices displayed by both platform presentations.</summary>
        public IReadOnlyList<AppearanceOption> AppearanceOptions { get; } =
        [
            new(AppearancePreference.Dark, "深色"),
            new(AppearancePreference.Light, "浅色"),
            new(AppearancePreference.System, "跟随系统")
        ];

        /// <summary>Gets or sets the immediate appearance preference without referencing a UI theme runtime.</summary>
        public AppearancePreference ThemePreference
        {
            get => _themePreference;
            set
            {
                if (Volatile.Read(ref _isDisposing))
                {
                    return;
                }

                if (!Enum.IsDefined(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                if (_themePreference != value && !_appearanceInitialized)
                {
                    _appearancePendingBeforeLoad = true;
                }

                if (SetField(ref _themePreference, value))
                {
                    _settings = _settings with { Appearance = value };
                    OnPropertyChanged(nameof(SelectedAppearanceOption));
                    if (_appearanceInitialized)
                    {
                        QueueAppearanceSettingsUpdate();
                    }
                    else
                    {
                        _appearancePendingBeforeLoad = true;
                    }
                }
            }
        }

        /// <summary>Gets or selects a real appearance option while ignoring a selector's transient empty selection.</summary>
        public AppearanceOption? SelectedAppearanceOption
        {
            get => AppearanceOptions.First(option => option.Value == ThemePreference);
            set
            {
                if (value is not null)
                {
                    ThemePreference = value.Value;
                }
            }
        }

        /// <summary>Gets completion of the most recently coalesced appearance save.</summary>
        public Task AppearanceSettingsUpdate => Volatile.Read(ref _appearanceSettingsUpdate);
        /// <summary>Gets whether a host can apply restored or explicitly edited appearance rather than a construction default.</summary>
        internal bool IsAppearanceInitialized => _appearanceInitialized || _appearancePendingBeforeLoad;

        /// <summary>Drains earlier local writes before loading a replacement preference snapshot.</summary>
        /// <returns>A task representing completed previous appearance persistence.</returns>
        private async Task PrepareAppearanceSettingsAsync()
        {
            await AppearanceSettingsUpdate;
            _appearanceInitialized = false;
        }

        /// <summary>Restores a persisted appearance while retaining an explicit startup edit.</summary>
        /// <param name="appearance">The appearance read from the current settings file.</param>
        private void RestoreAppearanceSettings(AppearancePreference appearance)
        {
            if (!_appearancePendingBeforeLoad)
            {
                _themePreference = appearance;
            }

            _settings = _settings with { Appearance = _themePreference };
            _appearanceInitialized = true;
            OnPropertyChanged(nameof(ThemePreference));
            OnPropertyChanged(nameof(SelectedAppearanceOption));
            if (_appearancePendingBeforeLoad)
            {
                _appearancePendingBeforeLoad = false;
                QueueAppearanceSettingsUpdate();
            }
        }

        /// <summary>Schedules a tracked local save without occupying the workspace's user-operation state.</summary>
        private void QueueAppearanceSettingsUpdate()
        {
            long workerId;
            lock (_appearanceGate)
            {
                _appearanceVersion++;
                if (_appearanceWorkerRunning)
                {
                    return;
                }

                _appearanceWorkerRunning = true;
                workerId = ++_appearanceWorkerId;
            }

            Volatile.Write(ref _appearanceSettingsUpdate, RunTrackedAsync(() => SaveAppearanceSettingsAsync(workerId)));
        }

        /// <summary>Persists the latest local choice and drains an already authorized final write during shutdown.</summary>
        /// <param name="workerId">The worker permitted to release this batch.</param>
        /// <returns>A task representing all choices merged into this observed batch.</returns>
        private async Task SaveAppearanceSettingsAsync(long workerId)
        {
            try
            {
                await Task.Yield();
                while (true)
                {
                    long version;
                    AppearancePreference appearance;
                    lock (_appearanceGate)
                    {
                        version = _appearanceVersion;
                        appearance = ThemePreference;
                    }

                    if (_workspace is IAppearancePreferencesWorkspace preferences)
                    {
                        await preferences.UpdateAppearanceAsync(appearance, CancellationToken.None);
                    }
                    else
                    {
                        var current = await _workspace.LoadSettingsAsync(CancellationToken.None);
                        await _workspace.SaveSettingsAsync(current with { Appearance = appearance }, CancellationToken.None);
                    }

                    _settings = _settings with { Appearance = ThemePreference };
                    lock (_appearanceGate)
                    {
                        if (version != _appearanceVersion)
                        {
                            continue;
                        }

                        _appearanceWorkerRunning = false;
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
                lock (_appearanceGate)
                {
                    if (_appearanceWorkerId == workerId)
                    {
                        _appearanceWorkerRunning = false;
                    }
                }
            }
        }
    }

    /// <summary>Labels a toolkit-independent appearance choice.</summary>
    /// <param name="Value">The persisted appearance preference.</param>
    /// <param name="Name">The visible choice label.</param>
    public sealed record AppearanceOption(AppearancePreference Value, string Name);
}
