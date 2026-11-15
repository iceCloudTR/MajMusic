#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Serializes independent source edits with other application preferences.</summary>
    public sealed partial class MusicWorkspace : IEndpointPreferencesWorkspace
    {
        /// <inheritdoc />
        public async Task<EndpointConfiguration> SaveEndpointAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AppSettings candidate;
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_settingsLoaded)
                    {
                        throw new InvalidOperationException("Load preferences before editing endpoints.");
                    }

                    var previous = _settings.Endpoints.FirstOrDefault(item => item.Id == endpoint.Id);
                    var updated = previous is null ? endpoint with
                    {
                        Accounts = Array.Empty<SavedEndpointAccount>(),
                        SelectedAccountId = null,
                        ServerId = null
                    } : previous with
                    {
                        Name = endpoint.Name,
                        BaseUri = endpoint.BaseUri,
                        ServerId = previous.BaseUri == endpoint.BaseUri ? previous.ServerId : null
                    };
                    candidate = _settings with
                    {
                        Endpoints = previous is null ? _settings.Endpoints.Append(updated).ToArray()
                            : _settings.Endpoints.Select(item => item.Id == updated.Id ? updated : item).ToArray(),
                        ActiveEndpointId = _settings.ActiveEndpointId ?? updated.Id
                    };
                }

                JsonSettingsStore.Validate(candidate);
                candidate = NormalizeAccountAddressChanges(candidate);
                await RemoveUnusedCredentialsAsync(candidate, cancellationToken).ConfigureAwait(false);
                await PersistSettingsAsync(candidate, null, cancellationToken).ConfigureAwait(false);
                lock (_stateGate)
                {
                    return _settings.Endpoints.Single(item => item.Id == endpoint.Id);
                }
            }
            finally
            {
                _settingsGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task RemoveEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AppSettings candidate;
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var remaining = _settings.Endpoints.Where(item => item.Id != endpointId).ToArray();
                    candidate = _settings with
                    {
                        Endpoints = remaining,
                        ActiveEndpointId = _settings.ActiveEndpointId == endpointId ? remaining.FirstOrDefault()?.Id : _settings.ActiveEndpointId
                    };
                }

                await RemoveUnusedCredentialsAsync(candidate, cancellationToken).ConfigureAwait(false);
                await PersistSettingsAsync(candidate, null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _settingsGate.Release();
            }
        }
    }
}
