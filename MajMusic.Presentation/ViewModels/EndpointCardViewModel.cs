#nullable enable

using System.ComponentModel;
using System.Runtime.CompilerServices;
using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Retains one source's editable address and account draft independently of other source cards.</summary>
    public sealed partial class EndpointCardViewModel : INotifyPropertyChanged, IDisposable
    {
        /// <summary>Coordinates tracked source operations and persistent preferences.</summary>
        private readonly MainViewModel _owner;
        /// <summary>Stores the latest saved source configuration.</summary>
        private EndpointConfiguration? _configuration;
        /// <summary>Stores the editable source name.</summary>
        private string _endpointName = string.Empty;
        /// <summary>Stores the editable API address.</summary>
        private string _endpointUrl = string.Empty;
        /// <summary>Stores the chosen remembered account or Guest.</summary>
        private SavedAccountOption? _selectedAccount;
        /// <summary>Stores the editable account name.</summary>
        private string _username = string.Empty;
        /// <summary>Retains a typed password only until its explicit save or login.</summary>
        private string _password = string.Empty;
        /// <summary>Stores the edited password retention choice.</summary>
        private bool _rememberPassword;
        /// <summary>Stores the edited automatic-login choice.</summary>
        private bool _autoLogin;
        /// <summary>Protects field restoration from being interpreted as an edit.</summary>
        private bool _loading;
        /// <summary>Preserves an unsaved source edit during another card's refresh.</summary>
        private bool _endpointDraft;
        /// <summary>Preserves an unsaved account edit during another card's refresh.</summary>
        private bool _accountDraft;
        /// <summary>Serializes only operations on this source card.</summary>
        private bool _busy;
        /// <summary>Rejects actions after the source has been removed.</summary>
        private bool _disposed;
        /// <summary>Stores the independent session label or a failed-operation message.</summary>
        private string _status = "Guest";

        /// <summary>Creates a saved source card or a new unsaved source draft.</summary>
        /// <param name="owner">The parent workspace presentation.</param>
        /// <param name="id">The stable source identity.</param>
        /// <param name="profile">The existing configuration and session, or null for a draft.</param>
        internal EndpointCardViewModel(MainViewModel owner, string id, EndpointProfile? profile)
        {
            _owner = owner;
            Id = id;
            SaveEndpointCommand = Command(() => owner.SaveEndpointCardAsync(this), () => HasValidAddress);
            SynchronizeCommand = Command(() => owner.SynchronizeEndpointCardAsync(this), () => HasValidAddress && owner.OnlineEnabled);
            RemoveEndpointCommand = Command(() => owner.RemoveEndpointCardAsync(this));
            NewAccountCommand = Command(() =>
            {
                SelectedAccount = null;
                LoadAccountFields(null);
                _accountDraft = true;
                return Task.CompletedTask;
            });
            RemoveAccountCommand = Command(() => owner.RemoveCardAccountAsync(this), () => SelectedAccount?.AccountId is not null);
            SaveAccountCommand = Command(() => owner.SaveCardAccountAsync(this), () => CanSaveAccount);
            LoginCommand = Command(() => owner.LoginEndpointCardAsync(this), () => owner.OnlineEnabled && CanSaveAccount
                && (!string.IsNullOrEmpty(Password) || HasRememberedPassword));
            SelectAccountCommand = Command(() => owner.SelectCardAccountAsync(this));
            if (profile is not null)
            {
                UpdateProfile(profile, true);
            }
            else
            {
                Accounts = new[] { new SavedAccountOption(null) };
                SelectedAccount = Accounts[0];
            }
        }

        /// <summary>Occurs when one card field or its session changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>Gets the stable source-card identity.</summary>
        public string Id { get; }
        /// <summary>Gets the current persisted configuration, or null for a draft.</summary>
        public EndpointConfiguration? Configuration => _configuration;
        /// <summary>Gets whether this source has not yet been saved.</summary>
        public bool IsNew => _configuration is null;
        /// <summary>Gets whether this card can accept another operation.</summary>
        public bool IsIdle => !_busy && !_disposed;
        /// <summary>Gets whether an account rather than Guest is being edited.</summary>
        public bool IsGuest => SelectedAccount is { AccountId: null };
        /// <summary>Gets whether account fields can be edited.</summary>
        public bool CanEditAccount => IsIdle && !IsGuest;
        /// <summary>Gets the source's remembered account options and Guest.</summary>
        public IReadOnlyList<SavedAccountOption> Accounts { get; private set; } = Array.Empty<SavedAccountOption>();
        /// <summary>Gets the current independent account-session label.</summary>
        public string Status
        {
            get => _status;
            internal set => SetField(ref _status, value);
        }
        /// <summary>Gets or sets the source name.</summary>
        public string EndpointName
        {
            get => _endpointName;
            set
            {
                if (SetField(ref _endpointName, value) && !_loading)
                {
                    _endpointDraft = true;
                }
            }
        }
        /// <summary>Gets or sets the source API root.</summary>
        public string EndpointUrl
        {
            get => _endpointUrl;
            set
            {
                if (SetField(ref _endpointUrl, value) && !_loading)
                {
                    _endpointDraft = true;
                }
            }
        }
        /// <summary>Gets or sets the remembered account selected inside this card.</summary>
        public SavedAccountOption? SelectedAccount
        {
            get => _selectedAccount;
            set
            {
                if (SetField(ref _selectedAccount, value) && !_loading)
                {
                    LoadAccountFields(value);
                }
            }
        }
        /// <summary>Gets or sets the account name.</summary>
        public string Username
        {
            get => _username;
            set
            {
                if (SetField(ref _username, value) && !_loading)
                {
                    _accountDraft = true;
                }
            }
        }
        /// <summary>Gets or sets the password typed for this source.</summary>
        public string Password
        {
            get => _password;
            set
            {
                if (SetField(ref _password, value) && !_loading)
                {
                    _accountDraft = true;
                }
            }
        }
        /// <summary>Gets or sets whether the typed password is retained.</summary>
        public bool RememberPassword
        {
            get => _rememberPassword;
            set
            {
                if (SetField(ref _rememberPassword, value))
                {
                    _accountDraft |= !_loading;
                    if (!value)
                    {
                        AutoLogin = false;
                    }
                }
            }
        }
        /// <summary>Gets or sets whether the chosen account is used automatically.</summary>
        public bool AutoLogin
        {
            get => _autoLogin;
            set
            {
                if (SetField(ref _autoLogin, value))
                {
                    _accountDraft |= !_loading;
                    if (value)
                    {
                        RememberPassword = true;
                    }
                }
            }
        }
        /// <summary>Gets the source-address save action.</summary>
        public AsyncCommand SaveEndpointCommand { get; }
        /// <summary>Gets the independent source synchronization action.</summary>
        public AsyncCommand SynchronizeCommand { get; }
        /// <summary>Gets the source-card removal action.</summary>
        public AsyncCommand RemoveEndpointCommand { get; }
        /// <summary>Gets the action creating a new account draft inside this card.</summary>
        public AsyncCommand NewAccountCommand { get; }
        /// <summary>Gets the saved-account removal action.</summary>
        public AsyncCommand RemoveAccountCommand { get; }
        /// <summary>Gets the account-preference save action.</summary>
        public AsyncCommand SaveAccountCommand { get; }
        /// <summary>Gets the independent account login action.</summary>
        public AsyncCommand LoginCommand { get; }
        /// <summary>Gets the action persisting a user-selected account or Guest.</summary>
        public AsyncCommand SelectAccountCommand { get; }
        /// <summary>Gets whether the displayed source address has unsaved edits.</summary>
        internal bool HasEndpointDraft => IsNew || _endpointDraft;
        /// <summary>Gets whether an existing vault password matches the edited username.</summary>
        internal bool HasRememberedPassword => SelectedAccount?.Account is { RememberPassword: true } account && account.Username == Username.Trim();
        /// <summary>Gets whether account preferences can be saved with the present fields.</summary>
        private bool CanSaveAccount => HasValidAddress && !IsGuest && !string.IsNullOrWhiteSpace(Username)
            && (!RememberPassword || !string.IsNullOrEmpty(Password) || HasRememberedPassword);
        /// <summary>Gets whether this source has a complete display name and absolute HTTPS root.</summary>
        private bool HasValidAddress => !string.IsNullOrWhiteSpace(EndpointName) && Uri.TryCreate(EndpointUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

        /// <summary>Refreshes a saved source while retaining this card's unrelated unsaved form values.</summary>
        /// <param name="profile">The latest source configuration and session.</param>
        /// <param name="force">Whether to replace all initial form fields.</param>
        /// <param name="forceAccount">Whether to accept explicitly saved account fields while preserving address drafts.</param>
        internal void UpdateProfile(EndpointProfile profile, bool force = false, bool forceAccount = false)
        {
            var previous = _configuration;
            _configuration = profile.Configuration;
            _loading = true;
            try
            {
                if (force || !_endpointDraft)
                {
                    EndpointName = profile.Configuration.Name;
                    EndpointUrl = profile.Configuration.BaseUri.AbsoluteUri;
                    _endpointDraft = false;
                }

                if (force || forceAccount || previous is null || !previous.Accounts.SequenceEqual(profile.Configuration.Accounts)
                    || previous.SelectedAccountId != profile.Configuration.SelectedAccountId)
                {
                    var preserveDraft = !force && !forceAccount && _accountDraft;
                    var selection = preserveDraft ? SelectedAccount?.AccountId : profile.Configuration.SelectedAccountId;
                    var newAccountDraft = preserveDraft && SelectedAccount is null;
                    Accounts = new[] { new SavedAccountOption(null) }.Concat(profile.Configuration.Accounts.Select(account => new SavedAccountOption(account))).ToArray();
                    OnPropertyChanged(nameof(Accounts));
                    _selectedAccount = newAccountDraft ? null : Accounts.FirstOrDefault(item => item.AccountId == selection);
                    OnPropertyChanged(nameof(SelectedAccount));
                    if (!preserveDraft)
                    {
                        LoadAccountFields(SelectedAccount);
                    }
                }

                RestoreCloudCollectionUsage(SelectedAccount);
            }
            finally
            {
                _loading = false;
            }

            Status = profile.AccountLabel;
            RefreshAvailability();
        }

        /// <summary>Accepts a saved address without discarding the independent account draft.</summary>
        /// <param name="configuration">The source configuration returned by storage.</param>
        internal void AcceptEndpoint(EndpointConfiguration configuration)
        {
            _endpointDraft = false;
            UpdateProfile(new EndpointProfile(configuration, null));
        }

        /// <summary>Clears a password after explicit authentication or persistence.</summary>
        internal void ClearPassword()
        {
            _password = string.Empty;
            OnPropertyChanged(nameof(Password));
            RefreshAvailability();
        }

        /// <summary>Refreshes command states and Guest-dependent field availability.</summary>
        internal void RefreshAvailability()
        {
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsGuest));
            OnPropertyChanged(nameof(CanEditAccount));
            OnPropertyChanged(nameof(CanUseCloudCollections));
            foreach (var command in new[] { SaveEndpointCommand, SynchronizeCommand, RemoveEndpointCommand, NewAccountCommand,
                RemoveAccountCommand, SaveAccountCommand, LoginCommand, SelectAccountCommand })
            {
                command?.NotifyCanExecuteChanged();
            }
        }

        /// <summary>Releases transient credentials and disables removed-card operations.</summary>
        public void Dispose()
        {
            _disposed = true;
            _cloudCollectionEdits.Clear();
            ClearPassword();
        }

        /// <summary>Creates one observed operation serialized only against this source card.</summary>
        /// <param name="action">The independent source operation.</param>
        /// <param name="canExecute">An optional field availability check.</param>
        /// <returns>The tracked asynchronous command.</returns>
        private AsyncCommand Command(Func<Task> action, Func<bool>? canExecute = null)
        {
            return new AsyncCommand(() => _owner.RunEndpointCardOperationAsync(async () =>
            {
                _busy = true;
                RefreshAvailability();
                try
                {
                    await action();
                }
                finally
                {
                    _busy = false;
                    RefreshAvailability();
                }
            }), exception =>
            {
                Status = exception switch
                {
                    MajMusic.Core.Network.LoginRejectedException login => login.UserMessage,
                    MajMusic.Core.Network.BrowserVerificationRequiredException => "接入点需要浏览器验证",
                    HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "请重新登录",
                    HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "接入点拒绝访问",
                    _ => "操作未完成"
                };
                _owner.ReportError(exception);
            }, () => IsIdle && !_loading && (canExecute?.Invoke() ?? true));
        }

        /// <summary>Loads non-secret remembered account fields without retrieving a password.</summary>
        /// <param name="option">The chosen account, Guest, or a new-account draft.</param>
        private void LoadAccountFields(SavedAccountOption? option)
        {
            var wasLoading = _loading;
            _loading = true;
            Username = option?.Account?.Username ?? string.Empty;
            Password = string.Empty;
            RememberPassword = option?.Account?.RememberPassword == true;
            AutoLogin = option?.Account?.AutoLogin == true;
            RestoreCloudCollectionUsage(option);
            _loading = wasLoading;
            _accountDraft = false;
            RefreshAvailability();
        }

        /// <summary>Updates one form field and its command availability.</summary>
        /// <typeparam name="T">The field value type.</typeparam>
        /// <param name="field">The retained field.</param>
        /// <param name="value">The edited value.</param>
        /// <param name="name">The notifying property.</param>
        /// <returns>Whether the field changed.</returns>
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(name);
            if (name is not nameof(Status))
            {
                RefreshAvailability();
            }

            return true;
        }

        /// <summary>Publishes a changed form property.</summary>
        /// <param name="name">The property name.</param>
        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
