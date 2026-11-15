#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Persists one explicit online choice independently of account, audio and directory edits.</summary>
    public sealed partial class MusicWorkspace : IOnlinePreferencesWorkspace
    {
        /// <inheritdoc />
        public async Task<AppSettings> UpdateOnlineEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            long permissionRevision;
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_settingsLoaded)
                {
                    throw new InvalidOperationException("Load preferences before saving the online-mode choice.");
                }

                permissionRevision = ++_permissionRevision;
                _settings = _settings with { OnlineEnabled = enabled };
                _networkPolicy.SetEnabled(enabled);
            }

            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AppSettings candidate;
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    candidate = _settings with { OnlineEnabled = enabled };
                }

                JsonSettingsStore.Validate(candidate);
                await PersistSettingsAsync(candidate, permissionRevision, cancellationToken).ConfigureAwait(false);
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
