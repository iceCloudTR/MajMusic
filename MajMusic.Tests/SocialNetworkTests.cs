#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Network;
using MajMusic.Infrastructure.Application;

namespace MajMusic.Tests
{
    /// <summary>Checks real source interaction routes and independent account boundaries using generated HTTP responses.</summary>
    internal static class SocialNetworkTests
    {
        /// <summary>Runs public reads, scoped writes, verified deletion and stale-session cancellation without sockets or real accounts.</summary>
        /// <returns>A task representing all social network regressions.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-social-network-" + Guid.NewGuid().ToString("N")));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await ValidatePublicAndOfflineAsync(Path.Combine(root, "public"), timeout.Token);
                await ValidateScopedActionsAsync(Path.Combine(root, "accounts"), timeout.Token);
                await ValidateRejectedResponsesAsync(Path.Combine(root, "responses"), timeout.Token);
                await ValidateRetiredSessionAsync(Path.Combine(root, "retired"), timeout.Token);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-social-network-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Social fixture cleanup must stay in its generated temporary root.");
                }
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Allows finite Guest reads while denying every Guest or offline write before HTTP.</summary>
        /// <param name="root">The synthetic private workspace directory.</param>
        /// <param name="token">Cancels the isolated operations.</param>
        /// <returns>A task representing public and offline boundaries.</returns>
        private static async Task ValidatePublicAndOfflineAsync(string root, CancellationToken token)
        {
            using var fixture = new Fixture(root);
            await fixture.InitializeAsync(false, token);
            var track = Fixture.Track("guest");
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => fixture.Workspace.GetTrackSocialAsync(track, token));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => fixture.Workspace.GetTrackCommentsAsync(track, cancellationToken: token));
            await AssertWritesRejectedAsync(fixture.Workspace, track, token);
            TestAssert.Equal(0, fixture.Transport.Requests.Count, "Offline social reads and writes must perform zero HTTP, profile or credential lookup.");

            await fixture.Workspace.UpdateOnlineEnabledAsync(true, token);
            var state = await fixture.Workspace.GetTrackSocialAsync(track, token);
            var page = await fixture.Workspace.GetTrackCommentsAsync(track, 2, "new", token);
            TestAssert.True(state is { ChartId: Fixture.Chart, PlayCount: 123, Score: 7, Upvotes: 9, Downvotes: 2, CommentCount: 21, MyVote: 0, CanInteract: false },
                "Guest sees the source's real interaction totals without acquiring account-only capabilities.");
            TestAssert.True(page is { Page: 2, PageSize: 20, Total: 41, Sort: "new", HasMore: true, CanInteract: false }
                && page.Items.Single().Replies.Single().Body == "Generated reply" && !page.Items.Single().IsOwn,
                "Public comment pages preserve source pagination and nested replies without inferring Guest ownership.");
            TestAssert.True(fixture.Transport.Requests.Last().Address.PathAndQuery == "/prefix/api/maichart/" + Fixture.Chart + "/comments?sort=new&page=2"
                && fixture.Transport.Requests.All(request => request.Cookie.Length == 0 && !request.Address.AbsolutePath.EndsWith("/account/profile", StringComparison.Ordinal)),
                "Comment reads use one-based server pages and do not fetch profiles or borrow another slot's cookies for Guest.");
            var count = fixture.Transport.Requests.Count;
            await AssertWritesRejectedAsync(fixture.Workspace, track, token);
            TestAssert.Equal(count, fixture.Transport.Requests.Count, "Guest mutation refusal cannot first read a parent, profile or comment thread.");

            await fixture.LoginAsync("alice", "Alice", token);
            await fixture.Workspace.UpdateOnlineEnabledAsync(false, token);
            count = fixture.Transport.Requests.Count;
            await AssertWritesRejectedAsync(fixture.Workspace, Fixture.Track("alice"), token);
            TestAssert.Equal(count, fixture.Transport.Requests.Count, "Retained authenticated sessions cannot send social writes after online mode is disabled.");
        }

        /// <summary>Checks exact JSON writes, source slots, canonical authors and chart-scoped reply and deletion verification.</summary>
        /// <param name="root">The synthetic private workspace directory.</param>
        /// <param name="token">Cancels source operations.</param>
        /// <returns>A task representing independently authenticated actions.</returns>
        private static async Task ValidateScopedActionsAsync(string root, CancellationToken token)
        {
            using var fixture = new Fixture(root);
            await fixture.InitializeAsync(true, token);
            await fixture.LoginAsync("alice", "Alice", token);
            await fixture.LoginAsync("bob", "Bob", token);
            var alice = Fixture.Track("alice");
            var bob = Fixture.Track("bob");
            TestAssert.Equal(1, (await fixture.Workspace.GetTrackSocialAsync(alice, token)).MyVote, "Alice's chart uses Alice's own authenticated slot.");
            TestAssert.Equal(-1, (await fixture.Workspace.GetTrackSocialAsync(bob, token)).MyVote, "Bob's equal-origin chart retains Bob's independently authenticated vote.");
            TestAssert.Equal(0, (await fixture.Workspace.GetTrackSocialAsync(Fixture.Track("guest"), token)).MyVote, "Equal-origin Guest never inherits either authenticated vote.");
            foreach (var value in new[] { 1, 0, -1 })
            {
                var result = await fixture.Workspace.SetTrackVoteAsync(alice, value, token);
                AssertVote(fixture.Transport.Requests.Last(), "/prefix/api/maichart/" + Fixture.Chart + "/vote", "Alice", value);
                TestAssert.Equal(value, result.MyVote, "A chart vote must publish the source-confirmed value including withdrawal.");
            }

            const string body = "  Generated 评论 😀\nsecond line  ";
            var posted = await fixture.Workspace.PostTrackCommentAsync(bob, body, cancellationToken: token);
            var request = fixture.Transport.Requests.Last();
            using (var json = JsonDocument.Parse(request.Body!))
            {
                TestAssert.True(request.Method == HttpMethod.Post && request.Address.AbsolutePath == "/prefix/api/maichart/" + Fixture.Chart + "/comments"
                    && request.Cookie == "account=Bob" && request.MediaType == "application/json"
                    && json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.GetProperty("body").GetString() == body,
                    "A new comment must send the captured slot's cookie and exact Unicode text as JSON, without a guessed parent field.");
            }
            TestAssert.True(posted is { Id: "created-comment", Author: "Bob" } && posted.Body == body, "Posting returns the server-created comment rather than a synthetic optimistic item.");

            await fixture.Workspace.PostTrackCommentAsync(alice, "Generated answer", "own-comment", token);
            request = fixture.Transport.Requests.Last();
            using (var json = JsonDocument.Parse(request.Body!))
            {
                TestAssert.True(json.RootElement.EnumerateObject().Count() == 2 && json.RootElement.GetProperty("parentId").GetString() == "own-comment",
                    "A reply must include the explicitly captured verified parent identity.");
            }
            var comments = await fixture.Workspace.GetTrackCommentsAsync(alice, cancellationToken: token);
            TestAssert.True(comments.Items.Single().CanDelete && !comments.Items.Single().Replies.Single().CanDelete,
                "Only comments owned by the canonical profile identity can offer author deletion, including nested replies.");
            await fixture.Workspace.SetTrackCommentVoteAsync(bob, "own-comment", -1, token);
            AssertVote(fixture.Transport.Requests.Last(), "/prefix/api/comment/own-comment/vote", "Bob", -1);
            var deleted = await fixture.Workspace.DeleteTrackCommentAsync(alice, "own-comment", token);
            request = fixture.Transport.Requests.Last();
            TestAssert.True(deleted is { Succeeded: true, Purged: false } && request.Method == HttpMethod.Delete
                && request.Address.AbsolutePath == "/prefix/api/comment/own-comment" && request.Cookie == "account=Alice" && request.Body is null,
                "Confirmed author deletion preserves the server's placeholder result and uses DELETE without a request body.");

            var mutations = fixture.Transport.Requests.Count(IsSocialMutation);
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => fixture.Workspace.DeleteTrackCommentAsync(bob, "own-comment", token));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => fixture.Workspace.DeleteTrackCommentAsync(alice, "foreign-comment", token));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => fixture.Workspace.PostTrackCommentAsync(alice, "Wrong chart", "foreign-comment", token));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => fixture.Workspace.SetTrackCommentVoteAsync(alice, "foreign-comment", 1, token));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => fixture.Workspace.SetTrackVoteAsync(alice with { SourceId = "replaced-server" }, 1, token));
            TestAssert.Equal(mutations, fixture.Transport.Requests.Count(IsSocialMutation), "Wrong-author, wrong-chart and changed-server targets must be rejected before their destructive or voting request.");
            TestAssert.True(fixture.Transport.Requests.All(item => !item.Address.AbsolutePath.Contains("favorite", StringComparison.Ordinal)),
                "Source votes and comments cannot silently change local or cloud collection membership.");
        }

        /// <summary>Rejects invalid targets and malformed responses, and never retries ambiguous mutations or browser challenges.</summary>
        /// <param name="root">The synthetic private workspace directory.</param>
        /// <param name="token">Cancels controlled HTTP operations.</param>
        /// <returns>A task representing validation and failure propagation.</returns>
        private static async Task ValidateRejectedResponsesAsync(string root, CancellationToken token)
        {
            using var fixture = new Fixture(root);
            await fixture.InitializeAsync(true, token);
            await fixture.LoginAsync("alice", "Alice", token);
            var track = Fixture.Track("alice");
            await fixture.Workspace.GetTrackSocialAsync(track, token);
            var before = fixture.Transport.Requests.Count;
            await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Workspace.SetTrackVoteAsync(track, 2, token));
            await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Workspace.GetTrackCommentsAsync(track, 0, cancellationToken: token));
            await TestAssert.ThrowsAsync<ArgumentException>(() => fixture.Workspace.GetTrackCommentsAsync(track, sort: "best&injected=true", cancellationToken: token));
            await TestAssert.ThrowsAsync<ArgumentException>(() => fixture.Workspace.PostTrackCommentAsync(track, "   ", cancellationToken: token));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => fixture.Workspace.SetTrackVoteAsync(track, 1, cancelled.Token));
            }
            TestAssert.Equal(before, fixture.Transport.Requests.Count, "Invalid input and pre-cancelled actions must not reach the transport.");

            fixture.Transport.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Address.AbsolutePath.EndsWith("/interactsum", StringComparison.Ordinal)
                ? Transport.Json(new { chartId = Fixture.OtherChart, plays = 1, score = 0, upvotes = 0, downvotes = 0, comments = 0, myVote = 0 }) : null);
            await TestAssert.ThrowsAsync<JsonException>(() => fixture.Workspace.GetTrackSocialAsync(track, token));
            fixture.Transport.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Address.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
                ? Transport.Json(new { page = 2, pageSize = 20, total = 0, sort = "best", items = Array.Empty<object>() }) : null);
            await TestAssert.ThrowsAsync<JsonException>(() => fixture.Workspace.GetTrackCommentsAsync(track, cancellationToken: token));
            fixture.Transport.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(IsSocialMutation(request)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") } : null);
            before = fixture.Transport.Requests.Count(IsSocialMutation);
            await TestAssert.ThrowsAsync<HttpRequestException>(() => fixture.Workspace.PostTrackCommentAsync(track, "Generated single attempt", cancellationToken: token));
            TestAssert.Equal(before + 1, fixture.Transport.Requests.Count(IsSocialMutation), "A failed comment write must not be retried automatically or create duplicate submissions.");

            fixture.Transport.Override = (request, _) =>
            {
                if (!request.Address.AbsolutePath.EndsWith("/interactsum", StringComparison.Ordinal))
                {
                    return Task.FromResult<HttpResponseMessage?>(null);
                }
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<!doctype html><title>Just a moment...</title>", Encoding.UTF8, "text/html") };
                response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
                return Task.FromResult<HttpResponseMessage?>(response);
            };
            await TestAssert.ThrowsAsync<BrowserVerificationRequiredException>(() => fixture.Workspace.GetTrackSocialAsync(track, token));
            TestAssert.True(fixture.Workspace.GetEndpointSessions().Single(session => session.EndpointId == "alice").IsLoggedIn,
                "A middleware browser challenge must not falsely expire the account session.");
            fixture.Transport.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Address.AbsolutePath.EndsWith("/interactsum", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") } : null);
            await TestAssert.ThrowsAsync<HttpRequestException>(() => fixture.Workspace.GetTrackSocialAsync(track, token));
            TestAssert.True(!fixture.Workspace.GetEndpointSessions().Single(session => session.EndpointId == "alice").IsLoggedIn,
                "An actual interaction HTTP 401 must expire only its authenticated source session.");
        }

        /// <summary>Retires an in-flight comment verification when its account changes before a delete can be sent.</summary>
        /// <param name="root">The synthetic private workspace directory.</param>
        /// <param name="token">Cancels the operation lifetime.</param>
        /// <returns>A task representing old-account cancellation and peer isolation.</returns>
        private static async Task ValidateRetiredSessionAsync(string root, CancellationToken token)
        {
            using var fixture = new Fixture(root);
            await fixture.InitializeAsync(true, token);
            await fixture.LoginAsync("alice", "Alice", token);
            await fixture.LoginAsync("bob", "Bob", token);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Transport.Override = async (request, lifetime) =>
            {
                if (request.Method == HttpMethod.Get && request.Address.AbsolutePath.EndsWith("/comment/own-comment", StringComparison.Ordinal)
                    && request.Cookie == "account=Alice")
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(lifetime);
                }
                return null;
            };
            var pending = fixture.Workspace.DeleteTrackCommentAsync(Fixture.Track("alice"), "own-comment", token);
            try
            {
                await started.Task.WaitAsync(token);
                await fixture.Workspace.UseGuestAsync(Fixture.Endpoint("alice"), token);
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => pending);
                await fixture.Workspace.SetTrackVoteAsync(Fixture.Track("bob"), 1, token);
                AssertVote(fixture.Transport.Requests.Last(), "/prefix/api/maichart/" + Fixture.Chart + "/vote", "Bob", 1);
                TestAssert.True(fixture.Transport.Requests.All(request => request.Method != HttpMethod.Delete),
                    "A retired account cannot finish verification and delete under a new Guest or another account's session.");
            }
            finally
            {
                release.TrySetResult();
            }
        }

        /// <summary>Checks all account-only entry points against a currently read-only source.</summary>
        /// <param name="workspace">The real source coordinator.</param>
        /// <param name="track">The captured target source entry.</param>
        /// <param name="token">Cancels each attempt.</param>
        /// <returns>A task representing all rejected mutations.</returns>
        private static async Task AssertWritesRejectedAsync(MusicWorkspace workspace, MusicTrack track, CancellationToken token)
        {
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.SetTrackVoteAsync(track, 1, token));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.PostTrackCommentAsync(track, "Generated text", cancellationToken: token));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.DeleteTrackCommentAsync(track, "own-comment", token));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.SetTrackCommentVoteAsync(track, "own-comment", 1, token));
        }

        /// <summary>Checks the exact single-field JSON vote protocol and the owning account cookie.</summary>
        /// <param name="request">The actual captured HTTP request.</param>
        /// <param name="path">The expected source route.</param>
        /// <param name="account">The fixture's authenticated owner.</param>
        /// <param name="value">The requested positive, withdrawn or negative vote.</param>
        private static void AssertVote(Request request, string path, string account, int value)
        {
            using var body = JsonDocument.Parse(request.Body!);
            TestAssert.True(request.Method == HttpMethod.Put && request.Address.AbsolutePath == path
                && request.Cookie == "account=" + account && request.MediaType == "application/json"
                && body.RootElement.EnumerateObject().Count() == 1 && body.RootElement.GetProperty("value").GetInt32() == value,
                "Voting must PUT one source-defined JSON value using only the captured target's endpoint session.");
        }

        /// <summary>Separates actual social mutations from public reads and synthetic login calls.</summary>
        /// <param name="request">The recorded HTTP operation.</param>
        /// <returns>Whether this operation can modify a chart or comment.</returns>
        internal static bool IsSocialMutation(Request request) => request.Method != HttpMethod.Get
            && (request.Address.AbsolutePath.Contains("/maichart/", StringComparison.Ordinal) || request.Address.AbsolutePath.Contains("/comment/", StringComparison.Ordinal));

        /// <summary>Captures only generated source operations for exact protocol assertions.</summary>
        /// <param name="Method">The HTTP operation.</param>
        /// <param name="Address">The generated absolute source address.</param>
        /// <param name="Body">The generated JSON body, excluding login form data.</param>
        /// <param name="Cookie">The synthetic account marker.</param>
        /// <param name="MediaType">The request content media type.</param>
        internal sealed record Request(HttpMethod Method, Uri Address, string? Body, string Cookie, string? MediaType);

        /// <summary>Owns a real workspace with three equal-origin isolated sessions and a fake credential boundary.</summary>
        internal sealed class Fixture : IDisposable
        {
            /// <summary>Identifies the shared generated chart.</summary>
            public const string Chart = "11111111-1111-4111-8111-111111111111";
            /// <summary>Identifies a different chart for destructive-operation guards.</summary>
            public const string OtherChart = "22222222-2222-4222-8222-222222222222";
            /// <summary>Owns the in-memory HTTP facade.</summary>
            private readonly HttpClient _client;
            /// <summary>Creates the isolated real workspace.</summary>
            /// <param name="root">The generated private configuration path.</param>
            public Fixture(string root)
            {
                _client = new HttpClient(Transport, false);
                Workspace = new MusicWorkspace(root, _client, _client, credentialStore: new NoCredentials());
            }
            /// <summary>Gets the generated protocol dispatcher.</summary>
            public Transport Transport { get; } = new();
            /// <summary>Gets the production coordinator under test.</summary>
            public MusicWorkspace Workspace { get; }
            /// <summary>Saves three slots without connecting to their generated source.</summary>
            /// <param name="online">Whether explicit reads and writes are permitted.</param>
            /// <param name="token">Cancels configuration I/O.</param>
            /// <returns>A task representing fixture initialization.</returns>
            public async Task InitializeAsync(bool online, CancellationToken token)
            {
                var settings = await Workspace.LoadSettingsAsync(token);
                await Workspace.SaveSettingsAsync(settings with { OnlineEnabled = online, Endpoints = [Endpoint("alice"), Endpoint("bob"), Endpoint("guest")] }, token);
            }
            /// <summary>Authenticates one slot with a generated credential that is never persisted.</summary>
            /// <param name="slot">The explicit source slot.</param>
            /// <param name="username">The generated account marker.</param>
            /// <param name="token">Cancels the synthetic login.</param>
            /// <returns>A task representing that independent login.</returns>
            public Task LoginAsync(string slot, string username, CancellationToken token) => Workspace.LoginAsync(Endpoint(slot), username, "generated-social-fixture-only", token);
            /// <summary>Creates an equal-origin source slot with an independent cookie owner.</summary>
            /// <param name="slot">The configured slot identity.</param>
            /// <returns>The generated source configuration.</returns>
            public static EndpointConfiguration Endpoint(string slot) => new() { Id = slot, Name = "Generated " + slot, ServerId = "social-source", BaseUri = new Uri("https://social.example.test/prefix/api/") };
            /// <summary>Creates a chart captured from one explicit source slot.</summary>
            /// <param name="slot">The owning source slot.</param>
            /// <returns>The generated remotely identified entry.</returns>
            public static MusicTrack Track(string slot) => new() { Id = "social:" + slot + ":" + Chart, RemoteId = Chart, EndpointId = slot, SourceId = "social-source", Title = "Generated social song", Audio = new MediaResource() };
            /// <inheritdoc />
            public void Dispose()
            {
                Workspace.Dispose();
                _client.Dispose();
                Transport.Dispose();
            }
        }

        /// <summary>Serves official interaction shapes without sockets, vault reads or actual service mutations.</summary>
        internal sealed class Transport : HttpMessageHandler
        {
            /// <summary>Retains exact generated HTTP operations.</summary>
            public ConcurrentQueue<Request> Requests { get; } = new();
            /// <summary>Gets or sets a controlled response or cancellable verification barrier.</summary>
            public Func<Request, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : string.Empty;
                var record = new Request(request.Method, request.RequestUri, path.EndsWith("/account/login", StringComparison.Ordinal) ? null : body,
                    cookie, request.Content?.Headers.ContentType?.MediaType);
                Requests.Enqueue(record);
                if (Override is { } replacement && await replacement(record, cancellationToken) is { } overridden)
                {
                    return overridden;
                }
                var account = cookie.Contains("account=Alice", StringComparison.Ordinal) ? "Alice" : cookie.Contains("account=Bob", StringComparison.Ordinal) ? "Bob" : "Guest";
                if (path.EndsWith("/account/login", StringComparison.Ordinal))
                {
                    var response = Json(new { code = 114514 });
                    response.Headers.TryAddWithoutValidation("Set-Cookie", "account=" + (body!.Contains("Alice", StringComparison.Ordinal) ? "Alice" : "Bob") + "; Path=/prefix/api/; Secure; HttpOnly");
                    return response;
                }
                if (path.EndsWith("/info", StringComparison.Ordinal))
                {
                    return Json(new { id = "social-source", name = "Generated social source" });
                }
                if (path.EndsWith("/account/profile", StringComparison.Ordinal))
                {
                    return Json(new { username = account });
                }
                if (path.EndsWith("/interactsum", StringComparison.Ordinal))
                {
                    return Json(new { chartId = Fixture.Chart, plays = 123, score = 7, upvotes = 9, downvotes = 2, comments = 21, myVote = account == "Alice" ? 1 : account == "Bob" ? -1 : 0 });
                }
                if (path.EndsWith("/vote", StringComparison.Ordinal))
                {
                    using var vote = JsonDocument.Parse(body!);
                    return Json(new { score = 8, upvotes = 10, downvotes = 2, myVote = vote.RootElement.GetProperty("value").GetInt32() });
                }
                if (path.EndsWith("/comments", StringComparison.Ordinal))
                {
                    if (request.Method == HttpMethod.Post)
                    {
                        using var submitted = JsonDocument.Parse(body!);
                        return Json(Comment("created-comment", Fixture.Chart, account, submitted.RootElement.GetProperty("body").GetString()!,
                            submitted.RootElement.TryGetProperty("parentId", out var parent) ? parent.GetString() : null));
                    }
                    var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
                    return Json(new { page = int.Parse(query["page"], System.Globalization.CultureInfo.InvariantCulture), pageSize = 20, total = 41, sort = query["sort"],
                        items = new[] { Comment("own-comment", Fixture.Chart, "Alice", "Generated thread", replies: [Comment("reply-comment", Fixture.Chart, "Bob", "Generated reply", "own-comment")]) } });
                }
                if (path.Contains("/comment/", StringComparison.Ordinal))
                {
                    if (request.Method == HttpMethod.Delete)
                    {
                        return Json(new { ok = true, purged = false });
                    }
                    var id = path.Split('/')[^1];
                    return Json(new { item = Comment(id, id == "foreign-comment" ? Fixture.OtherChart : Fixture.Chart, "Alice", "Generated thread"), sort = "best" });
                }
                throw new InvalidOperationException("Unexpected generated social request: " + path);
            }

            /// <summary>Creates a real-shaped finite source comment.</summary>
            /// <param name="id">The synthetic comment identity.</param>
            /// <param name="chart">The owning chart identity.</param>
            /// <param name="author">The canonical author marker.</param>
            /// <param name="body">The generated original text.</param>
            /// <param name="parentId">The optional thread parent.</param>
            /// <param name="replies">The included finite reply snapshot.</param>
            /// <returns>The serializable official response shape.</returns>
            private static object Comment(string id, string chart, string author, string body, string? parentId = null, object[]? replies = null) => new
            {
                id, chartId = chart, author, authorId = "generated-" + author, authorProfile = new { nickname = "Generated " + author, avatarId = "generated-avatar" },
                body, parentId, depth = parentId is null ? 0 : 1, createdAt = "2026-01-02T03:04:05Z", editedAt = (string?)null,
                deleted = false, removed = false, score = 2, myVote = 0, replyCount = replies?.Length ?? 0, replies = replies ?? []
            };
            /// <summary>Creates one owned JSON response with generated content.</summary>
            /// <param name="body">The nonsecret serializable response.</param>
            /// <returns>The fake source response.</returns>
            public static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }

        /// <summary>Fails immediately if synthetic social operations touch an operating-system credential store.</summary>
        private sealed class NoCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Social fixtures do not use a credential vault.");
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Social fixtures do not use a credential vault.");
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Social fixtures do not use a credential vault.");
        }
    }
}
