#nullable enable

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Network;
using MajMusic.Infrastructure.Storage;

namespace MajMusic.Tests
{
    /// <summary>Checks independent configured accounts, online collections and recommendation playback contracts.</summary>
    internal static class OnlineWorkspaceTests
    {
        /// <summary>Runs deterministic account and online-library checks without contacting any server.</summary>
        /// <returns>A task representing all checks.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-online-" + Guid.NewGuid().ToString("N"));
            try
            {
                await VerifyAccountCompositionAsync(root);
                await VerifyLegacyIdentitiesAsync(Path.Combine(root, "legacy"));
                await VerifySourceIdentityChangesAsync(Path.Combine(root, "changed-source"));
                await VerifyLoginCodesAsync();
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var fullRoot = Path.GetFullPath(root);
                var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullRoot.StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(fullRoot).StartsWith("MajMusic-online-", StringComparison.Ordinal) && Directory.Exists(fullRoot))
                {
                    Directory.Delete(fullRoot, true);
                }
            }
        }

        /// <summary>Checks two accounts and Guest at one address, strict cloud membership and cached metadata merges.</summary>
        /// <param name="root">The isolated source and data parent.</param>
        /// <returns>A task representing the checks.</returns>
        private static async Task VerifyAccountCompositionAsync(string root)
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            var maidata = "&title=Local title\n&artist=Local artist\n"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(source, "maidata.txt"), maidata);
            await File.WriteAllBytesAsync(Path.Combine(source, "track.mp3"), "local audio fixture"u8.ToArray());
            var hash = Convert.ToBase64String(MD5.HashData(maidata));
            using var handler = new AccountTransport(hash);
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(Path.Combine(root, "data"), client, client);
            var settings = await workspace.LoadSettingsAsync();
            TestAssert.True(!settings.OnlineEnabled && handler.Requests.Count == 0, "Account composition performs no implicit startup requests");
            var first = Endpoint("alice-slot");
            var second = Endpoint("bob-slot");
            var guest = Endpoint("guest-slot");
            await workspace.SaveSettingsAsync(settings with { OnlineEnabled = true, Endpoints = new[] { first, second, guest } });
            TestAssert.Equal(0, handler.Requests.Count, "Saving multiple source slots does not connect");
            await workspace.LoginAsync(first, "Alice", "password-a");
            await workspace.LoginAsync(second, "Bob", "password-b");
            var sessions = workspace.GetEndpointSessions();
            TestAssert.Equal("Alice", sessions.Single(item => item.EndpointId == first.Id).Username, "The first slot retains its own account");
            TestAssert.Equal("Bob", sessions.Single(item => item.EndpointId == second.Id).Username, "The second slot retains its own account");
            TestAssert.True(!sessions.Single(item => item.EndpointId == guest.Id).IsLoggedIn, "A third slot at the same address stays Guest");
            await workspace.SynchronizeAsync(first);
            TestAssert.Equal("Alice", handler.Requests.Last().Account, "Library sync uses the first slot's session");
            await workspace.SynchronizeAsync(second);
            TestAssert.Equal("Bob", handler.Requests.Last().Account, "Library sync uses the second slot's session");
            await workspace.SynchronizeAsync(guest);
            TestAssert.Equal("Guest", handler.Requests.Last().Account, "Guest sync never borrows a cookie from another slot");
            var local = (await workspace.IndexDirectoryAsync(source)).Tracks.Single();
            var library = await workspace.SearchAsync(null);
            var firstTrack = library.Single(track => track.EndpointId == first.Id);
            var secondTrack = library.Single(track => track.EndpointId == second.Id);
            var guestTrack = library.Single(track => track.EndpointId == guest.Id);
            TestAssert.Equal(3, new[] { firstTrack.Id, secondTrack.Id, guestTrack.Id }.Distinct(StringComparer.Ordinal).Count(),
                "Equal remote IDs at equal server identities still have separate configured-account identities");
            TestAssert.Equal(TimeSpan.FromSeconds(123.5), firstTrack.Duration, "Optional source audio duration is available before selection");
            TestAssert.Equal(192000L, firstTrack.AudioInfo!.BitrateBitsPerSecond, "Optional source bitrate is preserved");
            var cloud = (await workspace.GetCloudPlaylistsAsync(first)).Single();
            TestAssert.True(cloud.IsCloud && cloud.EndpointId == first.Id, "Cloud lists carry their owning account slot");
            TestAssert.True(cloud.TrackIds.SequenceEqual(new[] { firstTrack.Id }), "Cloud hashes never select an equal local hash or another account's song");
            handler.ExtendedCloudFixture = true;
            var extendedCloud = await workspace.GetCloudPlaylistsAsync(first);
            TestAssert.Equal(3, extendedCloud.Select(list => list.Id).Distinct(StringComparer.Ordinal).Count(),
                "Older cloud-list responses without identifiers keep all differently named and same-name lists");
            TestAssert.Equal(1, extendedCloud[0].MajCollection!.Entries.Count(entry => entry.TrackId is null),
                "Unresolved cloud references remain available for list status rather than disappearing");
            var repeatedCloud = await workspace.GetCloudPlaylistsAsync(first);
            TestAssert.True(extendedCloud.Select(list => list.Id).SequenceEqual(repeatedCloud.Select(list => list.Id)),
                "Missing-ID cloud lists have stable identities across the same snapshot");
            handler.ExtendedCloudFixture = false;
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.SavePlaylistAsync(cloud with
            {
                TrackIds = cloud.TrackIds.Append(local.Id).ToArray()
            }));
            TestAssert.Equal(0, (await workspace.GetPlaylistsAsync()).Count, "Cloud snapshots are not persisted as editable local lists");
            await workspace.SavePlaylistAsync(new MusicPlaylist { Id = "mixed", Name = "Mixed", TrackIds = new[] { firstTrack.Id, secondTrack.Id, local.Id } });
            TestAssert.Equal(3, (await workspace.GetPlaylistsAsync()).Single().TrackIds.Count, "Local playlists freely mix account slots and local music");
            var recommended = await workspace.GetRecommendationsAsync(second, 0, 50);
            TestAssert.Equal(1, recommended.Count, "Recommendations return a finite source page");
            TestAssert.True(handler.Requests.Last().Path.Contains("source=play-feed&page=0&pageSize=50", StringComparison.Ordinal),
                "Recommendation pagination uses Maj's actual query contract");
            TestAssert.Equal("Bob", handler.Requests.Last().Account, "Recommendations use the selected account");
            TestAssert.Equal(second.Id, recommended[0].EndpointId, "Recommended entries retain their owning playback session");
            var beforeGuest = handler.Requests.Count;
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.GetRecommendationsAsync(guest));
            TestAssert.Equal(beforeGuest, handler.Requests.Count, "Guest recommendations are rejected before any request");
            var downloadedFirst = await workspace.ResolveAudioAsync(firstTrack, true);
            TestAssert.Equal("Alice", handler.Requests.Last().Account, "Protected audio uses the first account cookie");
            TestAssert.Equal("audio-Alice", await File.ReadAllTextAsync(downloadedFirst), "The first account receives its own resource");
            var downloadedSecond = await workspace.ResolveAudioAsync(secondTrack, false);
            TestAssert.Equal("Bob", handler.Requests.Last().Account, "Protected audio uses the second account cookie");
            TestAssert.Equal("audio-Bob", await File.ReadAllTextAsync(downloadedSecond), "The second account receives its own resource");
            var downloadedGuest = await workspace.ResolveAudioAsync(guestTrack, false);
            TestAssert.Equal("Guest", handler.Requests.Last().Account, "Guest audio does not carry another account's cookie");
            TestAssert.Equal("audio-Guest", await File.ReadAllTextAsync(downloadedGuest), "Guest media remains independent");
            var current = (await workspace.SearchAsync(null)).Single(track => track.Id == firstTrack.Id);
            await workspace.UpdateTrackMetadataAsync(current with
            {
                Duration = TimeSpan.FromSeconds(130),
                AudioInfo = new AudioStreamInfo { Channels = 2, SampleRateHz = 48000 },
                AudioMetadataLength = new FileInfo(downloadedFirst).Length,
                AudioMetadataLastWriteUtcTicks = File.GetLastWriteTimeUtc(downloadedFirst).Ticks
            });
            current = (await workspace.SearchAsync(null)).Single(track => track.Id == firstTrack.Id);
            TestAssert.Equal(downloadedFirst, current.Audio.LocalPath, "Metadata updates do not erase downloaded resources");
            TestAssert.Equal(192000L, current.AudioInfo!.BitrateBitsPerSecond, "Partial parsed stream info retains an existing bitrate");
            await workspace.SynchronizeAsync(first);
            current = (await workspace.SearchAsync(null)).Single(track => track.Id == firstTrack.Id);
            TestAssert.Equal(downloadedFirst, current.Audio.LocalPath, "Metadata resync retains the complete local resource");
            TestAssert.Equal(48000, current.AudioInfo!.SampleRateHz, "Metadata resync retains additional parsed stream properties");
            handler.Revision = "new-revision";
            await workspace.SynchronizeAsync(first);
            await workspace.UpdateTrackMetadataAsync(current with { Duration = TimeSpan.FromSeconds(999) });
            var newRevision = (await workspace.SearchAsync(null)).Single(track => track.Id == firstTrack.Id);
            TestAssert.Equal("new-revision", newRevision.Revision, "The source revision has advanced");
            TestAssert.True(newRevision.Duration != TimeSpan.FromSeconds(999), "Stale parsed metadata cannot overwrite a newer source revision");
            await VerifySupersededLoginAsync(workspace, handler, first, second);
            handler.CloudStatus = HttpStatusCode.NotFound;
            await TestAssert.ThrowsAsync<HttpRequestException>(() => workspace.GetCloudPlaylistsAsync(second));
            TestAssert.True(workspace.GetEndpointSessions().Single(item => item.EndpointId == second.Id).IsLoggedIn,
                "A source without cloud support does not erase its valid account session");
            handler.CloudStatus = HttpStatusCode.Unauthorized;
            await TestAssert.ThrowsAsync<HttpRequestException>(() => workspace.GetCloudPlaylistsAsync(second));
            TestAssert.True(!workspace.GetEndpointSessions().Single(item => item.EndpointId == second.Id).IsLoggedIn,
                "An unauthorized cloud request marks only its own account session expired");
            handler.CloudStatus = HttpStatusCode.OK;
            await workspace.LoginAsync(second, "Bob", "password-b");
            handler.HoldRecommendation = true;
            var pendingRecommendation = workspace.GetRecommendationsAsync(second);
            await handler.RecommendationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var currentSettings = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(currentSettings with { Endpoints = currentSettings.Endpoints.Where(endpoint => endpoint.Id != second.Id).ToArray() });
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => pendingRecommendation);
            TestAssert.True(workspace.GetEndpointSessions().All(session => session.EndpointId != second.Id),
                "Removing a source cancels its active account work and removes the session from selectors");
            workspace.SetOnlineEnabled(false);
            var beforeOffline = handler.Requests.Count;
            TestAssert.Equal(downloadedSecond, await workspace.ResolveAudioAsync(secondTrack, false), "Downloaded account music remains available offline");
            TestAssert.Equal(beforeOffline, handler.Requests.Count, "Offline playback performs no account or media request");
        }

        /// <summary>Checks that a slow older login cannot undo a later Guest choice.</summary>
        /// <param name="workspace">The composed workspace.</param>
        /// <param name="handler">The in-memory transport.</param>
        /// <param name="first">The source being changed.</param>
        /// <param name="second">An unaffected logged-in source.</param>
        /// <returns>A task representing the race regression.</returns>
        private static async Task VerifySupersededLoginAsync(MusicWorkspace workspace, AccountTransport handler,
            EndpointConfiguration first, EndpointConfiguration second)
        {
            handler.HoldLogin = true;
            var pending = workspace.LoginAsync(first, "Slow", "slow-password");
            await handler.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await workspace.UseGuestAsync(first);
            handler.ReleaseLogin.TrySetResult();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => pending);
            var sessions = workspace.GetEndpointSessions();
            TestAssert.True(!sessions.Single(item => item.EndpointId == first.Id).IsLoggedIn, "A later Guest choice wins over an earlier pending login");
            TestAssert.Equal("Bob", sessions.Single(item => item.EndpointId == second.Id).Username, "Changing one account does not change another slot");
        }

        /// <summary>Checks that adding endpoint scope retains old music identities and playlist references.</summary>
        /// <param name="root">The isolated legacy workspace directory.</param>
        /// <returns>A task representing identity migration checks.</returns>
        private static async Task VerifyLegacyIdentitiesAsync(string root)
        {
            const string hash = "legacy-chart-hash";
            var first = Endpoint("original") with { ServerId = "shared-server" };
            var repository = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
            var legacy = new MusicTrack
            {
                Id = "majnet:shared-server:song",
                SourceId = "shared-server",
                RemoteId = "song",
                Revision = hash,
                Title = "Legacy song",
                Audio = new MediaResource { RemoteUri = new Uri(first.BaseUri, "maichart/song/track"), CacheVersion = hash }
            };
            var oldRecommendation = legacy with
            {
                Id = "majnet:shared-server:recommended",
                RemoteId = "recommended",
                Revision = "recommend-hash"
            };
            await repository.UpsertTracksAsync(new[] { legacy, oldRecommendation });
            await repository.SavePlaylistAsync(new MusicPlaylist { Id = "old-list", Name = "Old list", TrackIds = new[] { legacy.Id } });
            using var handler = new AccountTransport(hash);
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client);
            var settings = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(settings with { OnlineEnabled = true, Endpoints = new[] { first } });
            await workspace.SynchronizeAsync(first);
            await workspace.SynchronizeAsync(first);
            var afterSync = await workspace.SearchAsync(null);
            TestAssert.Equal(2, afterSync.Count, "Resynchronizing existing data does not create new scoped duplicates");
            TestAssert.Equal(first.Id, afterSync.Single(track => track.Id == legacy.Id).EndpointId,
                "A uniquely mapped legacy entry acquires its endpoint owner while keeping its identity");
            TestAssert.Equal(legacy.Id, (await workspace.GetPlaylistsAsync()).Single().TrackIds.Single(),
                "Adding account scope preserves old local playlist references");
            await workspace.LoginAsync(first, "Alice", "password-a");
            var page = await workspace.GetRecommendationsAsync(first);
            TestAssert.Equal(oldRecommendation.Id, page.Single().Id, "Recommendation pages return the persisted legacy identity after migration");
            var second = Endpoint("additional") with { ServerId = "shared-server" };
            var currentSettings = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(currentSettings with { Endpoints = new[] { first, second } });
            await workspace.SynchronizeAsync(first);
            await workspace.SynchronizeAsync(second);
            var scoped = await workspace.SearchAsync(null);
            TestAssert.Equal(3, scoped.Count, "A second account adds only its own scoped song after legacy entries have an owner");
            TestAssert.Equal(legacy.Id, scoped.Single(track => track.EndpointId == first.Id && track.RemoteId == "song").Id,
                "Multiple later accounts do not change an established legacy owner");
        }

        /// <summary>Checks that editing a source URL cannot replace an old song with another server's equal remote identifier.</summary>
        /// <param name="root">The isolated source-change workspace.</param>
        /// <returns>A task representing cross-server identity checks.</returns>
        private static async Task VerifySourceIdentityChangesAsync(string root)
        {
            const string hash = "same-chart-hash";
            using var handler = new AccountTransport(hash) { ServerId = "original-server" };
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(root, client, client);
            var endpoint = Endpoint("same-slot");
            var settings = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(settings with { OnlineEnabled = true, Endpoints = new[] { endpoint } });
            await workspace.LoginAsync(endpoint, "Alice", "password-a");
            await workspace.SynchronizeAsync(endpoint);
            var original = (await workspace.SearchAsync(null)).Single();
            var cached = await workspace.ResolveAudioAsync(original, true);
            var uncached = original with
            {
                Id = original.Id + "-uncached",
                RemoteId = "missing",
                Audio = original.Audio with { RemoteUri = new Uri(endpoint.BaseUri, "maichart/missing/track") }
            };
            var repository = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
            await repository.UpsertTracksAsync(new[] { uncached });
            handler.ServerId = "replacement-server";
            var replacement = endpoint with { BaseUri = new Uri("https://replacement.example.test/api/"), ServerId = null };
            var currentSettings = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(currentSettings with { Endpoints = new[] { replacement } });
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => workspace.ResolveAudioAsync(uncached, false));
            TestAssert.Equal("original-server", (await workspace.SearchAsync(null)).Single(track => track.Id == uncached.Id).SourceId,
                "A replacement source cannot overwrite the old server's resource identity");
            TestAssert.Equal(cached, await workspace.ResolveAudioAsync(original, false),
                "Already cached music from the original server remains playable after source reconfiguration");
            var enriched = await workspace.EnrichTrackMetadataAsync(uncached);
            TestAssert.Equal("original-server", enriched.SourceId, "Background metadata does not adopt a replacement server's equal remote identifier");
            await workspace.LoginAsync(replacement, "Bob", "password-b");
            var cloud = (await workspace.GetCloudPlaylistsAsync(replacement)).Single();
            var indexed = (await workspace.SearchAsync(null)).ToDictionary(track => track.Id, StringComparer.Ordinal);
            TestAssert.True(cloud.TrackIds.Count == 1 && cloud.TrackIds.All(id => indexed[id].SourceId == "replacement-server"),
                "Cloud hashes match the current server as well as its configured account slot");
        }

        /// <summary>Checks both Maj already-logged-in codes and explicit business failures in successful HTTP responses.</summary>
        /// <returns>A task representing protocol-status checks.</returns>
        private static async Task VerifyLoginCodesAsync()
        {
            foreach (var code in new[] { 0, 60001 })
            {
                using var policy = new NetworkAccessPolicy();
                policy.SetEnabled(true);
                using var transport = new HttpClient(new LoginStatusTransport(HttpStatusCode.Forbidden, $"{{\"code\":{code}}}"));
                using var provider = new MajNetClient(Endpoint("code"), policy, transport);
                await provider.LoginAsync("account", "password");
                TestAssert.True(provider.IsLoggedIn, "Maj's legacy and current already-logged-in codes are accepted");
            }

            foreach (var body in new[] { "{\"code\":60003}", "{\"success\":false}", "{\"code\":\"60003\",\"success\":false}" })
            {
                using var policy = new NetworkAccessPolicy();
                policy.SetEnabled(true);
                using var transport = new HttpClient(new LoginStatusTransport(HttpStatusCode.OK, body));
                using var provider = new MajNetClient(Endpoint("failure"), policy, transport);
                await TestAssert.ThrowsAsync<HttpRequestException>(() => provider.LoginAsync("account", "password"));
                TestAssert.True(!provider.IsLoggedIn, "An explicit JSON business failure never marks the account logged in");
            }
        }

        /// <summary>Creates a saved endpoint slot that may share an address with another slot.</summary>
        /// <param name="id">The independent local configuration identity.</param>
        /// <returns>The source configuration.</returns>
        private static EndpointConfiguration Endpoint(string id)
        {
            return new EndpointConfiguration { Id = id, Name = id, BaseUri = new Uri("https://accounts.example.test/api/") };
        }

        /// <summary>Emulates the known Maj API and account-protected media without any real network activity.</summary>
        private sealed class AccountTransport : HttpMessageHandler
        {
            /// <summary>Stores the stable fixture chart identity.</summary>
            private readonly string _hash;

            /// <summary>Constructs the isolated endpoint fixture.</summary>
            /// <param name="hash">The same chart hash used by the local collision fixture.</param>
            public AccountTransport(string hash)
            {
                _hash = hash;
                Revision = hash;
            }

            /// <summary>Gets observed paths and account identities without storing passwords or cookies.</summary>
            public List<(string Path, string Account)> Requests { get; } = new();

            /// <summary>Gets or sets the published catalog revision.</summary>
            public string Revision { get; set; }

            /// <summary>Gets or sets the independently discovered remote server identity.</summary>
            public string ServerId { get; set; } = "shared-server";

            /// <summary>Gets or sets whether the next Slow login waits for a later account choice.</summary>
            public bool HoldLogin { get; set; }

            /// <summary>Gets or sets whether cloud responses omit IDs and include an unknown hash.</summary>
            public bool ExtendedCloudFixture { get; set; }

            /// <summary>Gets or sets the HTTP status of the cloud-list route.</summary>
            public HttpStatusCode CloudStatus { get; set; } = HttpStatusCode.OK;

            /// <summary>Gets or sets whether recommendation requests wait for cancellation.</summary>
            public bool HoldRecommendation { get; set; }

            /// <summary>Gets the signal that a held recommendation reached the transport.</summary>
            public TaskCompletionSource RecommendationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Gets the notification that the held login reached the transport.</summary>
            public TaskCompletionSource LoginStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Gets the signal releasing the held login response.</summary>
            public TaskCompletionSource ReleaseLogin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Returns fixture data for a sanitized source request.</summary>
            /// <param name="request">The source request.</param>
            /// <param name="cancellationToken">Cancels the fixture operation.</param>
            /// <returns>The generated endpoint response.</returns>
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.PathAndQuery;
                var cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : string.Empty;
                var account = cookie.Contains("account=Alice", StringComparison.Ordinal) ? "Alice"
                    : cookie.Contains("account=Bob", StringComparison.Ordinal) ? "Bob" : "Guest";
                Requests.Add((path, account));
                if (path.EndsWith("account/login", StringComparison.Ordinal))
                {
                    var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var username = form.Contains("Alice", StringComparison.Ordinal) ? "Alice"
                        : form.Contains("Bob", StringComparison.Ordinal) ? "Bob" : "Slow";
                    if (username == "Slow" && HoldLogin)
                    {
                        LoginStarted.TrySetResult();
                        await ReleaseLogin.Task.WaitAsync(cancellationToken);
                    }

                    var response = Json("{\"code\":114514}");
                    response.Headers.TryAddWithoutValidation("Set-Cookie", $"account={username}; Path=/api; Secure; HttpOnly");
                    return response;
                }

                if (path == "/api/info")
                {
                    return Json(JsonSerializer.Serialize(new { id = ServerId, name = "Shared" }));
                }

                if (path == "/api/maichart/list")
                {
                    return Json("[" + Track("song", Revision) + "]");
                }

                if (path == "/api/account/favorite/collection/list")
                {
                    if (CloudStatus != HttpStatusCode.OK)
                    {
                        return new HttpResponseMessage(CloudStatus) { Content = new StringContent("{}") };
                    }

                    if (ExtendedCloudFixture)
                    {
                        return Json(JsonSerializer.Serialize(new[]
                        {
                            new { name = "First", songHashs = new[] { _hash, "unknown-chart-reference" } },
                            new { name = "Second", songHashs = new[] { _hash } },
                            new { name = "First", songHashs = new[] { _hash } }
                        }));
                    }

                    return Json(JsonSerializer.Serialize(new[] { new { id = "cloud-list", name = "Online favorites", songHashs = new[] { _hash } } }));
                }

                if (path.StartsWith("/api/recommend/feed?", StringComparison.Ordinal))
                {
                    if (HoldRecommendation)
                    {
                        RecommendationStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }

                    return Json("[" + Track("recommended", "recommend-hash") + "]");
                }

                var bytes = Encoding.UTF8.GetBytes("audio-" + account);
                if (path.StartsWith("/api/maichart/", StringComparison.Ordinal) && path.EndsWith("/info", StringComparison.Ordinal))
                {
                    var remoteId = path.Split('/')[3];
                    return Json("{\"metadata\":" + Track(remoteId, Revision) + ",\"resources\":{\"track\":{\"url\":\"maichart/" + remoteId + "/track\",\"sha256\":\""
                        + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\",\"length\":" + bytes.Length + "}}}");
                }

                if (path == "/api/maichart/song/track")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                }

                throw new InvalidOperationException("Unexpected fixture request: " + path);
            }

            /// <summary>Serializes one optional-extended metadata fixture.</summary>
            /// <param name="id">The remote identifier.</param>
            /// <param name="hash">The source revision.</param>
            /// <returns>The metadata object JSON.</returns>
            private static string Track(string id, string hash)
            {
                return JsonSerializer.Serialize(new { id, title = id, artist = "Artist", hash, durationSeconds = 123.5, audioInfo = new { bitrateBitsPerSecond = 192000 } });
            }

            /// <summary>Creates an application/json fixture response.</summary>
            /// <param name="body">The JSON body.</param>
            /// <returns>The response.</returns>
            private static HttpResponseMessage Json(string body)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }

        /// <summary>Returns one controlled login result.</summary>
        private sealed class LoginStatusTransport : HttpMessageHandler
        {
            /// <summary>Stores the HTTP status to return.</summary>
            private readonly HttpStatusCode _status;

            /// <summary>Stores the status body to return.</summary>
            private readonly string _body;

            /// <summary>Constructs a login-status fixture.</summary>
            /// <param name="status">The HTTP status.</param>
            /// <param name="body">The status object.</param>
            public LoginStatusTransport(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            /// <summary>Returns the controlled login response.</summary>
            /// <param name="request">The unused fixture request.</param>
            /// <param name="cancellationToken">The caller cancellation.</param>
            /// <returns>The login response.</returns>
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
            }
        }
    }
}
