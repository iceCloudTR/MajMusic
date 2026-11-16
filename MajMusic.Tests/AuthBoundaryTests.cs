#nullable enable

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Network;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Network;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Separates optional profile denial from real session expiry and authenticated media availability.</summary>
    internal static class AuthBoundaryTests
    {
        /// <summary>Runs only generated in-memory HTTP responses without reading stored credentials or contacting a source.</summary>
        /// <returns>A task representing status, cookie-boundary and mirror-read lifetime assertions.</returns>
        public static async Task RunAsync()
        {
            await ValidateProfileDenialAsync();
            await ValidateSessionExpiryAsync();
            await ValidateMirrorProfileFailureAsync();
            await ValidateBrowserChallengesAsync();
            await ValidateCanonicalIdentityCacheAsync();
            await ValidateLateCanonicalIdentityAsync();
            await ValidateUserMessagesAsync();
            await ValidateAutomaticLoginMessagesAsync();
        }

        /// <summary>Checks that a forbidden profile does not clear a successful login or its same-origin media authorization.</summary>
        /// <returns>A task representing independently available account and media reads.</returns>
        private static async Task ValidateProfileDenialAsync()
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var transport = new AuthTransport { ProfileStatus = HttpStatusCode.Forbidden };
            using var http = new HttpClient(transport, false);
            using var provider = new MajNetClient(Endpoint(), policy, http);
            await provider.LoginAsync("GeneratedUser", "Generated fixture password");
            await ExpectStatusAsync(() => provider.GetCurrentUsernameAsync(), HttpStatusCode.Forbidden);
            TestAssert.True(provider.IsLoggedIn, "A profile-specific HTTP 403 must not silently discard the successful source login.");
            var favorites = await provider.GetCloudPlaylistsAsync();
            TestAssert.Equal(1, favorites.Count, "The account's collection read can remain available independently of profile permission.");
            using var mediaTransport = new AuthTransport();
            using var media = provider.CreateMediaClient(mediaTransport);
            using var image = await media.GetAsync(new Uri(Endpoint().BaseUri, "maichart/generated/image"));
            TestAssert.True(image.StatusCode == HttpStatusCode.OK && mediaTransport.Observations.Single().HasCookie,
                "A profile denial cannot remove the same-origin session cookie used by an otherwise available cover route.");
            TestAssert.True(transport.Observations.Any(item => item.Path == "/api/account/profile" && item.Status == HttpStatusCode.Forbidden)
                && transport.Observations.Any(item => item.Path == "/api/account/favorite/collection/list" && item.Status == HttpStatusCode.OK && item.HasCookie),
                "The generated diagnostic must retain exact route/status evidence without storing cookie values or profile data.");
        }

        /// <summary>Checks that an unauthorized response invalidates only this endpoint's session.</summary>
        /// <returns>A task representing real expiry semantics and stopped account requests.</returns>
        private static async Task ValidateSessionExpiryAsync()
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var transport = new AuthTransport { ProfileStatus = HttpStatusCode.Unauthorized };
            using var http = new HttpClient(transport, false);
            using var provider = new MajNetClient(Endpoint(), policy, http);
            await provider.LoginAsync("GeneratedUser", "Generated fixture password");
            await ExpectStatusAsync(() => provider.GetCurrentUsernameAsync(), HttpStatusCode.Unauthorized);
            TestAssert.True(!provider.IsLoggedIn, "HTTP 401 must invalidate the source session rather than preserve an expired authenticated state.");
            var requests = transport.Observations.Count;
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => provider.GetCloudPlaylistsAsync());
            TestAssert.Equal(requests, transport.Observations.Count, "An expired account cannot issue another authenticated collection request.");
            using var mediaTransport = new AuthTransport();
            using var media = provider.CreateMediaClient(mediaTransport);
            using var image = await media.GetAsync(new Uri(Endpoint().BaseUri, "maichart/generated/image"));
            TestAssert.True(!mediaTransport.Observations.Single().HasCookie, "Expired session cookies cannot leak into later media reads.");
        }

        /// <summary>Keeps a successful cloud response usable when optional canonical annotation fails while retaining real expiry semantics.</summary>
        /// <returns>A task representing isolated workspace failure attribution and recovery.</returns>
        private static async Task ValidateMirrorProfileFailureAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-auth-boundary-" + Guid.NewGuid().ToString("N")));
            try
            {
                using var transport = new AuthTransport { ProfileStatus = HttpStatusCode.Forbidden };
                using var http = new HttpClient(transport, false);
                using var workspace = new MusicWorkspace(root, http, http, credentialStore: new EmptyCredentials());
                await workspace.LoadSettingsAsync();
                var endpoint = Endpoint() with
                {
                    Accounts = [new SavedEndpointAccount { Id = "generated-account", Username = "GeneratedUser", UseCloudCollections = true }],
                    SelectedAccountId = "generated-account"
                };
                await workspace.SaveSettingsAsync(new AppSettings { OnlineEnabled = true, Endpoints = [endpoint], ActiveEndpointId = endpoint.Id });
                await workspace.LoginAsync(endpoint, "GeneratedUser", "Generated fixture password");
                await File.WriteAllBytesAsync(Path.Combine(root, "cloud-collections.db"), Array.Empty<byte>());
                var available = await workspace.GetCloudPlaylistsAsync(endpoint);
                TestAssert.True(transport.Observations.Any(item => item.Path == "/api/account/favorite/collection/list" && item.Status == HttpStatusCode.OK)
                    && transport.Observations.Last().Path == "/api/account/profile" && workspace.GetEndpointSessions().Single().IsLoggedIn
                    && available.Count == 1 && available[0].LocalMirrorId is null,
                    "A denied optional profile cannot discard successful cloud data, clear the session, or guess a local ownership binding.");
                transport.ProfileStatus = HttpStatusCode.Unauthorized;
                await ExpectStatusAsync(() => workspace.GetCloudPlaylistsAsync(endpoint), HttpStatusCode.Unauthorized);
                TestAssert.True(!workspace.GetEndpointSessions().Single().IsLoggedIn,
                    "Optional annotation must still propagate a true HTTP 401 and invalidate its expired session.");
                transport.ProfileStatus = HttpStatusCode.OK;
                await workspace.LoginAsync(endpoint, "GeneratedUser", "Generated fixture password");
                var recovered = await workspace.GetCloudPlaylistsAsync(endpoint).WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(recovered.Count == 1 && recovered[0].LocalMirrorId is null,
                    "A failed optional-identity read must release the shared scope gate and cannot invent an unapplied local/cloud binding.");
            }
            finally
            {
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-auth-boundary-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated authentication fixture.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Classifies only explicit middleware challenges on API and media routes and releases their response bodies.</summary>
        /// <returns>A task representing challenge detection independently of account permission failures.</returns>
        private static async Task ValidateBrowserChallengesAsync()
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var transport = new AuthTransport();
            using var http = new HttpClient(transport, false);
            using var provider = new MajNetClient(Endpoint(), policy, http);
            await provider.LoginAsync("GeneratedUser", "Generated fixture password");
            transport.ChallengePaths.Add("/api/account/profile");
            await TestAssert.ThrowsAsync<BrowserVerificationRequiredException>(() => provider.GetCurrentUsernameAsync());
            TestAssert.True(provider.IsLoggedIn && transport.LastContent?.WasDisposed == true,
                "A middleware challenge must have its own safe exception, release its response and retain the source's authenticated session.");
            transport.ChallengePaths.Clear();
            TestAssert.Equal(1, (await provider.GetCloudPlaylistsAsync()).Count, "A browser challenge cannot be mistaken for an account logout.");
            using var mediaTransport = new AuthTransport();
            mediaTransport.ChallengePaths.Add("/api/maichart/generated/image");
            using var media = provider.CreateMediaClient(mediaTransport);
            await TestAssert.ThrowsAsync<BrowserVerificationRequiredException>(() => media.GetAsync(new Uri(Endpoint().BaseUri, "maichart/generated/image")));
            TestAssert.True(provider.IsLoggedIn && mediaTransport.LastContent?.WasDisposed == true,
                "A challenged cover must never be returned as image data or clear account cookies.");
            mediaTransport.ChallengeStatus = HttpStatusCode.OK;
            await TestAssert.ThrowsAsync<BrowserVerificationRequiredException>(() => media.GetAsync(new Uri(Endpoint().BaseUri, "maichart/generated/image")));
            mediaTransport.ChallengePaths.Clear();
            mediaTransport.ImageStatus = HttpStatusCode.Forbidden;
            using var ordinary = await media.GetAsync(new Uri(Endpoint().BaseUri, "maichart/generated/image"));
            TestAssert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode,
                "A plain source HTTP 403 without the explicit challenge marker cannot be labeled as browser verification.");
        }

        /// <summary>Reuses only a verified canonical principal in one live session and invalidates it on explicit relogin.</summary>
        /// <returns>A task representing same-session reuse, input separation and offline cancellation.</returns>
        private static async Task ValidateCanonicalIdentityCacheAsync()
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var transport = new AuthTransport { CanonicalUsername = "VerifiedCanonical" };
            using var http = new HttpClient(transport, false);
            using var provider = new MajNetClient(Endpoint(), policy, http);
            await provider.LoginAsync("alias@example.invalid", "Generated fixture password");
            TestAssert.Equal("VerifiedCanonical", await provider.GetCurrentUsernameAsync(), "Canonical scope identity must come from the source rather than login input.");
            transport.ProfileStatus = HttpStatusCode.Forbidden;
            TestAssert.Equal("VerifiedCanonical", await provider.GetCurrentUsernameAsync(), "The same verified session must not refetch an optional profile for each cloud-list refresh.");
            TestAssert.Equal(1, transport.Observations.Count(item => item.Path == "/api/account/profile"), "Canonical reuse cannot add repeated profile requests.");
            policy.SetEnabled(false);
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => provider.GetCurrentUsernameAsync());
            policy.SetEnabled(true);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => provider.GetCurrentUsernameAsync(canceled.Token));
            await provider.LoginAsync("different-alias@example.invalid", "Generated fixture password");
            await ExpectStatusAsync(() => provider.GetCurrentUsernameAsync(), HttpStatusCode.Forbidden);
            transport.ProfileStatus = HttpStatusCode.OK;
            transport.CanonicalUsername = "VerifiedOther";
            TestAssert.Equal("VerifiedOther", await provider.GetCurrentUsernameAsync(), "A new source session cannot reuse the previous principal or the newly entered alias as identity proof.");
        }

        /// <summary>Rejects an old successful profile response after another login has committed a new source session.</summary>
        /// <returns>A task representing canonical identity generation isolation.</returns>
        private static async Task ValidateLateCanonicalIdentityAsync()
        {
            using var policy = new NetworkAccessPolicy();
            policy.SetEnabled(true);
            using var transport = new AuthTransport { CanonicalUsername = "OldPrincipal", HoldNextProfile = true };
            using var http = new HttpClient(transport, false);
            using var provider = new MajNetClient(Endpoint(), policy, http);
            await provider.LoginAsync("old-alias@example.invalid", "Generated fixture password");
            var old = provider.GetCurrentUsernameAsync();
            try
            {
                await transport.ProfileStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                transport.CanonicalUsername = "NewPrincipal";
                await provider.LoginAsync("new-alias@example.invalid", "Generated fixture password");
                transport.ReleaseProfile.TrySetResult();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => old);
                TestAssert.Equal("NewPrincipal", await provider.GetCurrentUsernameAsync(), "A late previous-session profile cannot poison the current canonical cache.");
            }
            finally
            {
                transport.ReleaseProfile.TrySetResult();
            }
        }

        /// <summary>Reports short source failures without labeling a middleware denial as invalid credentials.</summary>
        /// <returns>A task representing the shared user-facing error distinction.</returns>
        private static async Task ValidateUserMessagesAsync()
        {
            await using var view = new MainViewModel(new LibraryExperienceTests.ExperienceWorkspace(), new PlaybackService(new UnavailablePlaybackBackend()));
            view.ReportError(new BrowserVerificationRequiredException(HttpStatusCode.Forbidden));
            TestAssert.Equal("接入点需要浏览器验证", view.Status, "Explicit middleware challenges need a precise source-level message.");
            view.ReportError(new HttpRequestException("Generated denied request.", null, HttpStatusCode.Forbidden));
            TestAssert.Equal("接入点拒绝访问", view.Status, "A generic HTTP 403 cannot assert that the account or its password is invalid.");
        }

        /// <summary>Retains precise source failures through remembered-login results and the existing single-account status.</summary>
        /// <returns>A task representing generated-vault challenge, ordinary denial and unchanged generic failure semantics.</returns>
        private static async Task ValidateAutomaticLoginMessagesAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-auto-auth-" + Guid.NewGuid().ToString("N")));
            try
            {
                foreach (var scenario in new[]
                {
                    (Challenge: true, Status: HttpStatusCode.Forbidden, Result: "接入点需要浏览器验证", Presentation: "接入点需要浏览器验证"),
                    (Challenge: false, Status: HttpStatusCode.Forbidden, Result: "接入点拒绝访问", Presentation: "接入点拒绝访问"),
                    (Challenge: false, Status: HttpStatusCode.InternalServerError, Result: "自动登录未完成，请重新登录", Presentation: "1 个账号未能自动登录")
                })
                {
                    using var transport = new AuthTransport { LoginStatus = scenario.Status, LoginCode = 60002 };
                    if (scenario.Challenge)
                    {
                        transport.ChallengePaths.Add("/api/account/login");
                    }

                    using var http = new HttpClient(transport, false);
                    var vault = new GeneratedCredentials();
                    using var workspace = new MusicWorkspace(Path.Combine(root, Guid.NewGuid().ToString("N")), http, http, credentialStore: vault);
                    await workspace.LoadSettingsAsync();
                    var endpoint = Endpoint();
                    await workspace.SaveSettingsAsync(new AppSettings { OnlineEnabled = true, Endpoints = [endpoint], ActiveEndpointId = endpoint.Id });
                    await workspace.SaveAccountAsync(endpoint, "GeneratedUser", "Generated fixture password", true, true);
                    var result = (await workspace.AutoLoginAsync()).Single();
                    TestAssert.True(!result.Succeeded && result.Error == scenario.Result && vault.Reads == 1,
                        "A generated remembered login must preserve the source failure classification without exposing credentials or a server body.");
                    await using var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
                    await view.InitializeAsync();
                    await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(3));
                    TestAssert.Equal(scenario.Presentation, view.Status,
                        "A single challenged or denied remembered login must retain its safe source message; other failures keep the existing count.");
                }
            }
            finally
            {
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-auto-auth-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated automatic-login fixture.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Checks one generated source exception's exact HTTP status.</summary>
        /// <param name="action">The isolated request to execute.</param>
        /// <param name="expected">The generated response code that must remain attributable.</param>
        /// <returns>A task representing the status assertion.</returns>
        private static async Task ExpectStatusAsync(Func<Task> action, HttpStatusCode expected)
        {
            try
            {
                await action();
                throw new InvalidOperationException("The generated denied request unexpectedly succeeded.");
            }
            catch (HttpRequestException exception)
            {
                TestAssert.Equal(expected, exception.StatusCode!.Value, "Source errors must preserve their actual HTTP status.");
            }
        }

        /// <summary>Creates the nonroutable source used only by the injected handler.</summary>
        /// <returns>The explicit synthetic HTTPS endpoint.</returns>
        private static EndpointConfiguration Endpoint()
        {
            return new EndpointConfiguration { Id = "generated-source", Name = "Generated source", BaseUri = new Uri("https://auth-boundary.example.invalid/api/") };
        }

        /// <summary>Retains only safe request metadata, never authorization values or profile bodies.</summary>
        /// <param name="Path">The generated API path without a query.</param>
        /// <param name="Status">The generated response status.</param>
        /// <param name="HasCookie">Whether an authorization cookie was present.</param>
        private sealed record Observation(string Path, HttpStatusCode Status, bool HasCookie);

        /// <summary>Returns explicit profile, account and image responses without a real network transport.</summary>
        private sealed class AuthTransport : HttpMessageHandler
        {
            /// <summary>Gets or sets the status of the generated canonical-profile route.</summary>
            public HttpStatusCode ProfileStatus { get; set; } = HttpStatusCode.OK;
            /// <summary>Gets or sets an ordinary generated login response status.</summary>
            public HttpStatusCode LoginStatus { get; set; } = HttpStatusCode.OK;
            /// <summary>Gets or sets the source business code used by generated login responses.</summary>
            public int LoginCode { get; set; }
            /// <summary>Gets or sets the status of generated media reads without an explicit challenge marker.</summary>
            public HttpStatusCode ImageStatus { get; set; } = HttpStatusCode.OK;
            /// <summary>Gets or sets the source principal returned independently of entered login aliases.</summary>
            public string CanonicalUsername { get; set; } = "GeneratedUser";
            /// <summary>Gets explicit middleware-challenged routes.</summary>
            public HashSet<string> ChallengePaths { get; } = new(StringComparer.Ordinal);
            /// <summary>Gets or sets the actual HTTP status of a declared middleware challenge.</summary>
            public HttpStatusCode ChallengeStatus { get; set; } = HttpStatusCode.Forbidden;
            /// <summary>Gets or sets whether the next profile captures its body before waiting for release.</summary>
            public bool HoldNextProfile { get; set; }
            /// <summary>Signals capture of the deliberately delayed profile response.</summary>
            public TaskCompletionSource ProfileStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the deliberately delayed profile response.</summary>
            public TaskCompletionSource ReleaseProfile { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Gets the last generated content so response disposal can be verified.</summary>
            public TrackedContent? LastContent { get; private set; }
            /// <summary>Gets safe observations made by the synthetic source.</summary>
            public List<Observation> Observations { get; } = new();

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = request.RequestUri!.AbsolutePath;
                var challenge = ChallengePaths.Contains(path);
                var status = challenge ? ChallengeStatus : path == "/api/account/login" ? LoginStatus : path == "/api/account/profile" ? ProfileStatus : path.EndsWith("/image", StringComparison.Ordinal) ? ImageStatus : HttpStatusCode.OK;
                Observations.Add(new Observation(path, status, request.Headers.Contains("Cookie")));
                var body = path switch
                {
                    "/api/account/login" => System.Text.Json.JsonSerializer.Serialize(new { code = LoginCode }),
                    "/api/info" => """{"id":"generated-server","name":"Generated source"}""",
                    "/api/account/profile" => System.Text.Json.JsonSerializer.Serialize(new { username = CanonicalUsername }),
                    "/api/account/favorite/collection/list" => """[{"id":"generated-collection","name":"Generated favorite","songHashs":[]}]""",
                    "/api/maichart/generated/image" => string.Empty,
                    _ => throw new InvalidOperationException("Unexpected generated authentication route.")
                };
                if (path == "/api/account/profile" && HoldNextProfile)
                {
                    HoldNextProfile = false;
                    ProfileStarted.TrySetResult();
                    await ReleaseProfile.Task;
                }

                LastContent = new TrackedContent(challenge ? "<html><title>Just a moment...</title></html>" : body);
                var response = new HttpResponseMessage(status) { Content = LastContent };
                if (path == "/api/account/login")
                {
                    response.Headers.TryAddWithoutValidation("Set-Cookie", "generated-session=fixture; Path=/; Secure");
                }

                if (path.EndsWith("/image", StringComparison.Ordinal))
                {
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                }

                if (challenge)
                {
                    response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                }

                return response;
            }
        }

        /// <summary>Records whether a generated response was released after challenge classification.</summary>
        private sealed class TrackedContent : StringContent
        {
            /// <summary>Creates non-sensitive synthetic response content.</summary>
            /// <param name="body">The generated body, never read from a user source.</param>
            public TrackedContent(string body) : base(body, Encoding.UTF8, "application/json")
            {
            }
            /// <summary>Gets whether the response owner released this content.</summary>
            public bool WasDisposed { get; private set; }
            /// <inheritdoc />
            protected override void Dispose(bool disposing)
            {
                WasDisposed = true;
                base.Dispose(disposing);
            }
        }

        /// <summary>Rejects any accidental attempt to read real or generated credentials in this explicit-login fixture.</summary>
        private sealed class EmptyCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("This fixture cannot read a credential vault.");
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("This fixture cannot save credentials.");
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("This fixture cannot delete credentials.");
            }
        }

        /// <summary>Stores only a generated fixture password in memory, never reading the operating-system vault.</summary>
        private sealed class GeneratedCredentials : ICredentialStore
        {
            /// <summary>Retains fixture values only for the current test scope.</summary>
            private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
            /// <summary>Gets the generated remembered-password lookup count.</summary>
            public int Reads { get; private set; }
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Reads++;
                return Task.FromResult(_values.GetValueOrDefault(key));
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
    }
}
