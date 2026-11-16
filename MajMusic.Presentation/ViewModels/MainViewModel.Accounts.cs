#nullable enable

using System.Collections.ObjectModel;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Edits remembered account choices and observes login attempts after explicit online activation.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Persists account metadata and system-vault passwords when supported by the host.</summary>
        private readonly IAccountPreferencesWorkspace? _accountPreferences;
        /// <summary>Tracks the selected saved account or Guest in the editor.</summary>
        private SavedAccountOption? _selectedSavedAccount;
        /// <summary>Suppresses selection actions while rebuilding the saved-account selector.</summary>
        private bool _loadingSavedAccounts;
        /// <summary>Stores whether the account editor should retain its password.</summary>
        private bool _rememberPassword;
        /// <summary>Stores whether the edited account should log in after online mode is enabled.</summary>
        private bool _autoLogin;
        /// <summary>Protects automatic-login cancellation against shutdown.</summary>
        private readonly object _autoLoginGate = new();
        /// <summary>Cancels an obsolete set of automatic login attempts.</summary>
        private CancellationTokenSource? _autoLoginCancellation;
        /// <summary>Tracks completion of the latest finite automatic-login batch.</summary>
        private Task _automaticLogin = Task.CompletedTask;
        /// <summary>Defers automatic login until saved endpoint choices have finished loading.</summary>
        private bool _accountSettingsLoaded;

        /// <summary>Gets the remembered accounts and explicit Guest choice for the selected endpoint.</summary>
        public ObservableCollection<SavedAccountOption> SavedAccounts { get; } = new();
        /// <summary>Gets the command creating an account draft.</summary>
        public AsyncCommand NewAccountCommand { get; private set; } = null!;
        /// <summary>Gets the command persisting account choices without requiring an online connection.</summary>
        public AsyncCommand SaveAccountCommand { get; private set; } = null!;
        /// <summary>Gets the command removing a remembered account and password.</summary>
        public AsyncCommand RemoveAccountCommand { get; private set; } = null!;
        /// <summary>Gets the latest observed automatic-login batch.</summary>
        public Task AutomaticLogin => _automaticLogin;
        /// <summary>Gets whether a remembered password can be used without displaying it.</summary>
        public bool HasRememberedPassword => SelectedSavedAccount?.Account is { RememberPassword: true } account
            && account.Username == LoginUsername.Trim();

        /// <summary>Gets or sets the saved-account selector without issuing a request from a property setter.</summary>
        public SavedAccountOption? SelectedSavedAccount
        {
            get => _selectedSavedAccount;
            set
            {
                if (SetField(ref _selectedSavedAccount, value) && !_loadingSavedAccounts)
                {
                    LoadAccountFields(value);
                }
            }
        }

        /// <summary>Gets or sets whether to retain the current password in the system vault.</summary>
        public bool RememberPassword
        {
            get => _rememberPassword;
            set
            {
                if (SetField(ref _rememberPassword, value))
                {
                    if (!value)
                    {
                        AutoLogin = false;
                    }

                    SaveAccountCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>Gets or sets whether the current account is the endpoint's automatic-login choice.</summary>
        public bool AutoLogin
        {
            get => _autoLogin;
            set
            {
                if (SetField(ref _autoLogin, value) && value)
                {
                    RememberPassword = true;
                }
            }
        }

        /// <summary>Creates account controls bound to the main view lifetime.</summary>
        private void InitializeAccountCommands()
        {
            NewAccountCommand = CreateCommand(() =>
            {
                SelectedSavedAccount = null;
                LoadAccountFields(null);
                return Task.CompletedTask;
            }, () => _accountPreferences is not null && SelectedEndpointProfile is not null);
            SaveAccountCommand = CreateCommand(SaveAccountAsync, () => _accountPreferences is not null
                && !string.IsNullOrWhiteSpace(EndpointUrl) && !string.IsNullOrWhiteSpace(LoginUsername)
                && (!RememberPassword || !string.IsNullOrEmpty(LoginPassword) || HasRememberedPassword));
            RemoveAccountCommand = CreateCommand(RemoveAccountAsync, () => _accountPreferences is not null
                && SelectedSavedAccount?.AccountId is not null);
        }

        /// <summary>Loads remembered choices without reading any password from the vault.</summary>
        /// <param name="profile">The endpoint and current runtime session.</param>
        private void ReloadSavedAccounts(EndpointProfile? profile)
        {
            _loadingSavedAccounts = true;
            try
            {
                SavedAccounts.Clear();
                SavedAccounts.Add(new SavedAccountOption(null));
                foreach (var account in profile?.Configuration.Accounts ?? Array.Empty<SavedEndpointAccount>())
                {
                    SavedAccounts.Add(new SavedAccountOption(account));
                }

                SelectedSavedAccount = SavedAccounts.FirstOrDefault(item => item.AccountId == profile?.Configuration.SelectedAccountId)
                    ?? SavedAccounts[0];
            }
            finally
            {
                _loadingSavedAccounts = false;
            }

            LoadAccountFields(SelectedSavedAccount);
            if (SelectedSavedAccount?.Account is null && profile?.IsLoggedIn == true)
            {
                LoginUsername = profile.Username ?? string.Empty;
            }
        }

        /// <summary>Populates an account editor without exposing a stored password.</summary>
        /// <param name="option">The saved account or a new draft.</param>
        private void LoadAccountFields(SavedAccountOption? option)
        {
            LoginUsername = option?.Account?.Username ?? string.Empty;
            LoginPassword = string.Empty;
            RememberPassword = option?.Account?.RememberPassword == true;
            AutoLogin = option?.Account?.AutoLogin == true;
            OnPropertyChanged(nameof(HasRememberedPassword));
            RefreshCommands();
        }

        /// <summary>Commits a user-selected remembered account or Guest without implicit password transmission.</summary>
        /// <returns>A task representing the saved selection and optional explicitly enabled automatic login.</returns>
        public Task CommitSavedAccountSelectionAsync()
        {
            if (_loadingSavedAccounts || _accountPreferences is null || SelectedEndpointProfile is not { } profile
                || SelectedSavedAccount is not { } option || profile.Configuration.SelectedAccountId == option.AccountId)
            {
                return Task.CompletedTask;
            }

            return RunBusyAsync(async () =>
            {
                await _accountPreferences.SelectAccountAsync(profile.Configuration, option.AccountId, _lifetime.Token);
                ClearAccountSnapshots(profile.Configuration.Id);
                await ReloadAccountStateAsync(profile.Configuration.Id);
                if (OnlineEnabled)
                {
                    BeginAutomaticLogin();
                }
            });
        }

        /// <summary>Saves an account draft and its optional system-vault password while remaining usable offline.</summary>
        /// <returns>A task representing local account persistence.</returns>
        private async Task SaveAccountAsync()
        {
            var preferences = _accountPreferences ?? throw new NotSupportedException("当前宿主不支持保存账号。");
            var username = LoginUsername.Trim();
            var password = string.IsNullOrEmpty(LoginPassword) ? null : LoginPassword;
            var remember = RememberPassword;
            var automatic = AutoLogin;
            try
            {
                await SaveSettingsAsync();
                var endpoint = RequireSelectedEndpoint();
                var updated = await preferences.SaveAccountAsync(endpoint, username, password, remember, automatic, _lifetime.Token);
                if (endpoint.SelectedAccountId != updated.SelectedAccountId)
                {
                    ClearAccountSnapshots(endpoint.Id);
                }

                await ReloadAccountStateAsync(endpoint.Id);
                Status = "账号已保存";
                if (OnlineEnabled)
                {
                    BeginAutomaticLogin();
                }
            }
            finally
            {
                LoginPassword = string.Empty;
            }
        }

        /// <summary>Removes the selected saved account and its protected password.</summary>
        /// <returns>A task representing local removal.</returns>
        private async Task RemoveAccountAsync()
        {
            if (_accountPreferences is null || SelectedSavedAccount?.AccountId is not { } accountId)
            {
                return;
            }

            var endpoint = RequireSelectedEndpoint();
            await _accountPreferences.RemoveAccountAsync(endpoint, accountId, _lifetime.Token);
            ClearAccountSnapshots(endpoint.Id);
            await ReloadAccountStateAsync(endpoint.Id);
            Status = "账号已移除";
        }

        /// <summary>Reloads local account metadata and refreshes account-scoped lists.</summary>
        /// <param name="endpointId">The preferred configuration selection.</param>
        /// <returns>A task representing the visible local-state refresh.</returns>
        private async Task ReloadAccountStateAsync(string endpointId)
        {
            _settings = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
            ReloadEndpointProfiles(endpointId);
            await ReloadPlaylistsAsync();
            await ReloadLibraryAsync();
        }

        /// <summary>Starts one observed automatic-login batch only after explicit online activation.</summary>
        private void BeginAutomaticLogin()
        {
            if (_accountPreferences is null || !_accountSettingsLoaded || !OnlineEnabled || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            CancelAutomaticLogin();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_autoLoginGate)
            {
                _autoLoginCancellation = cancellation;
            }

            _automaticLogin = RunTrackedAsync(() => RunAutomaticLoginAsync(cancellation));
        }

        /// <summary>Observes independent login results without discarding an account editor draft.</summary>
        /// <param name="cancellation">The online-activation batch lifetime.</param>
        /// <returns>A task representing the finite automatic-login batch.</returns>
        private async Task RunAutomaticLoginAsync(CancellationTokenSource cancellation)
        {
            try
            {
                var results = await _accountPreferences!.AutoLoginAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var loaded = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                var draft = CaptureAccountDraft();
                _settings = loaded;
                ReloadEndpointProfiles(draft.EndpointId);
                RestoreAccountDraft(draft);
                await EnsureHomeRecommendationsAsync();
                RequestCloudCollectionSync();
                var failures = results.Where(item => !item.Succeeded && _settings.Endpoints.Any(endpoint => endpoint.Id == item.EndpointId
                    && endpoint.SelectedAccountId == item.AccountId && endpoint.Accounts.Any(account => account.Id == item.AccountId && account.AutoLogin))).ToArray();
                if (failures.Length > 0)
                {
                    Status = failures.Length == 1 && failures[0].Error is "账号或密码错误" or "接入点需要浏览器验证" or "接入点拒绝访问"
                        ? failures[0].Error! : $"{failures.Length} 个账号未能自动登录";
                }
            }
            catch (OperationCanceledException)
            {
                // Offline mode, a newer choice, or shutdown ended this explicit online batch.
            }
            catch (Exception exception)
            {
                ReportError(exception);
            }
            finally
            {
                lock (_autoLoginGate)
                {
                    if (ReferenceEquals(_autoLoginCancellation, cancellation))
                    {
                        _autoLoginCancellation = null;
                    }
                }

                cancellation.Dispose();
            }
        }

        /// <summary>Cancels login work associated with an obsolete online activation.</summary>
        private void CancelAutomaticLogin()
        {
            lock (_autoLoginGate)
            {
                _autoLoginCancellation?.Cancel();
            }
        }

        /// <summary>Captures transient editor input before updating account-state labels.</summary>
        /// <returns>The unsaved editor values, without writing them anywhere.</returns>
        private AccountDraft CaptureAccountDraft()
        {
            return new AccountDraft(SelectedEndpointProfile?.Configuration.Id, EndpointName, EndpointUrl,
                SelectedSavedAccount?.AccountId, SelectedSavedAccount is null, LoginUsername, LoginPassword, RememberPassword, AutoLogin);
        }

        /// <summary>Restores an editor draft only when it still targets the same endpoint choice.</summary>
        /// <param name="draft">The values captured immediately before label refresh.</param>
        private void RestoreAccountDraft(AccountDraft draft)
        {
            if (SelectedEndpointProfile?.Configuration.Id != draft.EndpointId)
            {
                return;
            }

            EndpointName = draft.EndpointName;
            EndpointUrl = draft.EndpointUrl;
            _loadingSavedAccounts = true;
            try
            {
                SelectedSavedAccount = draft.IsNew ? null : SavedAccounts.FirstOrDefault(item => item.AccountId == draft.AccountId);
            }
            finally
            {
                _loadingSavedAccounts = false;
            }

            LoginUsername = draft.Username;
            LoginPassword = draft.Password;
            RememberPassword = draft.RememberPassword;
            AutoLogin = draft.AutoLogin;
            OnPropertyChanged(nameof(HasRememberedPassword));
            RefreshCommands();
        }

        /// <summary>Retains unsaved editor input during a runtime account-label refresh.</summary>
        /// <param name="EndpointId">The edited source slot.</param>
        /// <param name="EndpointName">The proposed source name.</param>
        /// <param name="EndpointUrl">The proposed API root.</param>
        /// <param name="AccountId">The selected remembered account, or null for Guest.</param>
        /// <param name="IsNew">Whether the editor contains a new account draft.</param>
        /// <param name="Username">The transient account name.</param>
        /// <param name="Password">The transient password input.</param>
        /// <param name="RememberPassword">The proposed password-retention choice.</param>
        /// <param name="AutoLogin">The proposed automatic-login choice.</param>
        private sealed record AccountDraft(string? EndpointId, string EndpointName, string EndpointUrl, string? AccountId,
            bool IsNew, string Username, string Password, bool RememberPassword, bool AutoLogin)
        {
            /// <summary>Returns the draft type without rendering transient password input.</summary>
            /// <returns>A non-secret label.</returns>
            public override string ToString()
            {
                return nameof(AccountDraft);
            }
        }
    }

    /// <summary>Represents a remembered account or the explicit Guest selector entry without any password.</summary>
    public sealed class SavedAccountOption
    {
        /// <summary>Creates a selector entry without retrieving a secret.</summary>
        /// <param name="account">The remembered account, or null for Guest.</param>
        public SavedAccountOption(SavedEndpointAccount? account)
        {
            Account = account;
        }

        /// <summary>Gets the remembered non-secret metadata.</summary>
        public SavedEndpointAccount? Account { get; }
        /// <summary>Gets the saved identity, or null for Guest.</summary>
        public string? AccountId => Account?.Id;
        /// <summary>Gets the selector label.</summary>
        public string DisplayName => Account?.Username ?? "Guest";
    }
}
