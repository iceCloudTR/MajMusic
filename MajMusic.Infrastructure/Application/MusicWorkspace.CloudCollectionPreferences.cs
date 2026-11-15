#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Persists cloud-collection authorization without editing credentials or changing the selected account.</summary>
    public sealed partial class MusicWorkspace : ICloudCollectionWorkspace
    {
        /// <inheritdoc />
        public async Task<EndpointConfiguration> SetCloudCollectionUsageAsync(EndpointConfiguration endpoint, string accountId,
            bool enabled, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = ReadAccountEndpoint(endpoint);
                if (!current.Accounts.Any(account => account.Id == accountId))
                {
                    throw new InvalidOperationException("所选账号已移除。");
                }

                if (current.Accounts.First(account => account.Id == accountId).UseCloudCollections != enabled)
                {
                    InvalidateCloudCollectionSync(endpoint.Id, accountId);
                }

                var updated = current with
                {
                    Accounts = current.Accounts.Select(account => account.Id == accountId
                        ? account with { UseCloudCollections = enabled } : account).ToArray()
                };
                await PersistAccountEndpointAsync(updated, cancellationToken).ConfigureAwait(false);
                return updated;
            }
            finally
            {
                _settingsGate.Release();
            }
        }
    }
}
