#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Network;

namespace MajMusic.Tests
{
    /// <summary>Checks the authenticated cloud-collection protocol against isolated in-memory HTTP responses.</summary>
    internal static class CollectionNetworkTests
    {
        /// <summary>Runs exact route, body, session, validation and cancellation regressions without real service requests.</summary>
        /// <returns>A task representing the collection API checks.</returns>
        public static async Task RunAsync()
        {
            await ValidateGuestAndOfflineAsync();
            await ValidateCollectionContractAsync();
            await ValidateMalformedResponsesAsync();
            await ValidateFailuresAndCancellationAsync();
        }

        /// <summary>Rejects every new account-only operation before sending HTTP while Guest or offline.</summary>
        /// <returns>A task representing account and network permission checks.</returns>
        private static async Task ValidateGuestAndOfflineAsync()
        {
            using var fixture = new Fixture();
            var calls = Operations(fixture.Client);
            foreach (var call in calls)
            {
                await TestAssert.ThrowsAsync<InvalidOperationException>(call);
            }

            TestAssert.Equal(0, fixture.Transport.Requests.Count, "Guest collection actions must send no HTTP or profile request.");
            await fixture.LoginAsync();
            TestAssert.Equal(1, fixture.Transport.Requests.Count, "Existing login must not start profile discovery or collection synchronization implicitly.");
            fixture.Policy.SetEnabled(false);
            foreach (var call in calls)
            {
                await TestAssert.ThrowsAsync<InvalidOperationException>(call);
            }

            TestAssert.Equal(1, fixture.Transport.Requests.Count, "Offline collection actions must send no HTTP even with retained session cookies.");
        }

        /// <summary>Checks the public chart-ID protocol, canonical username lookup and source-isolated session cookies.</summary>
        /// <returns>A task representing collection reads and mutations.</returns>
        private static async Task ValidateCollectionContractAsync()
        {
            using var fixture = new Fixture();
            await fixture.LoginAsync();
            TestAssert.Equal("Canonical_User", await fixture.Client.GetCurrentUsernameAsync(),
                "Cloud ownership must use the profile username rather than the email supplied at login.");
            var page = await fixture.Client.GetCollectionPageAsync("Canonical_User", 2, 17);
            TestAssert.True(page.Single() is { Id: "collection-one", CreatedBy: "Canonical_User", Description: "marker:owned", Count: 2, Visibility: null },
                "Owned collection pages must retain server identities, creator and exact marker text.");
            var request = fixture.Transport.Requests.Last();
            TestAssert.True(request.Method == HttpMethod.Get && request.Address.PathAndQuery == "/prefix/api/collection/list?page=2&pageSize=17&createdBy=Canonical_User",
                "Owned pagination must preserve the endpoint prefix and documented query parameters.");
            await fixture.Client.GetCollectionPageAsync("name&keyword=injected");
            TestAssert.True(fixture.Transport.Requests.Last().Address.Query.Contains("createdBy=name%26keyword%3Dinjected", StringComparison.Ordinal)
                && !fixture.Transport.Requests.Last().Address.Query.Contains("&keyword=", StringComparison.Ordinal),
                "Creator input must be encoded as one query value.");
            var collection = await fixture.Client.GetCollectionSongListAsync("collection-one");
            TestAssert.True(collection is { Visibility: 0 } && collection.Items.SequenceEqual([Fixture.FirstChart, Fixture.SecondChart]),
                "Songlist details must retain ordered chart IDs independently of portable hashes.");
            TestAssert.True(await fixture.Client.GetCollectionSongListAsync("missing") is null, "A missing collection must be represented explicitly.");

            await fixture.Client.CreateCollectionAsync("  Liked 音乐  ", "marker:owned");
            request = fixture.Transport.Requests.Last();
            AssertMutation(request, HttpMethod.Post, "/prefix/api/collection/create");
            using (var body = JsonDocument.Parse(request.Body!))
            {
                TestAssert.True(body.RootElement.GetProperty("name").GetString() == "Liked 音乐"
                    && body.RootElement.GetProperty("description").GetString() == "marker:owned"
                    && body.RootElement.GetProperty("visibility").GetInt32() == 0,
                    "New synchronized collections must default to private and retain their unique description marker.");
                TestAssert.Equal(3, body.RootElement.EnumerateObject().Count(), "Creation must not invent client IDs, chart hashes or undocumented fields.");
            }

            await fixture.Client.UpdateCollectionAsync("collection-one", "Ordered", "marker:owned", [Fixture.SecondChart, Fixture.FirstChart]);
            request = fixture.Transport.Requests.Last();
            AssertMutation(request, HttpMethod.Put, "/prefix/api/collection/collection-one/modify");
            using (var body = JsonDocument.Parse(request.Body!))
            {
                TestAssert.True(body.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetString())
                    .SequenceEqual([Fixture.SecondChart, Fixture.FirstChart]), "PUT modify must send the complete ordered chart-ID list.");
                TestAssert.True(!body.RootElement.TryGetProperty("songHashs", out _) && !body.RootElement.TryGetProperty("visibility", out _),
                    "Replacing collection contents must neither use removed hash mutations nor change existing visibility.");
            }

