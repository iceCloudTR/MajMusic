#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks remembered-account UI behavior through the real workspace without real credentials or HTTP services.</summary>
    internal static class AccountPresentationTests
    {
        /// <summary>Checks offline account editing, restart choices, explicit-online login and cancellation.</summary>
        /// <returns>A task representing the completed account-presentation checks.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-account-ui-" + Guid.NewGuid().ToString("N")));
            try
            {
                await ValidateRestartAndAutomaticChoiceAsync(Path.Combine(root, "restart"));
                await ValidateRememberedManualLoginAsync(Path.Combine(root, "manual"));
                await ValidateVaultFailureAfterAccountChangeAsync(Path.Combine(root, "vault-failure"));
                await ValidateDraftDuringAutomaticRefreshAsync(Path.Combine(root, "draft"));
                await ValidateAutomaticShutdownAsync(Path.Combine(root, "shutdown"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-account-ui-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove a directory outside the generated account-UI workspace.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Restores remembered account flags and Guest while waiting for explicit online activation.</summary>
        /// <param name="directory">The isolated settings and database location.</param>
        /// <returns>A task representing restart and selected-account checks.</returns>
        private static async Task ValidateRestartAndAutomaticChoiceAsync(string directory)
        {
            await using var harness = new Harness(directory);
            await harness.OpenAsync();
            var view = harness.View!;
            TestAssert.True(!view.OnlineEnabled && harness.Transport.RequestCount == 0, "Opening the account editor starts offline without requests.");
            await SaveAccountAsync(view, "Alice", "Generated-Alice-fixture", true, true);
            var aliceId = view.SelectedSavedAccount!.AccountId;
            await ExecuteAsync(view.NewAccountCommand, view);
            await SaveAccountAsync(view, "Alice alternate", "Generated-alternate-fixture", true, true);
            TestAssert.Equal(2, view.SavedAccounts.Count(item => item.AccountId is not null), "One endpoint can remember multiple named accounts.");
            view.SelectedSavedAccount = view.SavedAccounts.Single(item => item.AccountId == aliceId);
            await view.CommitSavedAccountSelectionAsync();
            view.AutoLogin = true;
            await ExecuteAsync(view.SaveAccountCommand, view);
            TestAssert.True(view.RememberPassword && view.AutoLogin && view.LoginPassword.Length == 0,
                "Saving automatic login retains the flags while clearing the transient password box.");

            SelectEndpoint(view, "b");
            await SaveAccountAsync(view, "Bob", "Generated-Bob-fixture", true, true);
            view.SelectedSavedAccount = view.SavedAccounts.Single(item => item.AccountId is null);
            await view.CommitSavedAccountSelectionAsync();
            TestAssert.True(view.SelectedSavedAccount?.AccountId is null && !view.RememberPassword && !view.AutoLogin,
                "Selecting Guest clears the editor flags without removing remembered accounts.");
            SelectEndpoint(view, "a");
            await ExecuteAsync(view.SaveSettingsCommand, view);
            TestAssert.Equal(0, harness.Transport.RequestCount, "Remembering accounts and Guest choices must not authenticate while offline.");
            var saved = await harness.Workspace!.LoadSettingsAsync();
            await harness.Workspace.SaveSettingsAsync(saved with { OnlineEnabled = true });
            var readsBeforeRestart = harness.Vault.ReadCount;
            await harness.ReopenAsync();
            view = harness.View!;
            TestAssert.True(!view.OnlineEnabled && !harness.Workspace!.IsOnline, "A saved online flag cannot bypass offline startup.");
            TestAssert.Equal(0, harness.Transport.RequestCount, "Restoring account choices cannot send an automatic login before online activation.");
            TestAssert.Equal(readsBeforeRestart, harness.Vault.ReadCount, "Restoring account labels must not retrieve a secret for the editor.");
            SelectEndpoint(view, "a");
            TestAssert.Equal("Alice", view.LoginUsername, "The selected remembered account survives a restart.");
            TestAssert.True(view.RememberPassword && view.AutoLogin && view.LoginPassword.Length == 0,
                "Remember and automatic flags survive restart without displaying the saved password.");
            SelectEndpoint(view, "b");
            TestAssert.True(view.SelectedSavedAccount?.AccountId is null && view.SavedAccounts.Any(item => item.DisplayName == "Bob"),
                "Guest survives restart independently of an endpoint's remembered accounts.");
            SelectEndpoint(view, "a");
            view.OnlineEnabled = true;
            await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(4));
            TestAssert.Equal("Alice", string.Join(',', harness.Transport.Usernames),
                "Only the selected automatic account may log in; another endpoint's Guest selection must be respected.");
            TestAssert.True(harness.Workspace.GetEndpointSessions().Single(item => item.EndpointId == "a").IsLoggedIn,
                "The automatic UI workflow must establish the selected endpoint session.");
            TestAssert.True(!harness.Workspace.GetEndpointSessions().Single(item => item.EndpointId == "b").IsLoggedIn,
                "The independent Guest endpoint must remain Guest.");
            TestAssert.Equal(string.Empty, view.LoginPassword, "Automatic authentication never places the saved secret in the password box.");
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
            TestAssert.True(!json.Contains("Generated-Alice-fixture", StringComparison.Ordinal)
                && !json.Contains("Generated-Bob-fixture", StringComparison.Ordinal), "The settings document must remain free of saved passwords.");
        }

        /// <summary>Uses a saved password for an explicit login without retrieving it into the presentation fields.</summary>
        /// <param name="directory">The isolated local-data location.</param>
        /// <returns>A task representing manual saved-password authentication.</returns>
        private static async Task ValidateRememberedManualLoginAsync(string directory)
        {
            await using var harness = new Harness(directory);
            await harness.OpenAsync();
            await SaveAccountAsync(harness.View!, "Manual account", "Generated-manual-fixture", true, false);
            await harness.ReopenAsync();
            var view = harness.View!;
            view.OnlineEnabled = true;
            await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(4));
            TestAssert.Equal(0, harness.Transport.RequestCount, "Remembering a password alone cannot enable automatic login.");
            TestAssert.True(view.HasRememberedPassword && view.LoginPassword.Length == 0 && view.LoginCommand.CanExecute(null),
                "A selected saved password permits an explicit login without populating the password box.");
            var reads = harness.Vault.ReadCount;
            await ExecuteAsync(view.LoginCommand, view);
            TestAssert.Equal("Manual account", harness.Transport.Usernames.Single(), "Explicit login must use the selected saved account.");
            TestAssert.True(harness.Vault.ReadCount > reads, "Explicit saved login retrieves its protected password only when requested.");
            var expectedDigest = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("Generated-manual-fixture")));
            TestAssert.True(harness.Transport.LastPasswordDigest == expectedDigest, "The remembered secret must reach only the expected password-digest wire field.");
            TestAssert.Equal(string.Empty, view.LoginPassword, "Manual saved authentication leaves no password in the presentation field.");
        }

        /// <summary>Clears old private snapshots after authentication even when remembering the new account fails.</summary>
        /// <param name="directory">The isolated local-data location.</param>
        /// <returns>A task representing authentication followed by a simulated protected-store failure.</returns>
        private static async Task ValidateVaultFailureAfterAccountChangeAsync(string directory)
        {
            await using var harness = new Harness(directory);
            harness.Transport.ProvideCollections = true;
            await harness.OpenAsync();
            var view = harness.View!;
            await SaveAccountAsync(view, "Account A", "Generated-private-owner-fixture", true, false);
            view.OnlineEnabled = true;
            await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(4));
            await ExecuteAsync(view.LoginCommand, view);
            await ExecuteAsync(view.RefreshCloudPlaylistsCommand, view);
            var cloud = view.Playlists.Single(item => item.IsCloud);
            var retainedIds = cloud.TrackIds.ToArray();
            await harness.Workspace!.SavePlaylistAsync(new MusicPlaylist
            {
                Id = "local-retained",
                Name = "Retained local list",
                TrackIds = retainedIds
            });
            await ExecuteAsync(view.RecommendationsCommand, view);
            TestAssert.True(view.IsRecommendationsView && view.Tracks.Count > 0 && view.Playlists.Any(item => item.IsCloud),
                "The previous account must have both private cloud and feed snapshots before the account change.");

            await ExecuteAsync(view.NewAccountCommand, view);
            view.LoginUsername = "Account B";
            view.LoginPassword = "Generated-new-owner-fixture";
            view.RememberPassword = true;
            harness.Vault.FailWrites = true;
            await ExecuteAsync(view.LoginCommand, view, false);
            TestAssert.Equal("Account B", harness.Workspace.GetEndpointSessions().Single(item => item.EndpointId == "a").Username,
                "Remembering a password failure cannot hide which account actually authenticated.");
            TestAssert.True(view.SelectedEndpointProfile is { IsLoggedIn: true, Username: "Account B" }
                && view.SelectedAccountLabel == "Account B", "The visible runtime session must refresh after the protected-store failure.");
            TestAssert.True(view.Playlists.All(item => !item.IsCloud) && view.Tracks.All(item => item.Title != "Account A music"),
                "The previous account's cloud and recommendation snapshots cannot survive a successful account switch.");
            TestAssert.True(view.Playlists.Single(item => item.Id == "local-retained").TrackIds.SequenceEqual(retainedIds),
                "Clearing account-private snapshots must retain ordinary local playlists and their membership.");
            TestAssert.True(view.Status.Contains("Simulated protected-store write failure", StringComparison.Ordinal),
                "The protected-store save error remains visible after the runtime session is refreshed.");
            TestAssert.Equal(string.Empty, view.LoginPassword, "A failed remember operation still clears the transient password input.");
        }

        /// <summary>Preserves the latest endpoint choice and draft when account labels refresh after asynchronous settings I/O.</summary>
        /// <param name="directory">The isolated local-data location.</param>
        /// <returns>A task representing the delayed-settings draft regression.</returns>
        private static async Task ValidateDraftDuringAutomaticRefreshAsync(string directory)
        {
            await using var harness = new Harness(directory);
            await harness.OpenAsync();
            var view = harness.View!;
            await SaveAccountAsync(view, "Automatic account", "Generated-draft-owner-fixture", true, true);
            harness.Transport.BlockLogin = true;
            view.OnlineEnabled = true;
            await harness.Transport.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            harness.Settings.BlockNextLoad();
            harness.Transport.ReleaseLogin.TrySetResult();
            await harness.Settings.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            SelectEndpoint(view, "b");
            await ExecuteAsync(view.NewAccountCommand, view);
            view.EndpointName = "Unsaved source name";
            view.EndpointUrl = "https://unsaved.example.invalid/api/";
            view.LoginUsername = "Unsaved account";
            view.LoginPassword = "Generated-unsaved-draft-fixture";
            view.AutoLogin = true;
            harness.Settings.ReleaseLoad.TrySetResult();
            await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(4));
            TestAssert.Equal("b", view.SelectedEndpointProfile?.Configuration.Id, "Completing an automatic login cannot roll back a newer endpoint selection.");
            TestAssert.Equal("Unsaved source name", view.EndpointName, "A label refresh retains the latest unsaved source name.");
            TestAssert.Equal("https://unsaved.example.invalid/api/", view.EndpointUrl, "A label refresh retains the latest unsaved API address.");
            TestAssert.Equal("Unsaved account", view.LoginUsername, "A label refresh retains the latest account draft.");
            TestAssert.True(view.LoginPassword == "Generated-unsaved-draft-fixture" && view.RememberPassword && view.AutoLogin,
                "A label refresh cannot discard or silently persist newly entered password options.");
            var settings = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
            TestAssert.True(!settings.Contains("Generated-unsaved-draft-fixture", StringComparison.Ordinal), "An editor draft cannot leak into settings during a label refresh.");
        }

        /// <summary>Waits for canceled automatic requests before releasing the workspace during view shutdown.</summary>
        /// <param name="directory">The isolated local-data location.</param>
        /// <returns>A task representing view-owned automatic-login cancellation.</returns>
        private static async Task ValidateAutomaticShutdownAsync(string directory)
        {
            await using var harness = new Harness(directory);
            await harness.OpenAsync();
            var view = harness.View!;
            await SaveAccountAsync(view, "Closing account", "Generated-closing-fixture", true, true);
            harness.Transport.BlockLogin = true;
            view.OnlineEnabled = true;
            await harness.Transport.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var automatic = view.AutomaticLogin;
            await harness.CloseAsync().WaitAsync(TimeSpan.FromSeconds(4));
            await automatic.WaitAsync(TimeSpan.FromSeconds(4));
            await harness.Transport.LoginCanceled.Task.WaitAsync(TimeSpan.FromSeconds(4));
            TestAssert.Equal(1, harness.Transport.RequestCount, "Shutdown must cancel the in-flight batch without starting another login.");
        }

        /// <summary>Saves an account through the actual view commands.</summary>
        /// <param name="view">The view model under test.</param>
        /// <param name="username">The synthetic account name.</param>
        /// <param name="password">The synthetic password, never included in assertion output.</param>
        /// <param name="remember">Whether to retain the generated password.</param>
        /// <param name="automatic">Whether this account should automatically authenticate after online activation.</param>
        /// <returns>A task representing the completed local UI operation.</returns>
        private static Task SaveAccountAsync(MainViewModel view, string username, string password, bool remember, bool automatic)
        {
            view.LoginUsername = username;
            view.LoginPassword = password;
            view.RememberPassword = remember;
            view.AutoLogin = automatic;
            return ExecuteAsync(view.SaveAccountCommand, view);
        }

        /// <summary>Selects a saved source slot without issuing an account operation.</summary>
        /// <param name="view">The view model.</param>
        /// <param name="endpointId">The saved endpoint identity.</param>
        private static void SelectEndpoint(MainViewModel view, string endpointId)
        {
            view.SelectedEndpointProfile = view.EndpointProfiles.Single(item => item.Configuration.Id == endpointId);
        }

        /// <summary>Executes an enabled actual command and observes its completed local operation.</summary>
        /// <param name="command">The command to execute.</param>
        /// <param name="view">The view model exposing operation ownership.</param>
        /// <param name="expectSuccess">Whether the operation must complete without a reported error.</param>
        /// <returns>A task representing command completion.</returns>
        private static async Task ExecuteAsync(AsyncCommand command, MainViewModel view, bool expectSuccess = true)
        {
            TestAssert.True(command.CanExecute(null), "The requested account editor operation must be available.");
            command.Execute(null);
            var elapsed = Stopwatch.StartNew();
            while (view.IsBusy)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(4))
                {
                    throw new InvalidOperationException("The account editor operation did not complete.");
                }

                await Task.Delay(5);
            }

            if (expectSuccess)
            {
                TestAssert.True(!view.Status.StartsWith("操作未完成", StringComparison.Ordinal), "The account editor operation must succeed.");
            }
        }

        /// <summary>Owns real temporary persistence, synthetic credentials and one isolated API transport.</summary>
        private sealed class Harness : IAsyncDisposable
        {
            /// <summary>Stores the isolated application-data location.</summary>
            private readonly string _directory;
            /// <summary>Owns the synthetic HTTP transport used by the workspace.</summary>
            private readonly HttpClient _client;

            /// <summary>Creates idle test dependencies without accessing any real account or credential vault.</summary>
            /// <param name="directory">The isolated application-data location.</param>
            public Harness(string directory)
            {
                _directory = directory;
                Settings = new GateSettingsStore(Path.Combine(directory, "settings.json"));
                _client = new HttpClient(Transport, false);
            }

            /// <summary>Gets generated protected-vault replacements shared across simulated restarts.</summary>
            public FakeCredentialStore Vault { get; } = new();
            /// <summary>Gets the synthetic login-only transport.</summary>
            public LoginTransport Transport { get; } = new();
            /// <summary>Gets the real settings adapter with an optional controlled load delay.</summary>
            public GateSettingsStore Settings { get; }
            /// <summary>Gets the current real application workspace.</summary>
            public MusicWorkspace? Workspace { get; private set; }
            /// <summary>Gets the current shared view model.</summary>
            public MainViewModel? View { get; private set; }

            /// <summary>Opens a real workspace and restores non-secret choices without network access.</summary>
            /// <returns>A task representing the restored editor.</returns>
            public async Task OpenAsync()
            {
                Workspace = new MusicWorkspace(_directory, _client, _client, Settings, Vault);
                var settings = await Workspace.LoadSettingsAsync();
                if (settings.Endpoints.Count == 0)
                {
                    await Workspace.SaveSettingsAsync(settings with
                    {
                        ActiveEndpointId = "a",
                        Endpoints =
                        [
                            new EndpointConfiguration { Id = "a", Name = "Source A", BaseUri = new Uri("https://example.invalid/api/") },
                            new EndpointConfiguration { Id = "b", Name = "Source B", BaseUri = new Uri("https://example.invalid/api/") }
                        ]
                    });
                }

                View = new MainViewModel(Workspace, new PlaybackService(new UnavailablePlaybackBackend()), action => action());
                await View.InitializeAsync();
            }

            /// <summary>Restarts the application composition while retaining settings and the synthetic system vault.</summary>
            /// <returns>A task representing completed editor restoration.</returns>
            public async Task ReopenAsync()
            {
                await CloseAsync();
                await OpenAsync();
            }

            /// <summary>Cancels and drains the view before disposing its owning workspace.</summary>
            /// <returns>A task representing complete view shutdown.</returns>
            public async Task CloseAsync()
            {
                if (View is not null)
                {
                    await View.DisposeAsync();
                    View = null;
                }

                Workspace?.Dispose();
                Workspace = null;
            }

            /// <inheritdoc />
            public async ValueTask DisposeAsync()
            {
                Settings.ReleaseLoad.TrySetResult();
                Transport.ReleaseLogin.TrySetResult();
                await CloseAsync();
                _client.Dispose();
                Transport.Dispose();
            }
        }

        /// <summary>Replaces the protected system vault with isolated in-memory test secrets.</summary>
        private sealed class FakeCredentialStore : ICredentialStore
        {
            /// <summary>Contains only generated synthetic credentials.</summary>
            private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);
            /// <summary>Counts explicit secret retrievals.</summary>
            private int _readCount;
            /// <summary>Gets the number of protected-store reads requested by the application.</summary>
            public int ReadCount => Volatile.Read(ref _readCount);
            /// <summary>Gets or sets whether writes simulate an unavailable or locked protected store.</summary>
            public bool FailWrites { get; set; }

            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _readCount);
                return Task.FromResult(_secrets.GetValueOrDefault(key));
            }

            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FailWrites)
                {
                    throw new InvalidOperationException("Simulated protected-store write failure.");
                }

                _secrets[key] = secret;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _secrets.TryRemove(key, out _);
                return Task.CompletedTask;
            }
        }

        /// <summary>Provides real atomic settings with a controlled asynchronous load for editor-race validation.</summary>
        private sealed class GateSettingsStore : ISettingsStore
        {
            /// <summary>Reads and writes the real temporary settings file.</summary>
            private readonly JsonSettingsStore _inner;
            /// <summary>Arms one delayed load.</summary>
            private int _blockNext;

            /// <summary>Creates a store for the isolated file.</summary>
            /// <param name="path">The temporary settings path.</param>
            public GateSettingsStore(string path)
            {
                _inner = new JsonSettingsStore(path);
            }

            /// <summary>Signals that the controlled asynchronous load has begun.</summary>
            public TaskCompletionSource LoadStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Allows the controlled load to complete.</summary>
            public TaskCompletionSource ReleaseLoad { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Arms exactly one later load after the initial editor has opened.</summary>
            public void BlockNextLoad()
            {
                LoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ReleaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _blockNext, 1);
            }

            /// <inheritdoc />
            public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _blockNext, 0) != 0)
                {
                    LoadStarted.TrySetResult();
                    await ReleaseLoad.Task.WaitAsync(cancellationToken);
                }

                return await _inner.LoadAsync(cancellationToken);
            }

            /// <inheritdoc />
            public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                return _inner.SaveAsync(settings, cancellationToken);
            }
        }

        /// <summary>Accepts only synthetic explicit login requests and can block until canceled or released.</summary>
        private sealed class LoginTransport : HttpMessageHandler
        {
            /// <summary>Counts every attempted HTTP request atomically.</summary>
            private int _requestCount;
            /// <summary>Gets the generated usernames sent to the isolated endpoint.</summary>
            public ConcurrentQueue<string> Usernames { get; } = new();
            /// <summary>Gets the total HTTP request count.</summary>
            public int RequestCount => Volatile.Read(ref _requestCount);
            /// <summary>Gets the last generated password digest for protocol verification without printing it.</summary>
            public string? LastPasswordDigest { get; private set; }
            /// <summary>Gets or sets whether login waits for the controlled lifecycle transition.</summary>
            public bool BlockLogin { get; set; }
            /// <summary>Gets or sets whether this fixture also provides account-private collection and feed reads.</summary>
            public bool ProvideCollections { get; set; }
            /// <summary>Signals the beginning of a controlled request.</summary>
            public TaskCompletionSource LoginStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Allows a controlled request to finish.</summary>
            public TaskCompletionSource ReleaseLogin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Signals observed cancellation of a controlled request.</summary>
            public TaskCompletionSource LoginCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requestCount);
                if (request.Method == HttpMethod.Get)
                {
                    var path = request.RequestUri!.AbsolutePath;
                    request.Headers.TryGetValues("Cookie", out var cookies);
                    var secondAccount = cookies?.Any(value => Uri.UnescapeDataString(value).Contains("session=Account B", StringComparison.Ordinal)) == true;
                    var hash = secondAccount ? "BBBBBBBBBBBBBBBBBBBBBB==" : "AAAAAAAAAAAAAAAAAAAAAA==";
                    var id = secondAccount ? "private-song-b" : "private-song";
                    var title = secondAccount ? "Account B music" : "Account A music";
                    var json = path.EndsWith("account/favorite/collection/list", StringComparison.Ordinal)
                        ? ProvideCollections ? JsonSerializer.Serialize(new[] { new { id = "private-cloud", name = "Account cloud", songHashs = new[] { hash } } }) : "[]"
                        : path.EndsWith("maichart/list", StringComparison.Ordinal) || path.EndsWith("recommend/feed", StringComparison.Ordinal)
                            ? ProvideCollections ? JsonSerializer.Serialize(new[] { new { id, title, hash } }) : "[]"
                            : path.EndsWith("/info", StringComparison.Ordinal) && !path.Contains("/maichart/", StringComparison.Ordinal)
                                ? "{\"id\":\"account-ui-server\",\"name\":\"Account UI server\"}" : null;
                    return new HttpResponseMessage(json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                    {
                        Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json")
                    };
                }

                if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath.EndsWith("account/login", StringComparison.Ordinal) != true
                    || request.Content is not MultipartFormDataContent content)
                {
                    throw new InvalidOperationException("The account UI attempted an unexpected request.");
                }

                var username = string.Empty;
                foreach (var part in content)
                {
                    switch (part.Headers.ContentDisposition?.Name?.Trim('"'))
                    {
                        case "username":
                            username = await part.ReadAsStringAsync(cancellationToken);
                            break;
                        case "password":
                            LastPasswordDigest = await part.ReadAsStringAsync(cancellationToken);
                            break;
                    }
                }

                Usernames.Enqueue(username);
                if (BlockLogin)
                {
                    LoginStarted.TrySetResult();
                    try
                    {
                        await ReleaseLogin.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        LoginCanceled.TrySetResult();
                        throw;
                    }
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                response.Headers.TryAddWithoutValidation("Set-Cookie", "session=" + Uri.EscapeDataString(username) + "; Path=/; Secure");
                return response;
            }
        }
    }
}
