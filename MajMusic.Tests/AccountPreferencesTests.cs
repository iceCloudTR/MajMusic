#nullable enable

using System.Net;
using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Tests
{
    /// <summary>Checks saved accounts, vault isolation and explicit-online authentication without using real credentials.</summary>
    internal static class AccountPreferencesTests
    {
        /// <summary>Runs restart, account-choice, credential-cleanup and late-login regressions.</summary>
        /// <returns>A task representing all saved-account checks.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-account-preferences-" + Guid.NewGuid().ToString("N"));
            try
            {
                await VerifyRestartAndIsolationAsync(Path.Combine(root, "restart"));
                await VerifyAccountChoicesAsync(Path.Combine(root, "choices"));
                await VerifyFailureIsolationAsync(Path.Combine(root, "failures"));
                await VerifyGuestDuringVaultReadAsync(Path.Combine(root, "guest-race"));
                await VerifyGuestDuringRequestAsync(Path.Combine(root, "guest-request-race"));
                await VerifyAutomaticChoiceDuringVaultReadAsync(Path.Combine(root, "automatic-choice-race"));
                await VerifyOfflineDuringLoginAsync(Path.Combine(root, "offline-race"));
                await VerifyAddressAndRemovalAsync(Path.Combine(root, "cleanup"));
                await VerifyTransientSessionReloadAsync(Path.Combine(root, "transient-reload"));
                await VerifyExplicitPermissionDuringLoadAsync(Path.Combine(root, "permission-load"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var resolved = Path.GetFullPath(root);
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("MajMusic-account-preferences-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the account-preferences validation workspace.");
                }

                if (Directory.Exists(resolved))
                {
                    Directory.Delete(resolved, true);
                }
            }
        }

        /// <summary>Restores account/Guest choices without startup requests and authenticates only after explicit opt-in.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing persistence and independent automatic login.</returns>
        private static async Task VerifyRestartAndIsolationAsync(string root)
        {
            var vault = new MemoryCredentialStore();
            using var handler = new LoginTransport();
            using var client = new HttpClient(handler, false);
            var first = Endpoint("first");
            var second = Endpoint("second");
            var guest = Endpoint("guest");
            string firstAccount;
            string secondAccount;
            using (var workspace = new MusicWorkspace(root, client, client, credentialStore: vault))
            {
                var settings = await workspace.LoadSettingsAsync();
                await workspace.SaveSettingsAsync(settings with { Endpoints = [first, second, guest], ActiveEndpointId = first.Id });
                first = await workspace.SaveAccountAsync(first, "Alice", "fixture-password-a", true, true);
                second = await workspace.SaveAccountAsync(second, "Bob", "fixture-password-b", true, true);
                guest = await workspace.SaveAccountAsync(guest, "Remembered guest account", "fixture-password-g", true, true);
                guest = await workspace.SelectAccountAsync(guest, null);
                firstAccount = first.SelectedAccountId!;
                secondAccount = second.SelectedAccountId!;
                TestAssert.Equal(3, vault.Count, "Identical endpoint addresses must still store independent account passwords.");
                TestAssert.Equal(0, handler.Usernames.Count, "Saving account preferences must never authenticate.");
                var onlinePreferences = await workspace.LoadSettingsAsync();
                await workspace.SaveSettingsAsync(onlinePreferences with { OnlineEnabled = true });
            }

            var serialized = await File.ReadAllTextAsync(Path.Combine(root, "settings.json"));
            foreach (var password in new[] { "fixture-password-a", "fixture-password-b", "fixture-password-g" })
            {
                TestAssert.True(!serialized.Contains(password, StringComparison.Ordinal)
                    && !serialized.Contains(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password))), StringComparison.Ordinal),
                    "Non-secret settings cannot contain passwords or wire-protocol password digests.");
            }

            using var reopened = new MusicWorkspace(root, client, client, credentialStore: vault);
            var restored = await reopened.LoadSettingsAsync();
            TestAssert.True(reopened.IsOnline && restored.OnlineEnabled, "The explicitly saved online choice must be restored after restart.");
            TestAssert.Equal(firstAccount, restored.Endpoints.Single(item => item.Id == first.Id).SelectedAccountId, "The first account selection survives restart.");
            TestAssert.Equal(secondAccount, restored.Endpoints.Single(item => item.Id == second.Id).SelectedAccountId, "The second account selection survives restart.");
            TestAssert.True(restored.Endpoints.Single(item => item.Id == guest.Id).SelectedAccountId is null, "Guest survives restart independently of saved passwords.");
            TestAssert.Equal(0, vault.ReadCount, "Loading saved online preferences cannot read passwords before the host begins automatic login.");
            TestAssert.Equal(0, handler.Usernames.Count, "Loading saved online preferences cannot itself send authentication requests.");
            var results = await reopened.AutoLoginAsync();
            TestAssert.Equal(2, results.Count, "Only selected accounts with automatic login may authenticate.");
            TestAssert.True(results.All(item => item.Succeeded), "Independent selected automatic accounts should authenticate.");
            TestAssert.True(handler.Usernames.SequenceEqual(new[] { "Alice", "Bob" }), "Each source must receive its own remembered account.");
            TestAssert.True(!reopened.GetEndpointSessions().Single(item => item.EndpointId == guest.Id).IsLoggedIn, "Guest cannot silently use a stored automatic account.");
            var beforeRepeated = handler.Usernames.Count;
            await reopened.AutoLoginAsync();
            TestAssert.Equal(beforeRepeated, handler.Usernames.Count, "An already authenticated selected account is not logged in twice.");
        }

        /// <summary>Allows multiple saved accounts while preserving a single explicitly selected automatic choice.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing account changes and remembered-password removal.</returns>
        private static async Task VerifyAccountChoicesAsync(string root)
        {
            var vault = new MemoryCredentialStore();
            using var handler = new LoginTransport();
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: vault);
            var endpoint = Endpoint("accounts");
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(new AppSettings { Endpoints = [endpoint] });
            endpoint = await workspace.SaveAccountAsync(endpoint, "Alice", "fixture-a", true, true);
            var alice = endpoint.SelectedAccountId!;
            endpoint = await workspace.SaveAccountAsync(endpoint, "Bob", "fixture-b", true, true);
            var bob = endpoint.SelectedAccountId!;
            TestAssert.Equal(2, endpoint.Accounts.Count, "Saving another account must retain the first account.");
            TestAssert.Equal(bob, endpoint.Accounts.Single(item => item.AutoLogin).Id, "Choosing automatic Bob must clear Alice's automatic flag.");
            endpoint = await workspace.SelectAccountAsync(endpoint, alice);
            workspace.SetOnlineEnabled(true);
            TestAssert.Equal(0, (await workspace.AutoLoginAsync()).Count, "Selecting a nonautomatic account cannot log in the other remembered account.");
            endpoint = await workspace.SaveAccountAsync(endpoint, "Alice", null, true, true);
            await workspace.LoginSavedAccountAsync(endpoint, alice);
            TestAssert.Equal("Alice", workspace.GetEndpointSessions().Single().Username, "A selected remembered password can be reused without retyping it.");
            endpoint = await workspace.SaveAccountAsync(endpoint, "Alice", null, false, false);
            TestAssert.Equal(1, vault.Count, "Turning remember-password off removes only that account's password.");
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.LoginSavedAccountAsync(endpoint, alice));
            endpoint = await workspace.RemoveAccountAsync(endpoint, alice);
            TestAssert.True(endpoint.SelectedAccountId is null && endpoint.Accounts.Single().Id == bob, "Removing the selected account chooses Guest and retains unrelated accounts.");
            TestAssert.True(!workspace.GetEndpointSessions().Single().IsLoggedIn, "Removing the selected account also clears its live session.");
            endpoint = await workspace.RemoveAccountAsync(endpoint, bob);
            TestAssert.Equal(0, vault.Count, "Removing the last remembered account removes its remaining vault entry.");
            TestAssert.Equal(0, endpoint.Accounts.Count, "Removed accounts cannot reappear in selectors.");
        }

        /// <summary>Isolates a refused account from another endpoint's successful automatic login.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing independent failure handling.</returns>
        private static async Task VerifyFailureIsolationAsync(string root)
        {
            var vault = new MemoryCredentialStore();
            using var handler = new LoginTransport();
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: vault);
            var refused = Endpoint("refused");
            var accepted = Endpoint("accepted");
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(new AppSettings { Endpoints = [refused, accepted] });
            refused = await workspace.SaveAccountAsync(refused, "Rejected", "fixture-refused", true, true);
            accepted = await workspace.SaveAccountAsync(accepted, "Accepted", "fixture-accepted", true, true);
            workspace.SetOnlineEnabled(true);
            var results = await workspace.AutoLoginAsync();
            TestAssert.True(!results.Single(item => item.EndpointId == refused.Id).Succeeded, "A rejected account must remain unauthenticated.");
            TestAssert.True(results.Single(item => item.EndpointId == accepted.Id).Succeeded, "A rejected account cannot prevent another source from authenticating.");
            TestAssert.True(!workspace.GetEndpointSessions().Single(item => item.EndpointId == refused.Id).IsLoggedIn, "Failed login cannot adopt another account's session.");
            var persisted = await workspace.LoadSettingsAsync();
            TestAssert.Equal(refused.SelectedAccountId, persisted.Endpoints.Single(item => item.Id == refused.Id).SelectedAccountId,
                "Authentication failure does not silently change the saved account selection to Guest.");
        }

        /// <summary>Rejects a saved-account login superseded by Guest while its vault read is pending.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing the saved-choice race.</returns>
        private static async Task VerifyGuestDuringVaultReadAsync(string root)
        {
            var vault = new MemoryCredentialStore();
            using var handler = new LoginTransport();
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: vault);
            var endpoint = Endpoint("guest-race");
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(new AppSettings { Endpoints = [endpoint] });
            endpoint = await workspace.SaveAccountAsync(endpoint, "Alice", "fixture-race", true, true);
            workspace.SetOnlineEnabled(true);
            vault.BlockNextRead = true;
            var automatic = workspace.AutoLoginAsync();
            await vault.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            endpoint = await workspace.SelectAccountAsync(endpoint, null);
            vault.ReleaseRead.TrySetResult();
            await automatic.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(0, handler.Usernames.Count, "A newer Guest selection must prevent even sending the obsolete saved password.");
            TestAssert.True(!workspace.GetEndpointSessions().Single().IsLoggedIn, "A late vault read cannot replace Guest with a logged-in session.");
            TestAssert.True((await workspace.LoadSettingsAsync()).Endpoints.Single().SelectedAccountId is null, "The newest Guest choice remains persisted.");
        }

        /// <summary>Rejects a successful response delivered after online mode was disabled.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing network cancellation and publication ordering.</returns>
        private static async Task VerifyOfflineDuringLoginAsync(string root)
        {
            var vault = new MemoryCredentialStore();
            using var handler = new LoginTransport { BlockLogin = true };
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: vault);
            var endpoint = Endpoint("offline-race");
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(new AppSettings { Endpoints = [endpoint] });
            endpoint = await workspace.SaveAccountAsync(endpoint, "Alice", "fixture-offline", true, true);
            workspace.SetOnlineEnabled(true);
            var automatic = workspace.AutoLoginAsync();
            await handler.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            workspace.SetOnlineEnabled(false);
            handler.ReleaseLogin.TrySetResult();
            await automatic.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(!workspace.IsOnline && !workspace.GetEndpointSessions().Single().IsLoggedIn,
                "Disabling online mode prevents a late automatic-login response from publishing a session.");
        }

        /// <summary>Cancels an already sent automatic-login request when Guest is chosen.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing endpoint-owned authentication cancellation.</returns>
        private static async Task VerifyGuestDuringRequestAsync(string root)
        {
            var vault = new MemoryCredentialStore();
            using var handler = new LoginTransport { BlockLogin = true, RespectCancellation = true };
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: vault);
            var endpoint = Endpoint("guest-request-race");
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(new AppSettings { Endpoints = [endpoint] });
            endpoint = await workspace.SaveAccountAsync(endpoint, "Alice", "fixture-pending-request", true, true);
            workspace.SetOnlineEnabled(true);
            var automatic = workspace.AutoLoginAsync();
            await handler.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await workspace.SelectAccountAsync(endpoint, null);
            await handler.LoginCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await automatic.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(!workspace.GetEndpointSessions().Single().IsLoggedIn, "Guest must cancel authentication immediately without waiting for a remote response.");
        }

        /// <summary>Honors an automatic-login checkbox disabled while password retrieval is still pending.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing a newer automatic-login preference.</returns>
        private static async Task VerifyAutomaticChoiceDuringVaultReadAsync(string root)
        {
            var vault = new MemoryCredentialStore();
            using var handler = new LoginTransport();
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: vault);
            var endpoint = Endpoint("automatic-choice-race");
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(new AppSettings { Endpoints = [endpoint] });
            endpoint = await workspace.SaveAccountAsync(endpoint, "Alice", "fixture-automatic-choice", true, true);
            workspace.SetOnlineEnabled(true);
            vault.BlockNextRead = true;
            var automatic = workspace.AutoLoginAsync();
            await vault.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            endpoint = await workspace.SaveAccountAsync(endpoint, "Alice", null, true, false);
            vault.ReleaseRead.TrySetResult();
            await automatic.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(0, handler.Usernames.Count, "A newer unchecked automatic-login choice must prevent sending the old pending credential.");
            TestAssert.True(!workspace.GetEndpointSessions().Single().IsLoggedIn, "Unchecking pending automatic login cannot publish an obsolete account session.");
        }

        /// <summary>Forgets secrets when their address or source is removed and validates before destructive cleanup.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing credential scoping and cleanup.</returns>
        private static async Task VerifyAddressAndRemovalAsync(string root)
        {
            var vault = new MemoryCredentialStore();
            using var handler = new LoginTransport();
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: vault);
            var endpoint = Endpoint("cleanup");
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(new AppSettings { Endpoints = [endpoint] });
            endpoint = await workspace.SaveAccountAsync(endpoint, "Alice", "fixture-cleanup", true, true);
            var settings = await workspace.LoadSettingsAsync();
            await TestAssert.ThrowsAsync<InvalidDataException>(() => workspace.SaveSettingsAsync(settings with
            {
                Endpoints = [endpoint with { BaseUri = new Uri("http://not-allowed.example/api/") }]
            }));
            TestAssert.Equal(1, vault.Count, "Invalid settings must not remove the existing remembered password.");
            var changed = endpoint with { BaseUri = new Uri("https://other.example.invalid/api/") };
            await workspace.SaveSettingsAsync(settings with { Endpoints = [changed] });
            changed = (await workspace.LoadSettingsAsync()).Endpoints.Single();
            TestAssert.True(changed.Accounts.Single() is { RememberPassword: false, AutoLogin: false },
                "Changing an API root retains the account name but disables old automatic credentials.");
            TestAssert.Equal(0, vault.Count, "An old API root's remembered password is removed rather than sent to a new root.");
            workspace.SetOnlineEnabled(true);
            TestAssert.Equal(0, (await workspace.AutoLoginAsync()).Count, "A changed server cannot automatically receive an old account password.");
            changed = await workspace.SaveAccountAsync(changed, "Alice", "fixture-new-root", true, true);
            await workspace.SaveSettingsAsync((await workspace.LoadSettingsAsync()) with { Endpoints = Array.Empty<EndpointConfiguration>(), ActiveEndpointId = null });
            TestAssert.Equal(0, vault.Count, "Removing a source removes all remembered passwords that it owned.");
            TestAssert.Equal(0, handler.Usernames.Count, "Account cleanup and address edits are entirely local operations.");
        }

        /// <summary>Creates an isolated endpoint whose address is shared deliberately by account slots.</summary>
        /// <param name="id">The independent configuration identity.</param>
        /// <returns>The configured test source.</returns>
        private static EndpointConfiguration Endpoint(string id)
        {
            return new EndpointConfiguration { Id = id, Name = id, BaseUri = new Uri("https://accounts.example.invalid/api/") };
        }

        /// <summary>Keeps a successful transient login across unchanged settings while honoring actual saved-choice changes.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing session reconciliation against changed preferences.</returns>
        private static async Task VerifyTransientSessionReloadAsync(string root)
        {
            var vault = new MemoryCredentialStore();
            using var handler = new LoginTransport();
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: vault);
            var endpoint = Endpoint("transient-reload");
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(new AppSettings { Endpoints = [endpoint] });
            endpoint = await workspace.SaveAccountAsync(endpoint, "Saved Alice", "fixture-saved-choice", true, false);
            workspace.SetOnlineEnabled(true);
            await workspace.LoginAsync(endpoint, "Transient Bob", "fixture-transient-choice");
            var settings = await workspace.LoadSettingsAsync();
            TestAssert.Equal("Transient Bob", workspace.GetEndpointSessions().Single().Username,
                "Reloading unchanged saved choices cannot discard a successfully authenticated transient account.");
            await workspace.SaveSettingsAsync(settings with { Endpoints = [endpoint with { Name = "Renamed source" }] });
            TestAssert.Equal("Transient Bob", workspace.GetEndpointSessions().Single().Username,
                "Unrelated endpoint metadata changes must preserve the actual authenticated account.");

            var disk = new JsonSettingsStore(Path.Combine(root, "settings.json"));
            settings = await disk.LoadAsync();
            await disk.SaveAsync(settings with { Endpoints = [settings.Endpoints.Single() with { SelectedAccountId = null }] });
            await workspace.LoadSettingsAsync();
            TestAssert.True(!workspace.GetEndpointSessions().Single().IsLoggedIn,
                "An actual saved-choice change to Guest must still retire an active transient session on reload.");
            endpoint = await workspace.SelectAccountAsync(endpoint, endpoint.SelectedAccountId);
            await workspace.LoginSavedAccountAsync(endpoint, endpoint.SelectedAccountId!);
            settings = await workspace.LoadSettingsAsync();
            var renamedAccount = endpoint.Accounts.Single() with { Username = "Another saved name" };
            await workspace.SaveSettingsAsync(settings with { Endpoints = [endpoint with { Accounts = [renamedAccount] }] });
            TestAssert.True(!workspace.GetEndpointSessions().Single().IsLoggedIn,
                "Changing the selected account's identity must still retire an account that no longer matches it.");
        }

        /// <summary>Preserves an explicit runtime online choice made while initial preferences are still loading.</summary>
        /// <param name="root">The isolated application-data directory.</param>
        /// <returns>A task representing preference-load permission ordering.</returns>
        private static async Task VerifyExplicitPermissionDuringLoadAsync(string root)
        {
            var store = new DelayedSettingsStore();
            using var workspace = new MusicWorkspace(root, settingsStore: store, credentialStore: new MemoryCredentialStore());
            var loading = workspace.LoadSettingsAsync();
            await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            workspace.SetOnlineEnabled(true);
            store.ReleaseLoad.TrySetResult();
            var loaded = await loading.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(loaded.OnlineEnabled && workspace.IsOnline,
                "Offline startup defaults cannot override an explicit online choice made during asynchronous settings loading.");
            workspace.SetOnlineEnabled(false);
            loaded = await workspace.LoadSettingsAsync();
            TestAssert.True(!loaded.OnlineEnabled && !workspace.IsOnline,
                "A later explicit offline choice must still win over saved online preferences.");
        }

        /// <summary>Delays an initial preference read while keeping all data and network behavior inside the test.</summary>
        private sealed class DelayedSettingsStore : ISettingsStore
        {
            /// <summary>Signals that the asynchronous preference read is pending.</summary>
            public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the pending preference read after a newer runtime permission choice.</summary>
            public TaskCompletionSource ReleaseLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            {
                LoadStarted.TrySetResult();
                await ReleaseLoad.Task.WaitAsync(cancellationToken);
                return new AppSettings { OnlineEnabled = true };
            }

            /// <inheritdoc />
            public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }

        /// <summary>Stores generated test passwords in memory without touching the user's platform vault.</summary>
        private sealed class MemoryCredentialStore : ICredentialStore
        {
            /// <summary>Contains generated secrets under opaque scoped keys.</summary>
            private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
            /// <summary>Gets the number of independent stored secrets.</summary>
            public int Count => _values.Count;
            /// <summary>Gets the number of password lookups.</summary>
            public int ReadCount { get; private set; }
            /// <summary>Gets or sets whether the next lookup waits for an explicit test release.</summary>
            public bool BlockNextRead { get; set; }
            /// <summary>Signals that the blocked lookup is pending.</summary>
            public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the blocked lookup after a newer user choice.</summary>
            public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadCount++;
                if (BlockNextRead)
                {
                    BlockNextRead = false;
                    ReadStarted.TrySetResult();
                    await ReleaseRead.Task.WaitAsync(cancellationToken);
                }

                return _values.GetValueOrDefault(key);
            }

            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _values[key] = secret;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _values.Remove(key);
                return Task.CompletedTask;
            }
        }

        /// <summary>Accepts generated account requests and models late responses without contacting any endpoint.</summary>
        private sealed class LoginTransport : HttpMessageHandler
        {
            /// <summary>Gets the account names sent to the simulated source.</summary>
            public List<string> Usernames { get; } = new();
            /// <summary>Gets whether login waits for the test to change online permission.</summary>
            public bool BlockLogin { get; init; }
            /// <summary>Gets whether a blocked simulated request observes the endpoint's cancellation token.</summary>
            public bool RespectCancellation { get; init; }
            /// <summary>Signals that a blocked request has been sent.</summary>
            public TaskCompletionSource LoginStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the blocked simulated response.</summary>
            public TaskCompletionSource ReleaseLogin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Signals cancellation of a source-owned login request.</summary>
            public TaskCompletionSource LoginCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("account/login", StringComparison.Ordinal)
                    || request.Content is not MultipartFormDataContent content)
                {
                    throw new InvalidOperationException("The account-preferences test attempted an unexpected request.");
                }

                var username = string.Empty;
                foreach (var part in content)
                {
                    if (part.Headers.ContentDisposition?.Name?.Trim('"') == "username")
                    {
                        username = await part.ReadAsStringAsync(cancellationToken);
                    }
                }

                Usernames.Add(username);
                if (BlockLogin)
                {
                    LoginStarted.TrySetResult();
                    try
                    {
                        if (RespectCancellation)
                        {
                            await ReleaseLogin.Task.WaitAsync(cancellationToken);
                        }
                        else
                        {
                            await ReleaseLogin.Task;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        LoginCanceled.TrySetResult();
                        throw;
                    }
                }

                var response = new HttpResponseMessage(username == "Rejected" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
                response.Headers.TryAddWithoutValidation("Set-Cookie", "session=" + Uri.EscapeDataString(username) + "; Path=/; Secure");
                return response;
            }
        }
    }
}
