#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Applies narrow audio preference edits under the existing settings serialization gate.</summary>
    public sealed partial class MusicWorkspace : IPlaybackPreferencesWorkspace
    {
        /// <inheritdoc />
        public async Task<AppSettings> UpdateAudioOutputAsync(AudioOutputPreferences preferences, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(preferences);
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

                    candidate = _settings with { AudioOutput = preferences };
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
