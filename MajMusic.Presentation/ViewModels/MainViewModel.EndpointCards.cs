#nullable enable

using System.Collections.ObjectModel;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Coordinates independent endpoint cards without using the browsing source as an account editor.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Serializes source-list refreshes after independent source operations.</summary>
        private readonly SemaphoreSlim _endpointCardRefresh = new(1, 1);
        /// <summary>Gets saved source cards and unsaved source drafts in display order.</summary>
        public ObservableCollection<EndpointCardViewModel> EndpointCards { get; } = new();
        /// <summary>Gets the action adding a visible source draft.</summary>
        public AsyncCommand AddEndpointCardCommand { get; private set; } = null!;
        /// <summary>Gets whether the workspace supports account-specific cloud-collection preferences.</summary>
        internal bool CanConfigureCloudCollections => _workspace is ICloudCollectionWorkspace;

        /// <summary>Saves one captured account's cloud-collection permission without changing other editor or session state.</summary>
        /// <param name="endpoint">The source identity captured when the checkbox changed.</param>
        /// <param name="accountId">The saved account explicitly authorized by that edit.</param>
        /// <param name="enabled">The latest requested cloud-collection preference.</param>
        /// <returns>The endpoint snapshot after the narrow local update.</returns>
        internal async Task<EndpointConfiguration> SetCardCloudCollectionUsageAsync(EndpointConfiguration endpoint, string accountId, bool enabled)
        {
            var cloud = _workspace as ICloudCollectionWorkspace
                ?? throw new NotSupportedException("当前宿主不支持云端收藏夹。");
            var updated = await cloud.SetCloudCollectionUsageAsync(endpoint, accountId, enabled, CancellationToken.None);
            _settings = _settings with
            {
                Endpoints = _settings.Endpoints.Select(current => current.Id == endpoint.Id && current.BaseUri == endpoint.BaseUri
                    ? current with
                    {
                        Accounts = current.Accounts.Select(account => account.Id == accountId
                            ? account with { UseCloudCollections = enabled } : account).ToArray()
                    } : current).ToArray()
            };
            return updated;
        }

        /// <summary>Creates the source-card action without changing the selected browsing endpoint.</summary>
        private void InitializeEndpointCardCommands()
        {
            AddEndpointCardCommand = CreateCommand(() =>
            {
                EndpointCards.Add(new EndpointCardViewModel(this, Guid.NewGuid().ToString("N"), null));
                return Task.CompletedTask;
            }, () => _workspace is IEndpointPreferencesWorkspace);
        }

        /// <summary>Reconciles saved profiles while retaining other cards' draft fields and object identities.</summary>
        private void RefreshEndpointCards()
        {
            var existing = EndpointCards.ToDictionary(card => card.Id, StringComparer.Ordinal);
            var desired = new List<EndpointCardViewModel>();
            foreach (var profile in EndpointProfiles)
            {
                if (!existing.TryGetValue(profile.Configuration.Id, out var card))
                {
                    card = new EndpointCardViewModel(this, profile.Configuration.Id, profile);
                }
                else
                {
                    card.UpdateProfile(profile);
                }

                desired.Add(card);
            }

            desired.AddRange(EndpointCards.Where(card => card.IsNew && !desired.Any(saved => saved.Id == card.Id)));
            foreach (var removed in EndpointCards.Where(card => !desired.Contains(card)).ToArray())
            {
                EndpointCards.Remove(removed);
                removed.Dispose();
            }

            for (var index = 0; index < desired.Count; index++)
            {
                var current = EndpointCards.IndexOf(desired[index]);
                if (current < 0)
                {
                    EndpointCards.Insert(index, desired[index]);
                }
                else if (current != index)
                {
                    EndpointCards.Move(current, index);
                }
            }
        }

        /// <summary>Tracks an independent card command through the parent shutdown lifetime.</summary>
        /// <param name="action">The source-specific operation.</param>
        /// <returns>A task representing the observed card operation.</returns>
        internal Task RunEndpointCardOperationAsync(Func<Task> action)
        {
            return RunTrackedAsync(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await _endpointCardRefresh.WaitAsync(_lifetime.Token);
                    try
                    {
                        await ReconcileExpiredSessionsAsync();
                    }
                    finally
                    {
                        _endpointCardRefresh.Release();
                    }

                    throw;
                }
            });
        }

        /// <summary>Saves one visible source address and refreshes its routing state.</summary>
        /// <param name="card">The source form being saved.</param>
        /// <returns>A task representing the saved configuration.</returns>
        internal async Task SaveEndpointCardAsync(EndpointCardViewModel card)
        {
            await SaveEndpointCardCoreAsync(card);
        }

        /// <summary>Persists a changed source address through the narrow settings interface.</summary>
        /// <param name="card">The source form containing the desired address.</param>
        /// <returns>The latest saved source configuration.</returns>
        private async Task<EndpointConfiguration> SaveEndpointCardCoreAsync(EndpointCardViewModel card)
        {
            if (!card.HasEndpointDraft && card.Configuration is { } saved)
            {
                return saved;
            }

            var preferences = _workspace as IEndpointPreferencesWorkspace
                ?? throw new NotSupportedException("当前宿主不支持编辑接入点。");
            var uri = new Uri(card.EndpointUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
            var previous = card.Configuration;
            var endpoint = await preferences.SaveEndpointAsync(new EndpointConfiguration
            {
                Id = card.Id,
                Name = card.EndpointName.Trim(),
                BaseUri = uri
            }, _lifetime.Token);
            card.AcceptEndpoint(endpoint);
            await ReloadEndpointCardsAsync(card.Id, previous is not null && previous.BaseUri != uri);
            return endpoint;
        }

        /// <summary>Synchronizes one source without changing or logging out any other source.</summary>
        /// <param name="card">The independently authenticated source.</param>
        /// <returns>A task representing metadata synchronization.</returns>
        internal async Task SynchronizeEndpointCardAsync(EndpointCardViewModel card)
        {
            var endpoint = await SaveEndpointCardCoreAsync(card);
            await _workspace.SynchronizeAsync(endpoint, _lifetime.Token);
            await ReloadEndpointCardsAsync(endpoint.Id, false);
        }

        /// <summary>Removes one source draft or its saved routing and credentials.</summary>
        /// <param name="card">The source card to remove.</param>
        /// <returns>A task representing completed source removal.</returns>
        internal async Task RemoveEndpointCardAsync(EndpointCardViewModel card)
        {
            if (card.IsNew)
            {
                EndpointCards.Remove(card);
                card.Dispose();
                return;
            }

            var preferences = _workspace as IEndpointPreferencesWorkspace
                ?? throw new NotSupportedException("当前宿主不支持编辑接入点。");
            await preferences.RemoveEndpointAsync(card.Id, _lifetime.Token);
            await ReloadEndpointCardsAsync(card.Id, true);
        }

        /// <summary>Persists one account's flags and optional system-vault password.</summary>
        /// <param name="card">The independent account form.</param>
        /// <returns>A task representing the saved account preferences.</returns>
        internal async Task SaveCardAccountAsync(EndpointCardViewModel card)
        {
            try
            {
                var endpoint = await SaveEndpointCardCoreAsync(card);
                var accounts = _accountPreferences ?? throw new NotSupportedException("当前宿主不支持保存账号。");
                var updated = await accounts.SaveAccountAsync(endpoint, card.Username.Trim(),
                    string.IsNullOrEmpty(card.Password) ? null : card.Password, card.RememberPassword, card.AutoLogin, _lifetime.Token);
                card.UpdateProfile(new EndpointProfile(updated, null), forceAccount: true);
                await ReloadEndpointCardsAsync(card.Id, true);
            }
            finally
            {
                card.ClearPassword();
            }
        }

        /// <summary>Authenticates one source while preserving every other source's active session.</summary>
        /// <param name="card">The independent source and account form.</param>
        /// <returns>A task representing account persistence and authentication.</returns>
        internal async Task LoginEndpointCardAsync(EndpointCardViewModel card)
        {
            var password = card.Password;
            var username = card.Username.Trim();
            try
            {
                var endpoint = await SaveEndpointCardCoreAsync(card);
                var online = _onlineWorkspace ?? throw new NotSupportedException("当前宿主不支持登录账号。");
                if (_accountPreferences is not null)
                {
                    endpoint = await _accountPreferences.SaveAccountAsync(endpoint, username,
                        string.IsNullOrEmpty(password) ? null : password, card.RememberPassword, card.AutoLogin, _lifetime.Token);
                    card.UpdateProfile(new EndpointProfile(endpoint, null), forceAccount: true);
                }

                if (string.IsNullOrEmpty(password) && _accountPreferences is not null && endpoint.SelectedAccountId is { } accountId)
                {
                    await _accountPreferences.LoginSavedAccountAsync(endpoint, accountId, _lifetime.Token);
                }
                else
                {
                    await online.LoginAsync(endpoint, username, password, _lifetime.Token);
                }
            }
            finally
            {
                card.ClearPassword();
                await ReloadEndpointCardsAsync(card.Id, true);
            }
        }

        /// <summary>Persists a remembered account or Guest choice for one source card.</summary>
        /// <param name="card">The source whose account selector changed.</param>
        /// <returns>A task representing the saved selection and eligible automatic login.</returns>
        internal async Task SelectCardAccountAsync(EndpointCardViewModel card)
        {
            if (card.Configuration is not { } endpoint || card.SelectedAccount is not { } option
                || endpoint.SelectedAccountId == option.AccountId || _accountPreferences is null)
            {
                return;
            }

            var updated = await _accountPreferences.SelectAccountAsync(endpoint, option.AccountId, _lifetime.Token);
            card.UpdateProfile(new EndpointProfile(updated, null), forceAccount: true);
            try
            {
                if (option.AccountId is null && _onlineWorkspace is not null)
                {
                    await _onlineWorkspace.UseGuestAsync(updated, _lifetime.Token);
                }
                else if (OnlineEnabled && option.Account is { AutoLogin: true } && option.AccountId is { } accountId)
                {
                    await _accountPreferences.LoginSavedAccountAsync(updated, accountId, _lifetime.Token);
                }
            }
            finally
            {
                await ReloadEndpointCardsAsync(card.Id, true);
            }
        }

        /// <summary>Removes one remembered account while retaining the source and unrelated sessions.</summary>
        /// <param name="card">The source's account selector.</param>
        /// <returns>A task representing credential removal and source refresh.</returns>
        internal async Task RemoveCardAccountAsync(EndpointCardViewModel card)
        {
            if (card.Configuration is not { } endpoint || card.SelectedAccount?.AccountId is not { } accountId || _accountPreferences is null)
            {
                return;
            }

            var updated = await _accountPreferences.RemoveAccountAsync(endpoint, accountId, _lifetime.Token);
            card.UpdateProfile(new EndpointProfile(updated, null), forceAccount: true);
            await ReloadEndpointCardsAsync(card.Id, true);
        }

        /// <summary>Refreshes source routing and account-private snapshots without switching the browsing source.</summary>
        /// <param name="endpointId">The source that changed.</param>
        /// <param name="accountChanged">Whether its private snapshots must be discarded.</param>
        /// <returns>A task representing serialized source and library refresh.</returns>
        private async Task ReloadEndpointCardsAsync(string endpointId, bool accountChanged)
        {
            var refreshRecommendations = false;
            await _endpointCardRefresh.WaitAsync(_lifetime.Token);
            try
            {
                var selected = SelectedEndpointProfile?.Configuration.Id;
                _settings = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
                if (accountChanged)
                {
                    ClearAccountSnapshots(endpointId);
                }

                ReloadEndpointProfiles(selected);
                await ReloadPlaylistsAsync();
                await ReloadLibraryAsync();
                refreshRecommendations = accountChanged && SelectedEndpointProfile?.Configuration.Id == endpointId;
            }
            finally
            {
                _endpointCardRefresh.Release();
            }

            if (refreshRecommendations)
            {
                await EnsureHomeRecommendationsAsync();
            }
        }

        /// <summary>Updates card actions after network availability changes.</summary>
        private void RefreshEndpointCardCommands()
        {
            AddEndpointCardCommand?.NotifyCanExecuteChanged();
            foreach (var card in EndpointCards)
            {
                card.RefreshAvailability();
            }
        }
    }
}
