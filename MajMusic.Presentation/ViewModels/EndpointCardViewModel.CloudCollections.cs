#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Persists a cloud-collection checkbox against the saved identity captured when it was edited.</summary>
    public sealed partial class EndpointCardViewModel
    {
        /// <summary>Stores the checkbox projected from the chosen saved account.</summary>
        private bool _useCloudCollections;
        /// <summary>Coalesces the newest explicit choice independently for every saved account.</summary>
        private readonly Dictionary<string, CloudCollectionEdit> _cloudCollectionEdits = new(StringComparer.Ordinal);
        /// <summary>Orders edits so an older completion cannot restore a later checkbox value.</summary>
        private long _cloudCollectionEditVersion;
        /// <summary>Ensures one source card has only one local preference worker.</summary>
        private bool _cloudCollectionWorkerRunning;
        /// <summary>Exposes the current observed local-save batch for hosts and regressions.</summary>
        private Task _cloudCollectionUsageUpdate = Task.CompletedTask;

        /// <summary>Gets whether the selected identity is a saved account eligible for a cloud-collection preference.</summary>
        public bool CanUseCloudCollections => IsIdle && !_loading && _owner.CanConfigureCloudCollections
            && _configuration is { } configuration && SelectedAccount?.AccountId is { } id
            && configuration.Accounts.Any(account => account.Id == id);
        /// <summary>Gets completion of the latest merged account-specific checkbox save.</summary>
        public Task CloudCollectionUsageUpdate => _cloudCollectionUsageUpdate;
        /// <summary>Gets or sets whether the explicitly chosen saved account uses its endpoint's collections.</summary>
        public bool UseCloudCollections
        {
            get => _useCloudCollections;
            set
            {
                if (!_loading && !CanUseCloudCollections)
                {
                    return;
                }

                if (SetField(ref _useCloudCollections, value) && !_loading
                    && _configuration is { } endpoint && SelectedAccount?.AccountId is { } accountId)
                {
                    _cloudCollectionEdits[accountId] = new CloudCollectionEdit(endpoint, accountId, value, ++_cloudCollectionEditVersion);
                    if (!_cloudCollectionWorkerRunning)
                    {
                        _cloudCollectionWorkerRunning = true;
                        _cloudCollectionUsageUpdate = _owner.RunEndpointCardOperationAsync(SaveCloudCollectionEditsAsync);
                    }
                }
            }
        }

        /// <summary>Restores the selected account's flag without treating a form refresh as permission for another identity.</summary>
        /// <param name="option">The newly selected saved account, Guest or unsaved account draft.</param>
        private void RestoreCloudCollectionUsage(SavedAccountOption? option)
        {
            var enabled = false;
            if (option?.AccountId is { } id)
            {
                enabled = _cloudCollectionEdits.TryGetValue(id, out var pending) && pending.Endpoint.BaseUri == _configuration?.BaseUri
                    ? pending.Enabled : _configuration?.Accounts.FirstOrDefault(account => account.Id == id)?.UseCloudCollections == true;
            }

            var wasLoading = _loading;
            _loading = true;
            UseCloudCollections = enabled;
            _loading = wasLoading;
        }

        /// <summary>Applies only a saved flag while retaining the current source, selected account and unrelated editor drafts.</summary>
        /// <param name="updated">The endpoint snapshot returned by the narrow preference method.</param>
        /// <param name="accountId">The identity captured by the original checkbox edit.</param>
        private void AcceptCloudCollectionUsage(EndpointConfiguration updated, string accountId)
        {
            if (_disposed || _configuration is not { } current || current.BaseUri != updated.BaseUri
                || updated.Accounts.FirstOrDefault(account => account.Id == accountId) is not { } saved)
            {
                return;
            }

            _configuration = current with
            {
                Accounts = current.Accounts.Select(account => account.Id == accountId
                    ? account with { UseCloudCollections = saved.UseCloudCollections } : account).ToArray()
            };
            var wasLoading = _loading;
            _loading = true;
            try
            {
                var selected = SelectedAccount?.AccountId;
                var newAccountDraft = SelectedAccount is null;
                Accounts = Accounts.Select(option => option.Account is { } account && account.Id == accountId
                    ? new SavedAccountOption(account with { UseCloudCollections = saved.UseCloudCollections }) : option).ToArray();
                _selectedAccount = newAccountDraft ? null : Accounts.FirstOrDefault(option => option.AccountId == selected);
                OnPropertyChanged(nameof(Accounts));
                OnPropertyChanged(nameof(SelectedAccount));
                RestoreCloudCollectionUsage(SelectedAccount);
            }
            finally
            {
                _loading = wasLoading;
            }

            RefreshAvailability();
        }

        /// <summary>Drains explicit local edits and requests synchronization only after the batch's latest choices are saved.</summary>
        /// <returns>A task representing the observed source-card preference batch.</returns>
        private async Task SaveCloudCollectionEditsAsync()
        {
            var savedAny = false;
            try
            {
                await Task.Yield();
                while (!_disposed && _cloudCollectionEdits.Count > 0)
                {
                    var edit = _cloudCollectionEdits.Values.First();
                    try
                    {
                        var updated = await _owner.SetCardCloudCollectionUsageAsync(edit.Endpoint, edit.AccountId, edit.Enabled);
                        if (_cloudCollectionEdits.GetValueOrDefault(edit.AccountId)?.Version == edit.Version)
                        {
                            _cloudCollectionEdits.Remove(edit.AccountId);
                        }

                        AcceptCloudCollectionUsage(updated, edit.AccountId);
                        savedAny = true;
                    }
                    catch (Exception exception)
                    {
                        if (_cloudCollectionEdits.GetValueOrDefault(edit.AccountId)?.Version == edit.Version)
                        {
                            _cloudCollectionEdits.Remove(edit.AccountId);
                        }

                        if (!_disposed)
                        {
                            RestoreCloudCollectionUsage(SelectedAccount);
                            Status = "收藏夹设置未保存";
                            _owner.ReportError(exception);
                        }
                    }
                }

                if (savedAny && !_disposed)
                {
                    _owner.RequestCloudCollectionSync();
                }
            }
            finally
            {
                _cloudCollectionWorkerRunning = false;
            }
        }

        /// <summary>Captures authorization against the endpoint and account that were selected at edit time.</summary>
        /// <param name="Endpoint">The saved source identity and API root.</param>
        /// <param name="AccountId">The saved account that owns this edit.</param>
        /// <param name="Enabled">The latest explicit checkbox choice.</param>
        /// <param name="Version">The monotonically increasing local edit generation.</param>
        private sealed record CloudCollectionEdit(EndpointConfiguration Endpoint, string AccountId, bool Enabled, long Version);
    }
}
