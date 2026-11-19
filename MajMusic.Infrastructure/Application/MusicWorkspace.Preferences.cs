#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Applies narrow audio preference edits under the existing settings serialization gate.</summary>
    public sealed partial class MusicWorkspace : IPlaybackPreferencesWorkspace, IChartPracticePreferencesWorkspace
    {
        /// <inheritdoc />
        public Task<AppSettings> UpdateAudioOutputAsync(AudioOutputPreferences preferences, CancellationToken cancellationToken = default)
        {
            return UpdatePlayerPreferencesAsync(settings => settings with { AudioOutput = preferences }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<AppSettings> UpdateChartPracticeAsync(ChartPracticeOptions options, CancellationToken cancellationToken = default)
        {
            return UpdatePlayerPreferencesAsync(settings => settings with { ChartPractice = options.Normalize() }, cancellationToken);
        }

        private async Task<AppSettings> UpdatePlayerPreferencesAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken)
        {
            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AppSettings candidate;
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_settingsLoaded)
                    {
                        throw new InvalidOperationException("Load preferences before updating audio output.");
                    }

                    candidate = update(_settings);
                }

                JsonSettingsStore.Validate(candidate);
                await PersistSettingsAsync(candidate, null, cancellationToken).ConfigureAwait(false);
                lock (_stateGate)
                {
                    return _settings;
                }
            }
            finally
            {
                _settingsGate.Release();
            }
        }
    }
}
