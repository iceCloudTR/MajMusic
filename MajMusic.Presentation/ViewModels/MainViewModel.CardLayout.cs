#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Restores and persists the shared music-card layout for desktop and mobile settings.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Protects ownership of a coalesced local preference write.</summary>
        private readonly object _cardLayoutSettingsGate = new();
        /// <summary>Retains the immediate toolkit-independent list format.</summary>
        private SongCardLayout _selectedCardLayout = SongCardLayout.Loose;
        /// <summary>Distinguishes restored preferences from construction defaults.</summary>
        private bool _cardLayoutSettingsInitialized;
        /// <summary>Preserves an explicit layout choice made while initialization is loading.</summary>
        private bool _cardLayoutPendingBeforeLoad;
        /// <summary>Identifies the latest explicit local choice.</summary>
        private long _cardLayoutSettingsVersion;
        /// <summary>Coalesces rapid picker changes into one observed save worker.</summary>
        private bool _cardLayoutSettingsWorkerRunning;
        /// <summary>Exposes completion of the current local-save batch.</summary>
        private Task _cardLayoutSettingsUpdate = Task.CompletedTask;

        /// <summary>Gets the three list formats displayed by both platform presentations.</summary>
        public IReadOnlyList<CardLayoutOption> CardLayoutChoices { get; } =
        [
            new(SongCardLayout.Loose, "宽松"),
            new(SongCardLayout.Compact, "紧凑"),
            new(SongCardLayout.Small, "小型")
        ];

        /// <summary>Gets or sets the immediate music-list layout independently of a specific view.</summary>
        public SongCardLayout SelectedCardLayout
        {
            get => _selectedCardLayout;
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

                if (SetField(ref _selectedCardLayout, value))
                {
                    _settings = _settings with { CardLayout = value };
                    OnPropertyChanged(nameof(SelectedCardLayoutOption));
                    if (_cardLayoutSettingsInitialized)
                    {
                        QueueCardLayoutSettingsUpdate();
                    }
                    else
                    {
                        _cardLayoutPendingBeforeLoad = true;
                    }
                }
            }
        }

        /// <summary>Gets or selects a localized card-layout option while ignoring transient empty picker selections.</summary>
        public CardLayoutOption? SelectedCardLayoutOption
        {
            get => CardLayoutChoices.First(option => option.Value == SelectedCardLayout);
            set
            {
                if (value is not null)
                {
                    SelectedCardLayout = value.Value;
                }
            }
        }

        /// <summary>Gets completion of the latest merged list-format preference write.</summary>
        public Task CardLayoutSettingsUpdate => Volatile.Read(ref _cardLayoutSettingsUpdate);

        /// <summary>Drains earlier writes before replacing the saved preference snapshot.</summary>
        /// <returns>A task representing the previous local save.</returns>
        private async Task PrepareCardLayoutSettingsAsync()
        {
            await CardLayoutSettingsUpdate;
            _cardLayoutSettingsInitialized = false;
        }

        /// <summary>Restores the saved format while retaining an explicit choice made during startup.</summary>
        /// <param name="layout">The format loaded from local preferences.</param>
        private void RestoreCardLayoutSettings(SongCardLayout layout)
        {
            if (!_cardLayoutPendingBeforeLoad)
            {
                _selectedCardLayout = layout;
            }

            _settings = _settings with { CardLayout = SelectedCardLayout };
            _cardLayoutSettingsInitialized = true;
            OnPropertyChanged(nameof(SelectedCardLayout));
            OnPropertyChanged(nameof(SelectedCardLayoutOption));
            if (_cardLayoutPendingBeforeLoad)
            {
                _cardLayoutPendingBeforeLoad = false;
                QueueCardLayoutSettingsUpdate();
            }
        }

        /// <summary>Tracks one merged preference-save worker without blocking navigation or playback.</summary>
        private void QueueCardLayoutSettingsUpdate()
        {
            lock (_cardLayoutSettingsGate)
            {
                _cardLayoutSettingsVersion++;
                if (_cardLayoutSettingsWorkerRunning)
                {
                    return;
                }

                _cardLayoutSettingsWorkerRunning = true;
            }

            Volatile.Write(ref _cardLayoutSettingsUpdate, RunTrackedAsync(SaveCardLayoutSettingsAsync));
        }

        /// <summary>Saves the most recent local choice and drains already accepted edits during shutdown.</summary>
        /// <returns>A task representing the coalesced preference batch.</returns>
        private async Task SaveCardLayoutSettingsAsync()
        {
            try
            {
                await Task.Yield();
                while (true)
                {
                    long version;
                    SongCardLayout layout;
                    lock (_cardLayoutSettingsGate)
                    {
                        version = _cardLayoutSettingsVersion;
                        layout = SelectedCardLayout;
                    }

                    if (_workspace is ICardLayoutPreferencesWorkspace preferences)
                    {
                        await preferences.UpdateCardLayoutAsync(layout, CancellationToken.None);
                    }
                    else
                    {
                        var current = await _workspace.LoadSettingsAsync(CancellationToken.None);
                        await _workspace.SaveSettingsAsync(current with { CardLayout = layout }, CancellationToken.None);
                    }

                    lock (_cardLayoutSettingsGate)
                    {
                        if (version != _cardLayoutSettingsVersion)
                        {
                            continue;
                        }

                        _cardLayoutSettingsWorkerRunning = false;
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                lock (_cardLayoutSettingsGate)
                {
                    _cardLayoutSettingsWorkerRunning = false;
                }

                ReportError(exception);
            }
        }
    }

    /// <summary>Labels a toolkit-independent list format for the settings picker.</summary>
    /// <param name="Value">The persisted layout choice.</param>
    /// <param name="Name">The localized picker label.</param>
    public sealed record CardLayoutOption(SongCardLayout Value, string Name);
}
