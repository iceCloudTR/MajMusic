#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Persists appearance independently of unrelated preference editors.</summary>
    public sealed partial class MusicWorkspace : IAppearancePreferencesWorkspace
    {
        /// <inheritdoc />
        public async Task<AppSettings> UpdateAppearanceAsync(AppearancePreference appearance, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(appearance))
            {
                throw new ArgumentOutOfRangeException(nameof(appearance));
            }

            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AppSettings candidate;
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_settingsLoaded)
                    {
                        throw new InvalidOperationException("Load preferences before updating appearance.");
                    }

                    candidate = _settings with { Appearance = appearance };
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
