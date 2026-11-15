#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Stores non-secret account choices separately from system-protected passwords.</summary>
    public sealed partial class MusicWorkspace
    {
        /// <summary>Reads and writes passwords in the platform's user credential vault.</summary>
        private readonly ICredentialStore _credentialStore;

        /// <summary>Coalesces automatic login requests for one explicit online period.</summary>
        private readonly SemaphoreSlim _autoLoginGate = new(1, 1);

        /// <inheritdoc />
        public async Task<EndpointConfiguration> SaveAccountAsync(EndpointConfiguration endpoint, string username, string? password,
            bool rememberPassword, bool autoLogin, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            if (autoLogin && !rememberPassword)
            {
                throw new ArgumentException("自动登录需要记住密码。", nameof(autoLogin));
            }

            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = ReadAccountEndpoint(endpoint);
                username = username.Trim();
                var previous = current.Accounts.FirstOrDefault(account => account.Username == username);
                var accountId = previous?.Id ?? Guid.NewGuid().ToString("N");
                var key = GetCredentialKey(current, accountId);
                if (rememberPassword)
                {
                    if (password is not null)
                    {
                        ArgumentException.ThrowIfNullOrWhiteSpace(password);
                        await _credentialStore.WriteAsync(key, password, cancellationToken).ConfigureAwait(false);
                    }
                    else if (previous?.RememberPassword != true)
                    {
                        throw new InvalidOperationException("请输入要记住的密码。");
                    }
                }
                else if (previous?.RememberPassword == true)
                {
                    await _credentialStore.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                }

                var saved = new SavedEndpointAccount
                {
                    Id = accountId,
                    Username = username,
                    RememberPassword = rememberPassword,
                    AutoLogin = autoLogin
                };
                var accounts = current.Accounts.Where(account => account.Id != accountId)
                    .Select(account => autoLogin ? account with { AutoLogin = false } : account).Append(saved).ToArray();
                var updated = current with { Accounts = accounts, SelectedAccountId = accountId };
                await PersistAccountEndpointAsync(updated, cancellationToken).ConfigureAwait(false);
                if (current.SelectedAccountId != accountId || previous?.RememberPassword != rememberPassword
                    || previous?.AutoLogin != autoLogin || password is not null)
                {
                    InvalidateAccountChoice(updated, username);
                }

                return updated;
            }
            finally
            {
                _settingsGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<EndpointConfiguration> SelectAccountAsync(EndpointConfiguration endpoint, string? accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chosen = ReadAccountEndpoint(endpoint);
            var account = accountId is null ? null : chosen.Accounts.FirstOrDefault(item => item.Id == accountId)
                ?? throw new InvalidOperationException("所选账号已移除。");
            InvalidateAccountChoice(chosen, account?.Username);
            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = ReadAccountEndpoint(endpoint);
                if (accountId is not null && !current.Accounts.Any(item => item.Id == accountId))
                {
                    throw new InvalidOperationException("所选账号已移除。");
                }

                var updated = current with { SelectedAccountId = accountId };
                await PersistAccountEndpointAsync(updated, cancellationToken).ConfigureAwait(false);
                return updated;
            }
            finally
            {
                _settingsGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<EndpointConfiguration> RemoveAccountAsync(EndpointConfiguration endpoint, string accountId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
            var selected = ReadAccountEndpoint(endpoint);
            if (selected.SelectedAccountId == accountId)
            {
                InvalidateAccountChoice(selected, null);
            }

            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = ReadAccountEndpoint(endpoint);
                var removed = current.Accounts.FirstOrDefault(account => account.Id == accountId);
                if (removed is null)
                {
                    return current;
                }

                if (removed.RememberPassword)
                {
                    await _credentialStore.DeleteAsync(GetCredentialKey(current, accountId), cancellationToken).ConfigureAwait(false);
                }

                var updated = current with
                {
                    Accounts = current.Accounts.Where(account => account.Id != accountId).ToArray(),
                    SelectedAccountId = current.SelectedAccountId == accountId ? null : current.SelectedAccountId
                };
                await PersistAccountEndpointAsync(updated, cancellationToken).ConfigureAwait(false);
                return updated;
            }
            finally
            {
                _settingsGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task LoginSavedAccountAsync(EndpointConfiguration endpoint, string accountId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
            EndpointConfiguration current;
            SavedEndpointAccount account;
            long choice;
            lock (_stateGate)
            {
                current = ReadAccountEndpoint(endpoint);
                account = current.Accounts.FirstOrDefault(item => item.Id == accountId)
                    ?? throw new InvalidOperationException("所选账号已移除。");
                if (current.SelectedAccountId != accountId)
                {
                    throw new InvalidOperationException("请先选择该账号。");
                }

                if (!_networkPolicy.IsEnabled)
                {
                    throw new InvalidOperationException("请先开启在线模式。");
                }

                choice = _accountChoices.GetValueOrDefault(current.Id);
            }

            if (!account.RememberPassword)
            {
                throw new InvalidOperationException("请重新输入密码。");
            }

            var password = await _credentialStore.ReadAsync(GetCredentialKey(current, account.Id), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("已保存的密码不可用，请重新输入。");
            await LoginCoreAsync(current, account.Username, password, choice, account.Id, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AutoLoginResult>> AutoLoginAsync(CancellationToken cancellationToken = default)
        {
            if (!IsOnline)
            {
                return Array.Empty<AutoLoginResult>();
            }

            await _autoLoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EndpointConfiguration[] endpoints;
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    endpoints = _settings.Endpoints.ToArray();
                }

                var results = new List<AutoLoginResult>();
                foreach (var endpoint in endpoints)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsOnline)
                    {
                        break;
                    }

                    var account = endpoint.Accounts.FirstOrDefault(item => item.Id == endpoint.SelectedAccountId);
                    if (account is not { AutoLogin: true, RememberPassword: true })
                    {
                        continue;
                    }

                    var session = GetEndpointSessions().FirstOrDefault(item => item.EndpointId == endpoint.Id);
                    if (session is { IsLoggedIn: true } && session.Username == account.Username)
                    {
                        continue;
                    }

                    try
                    {
                        await LoginSavedAccountAsync(endpoint, account.Id, cancellationToken).ConfigureAwait(false);
                        results.Add(new AutoLoginResult { EndpointId = endpoint.Id, AccountId = account.Id, Succeeded = true });
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (!IsOnline)
                        {
                            break;
                        }

                        results.Add(new AutoLoginResult { EndpointId = endpoint.Id, AccountId = account.Id, Error = "账号选择已更改" });
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        results.Add(new AutoLoginResult { EndpointId = endpoint.Id, AccountId = account.Id, Error = "自动登录未完成，请重新登录" });
                    }
                }

                return results;
            }
            finally
            {
                _autoLoginGate.Release();
            }
        }

        /// <summary>Reads the latest saved endpoint instead of trusting a stale editor snapshot.</summary>
        /// <param name="endpoint">The requested endpoint identity and API root.</param>
        /// <returns>The latest saved configuration.</returns>
        /// <exception cref="InvalidOperationException">The endpoint was removed or its address changed.</exception>
        private EndpointConfiguration ReadAccountEndpoint(EndpointConfiguration endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _settings.Endpoints.FirstOrDefault(item => item.Id == endpoint.Id && item.BaseUri == endpoint.BaseUri)
                    ?? throw new InvalidOperationException("请先保存接入点配置。");
            }
        }

        /// <summary>Persists one account edit while retaining other endpoints and the current online permission.</summary>
        /// <param name="updated">The latest endpoint carrying edited non-secret account preferences.</param>
        /// <param name="cancellationToken">Cancels persistence.</param>
        /// <returns>A task representing the atomic preference update.</returns>
        private Task PersistAccountEndpointAsync(EndpointConfiguration updated, CancellationToken cancellationToken)
        {
            AppSettings settings;
            lock (_stateGate)
            {
                settings = _settings with
                {
                    Endpoints = _settings.Endpoints.Select(item => item.Id == updated.Id ? updated : item).ToArray()
                };
            }

            return PersistSettingsAsync(settings, null, cancellationToken);
        }

        /// <summary>Invalidates pending logins and retains a session only when it already belongs to the chosen account.</summary>
        /// <param name="endpoint">The endpoint whose choice changed.</param>
        /// <param name="username">The chosen account name, or null for Guest.</param>
        private void InvalidateAccountChoice(EndpointConfiguration endpoint, string? username)
        {
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _accountChoices[endpoint.Id] = _accountChoices.GetValueOrDefault(endpoint.Id) + 1;
                if (_pendingLogins.Remove(endpoint.Id, out var pending))
                {
                    pending.Retire();
                }

                if (username is not null && _connections.TryGetValue(endpoint.Id, out var current)
                    && current.Endpoint.BaseUri == endpoint.BaseUri && current.Provider.IsLoggedIn && current.Username == username)
                {
                    return;
                }

                ReplaceConnection(endpoint.Id, CreateConnection(endpoint));
            }
        }

        /// <summary>Builds an opaque vault identity bound to both the saved slot and the complete configured API root.</summary>
        /// <param name="endpoint">The saved endpoint configuration.</param>
        /// <param name="accountId">The saved account identity.</param>
        /// <returns>A deterministic credential key containing no username or password.</returns>
        private static string GetCredentialKey(EndpointConfiguration endpoint, string accountId)
        {
            var scope = JsonSerializer.Serialize(new[] { endpoint.Id, accountId, endpoint.BaseUri.AbsoluteUri.TrimEnd('/') + "/" });
            return "MajMusic/account/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        }

        /// <summary>Retains account names but disables remembered credentials when an API root changes.</summary>
        /// <param name="settings">The proposed non-secret preferences.</param>
        /// <returns>The preferences with changed-address automatic authentication disabled.</returns>
        private AppSettings NormalizeAccountAddressChanges(AppSettings settings)
        {
            lock (_stateGate)
            {
                return settings with
                {
                    Endpoints = settings.Endpoints.Select(endpoint =>
                    {
                        var previous = _settings.Endpoints.FirstOrDefault(item => item.Id == endpoint.Id);
                        return previous is not null && previous.BaseUri != endpoint.BaseUri ? endpoint with
                        {
                            Accounts = endpoint.Accounts.Select(account => account with { RememberPassword = false, AutoLogin = false }).ToArray()
                        } : endpoint;
                    }).ToArray()
                };
            }
        }

        /// <summary>Removes vault passwords whose configuration, address or remember choice was removed.</summary>
        /// <param name="settings">The validated next preferences.</param>
        /// <param name="cancellationToken">Cancels credential cleanup before settings publication.</param>
        /// <returns>A task representing all required credential removals.</returns>
        private async Task RemoveUnusedCredentialsAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            EndpointConfiguration[] previous;
            lock (_stateGate)
            {
                previous = _settings.Endpoints.ToArray();
            }

            foreach (var endpoint in previous)
            {
                var replacement = settings.Endpoints.FirstOrDefault(item => item.Id == endpoint.Id && item.BaseUri == endpoint.BaseUri);
                foreach (var account in endpoint.Accounts.Where(item => item.RememberPassword))
                {
                    var retained = replacement?.Accounts.Any(item => item.Id == account.Id && item.Username == account.Username && item.RememberPassword) == true;
                    if (!retained)
                    {
                        await _credentialStore.DeleteAsync(GetCredentialKey(endpoint, account.Id), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
