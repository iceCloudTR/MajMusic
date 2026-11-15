#nullable enable

using System.Net;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks saved-account cloud permission without using real credentials or cloud services.</summary>
    internal static class CloudCollectionPreferenceTests
    {
        /// <summary>Validates narrow persistence, captured checkbox ownership and compatibility with an in-flight login.</summary>
        /// <returns>A task representing completed permission regressions.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-cloud-preferences-" + Guid.NewGuid().ToString("N"));
            try
            {
                await ValidateNarrowPersistenceAsync(Path.Combine(root, "persistence"));
                await ValidateCheckboxOwnershipAsync(Path.Combine(root, "checkbox"));
                await ValidatePendingLoginAsync(Path.Combine(root, "pending-login"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var resolved = Path.GetFullPath(root);
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("MajMusic-cloud-preferences-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated cloud-preference fixture.");
                }

                if (Directory.Exists(resolved))
                {
                    Directory.Delete(resolved, true);
                }
            }
        }

        /// <summary>Changes one account flag while retaining other accounts, settings and password-retention choices.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing persisted account-specific authorization.</returns>
        private static async Task ValidateNarrowPersistenceAsync(string root)
        {
            using var transport = new FixtureTransport();
            using var client = new HttpClient(transport);
            var vault = new MemoryCredentials();
            var endpoint = Endpoint();
            var directory = new LocalDirectorySource { Id = "charts", Path = Path.Combine(root, "original-charts") };
            using (var workspace = new MusicWorkspace(root, client, client, credentialStore: vault))
            {
                var initial = await workspace.LoadSettingsAsync();
                await workspace.SaveSettingsAsync(initial with { Endpoints = [endpoint], LocalDirectorySources = [directory] });
                var updated = await workspace.SetCloudCollectionUsageAsync(endpoint, "alice", true);
                TestAssert.True(updated.Accounts.Single(account => account.Id == "alice").UseCloudCollections
                    && !updated.Accounts.Single(account => account.Id == "bob").UseCloudCollections,
                    "Only the explicitly captured saved account may receive cloud-collection permission.");
                TestAssert.Equal("alice", updated.SelectedAccountId, "Changing cloud permission cannot change the selected account.");
                await workspace.UpdateAudioOutputAsync(new AudioOutputPreferences { Volume = 37, Muted = true });
                await workspace.SaveAccountAsync(endpoint, "Alice", null, false, false);
                var saved = await workspace.LoadSettingsAsync();
                TestAssert.True(saved.Endpoints.Single().Accounts.Single(account => account.Id == "alice").UseCloudCollections,
                    "Saving account details must retain existing cloud permission.");
                TestAssert.True(saved.AudioOutput is { Volume: 37, Muted: true } && saved.LocalDirectorySources.Single().Id == directory.Id,
                    "A narrow cloud preference cannot replace audio or directory settings.");
                var current = saved.Endpoints.Single();
                await workspace.SelectAccountAsync(current, "bob");
                updated = await workspace.SetCloudCollectionUsageAsync(endpoint, "alice", false);
                TestAssert.True(updated.SelectedAccountId == "bob" && !updated.Accounts.Single(account => account.Id == "bob").UseCloudCollections,
                    "A delayed edit to account A cannot select or authorize account B.");
                await workspace.SetCloudCollectionUsageAsync(endpoint, "alice", true);
                await workspace.SaveEndpointAsync(endpoint with { BaseUri = new Uri("https://replacement.example.invalid/api/") });
                TestAssert.True((await workspace.LoadSettingsAsync()).Endpoints.Single().Accounts.All(account => !account.UseCloudCollections),
                    "Replacing the API root must not transfer cloud-upload permission to another server.");
                await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.SetCloudCollectionUsageAsync(endpoint, "alice", true));
            }

            using var reopened = new MusicWorkspace(root, client, client, credentialStore: vault);
            var restored = await reopened.LoadSettingsAsync();
            TestAssert.True(restored.Endpoints.Single().Accounts.All(account => !account.UseCloudCollections),
                "Per-account cloud choices must survive restart after an address reset.");
            TestAssert.True(transport.Requests == 0 && vault.Reads == 0 && vault.Writes == 0 && vault.Deletes == 0,
                "Local cloud preferences cannot authenticate, contact a service or access the credential vault.");
        }

        /// <summary>Keeps the checkbox bound to the current saved identity while an earlier account's write is pending.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing account-switch and Guest checkbox behavior.</returns>
        private static async Task ValidateCheckboxOwnershipAsync(string root)
        {
            using var transport = new FixtureTransport();
            using var client = new HttpClient(transport);
            var vault = new MemoryCredentials();
            var store = new GateStore(Path.Combine(root, "settings.json"));
            using var workspace = new MusicWorkspace(root, client, client, store, vault);
            var initial = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(initial with { Endpoints = [Endpoint()] });
            await using var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await view.InitializeAsync();
            var card = view.EndpointCards.Single();
            TestAssert.True(card.CanUseCloudCollections && !card.UseCloudCollections, "An existing saved account starts with cloud collection usage disabled.");
            card.Password = "Unsaved generated draft";
            store.PauseNext();
            card.UseCloudCollections = true;
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                card.SelectedAccount = card.Accounts.Single(option => option.AccountId == "bob");
                TestAssert.True(!card.UseCloudCollections, "Selecting B must immediately restore B's own checkbox state while A is saving.");
                card.SelectAccountCommand.Execute(null);
            }
            finally
            {
                store.Release.TrySetResult();
            }

            await card.CloudCollectionUsageUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitIdleAsync(card);
            var saved = await workspace.LoadSettingsAsync();
            TestAssert.True(saved.Endpoints.Single().SelectedAccountId == "bob"
                && saved.Endpoints.Single().Accounts.Single(account => account.Id == "alice").UseCloudCollections
                && !saved.Endpoints.Single().Accounts.Single(account => account.Id == "bob").UseCloudCollections,
                "Completing A's captured checkbox edit must retain B's selection without granting B permission.");
            TestAssert.True(card.SelectedAccount?.AccountId == "bob" && !card.UseCloudCollections,
                "A's asynchronous completion cannot repaint B's checkbox using A's value.");
            card.SelectedAccount = card.Accounts.Single(option => option.AccountId == "alice");
            card.SelectAccountCommand.Execute(null);
            await WaitIdleAsync(card);
            TestAssert.True(card.UseCloudCollections, "Returning to A must restore A's independently saved flag.");
            card.UseCloudCollections = false;
            card.UseCloudCollections = true;
            card.UseCloudCollections = false;
            await card.CloudCollectionUsageUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(!(await workspace.LoadSettingsAsync()).Endpoints.Single().Accounts.Single(account => account.Id == "alice").UseCloudCollections,
                "A burst of checkbox edits must persist its latest value.");
            card.SelectedAccount = card.Accounts.Single(option => option.AccountId is null);
            card.SelectAccountCommand.Execute(null);
            await WaitIdleAsync(card);
            TestAssert.True(!card.CanUseCloudCollections && !card.UseCloudCollections, "Guest must disable and clear the cloud-collection checkbox.");
            card.UseCloudCollections = true;
            TestAssert.True(!card.UseCloudCollections, "A disabled Guest checkbox cannot grant permission to a remembered account.");
            card.NewAccountCommand.Execute(null);
            await WaitIdleAsync(card);
            TestAssert.True(!card.CanUseCloudCollections && !card.UseCloudCollections, "An unsaved account draft cannot inherit another account's permission.");
            TestAssert.True(vault.Reads == 0 && vault.Writes == 0 && transport.Requests == 0,
                "Checkbox saves cannot persist the typed password, read vault credentials or issue cloud requests while offline.");
        }

        /// <summary>Retains an in-flight login when only its account's cloud preference changes.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing login-independent permission persistence.</returns>
        private static async Task ValidatePendingLoginAsync(string root)
        {
            using var transport = new FixtureTransport { AllowBlockedLogin = true };
            using var client = new HttpClient(transport);
            var vault = new MemoryCredentials();
            using var workspace = new MusicWorkspace(root, client, client, credentialStore: vault);
            var initial = await workspace.LoadSettingsAsync();
            var endpoint = Endpoint() with { Accounts = Array.Empty<SavedEndpointAccount>(), SelectedAccountId = null };
            await workspace.SaveSettingsAsync(initial with { Endpoints = [endpoint] });
            endpoint = await workspace.SaveAccountAsync(endpoint, "Generated login", "Generated fixture secret", true, true);
            workspace.SetOnlineEnabled(true);
            var login = workspace.LoginSavedAccountAsync(endpoint, endpoint.SelectedAccountId!);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                await workspace.SetCloudCollectionUsageAsync(endpoint, endpoint.SelectedAccountId!, true);
                TestAssert.True(!transport.LoginCancelled, "A cloud-only preference cannot retire a pending login candidate.");
            }
            finally
            {
                transport.Release.TrySetResult();
            }

            await login.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(workspace.GetEndpointSessions().Single().IsLoggedIn,
                "The independently pending login must still establish its original saved account session.");
        }

        /// <summary>Waits for one account-card command to release its own busy state.</summary>
        /// <param name="card">The independently edited endpoint card.</param>
        /// <returns>A task representing completed local card work.</returns>
        private static async Task WaitIdleAsync(EndpointCardViewModel card)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!card.IsIdle)
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        /// <summary>Creates two saved identities whose cloud permission defaults to false.</summary>
        /// <returns>The generated endpoint with account A initially selected.</returns>
        private static EndpointConfiguration Endpoint()
        {
            return new EndpointConfiguration
            {
                Id = "source",
                Name = "Source",
                BaseUri = new Uri("https://source.example.invalid/api/"),
                Accounts = [new SavedEndpointAccount { Id = "alice", Username = "Alice" }, new SavedEndpointAccount { Id = "bob", Username = "Bob" }],
                SelectedAccountId = "alice"
            };
        }

        /// <summary>Blocks one generated settings publication to expose delayed checkbox completion.</summary>
        private sealed class GateStore : ISettingsStore
        {
            /// <summary>Owns the explicit fixture settings file.</summary>
            private readonly JsonSettingsStore _inner;
            /// <summary>Marks the next write for explicit release.</summary>
            private int _pause;
            /// <summary>Creates one fixture preference store.</summary>
            /// <param name="path">The generated settings path.</param>
            public GateStore(string path)
            {
                _inner = new JsonSettingsStore(path);
            }
            /// <summary>Signals that a blocked preference write is pending.</summary>
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the blocked preference write.</summary>
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Configures one blocked preference publication.</summary>
            public void PauseNext()
            {
                Interlocked.Exchange(ref _pause, 1);
            }
            /// <inheritdoc />
            public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            {
                return _inner.LoadAsync(cancellationToken);
            }
            /// <inheritdoc />
            public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _pause, 0) == 1)
                {
                    Started.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }

                await _inner.SaveAsync(settings, cancellationToken);
            }
        }

        /// <summary>Rejects all real source operations while allowing one explicitly generated login.</summary>
        private sealed class FixtureTransport : HttpMessageHandler
        {
            /// <summary>Gets whether the generated login route may wait for release.</summary>
            public bool AllowBlockedLogin { get; init; }
            /// <summary>Gets the number of requests that reached the fake handler.</summary>
            public int Requests { get; private set; }
            /// <summary>Gets whether the generated pending login was canceled.</summary>
            public bool LoginCancelled { get; private set; }
            /// <summary>Signals the generated login request.</summary>
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the generated login response.</summary>
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                if (!AllowBlockedLogin || !request.RequestUri!.AbsolutePath.EndsWith("account/login", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A local cloud preference attempted a source request.");
                }

                Started.TrySetResult();
                try
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    LoginCancelled = true;
                    throw;
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                response.Headers.TryAddWithoutValidation("Set-Cookie", "session=generated; Path=/; Secure");
                return response;
            }
        }

        /// <summary>Stores generated test credentials in memory without opening the platform vault.</summary>
        private sealed class MemoryCredentials : ICredentialStore
        {
            /// <summary>Stores only generated fixture secrets.</summary>
            private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
            /// <summary>Gets how many generated password reads occurred.</summary>
            public int Reads { get; private set; }
            /// <summary>Gets how many generated password writes occurred.</summary>
            public int Writes { get; private set; }
            /// <summary>Gets how many generated password removals occurred.</summary>
            public int Deletes { get; private set; }
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                Reads++;
                return Task.FromResult(_values.GetValueOrDefault(key));
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                Writes++;
                _values[key] = secret;
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                Deletes++;
                _values.Remove(key);
                return Task.CompletedTask;
            }
        }
    }
}
