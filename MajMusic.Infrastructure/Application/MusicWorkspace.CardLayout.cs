#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Persists list density without replacing other preference editors' changes.</summary>
    public sealed partial class MusicWorkspace : ICardLayoutPreferencesWorkspace
    {
        /// <inheritdoc />
        public async Task<AppSettings> UpdateCardLayoutAsync(SongCardLayout layout, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(layout))
            {
                throw new ArgumentOutOfRangeException(nameof(layout));
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
                        throw new InvalidOperationException("Load preferences before updating the card layout.");
                    }

                    candidate = _settings with { CardLayout = layout };
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
