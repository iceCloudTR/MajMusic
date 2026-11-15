#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Network;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;
using Microsoft.Data.Sqlite;

namespace MajMusic.Tests
{
    /// <summary>Checks private, durable collection mirroring through real workspace and presentation boundaries.</summary>
    internal static class CloudCollectionSyncTests
    {
        /// <summary>Runs isolated account, ownership, restart, cancellation and user-action regressions.</summary>
        /// <returns>A task representing the synchronization checks.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-cloud-sync-" + Guid.NewGuid().ToString("N")));
            try
            {
                await ValidateProjectionAndRestartAsync(Path.Combine(root, "durable"));
                await ValidateOwnershipAsync(Path.Combine(root, "ownership"));
                await ValidateLostCreationAsync(Path.Combine(root, "lost"));
                await ValidateRejectedCreationAsync(Path.Combine(root, "rejected"));
                await ValidateAccountCancellationAsync(Path.Combine(root, "accounts"));
                await ValidatePresentationAsync(Path.Combine(root, "presentation"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-cloud-sync-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the cloud synchronization fixture.");
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Projects only chart IDs, keeps private ownership durable, and removes only deleted application mirrors.</summary>
        /// <param name="root">The generated application-data directory.</param>
        /// <returns>A task representing durable projection checks.</returns>
        private static async Task ValidateProjectionAndRestartAsync(string root)
        {
            using var transport = new FixtureTransport();
            using (var harness = new SyncHarness(root, transport))
            {
                await harness.InitializeAsync();
                await harness.Workspace.SavePlaylistAsync(List("pure", ["local"]));
                TestAssert.Equal(0, (await harness.Workspace.SynchronizeCloudCollectionsAsync()).Count, "Default offline mode has no synchronization targets.");
                TestAssert.True(!File.Exists(Path.Combine(root, "cloud-collections.db")), "Offline startup cannot create the cloud binding database.");
                await harness.EnableAccountAsync("first", "Alice", false);
                TestAssert.Equal(0, (await harness.Workspace.SynchronizeCloudCollectionsAsync()).Count, "An authenticated account still needs explicit cloud permission.");
                await harness.EnableAccountAsync("first", "Alice");
                var result = (await harness.Workspace.SynchronizeCloudCollectionsAsync()).Single();
                TestAssert.True(result.Succeeded && result.CollectionCount == 0 && transport.Creates == 0,
                    "A pure local playlist cannot create an empty cloud collection.");
                TestAssert.True(!File.Exists(Path.Combine(root, "cloud-collections.db")), "Pure local collections do not create cloud binding storage.");
                transport.AddForeign("a.example.test", "Alice", "Mixed");
                await harness.Workspace.SavePlaylistAsync(List("mixed", ["first-track", "local", "other-source", "invalid", "second-track", "first-track"]));
                result = (await harness.Workspace.SynchronizeCloudCollectionsAsync()).Single();
                TestAssert.True(result.Succeeded && result.CollectionCount == 1, "One mixed playlist creates one authorized mirror.");
                var mirror = transport.Owned("a.example.test", "Alice").Single(item => item.Description?.StartsWith("MajMusic · ", StringComparison.Ordinal) == true);
                TestAssert.True(mirror.Visibility == 0 && mirror.Items.SequenceEqual([FixtureTransport.FirstChart, FixtureTransport.SecondChart, FixtureTransport.FirstChart]),
                    "Private cloud contents must be ordered same-server chart GUIDs, without local songs, hashes or other-source entries.");
                TestAssert.True(transport.Owned("a.example.test", "Alice").Single(item => item.Description == "User collection").Items.Count == 0,
                    "An existing same-name user collection must remain unchanged.");
                TestAssert.True((await harness.Workspace.GetCloudPlaylistsAsync(await harness.EndpointAsync("first"))).Single().MajCollection!.Entries.Count == 3,
                    "Subscribing the application's private mirror makes its portable chart hashes visible through Play's GET collection route.");
                TestAssert.Equal(1, transport.Creates, "Initial synchronization creates exactly one mirror.");
                await harness.Workspace.SynchronizeCloudCollectionsAsync();
                TestAssert.Equal(1, transport.Updates, "An unchanged remote mirror does not receive another full replacement.");
            }

            using (var reopened = new SyncHarness(root, transport))
            {
                await reopened.InitializeAsync();
                TestAssert.True(!reopened.Workspace.IsOnline, "Restart retains the default offline entry behavior.");
                await reopened.EnableAccountAsync("first", "Alice");
                await reopened.Workspace.SynchronizeCloudCollectionsAsync();
                TestAssert.Equal(1, transport.Creates, "A durable source/account binding prevents duplicate creation after restart.");
                reopened.Workspace.SetOnlineEnabled(false);
                await reopened.Workspace.SavePlaylistAsync(List("mixed", ["second-track"]) with { Name = "Renamed" });
                var before = transport.Writes.Count;
                await reopened.Workspace.SynchronizeCloudCollectionsAsync();
                TestAssert.Equal(before, transport.Writes.Count, "Offline local changes send no cloud writes.");
                await reopened.EnableAccountAsync("first", "Alice");
                await reopened.Workspace.SynchronizeCloudCollectionsAsync();
                var mirror = transport.Owned("a.example.test", "Alice").Single(item => item.Description?.StartsWith("MajMusic · ", StringComparison.Ordinal) == true);
                TestAssert.True(mirror.Name == "Renamed" && mirror.Items.SequenceEqual([FixtureTransport.SecondChart]), "Relogin publishes current offline membership and title.");
                await reopened.Workspace.SavePlaylistAsync(List("mixed", ["local"]));
                await reopened.Workspace.SynchronizeCloudCollectionsAsync();
                TestAssert.Equal(0, transport.Owned("a.example.test", "Alice").Single(item => item.Id == mirror.Id).Items.Count,
                    "Removing the last online song clears a previously bound cloud list.");
                transport.Replace("a.example.test", mirror with { Id = Guid.NewGuid().ToString("D") });
                await ((IPlaylistManagementWorkspace)reopened.Workspace).DeletePlaylistAsync(List("mixed", []));
                await reopened.Workspace.SynchronizeCloudCollectionsAsync();
                TestAssert.True(transport.Owned("a.example.test", "Alice").All(item => item.Id != mirror.Id)
                    && transport.Owned("a.example.test", "Alice").Single().Description == "User collection",
                    "Deleting a local playlist removes only its verified private mirror and subscription.");
                TestAssert.Equal(5, (await reopened.Workspace.SearchAsync(null)).Count, "Cloud mirroring never removes source music metadata.");
            }
        }

        /// <summary>Stops writes when a remote owner, marker or private visibility changes.</summary>
        /// <param name="root">The generated application-data directory.</param>
        /// <returns>A task representing remote ownership protection.</returns>
        private static async Task ValidateOwnershipAsync(string root)
        {
            using var transport = new FixtureTransport();
            using var harness = new SyncHarness(root, transport);
            await harness.InitializeAsync();
            await harness.EnableAccountAsync("first", "Alice");
            await harness.Workspace.SavePlaylistAsync(List("owned", ["first-track"]));
            await harness.Workspace.SynchronizeCloudCollectionsAsync();
            var mirror = transport.Owned("a.example.test", "Alice").Single();
            foreach (var changed in new[] { mirror with { Description = "Changed externally" }, mirror with { CreatedBy = "SomeoneElse" }, mirror with { Visibility = 1 } })
            {
                transport.Replace("a.example.test", changed);
                await harness.Workspace.SavePlaylistAsync(List("owned", ["second-track"]));
                var before = transport.Writes.Count;
                var result = (await harness.Workspace.SynchronizeCloudCollectionsAsync()).Single();
                TestAssert.True(!result.Succeeded && transport.Writes.Count == before, "A changed remote ownership boundary cannot receive local membership or deletion writes.");
            }

            transport.Replace("a.example.test", mirror);
            await harness.Workspace.SynchronizeCloudCollectionsAsync();
            TestAssert.True(transport.Owned("a.example.test", "Alice").Single().Items.SequenceEqual([FixtureTransport.SecondChart]),
                "A failed ownership check preserves pending local edits for a later valid retry.");
            transport.RemoveRemote("a.example.test", mirror.Id);
            await harness.Workspace.SavePlaylistAsync(List("owned", ["first-track"]));
            TestAssert.True((await harness.Workspace.SynchronizeCloudCollectionsAsync()).Single().Succeeded,
                "A mirror explicitly missing from both complete owned inventory and its old songlist route must be recreated automatically.");
            var recreated = transport.Owned("a.example.test", "Alice").Single();
            TestAssert.True(recreated.Id != mirror.Id && recreated.Visibility == 0 && recreated.Items.SequenceEqual([FixtureTransport.FirstChart]),
                "A collection removed on the website must recover as one new private mirror of the current local playlist.");
            TestAssert.Equal(2, transport.Creates, "Remote deletion recovery creates exactly one replacement collection.");
        }

        /// <summary>Recovers a server-created collection after its response was lost without duplicating it.</summary>
        /// <param name="root">The generated application-data directory.</param>
        /// <returns>A task representing journal recovery across restart.</returns>
        private static async Task ValidateLostCreationAsync(string root)
        {
            using var transport = new FixtureTransport { LoseNextCreateResponse = true };
            for (var index = 0; index < 103; index++)
            {
                transport.AddForeign("a.example.test", "Alice", "Existing " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            using (var harness = new SyncHarness(root, transport))
            {
                await harness.InitializeAsync();
                await harness.EnableAccountAsync("first", "Alice");
                await harness.Workspace.SavePlaylistAsync(List("recover", ["first-track"]));
                TestAssert.True(!(await harness.Workspace.SynchronizeCloudCollectionsAsync()).Single().Succeeded, "An uncertain creation response must remain an observed failed attempt.");
                TestAssert.Equal(1, transport.Creates, "The fake server committed creation before losing its response.");
            }

            using var reopened = new SyncHarness(root, transport);
            await reopened.InitializeAsync();
            await reopened.EnableAccountAsync("first", "Alice");
            TestAssert.True((await reopened.Workspace.SynchronizeCloudCollectionsAsync()).Single().Succeeded, "A restarted account recovers the exact precommitted ownership marker.");
            TestAssert.Equal(1, transport.Creates, "Recovery must never create a second collection for the same local binding.");
            var mirror = transport.Owned("a.example.test", "Alice").Single(item => item.Description?.StartsWith("MajMusic · ", StringComparison.Ordinal) == true);
            transport.Replace("a.example.test", mirror with { Id = Guid.NewGuid().ToString("D") });
            TestAssert.True((await reopened.Workspace.SynchronizeCloudCollectionsAsync()).Single().Succeeded,
                "A late duplicate creation must converge after complete owned pagination and private ownership verification.");
            TestAssert.True(transport.Owned("a.example.test", "Alice").Count(item => item.Description == mirror.Description) == 1
                && transport.Owned("a.example.test", "Alice").Any(item => item.Id == mirror.Id),
                "Duplicate convergence must preserve the durable primary binding while leaving preexisting user collections untouched.");
            TestAssert.Equal(104, transport.Owned("a.example.test", "Alice").Count, "Duplicate cleanup cannot delete unrelated owned collections from earlier pages.");
        }

        /// <summary>Retries an uncertain creation when a later complete inventory proves no mirror currently exists.</summary>
        /// <param name="root">The generated application-data directory.</param>
        /// <returns>A task representing unsuccessful creation recovery.</returns>
        private static async Task ValidateRejectedCreationAsync(string root)
        {
            using var transport = new FixtureTransport { RejectNextCreate = true };
            using (var harness = new SyncHarness(root, transport))
            {
                await harness.InitializeAsync();
                await harness.EnableAccountAsync("first", "Alice");
                await harness.Workspace.SavePlaylistAsync(List("retry", ["first-track"]));
                TestAssert.True(!(await harness.Workspace.SynchronizeCloudCollectionsAsync()).Single().Succeeded, "An HTTP 500 before creation is an observed failed attempt.");
                TestAssert.Equal(0, transport.Creates, "The failed creation must leave no remote collection.");
            }

            using var reopened = new SyncHarness(root, transport);
            await reopened.InitializeAsync();
            await reopened.EnableAccountAsync("first", "Alice");
            TestAssert.True((await reopened.Workspace.SynchronizeCloudCollectionsAsync()).Single().Succeeded,
                "A persisted uncertain creation with no server commit must retry automatically after a successful complete owned read.");
            TestAssert.Equal(1, transport.Creates, "The recovery batch creates exactly one usable private mirror.");
        }

        /// <summary>Cancels stale target writes independently while another endpoint continues and caller cancellation remains observable.</summary>
        /// <param name="root">The generated application-data directory.</param>
        /// <returns>A task representing account and permission cancellation.</returns>
        private static async Task ValidateAccountCancellationAsync(string root)
        {
            using var transport = new FixtureTransport();
            using var harness = new SyncHarness(root, transport);
            await harness.InitializeAsync();
            await harness.EnableAccountAsync("first", "Alice");
            await harness.EnableAccountAsync("second", "Bob");
            await harness.Workspace.SavePlaylistAsync(List("shared", ["first-track"]));
            transport.BlockNextUpdate("a.example.test");
            var pending = harness.Workspace.SynchronizeCloudCollectionsAsync();
            await transport.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await harness.Workspace.SetCloudCollectionUsageAsync(await harness.EndpointAsync("first"), "Alice", false);
            var results = await pending.WaitAsync(TimeSpan.FromSeconds(4));
            TestAssert.True(!results.Single(item => item.EndpointId == "first").Succeeded && results.Single(item => item.EndpointId == "second").Succeeded,
                "Revoking one account cancels only its in-flight mirror while another endpoint completes.");
            TestAssert.Equal(0, transport.Owned("a.example.test", "Alice").Single().Items.Count, "The canceled delayed write cannot publish retired account contents.");
            await harness.EnableAccountAsync("first", "Alice");
            await harness.Workspace.SynchronizeCloudCollectionsAsync();
            var alice = transport.Owned("a.example.test", "Alice").Single();
            await harness.Workspace.SavePlaylistAsync(List("shared", ["second-track"]));
            transport.BlockNextUpdate("a.example.test");
            var switching = harness.Workspace.SynchronizeCloudCollectionsAsync();
            await transport.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var carol = await harness.Workspace.SaveAccountAsync(await harness.EndpointAsync("first"), "Carol", null, false, false);
            carol = await harness.Workspace.SetCloudCollectionUsageAsync(carol, carol.SelectedAccountId!, true);
            await harness.Workspace.LoginAsync(carol, "Carol", "Generated fixture password");
            TestAssert.True(!(await switching.WaitAsync(TimeSpan.FromSeconds(4))).Single(item => item.EndpointId == "first").Succeeded,
                "Switching saved accounts must cancel the previous account's delayed replacement.");
            TestAssert.True(transport.Owned("a.example.test", "Alice").Single().Items.SequenceEqual([FixtureTransport.FirstChart]),
                "An old account's delayed write must never publish after a different account logs in.");
            await harness.Workspace.SynchronizeCloudCollectionsAsync();
            TestAssert.True(transport.Owned("a.example.test", "Carol").Single().Id != alice.Id, "A different saved account receives an independent durable cloud binding.");
            await harness.Workspace.SelectAccountAsync(await harness.EndpointAsync("first"), null);
            var writes = transport.Writes.Count;
            await harness.Workspace.SynchronizeCloudCollectionsAsync();
            TestAssert.True(transport.Writes.Skip(writes).All(item => item.Host != "a.example.test"), "Guest cannot reuse a remembered account's cloud permission or cookies.");
            await harness.EnableAccountAsync("first", "Alice");
            await harness.Workspace.SavePlaylistAsync(List("shared", ["second-track"]));
            transport.BlockNextUpdate("a.example.test");
            using var cancellation = new CancellationTokenSource();
            var canceled = harness.Workspace.SynchronizeCloudCollectionsAsync(cancellation.Token);
            await transport.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            cancellation.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => canceled);
        }

        /// <summary>Exercises the real favorite command, coalesced worker and Play-compatible GET, including final-list deletion.</summary>
        /// <param name="root">The generated application-data directory.</param>
        /// <returns>A task representing user-action integration.</returns>
        private static async Task ValidatePresentationAsync(string root)
        {
            using var transport = new FixtureTransport();
            using var harness = new SyncHarness(root, transport);
            await harness.InitializeAsync();
            await harness.EnableAccountAsync("first", "Alice");
            await using var view = new MainViewModel(harness.Workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await view.InitializeAsync();
            await view.CloudCollectionsUpdate.WaitAsync(TimeSpan.FromSeconds(5));
            view.SelectedTrack = (await harness.Workspace.SearchAsync(null)).Single(track => track.Id == "first-track");
            transport.BlockNextUpdate("a.example.test");
            await ExecuteAsync(view, view.ToggleFavoriteCommand);
            await transport.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            TestAssert.True(!view.IsBusy && view.ToggleFavoriteCommand.CanExecute(null), "A cloud upload cannot block library interaction or another local favorite edit.");
            await ExecuteAsync(view, view.ToggleFavoriteCommand);
            await ExecuteAsync(view, view.ToggleFavoriteCommand);
            transport.ReleaseUpdate.TrySetResult();
            await view.CloudCollectionsUpdate.WaitAsync(TimeSpan.FromSeconds(5));
            var cloud = (await harness.Workspace.GetCloudPlaylistsAsync(await harness.EndpointAsync("first"))).Single();
            TestAssert.True(cloud.MajCollection!.Entries.Select(entry => entry.Hash).SequenceEqual([FixtureTransport.Hash(FixtureTransport.FirstChart)]),
                "A real like action must create, upload and subscribe a cloud collection whose Play GET exposes the matching hash.");
            TestAssert.True(transport.Writes.Any(item => item.Path == "/api/collection/create")
                && transport.Writes.Any(item => item.Method == "PUT")
                && transport.Writes.Any(item => item.Path == "/api/account/favorite/collection/diff" && item.Method == "POST"),
                "The view worker must traverse the actual documented create, modify and subscribe routes.");
            var mirror = transport.Owned("a.example.test", "Alice").Single();
            TestAssert.True(mirror.Items.SequenceEqual([FixtureTransport.FirstChart]), "Rapid local like edits coalesce to the final saved membership.");
            view.SelectedTrack = (await harness.Workspace.SearchAsync(null)).Single(track => track.Id == "local");
            await ExecuteAsync(view, view.ToggleFavoriteCommand);
            await view.CloudCollectionsUpdate.WaitAsync(TimeSpan.FromSeconds(5));
            TestAssert.True(transport.Owned("a.example.test", "Alice").Single().Items.SequenceEqual([FixtureTransport.FirstChart]),
                "Liking a local song must never upload a local identity or audio file.");
            await harness.Workspace.SavePlaylistAsync(List("temporary", ["second-track"]));
            view.NewPlaylistName = "Refresh";
            await ExecuteAsync(view, view.CreatePlaylistCommand);
            await view.CloudCollectionsUpdate.WaitAsync(TimeSpan.FromSeconds(5));
            var temporary = transport.Owned("a.example.test", "Alice").Single(item => item.Name == "Mixed");
            await ((IPlaylistManagementWorkspace)harness.Workspace).DeletePlaylistAsync(List("temporary", []));
            view.NewPlaylistName = "Refresh again";
            await ExecuteAsync(view, view.CreatePlaylistCommand);
            await view.CloudCollectionsUpdate.WaitAsync(TimeSpan.FromSeconds(5));
            TestAssert.True(transport.Owned("a.example.test", "Alice").All(item => item.Id != temporary.Id)
                && view.Playlists.All(item => item.RemoteId != temporary.Id), "Deleting a final mirrored local list must remove its stale cloud snapshot from the view.");
        }

        /// <summary>Executes one observed local command and waits for its own availability to return.</summary>
        /// <param name="view">The real view model.</param>
        /// <param name="command">The finite local-edit command.</param>
        /// <returns>A task representing local command completion.</returns>
        private static async Task ExecuteAsync(MainViewModel view, AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The local edit command must remain available.");
            command.Execute(null);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (view.IsBusy || !command.CanExecute(null))
            {
                await Task.Delay(5, deadline.Token);
            }
        }

        /// <summary>Creates a local test playlist with stable identity.</summary>
        /// <param name="id">The local list identity.</param>
        /// <param name="tracks">The ordered local library references.</param>
        /// <returns>The local playlist.</returns>
        private static MusicPlaylist List(string id, IReadOnlyList<string> tracks)
        {
            return new MusicPlaylist { Id = id, Name = "Mixed", TrackIds = tracks };
        }

        /// <summary>Owns one isolated real workspace sharing only a generated fake service state.</summary>
        internal sealed class SyncHarness : IDisposable
        {
            /// <summary>Stores the generated application data directory.</summary>
            private readonly string _root;
            /// <summary>Creates the workspace and transports without making requests.</summary>
            /// <param name="root">The generated application-data directory.</param>
            /// <param name="transport">The synthetic remote service state.</param>
            public SyncHarness(string root, FixtureTransport transport)
            {
                _root = root;
                Transport = transport;
                Client = new HttpClient(transport, false);
                Workspace = new MusicWorkspace(root, Client, Client, credentialStore: new MemoryCredentials());
            }
            /// <summary>Gets the real workspace under test.</summary>
            public MusicWorkspace Workspace { get; }
            /// <summary>Gets the reusable fake service state.</summary>
            public FixtureTransport Transport { get; }
            /// <summary>Gets the explicitly injected synthetic transport.</summary>
            public HttpClient Client { get; }
            /// <summary>Seeds only generated settings and library entries if this is a new data directory.</summary>
            /// <returns>A task representing fixture initialization.</returns>
            public async Task InitializeAsync()
            {
                var settings = await Workspace.LoadSettingsAsync();
                if (!settings.Endpoints.Any(endpoint => endpoint.Id == "first"))
                {
                    await Workspace.SaveSettingsAsync(settings with { Endpoints = [Endpoint("first", "a"), Endpoint("second", "b")] });
                    var library = new SqliteLibraryRepository(Path.Combine(_root, "library.db"));
                    await library.UpsertTracksAsync([
                        Track("first-track", FixtureTransport.FirstChart), Track("second-track", FixtureTransport.SecondChart),
                        Track("other-source", FixtureTransport.SecondChart) with { SourceId = "another-server" },
                        Track("invalid", "portable-hash"), Track("local", null) with { SourceId = "local" }
                    ]);
                }
            }
            /// <summary>Selects and explicitly authenticates one generated account without system vault access.</summary>
            /// <param name="endpointId">The configured source slot.</param>
            /// <param name="username">The generated account identity.</param>
            /// <param name="enabled">Whether to authorize cloud collection usage.</param>
            /// <returns>A task representing account authorization.</returns>
            public async Task EnableAccountAsync(string endpointId, string username, bool enabled = true)
            {
                var endpoint = await EndpointAsync(endpointId);
                var accounts = endpoint.Accounts.Where(account => account.Id != username).Append(new SavedEndpointAccount
                {
                    Id = username,
                    Username = username,
                    UseCloudCollections = enabled
                }).ToArray();
                await Workspace.SaveSettingsAsync((await Workspace.LoadSettingsAsync()) with
                {
                    OnlineEnabled = false,
                    Endpoints = (await Workspace.LoadSettingsAsync()).Endpoints.Select(item => item.Id == endpointId
                        ? endpoint with { Accounts = accounts, SelectedAccountId = username } : item).ToArray()
                });
                Workspace.SetOnlineEnabled(true);
                await Workspace.LoginAsync(await EndpointAsync(endpointId), username, "Generated fixture password");
            }
            /// <summary>Reads the current source configuration.</summary>
            /// <param name="id">The source slot identity.</param>
            /// <returns>The saved source configuration.</returns>
            public async Task<EndpointConfiguration> EndpointAsync(string id)
            {
                return (await Workspace.LoadSettingsAsync()).Endpoints.Single(endpoint => endpoint.Id == id);
            }
            /// <inheritdoc />
            public void Dispose()
            {
                Workspace.Dispose();
                Client.Dispose();
            }
            /// <summary>Creates one synthetic source configuration.</summary>
            /// <param name="id">The source slot identity.</param>
            /// <param name="host">The generated host prefix.</param>
            /// <returns>The source configuration.</returns>
            private static EndpointConfiguration Endpoint(string id, string host)
            {
                return new EndpointConfiguration { Id = id, Name = id, BaseUri = new Uri("https://" + host + ".example.test/api/") };
            }
            /// <summary>Creates one generated online or local library entry.</summary>
            /// <param name="id">The library reference.</param>
            /// <param name="remote">The optional synthetic chart GUID.</param>
            /// <returns>The generated entry.</returns>
            private static MusicTrack Track(string id, string? remote)
            {
                return new MusicTrack
                {
                    Id = id,
                    Title = id,
                    SourceId = "fixture-server",
                    EndpointId = remote is null ? null : "first",
                    RemoteId = remote,
                    MajChartHash = remote is not null && Guid.TryParse(remote, out _) ? FixtureTransport.Hash(remote) : null,
                    Audio = new MediaResource { RemoteUri = remote is null ? null : new Uri("https://a.example.test/api/maichart/" + remote + "/track") }
                };
            }
        }

        /// <summary>Emulates independent authenticated collection APIs without any network or credentials.</summary>
        internal sealed class FixtureTransport : HttpMessageHandler
        {
            /// <summary>Stores the first valid synthetic chart identity.</summary>
            public const string FirstChart = "11111111-1111-1111-1111-111111111111";
            /// <summary>Stores the second valid synthetic chart identity.</summary>
            public const string SecondChart = "22222222-2222-2222-2222-222222222222";
            /// <summary>Serializes the fake remote state.</summary>
            private readonly object _gate = new();
            /// <summary>Stores remote state by exact synthetic origin and collection identity.</summary>
            private readonly Dictionary<(string Host, string Id), RemoteCollection> _collections = new();
            /// <summary>Stores subscriptions independently for each authenticated principal.</summary>
            private readonly Dictionary<(string Host, string Account), HashSet<string>> _favorites = new();
            /// <summary>Identifies the next source whose update should wait.</summary>
            private string? _blockedHost;
            /// <summary>Gets observed mutation metadata without logging request cookies or passwords.</summary>
            public ConcurrentQueue<(string Host, string Account, string Method, string Path)> Writes { get; } = new();
            /// <summary>Gets the count of committed collection creations.</summary>
            public int Creates { get; private set; }
            /// <summary>Gets the count of committed collection replacements.</summary>
            public int Updates { get; private set; }
            /// <summary>Gets or sets whether one committed creation loses its response.</summary>
            public bool LoseNextCreateResponse { get; set; }
            /// <summary>Gets or sets whether one create returns HTTP 500 without committing anything.</summary>
            public bool RejectNextCreate { get; set; }
            /// <summary>Signals entry into the blocked replacement request.</summary>
            public TaskCompletionSource UpdateStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the one intentionally blocked replacement request.</summary>
            public TaskCompletionSource ReleaseUpdate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Blocks the next replacement at a particular synthetic origin.</summary>
            /// <param name="host">The exact synthetic origin.</param>
            public void BlockNextUpdate(string host)
            {
                _blockedHost = host;
                UpdateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                ReleaseUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            /// <summary>Reads a stable copy of one principal's owned collections.</summary>
            /// <param name="host">The exact synthetic origin.</param>
            /// <param name="account">The canonical principal.</param>
            /// <returns>The owned remote collection snapshot.</returns>
            public IReadOnlyList<RemoteCollection> Owned(string host, string account)
            {
                lock (_gate)
                {
                    return _collections.Where(pair => pair.Key.Host == host && pair.Value.CreatedBy == account).Select(pair => pair.Value).ToArray();
                }
            }
            /// <summary>Adds an unrelated preexisting user collection.</summary>
            /// <param name="host">The exact synthetic origin.</param>
            /// <param name="account">The canonical principal.</param>
            /// <param name="name">The user-created display name.</param>
            public void AddForeign(string host, string account, string name)
            {
                Replace(host, new RemoteCollection { Id = Guid.NewGuid().ToString("D"), Name = name, CreatedBy = account, Description = "User collection", Visibility = 0 });
            }
            /// <summary>Changes remote state as if another client had edited it.</summary>
            /// <param name="host">The exact synthetic origin.</param>
            /// <param name="collection">The replacement remote state.</param>
            public void Replace(string host, RemoteCollection collection)
            {
                lock (_gate)
                {
                    _collections[(host, collection.Id)] = collection;
                }
            }
            /// <summary>Deletes a remote mirror as if its owner removed it from the website.</summary>
            /// <param name="host">The exact synthetic origin.</param>
            /// <param name="id">The removed collection identity.</param>
            public void RemoveRemote(string host, string id)
            {
                lock (_gate)
                {
                    _collections.Remove((host, id));
                }
            }
            /// <summary>Creates a portable hash distinct from the chart GUID.</summary>
            /// <param name="id">The synthetic chart identifier.</param>
            /// <returns>A Base64 MD5 fixture value.</returns>
            public static string Hash(string id)
            {
                return Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(id)));
            }
            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var address = request.RequestUri!;
                var path = address.AbsolutePath;
                var host = address.Host;
                if (path == "/api/account/login")
                {
                    var multipart = (MultipartFormDataContent)request.Content!;
                    var username = await multipart.Single(part => part.Headers.ContentDisposition!.Name?.Trim('"') == "username").ReadAsStringAsync(cancellationToken);
                    var response = Json(new { status = 0 });
                    response.Headers.TryAddWithoutValidation("Set-Cookie", "session=" + username + "; Path=/; Secure");
                    return response;
                }

                var cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : string.Empty;
                var account = cookie.Split(';', StringSplitOptions.TrimEntries).FirstOrDefault(value => value.StartsWith("session=", StringComparison.Ordinal))?[8..] ?? "Guest";
                if (path == "/api/info")
                {
                    return Json(new { id = "fixture-server", name = "Fixture" });
                }

                if (path == "/api/account/profile")
                {
                    return Json(new { username = account });
                }

                if (path == "/api/recommend/feed" || path == "/api/maichart/list")
                {
                    return Json(Array.Empty<object>());
                }

                if (path.StartsWith("/api/maichart/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                if (request.Method == HttpMethod.Put && host == _blockedHost)
                {
                    _blockedHost = null;
                    UpdateStarted.TrySetResult();
                    await ReleaseUpdate.Task.WaitAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    var favoritesKey = (host, account);
                    if (!_favorites.TryGetValue(favoritesKey, out var favorites))
                    {
                        favorites = new HashSet<string>(StringComparer.Ordinal);
                        _favorites[favoritesKey] = favorites;
                    }

                    if (path == "/api/collection/list")
                    {
                        var page = int.Parse(Query(address, "page") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                        return Json(Owned(host, Query(address, "createdBy") ?? account).Skip(page * 100).Take(100).Select(Shape).ToArray());
                    }

                    if (path == "/api/account/favorite/collection/list")
                    {
                        return Json(favorites.Where(id => _collections.ContainsKey((host, id))).Select(id => _collections[(host, id)])
                            .Select(collection => new { id = collection.Id, name = collection.Name, description = collection.Description, songHashs = collection.Items.Select(Hash).ToArray(), isPlayList = true }).ToArray());
                    }

                    if (request.Method != HttpMethod.Get)
                    {
                        Writes.Enqueue((host, account, request.Method.Method, path));
                    }

                    if (path == "/api/collection/create")
                    {
                        if (RejectNextCreate)
                        {
                            RejectNextCreate = false;
                            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                        }

                        using var document = JsonDocument.Parse(body!);
                        var collection = new RemoteCollection
                        {
                            Id = Guid.NewGuid().ToString("D"),
                            CreatedBy = account,
                            Name = document.RootElement.GetProperty("name").GetString()!,
                            Description = document.RootElement.GetProperty("description").GetString(),
                            Visibility = document.RootElement.GetProperty("visibility").GetInt32()
                        };
                        _collections[(host, collection.Id)] = collection;
                        Creates++;
                        if (LoseNextCreateResponse)
                        {
                            LoseNextCreateResponse = false;
                            throw new HttpRequestException("Synthetic response lost after server commit.");
                        }

                        return Json(new { accepted = true });
                    }

                    if (path == "/api/account/favorite/collection/diff")
                    {
                        foreach (var id in JsonSerializer.Deserialize<string[]>(body!)!)
                        {
                            if (request.Method == HttpMethod.Post)
                            {
                                favorites.Add(id);
                            }
                            else
                            {
                                favorites.Remove(id);
                            }
                        }

                        return Json(new { });
                    }

                    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length == 4 && segments[1] == "collection" && _collections.TryGetValue((host, segments[2]), out var current))
                    {
                        if (segments[3] == "songlist")
                        {
                            return Json(Shape(current));
                        }

                        if (segments[3] == "modify")
                        {
                            TestAssert.Equal(account, current.CreatedBy, "A fake server mutation must use its owning authenticated session.");
                            using var document = JsonDocument.Parse(body!);
                            var items = document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetString()!).ToArray();
                            TestAssert.True(items.All(item => Guid.TryParse(item, out _)), "Mutation items must be chart GUIDs rather than hashes or local paths.");
                            _collections[(host, current.Id)] = current with { Name = document.RootElement.GetProperty("name").GetString()!, Items = items, Count = items.Length };
                            Updates++;
                            return Json(new { });
                        }

                        if (segments[3] == "destroy")
                        {
                            TestAssert.Equal(account, current.CreatedBy, "Collection deletion must use the owner session.");
                            _collections.Remove((host, current.Id));
                            return Json(new { });
                        }
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            }
            /// <summary>Creates the documented collection JSON shape.</summary>
            /// <param name="collection">The remote collection state.</param>
            /// <returns>The serializable metadata and chart identities.</returns>
            private static object Shape(RemoteCollection collection)
            {
                return new { id = collection.Id, name = collection.Name, createdBy = collection.CreatedBy, description = collection.Description, count = collection.Items.Count, visibility = collection.Visibility, items = collection.Items };
            }
            /// <summary>Reads one URI-encoded query value.</summary>
            /// <param name="address">The generated request URI.</param>
            /// <param name="name">The query field name.</param>
            /// <returns>The decoded value or null.</returns>
            private static string? Query(Uri address, string name)
            {
                return address.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
                    .Where(pair => pair.Length == 2 && pair[0] == name).Select(pair => Uri.UnescapeDataString(pair[1])).FirstOrDefault();
            }
            /// <summary>Creates one synthetic JSON response.</summary>
            /// <param name="value">The response payload.</param>
            /// <returns>The HTTP success response.</returns>
            private static HttpResponseMessage Json(object value)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
            }
        }

        /// <summary>Rejects accidental operating-system credential access from generated account fixtures.</summary>
        private sealed class MemoryCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("This fixture cannot read credentials.");
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("This fixture cannot write credentials.");
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("This fixture cannot delete credentials.");
            }
        }
    }
}