            await fixture.Client.UpdateCollectionAsync("collection-one", "Empty", null, []);
            using (var body = JsonDocument.Parse(fixture.Transport.Requests.Last().Body!))
            {
                TestAssert.True(body.RootElement.GetProperty("items").GetArrayLength() == 0
                    && !body.RootElement.TryGetProperty("description", out _), "An empty list clears contents while a null description leaves existing metadata intact.");
            }

            foreach (var favorite in new[] { true, false })
            {
                await fixture.Client.SetCollectionFavoriteAsync("collection-one", favorite);
                request = fixture.Transport.Requests.Last();
                AssertMutation(request, favorite ? HttpMethod.Post : HttpMethod.Delete, "/prefix/api/account/favorite/collection/diff");
                using var body = JsonDocument.Parse(request.Body!);
                TestAssert.True(body.RootElement.ValueKind == JsonValueKind.Array
                    && body.RootElement.GetArrayLength() == 1 && body.RootElement[0].GetString() == "collection-one",
                    "Collection subscriptions must send an array of collection IDs rather than chart IDs.");
            }

            await fixture.Client.DeleteCollectionAsync("collection-one");
            request = fixture.Transport.Requests.Last();
            TestAssert.True(request.Method == HttpMethod.Post && request.Address.AbsolutePath == "/prefix/api/collection/collection-one/destroy" && request.Body is null,
                "Owned collection deletion must use POST destroy without a request body.");
            TestAssert.True(fixture.Transport.Requests.Skip(1).All(item => item.Cookie == "session=fixture" && item.Address.Host == "collection.example.test"),
                "Every collection operation must retain only the configured source's session and origin.");
            TestAssert.True(fixture.Transport.Requests.All(item => !item.Address.AbsolutePath.Contains("/collection/collection-one/diff", StringComparison.Ordinal)),
                "The removed per-collection diff endpoint must never be used.");

            var beforeInvalid = fixture.Transport.Requests.Count;
            await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.GetCollectionPageAsync("Canonical_User", -1));
            await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.GetCollectionPageAsync("Canonical_User", pageSize: 101));
            await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.CreateCollectionAsync("Name", "marker", 2));
            await TestAssert.ThrowsAsync<ArgumentException>(() => fixture.Client.CreateCollectionAsync(" ", "marker"));
            await TestAssert.ThrowsAsync<ArgumentException>(() => fixture.Client.UpdateCollectionAsync("collection-one", "Name", null, [""]));
            await TestAssert.ThrowsAsync<ArgumentException>(() => fixture.Client.DeleteCollectionAsync(".."));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => fixture.Client.SetCollectionFavoriteAsync("collection-one", true, canceled.Token));
            TestAssert.Equal(beforeInvalid, fixture.Transport.Requests.Count, "Invalid or canceled collection operations must stop before constructing HTTP work.");
        }

        /// <summary>Rejects incomplete ownership and obsolete hash-only songlist shapes instead of guessing mutable identities.</summary>
        /// <returns>A task representing response-validation checks.</returns>
        private static async Task ValidateMalformedResponsesAsync()
        {
            using var fixture = new Fixture();
            await fixture.LoginAsync();
            foreach (var json in new[] { "{}", "{\"username\":null}", "[]" })
            {
                fixture.Transport.Override = (_, _) => Task.FromResult(Json(json));
                await TestAssert.ThrowsAsync<JsonException>(() => fixture.Client.GetCurrentUsernameAsync());
            }

            fixture.Transport.Override = (_, _) => Task.FromResult(Json("[{\"id\":\"collection-one\",\"name\":\"Name\"}]"));
            await TestAssert.ThrowsAsync<JsonException>(() => fixture.Client.GetCollectionPageAsync("Canonical_User"));
            fixture.Transport.Override = (_, _) => Task.FromResult(Json("{\"id\":\"collection-one\",\"name\":\"Name\",\"createdBy\":\"Canonical_User\",\"songHashs\":[\"legacy-hash\"]}"));
            await TestAssert.ThrowsAsync<JsonException>(() => fixture.Client.GetCollectionSongListAsync("collection-one"));
            fixture.Transport.Override = (_, _) => Task.FromResult(Json("{\"id\":\"collection-one\",\"name\":\"Name\",\"createdBy\":\"Canonical_User\",\"items\":[null]}"));
            await TestAssert.ThrowsAsync<JsonException>(() => fixture.Client.GetCollectionSongListAsync("collection-one"));
        }

        /// <summary>Checks rejected writes, redirect refusal, expired sessions and cancellation after requests have started.</summary>
        /// <returns>A task representing mutation failure and lifetime checks.</returns>
        private static async Task ValidateFailuresAndCancellationAsync()
        {
            using var fixture = new Fixture();
            await fixture.LoginAsync();
            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.MethodNotAllowed, HttpStatusCode.Found })
            {
                var before = fixture.Transport.Requests.Count;
                fixture.Transport.Override = (_, _) => Task.FromResult(new HttpResponseMessage(status)
                {
                    Headers = { Location = new Uri("https://other.example.test/collection/create") },
                    Content = new StringContent("private server details must not enter a public error")
                });
                await TestAssert.ThrowsAsync<HttpRequestException>(() => fixture.Client.CreateCollectionAsync("Name", "marker"));
                TestAssert.Equal(before + 1, fixture.Transport.Requests.Count, "A rejected or redirected write must not be retried against another origin or legacy mutation route.");
            }

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Transport.Override = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Json("{}");
            };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = fixture.Client.UpdateCollectionAsync("collection-one", "Name", "marker", [], cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => pending);

            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = fixture.Client.CreateCollectionAsync("Name", "marker");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Policy.SetEnabled(false);
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => pending);
            fixture.Policy.SetEnabled(true);
            fixture.Transport.Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            await TestAssert.ThrowsAsync<HttpRequestException>(() => fixture.Client.DeleteCollectionAsync("collection-one"));
            TestAssert.True(!fixture.Client.IsLoggedIn, "An expired collection session must clear the existing authenticated state.");
            var beforeGuest = fixture.Transport.Requests.Count;
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.SetCollectionFavoriteAsync("collection-one", true));
            TestAssert.Equal(beforeGuest, fixture.Transport.Requests.Count, "An expired session cannot send a subsequent collection write as Guest.");
        }

        /// <summary>Creates every account-only call without eagerly starting it.</summary>
        /// <param name="client">The client whose session is under test.</param>
        /// <returns>The independently invokable account-only operations.</returns>
        private static Func<Task>[] Operations(MajNetClient client)
        {
            return [
                () => client.GetCurrentUsernameAsync(),
                () => client.GetCollectionPageAsync("Canonical_User"),
                () => client.GetCollectionSongListAsync("collection-one"),
                () => client.CreateCollectionAsync("Name", "marker"),
                () => client.UpdateCollectionAsync("collection-one", "Name", "marker", []),
                () => client.SetCollectionFavoriteAsync("collection-one", true),
                () => client.DeleteCollectionAsync("collection-one")
            ];
        }

        /// <summary>Checks the exact authenticated JSON mutation envelope.</summary>
        /// <param name="request">The captured request.</param>
        /// <param name="method">The required HTTP method.</param>
        /// <param name="path">The required prefixed route.</param>
        private static void AssertMutation(Request request, HttpMethod method, string path)
        {
            TestAssert.True(request.Method == method && request.Address.AbsolutePath == path && request.MediaType == "application/json",
                "Collection mutations must use the documented route, method and UTF-8 JSON body.");
        }

        /// <summary>Creates a buffered successful fake JSON response.</summary>
        /// <param name="body">The response payload.</param>
        /// <returns>The response owned by the client.</returns>
        private static HttpResponseMessage Json(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        /// <summary>Retains a sanitized request snapshot from a synthetic endpoint.</summary>
        /// <param name="Method">The actual HTTP method.</param>
        /// <param name="Address">The requested fake address.</param>
        /// <param name="Body">The synthetic request body, when present.</param>
        /// <param name="Cookie">The fake session header.</param>
        /// <param name="MediaType">The declared content media type.</param>
        private sealed record Request(HttpMethod Method, Uri Address, string? Body, string Cookie, string? MediaType);

        /// <summary>Owns one entirely fake endpoint, account session and network permission lifetime.</summary>
        private sealed class Fixture : IDisposable
        {
            /// <summary>Identifies one synthetic chart using the current GUID item protocol.</summary>
            public const string FirstChart = "11111111-1111-4111-8111-111111111111";
            /// <summary>Identifies another synthetic chart without using hashes as item IDs.</summary>
            public const string SecondChart = "22222222-2222-4222-8222-222222222222";
            /// <summary>Owns the in-memory HTTP facade.</summary>
            private readonly HttpClient _http;

            /// <summary>Creates an offline initially unauthenticated endpoint.</summary>
            public Fixture()
            {
                _http = new HttpClient(Transport);
                Client = new MajNetClient(new EndpointConfiguration
                {
                    Id = "collection-source",
                    Name = "Collection fixture",
                    BaseUri = new Uri("https://collection.example.test/prefix/api/")
                }, Policy, _http);
            }

            /// <summary>Gets explicit online permission for this test.</summary>
            public NetworkAccessPolicy Policy { get; } = new();
            /// <summary>Gets the fake request dispatcher.</summary>
            public CollectionTransport Transport { get; } = new();
            /// <summary>Gets the real adapter being exercised.</summary>
            public MajNetClient Client { get; }

            /// <summary>Creates the fake authenticated session with an email login input.</summary>
            /// <returns>A task representing that explicit synthetic login.</returns>
            public Task LoginAsync()
            {
                Policy.SetEnabled(true);
                return Client.LoginAsync("email@example.test", "generated-test-only-password");
            }

            /// <inheritdoc />
            public void Dispose()
            {
                Client.Dispose();
                _http.Dispose();
                Policy.Dispose();
            }
        }

        /// <summary>Captures requests and serves documented collection responses without accessing a socket.</summary>
        private sealed class CollectionTransport : HttpMessageHandler
        {
            /// <summary>Gets all synthetic requests in their invocation order.</summary>
            public ConcurrentQueue<Request> Requests { get; } = new();
            /// <summary>Gets or sets a failure response or controlled pending request.</summary>
            public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override { get; set; }

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                Requests.Enqueue(new Request(request.Method, request.RequestUri!, body,
                    request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : string.Empty,
                    request.Content?.Headers.ContentType?.MediaType));
                if (Override is { } replacement)
                {
                    return await replacement(request, cancellationToken);
                }

                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/account/login", StringComparison.Ordinal))
                {
                    var response = Json("{}");
                    response.Headers.Add("Set-Cookie", "session=fixture; Path=/prefix/api/; Secure; HttpOnly");
                    return response;
                }

                if (path.EndsWith("/account/profile", StringComparison.Ordinal))
                {
                    return Json("{\"username\":\"Canonical_User\"}");
                }

                if (path.EndsWith("/collection/list", StringComparison.Ordinal))
                {
                    return Json("[{\"id\":\"collection-one\",\"name\":\"Owned\",\"createdBy\":\"Canonical_User\",\"description\":\"marker:owned\",\"count\":2}]");
                }

                if (path.EndsWith("/collection/missing/songlist", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (path.EndsWith("/collection/collection-one/songlist", StringComparison.Ordinal))
                {
                    return Json(JsonSerializer.Serialize(new
                    {
                        id = "collection-one",
                        name = "Owned",
                        createdBy = "Canonical_User",
                        description = "marker:owned",
                        visibility = 0,
                        items = new[] { Fixture.FirstChart, Fixture.SecondChart },
                        count = 2
                    }));
                }

                if (path.EndsWith("/collection/create", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("undocumented creation response") };
                }

                if (request.Method is { } method && (method == HttpMethod.Put || method == HttpMethod.Post || method == HttpMethod.Delete))
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                throw new InvalidOperationException("Unexpected collection test request.");
            }
        }
    }
}
