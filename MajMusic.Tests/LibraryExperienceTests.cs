#nullable enable

using System.Collections.Concurrent;
using System.Globalization;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.Converters;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Exercises visible-row enrichment, independent source accounts and mixed-library workflows through the shared view model.</summary>
    internal static class LibraryExperienceTests
    {
        /// <summary>Runs bounded metadata, account, playlist and finite recommendation regressions without external resources.</summary>
        /// <returns>A task representing the completed library experience checks.</returns>
        public static async Task RunAsync()
        {
            await VerifyVisibleMetadataAsync();
            await VerifyObsoleteMetadataAsync();
            await VerifyAccountsAndCollectionsAsync();
        }

        /// <summary>Checks that unselected rows gain audio information and only the visible result page is parsed.</summary>
        /// <returns>A task representing progressive metadata and page-boundary assertions.</returns>
        private static async Task VerifyVisibleMetadataAsync()
        {
            var workspace = new ExperienceWorkspace();
            workspace.Library.AddRange(Enumerable.Range(0, 205).Select(index => Track($"local-{index:000}", $"Track {index:000}")));
            var provider = new ControlledMetadataProvider { BlockId = "local-000" };
            var backend = new ExperienceBackend();
            await using var viewModel = CreateViewModel(workspace, backend, provider);
            try
            {
                await viewModel.InitializeAsync();
                await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(viewModel.SelectedTrack is null && backend.OpenCount == 0,
                    "Library metadata preparation must not require selection or playback");
                var pageSize = viewModel.VisiblePageSize;
                TestAssert.Equal(pageSize, viewModel.VisibleTracks.Count, "A large library initially exposes one viewport-sized page");
                TestAssert.True(viewModel.VisibleTracks.All(track => track.Duration is null), "Rows can render while their metadata is pending");
                provider.Release.TrySetResult();
                await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.Equal(pageSize, provider.RequestedIds.Count, "Only the first visible page is parsed initially");
                TestAssert.True(viewModel.VisibleTracks.All(track => track.Duration == TimeSpan.FromSeconds(120)
                    && track.AudioInfo?.BitrateBitsPerSecond == 192000), "Unselected visible rows receive their duration and bitrate progressively");
                TestAssert.True(viewModel.Tracks.Skip(pageSize).All(track => track.Duration is null), "Hidden pages do not receive speculative metadata work");
                await ExecuteAsync(viewModel, viewModel.NextLibraryPageCommand);
                TestAssert.Equal(pageSize * 2, provider.RequestedIds.Count, "Opening the next page enriches only its visible entries");
                TestAssert.Equal($"local-{pageSize:000}", viewModel.VisibleTracks[0].Id, "Page navigation starts at the next ordered library entry");
                while (viewModel.NextLibraryPageCommand.CanExecute(null))
                {
                    await ExecuteAsync(viewModel, viewModel.NextLibraryPageCommand);
                }

                TestAssert.Equal(205 % pageSize, viewModel.VisibleTracks.Count, "The last page exposes only remaining entries");
                TestAssert.Equal(205, provider.RequestedIds.Count, "The last partial page does not schedule nonexistent rows");
                TestAssert.True(viewModel.SelectedTrack is null && backend.OpenCount == 0,
                    "Browsing all metadata pages must not choose or open music");
            }
            finally
            {
                provider.Release.TrySetResult();
            }
        }

        /// <summary>Checks that a slow parser from an obsolete page cannot publish into a later search.</summary>
        /// <returns>A task representing cancellation ownership and result isolation.</returns>
        private static async Task VerifyObsoleteMetadataAsync()
        {
            var workspace = new ExperienceWorkspace();
            workspace.Library.Add(Track("old", "Old result"));
            workspace.Library.Add(Track("new", "New result"));
            var provider = new ControlledMetadataProvider { BlockId = "old" };
            await using var viewModel = CreateViewModel(workspace, new ExperienceBackend(), provider);
            try
            {
                await viewModel.InitializeAsync();
                await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var obsolete = viewModel.MetadataRefresh;
                viewModel.SearchText = "New result";
                await ExecuteAsync(viewModel, viewModel.SearchCommand);
                TestAssert.True(viewModel.VisibleTracks.Select(track => track.Id).SequenceEqual(new[] { "new" }),
                    "A new search must replace the previous visible snapshot before the old parser finishes");
                TestAssert.Equal(TimeSpan.FromSeconds(120), viewModel.VisibleTracks[0].Duration,
                    "A new search can finish enrichment independently of its canceled predecessor");
                provider.Release.TrySetResult();
                await obsolete.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(viewModel.VisibleTracks.Select(track => track.Id).SequenceEqual(new[] { "new" })
                    && workspace.Library.Single(track => track.Id == "old").Duration is null,
                    "A parser ignoring cancellation cannot publish stale rows or write obsolete metadata after returning");
            }
            finally
            {
                provider.Release.TrySetResult();
            }
        }

        /// <summary>Checks independent endpoint accounts, cloud ownership, mixed local lists and finite recommendation playback.</summary>
        /// <returns>A task representing the connected account and playlist workflows.</returns>
        private static async Task VerifyAccountsAndCollectionsAsync()
        {
            var first = Endpoint("first", "First source", "https://example.invalid/api/");
            var second = Endpoint("second", "Second source", "https://example.invalid/api/");
            var guest = Endpoint("guest", "Guest source", "https://other.invalid/api/");
            var workspace = new ExperienceWorkspace
            {
                Settings = new AppSettings { OnlineEnabled = true, ActiveEndpointId = first.Id, Endpoints = [first, second, guest] }
            };
            var local = Track("local", "Local song");
            var firstSong = Track("first-song", "First online song", first);
            var firstOther = Track("first-other", "Another first song", first);
            var secondSong = Track("second-song", "Second online song", second);
            workspace.Library.AddRange([local, firstSong, firstOther, secondSong]);
            workspace.LocalPlaylists["local:favorites"] = new MusicPlaylist
            {
                Id = "local:favorites",
                Name = "我喜欢",
                IsFavorite = true,
                TrackIds = [local.Id]
            };
            workspace.LocalPlaylists["mixed"] = new MusicPlaylist { Id = "mixed", Name = "My local list" };
            workspace.Cloud[first.Id] = [CloudPlaylist(first, firstSong.Id, firstOther.Id)];
            workspace.Cloud[second.Id] = [CloudPlaylist(second, secondSong.Id)];
            var recommended = Enumerable.Range(0, 52).Select(index => Track($"recommend-{index:000}", $"Recommendation {index:000}", first)).ToArray();
            workspace.Recommendations[(first.Id, 0)] = recommended.Take(50).ToArray();
            workspace.Recommendations[(first.Id, 1)] = [recommended[49], recommended[50], recommended[51]];
            var backend = new ExperienceBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: (track, token) => workspace.ResolveAudioAsync(track, false, token));
            await using var viewModel = new MainViewModel(workspace, playback, static action => action());
            await viewModel.InitializeAsync();
            await TestAssert.ExecuteAsync(viewModel.ShowAllCommand);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(viewModel.EndpointProfiles.All(profile => !profile.IsLoggedIn), "Every saved configuration initially uses its own Guest session");
            AssertCurrentFavorites(viewModel);

            await ExecuteAsync(viewModel, viewModel.NewEndpointCommand);
            viewModel.LoginUsername = "new-account";
            viewModel.LoginPassword = "fixture-password";
            TestAssert.True(!viewModel.LoginCommand.CanExecute(null), "A new account without an endpoint address must not target an existing source");
            viewModel.LoginCommand.Execute(null);
            TestAssert.Equal(0, workspace.Logins.Count, "Missing new-source address must send no credentials to an old endpoint");
            viewModel.SelectedEndpointProfile = viewModel.EndpointProfiles.Single(profile => profile.Configuration.Id == first.Id);
            viewModel.LoginUsername = "Alice";
            viewModel.LoginPassword = "fixture-password-a";
            await ExecuteAsync(viewModel, viewModel.LoginCommand);
            TestAssert.Equal("Alice", workspace.Sessions[first.Id].Username, "The first source retains its explicit account");
            TestAssert.True(!viewModel.EndpointProfiles.Single(profile => profile.Configuration.Id == second.Id).IsLoggedIn,
                "An identical server address does not log another configuration in");
            viewModel.SelectedEndpointProfile = viewModel.EndpointProfiles.Single(profile => profile.Configuration.Id == second.Id);
            viewModel.LoginUsername = "Bob";
            viewModel.LoginPassword = "fixture-password-b";
            await ExecuteAsync(viewModel, viewModel.LoginCommand);
            TestAssert.True(workspace.Sessions[first.Id].Username == "Alice" && workspace.Sessions[second.Id].Username == "Bob"
                && !viewModel.EndpointProfiles.Single(profile => profile.Configuration.Id == guest.Id).IsLoggedIn,
                "Two independent accounts and a Guest can coexist across saved sources");
            TestAssert.Equal(string.Empty, viewModel.LoginPassword, "Completed login clears transient password input");

            viewModel.SelectedEndpointProfile = viewModel.EndpointProfiles.Single(profile => profile.Configuration.Id == first.Id);
            await ExecuteAsync(viewModel, viewModel.RefreshCloudPlaylistsCommand);
            viewModel.SelectedEndpointProfile = viewModel.EndpointProfiles.Single(profile => profile.Configuration.Id == second.Id);
            await ExecuteAsync(viewModel, viewModel.RefreshCloudPlaylistsCommand);
            var firstCloud = viewModel.Playlists.Single(playlist => playlist.EndpointId == first.Id);
            var secondCloud = viewModel.Playlists.Single(playlist => playlist.EndpointId == second.Id);
            var converter = new MusicDisplayConverter();
            var firstLabel = (string)converter.Convert(firstCloud, typeof(string), null, CultureInfo.InvariantCulture);
            var secondLabel = (string)converter.Convert(secondCloud, typeof(string), null, CultureInfo.InvariantCulture);
            TestAssert.True(firstCloud.Name == secondCloud.Name && firstLabel != secondLabel
                && firstLabel.Contains(first.Name, StringComparison.Ordinal) && secondLabel.Contains(second.Name, StringComparison.Ordinal),
                "Same-named cloud collections display their independent source names");
            viewModel.SelectedTrack = local;
            viewModel.SelectedPlaylist = firstCloud;
            TestAssert.True(!viewModel.AddToPlaylistCommand.CanExecute(null), "Cloud lists cannot accept a local list-membership save");
            viewModel.AddToPlaylistCommand.Execute(null);
            TestAssert.Equal(0, workspace.CloudSaveAttempts, "A disabled cloud action must never call local playlist persistence");

            foreach (var track in new[] { local, firstSong, secondSong })
            {
                viewModel.SelectedPlaylist = viewModel.Playlists.Single(playlist => playlist.Id == "mixed");
                viewModel.SelectedTrack = track;
                await ExecuteAsync(viewModel, viewModel.AddToPlaylistCommand);
            }

            var mixed = viewModel.Playlists.Single(playlist => playlist.Id == "mixed");
            TestAssert.True(mixed.TrackIds.SequenceEqual(new[] { local.Id, firstSong.Id, secondSong.Id })
                && mixed.ContentKind == PlaylistContentKind.Mixed, "Local lists allow any combination of local files and account sources and receive a mixed label");
            await ExecuteAsync(viewModel, viewModel.OpenFavoritesCommand);
            AssertCurrentFavorites(viewModel);
            viewModel.SelectedPlaylist = viewModel.Playlists.Single(playlist => playlist.EndpointId == first.Id);
            await ExecuteAsync(viewModel, viewModel.OpenPlaylistCommand);
            TestAssert.True(viewModel.Tracks.Select(track => track.Id).SequenceEqual(new[] { firstSong.Id, firstOther.Id }),
                "Opening a cloud collection displays its ordered membership");
            workspace.Cloud[first.Id] = [CloudPlaylist(first, firstOther.Id)];
            viewModel.SelectedEndpointProfile = viewModel.EndpointProfiles.Single(profile => profile.Configuration.Id == first.Id);
            await ExecuteAsync(viewModel, viewModel.RefreshCloudPlaylistsCommand);
            TestAssert.True(viewModel.Tracks.Select(track => track.Id).SequenceEqual(new[] { firstOther.Id }),
                "Refreshing the currently open cloud collection replaces its displayed membership");
            TestAssert.True(ReferenceEquals(viewModel.SelectedPlaylist, viewModel.Playlists.Single(playlist => playlist.EndpointId == first.Id)),
                "Cloud refresh selects the newest projected playlist object");

            var recommendationRequestsBeforeRefresh = workspace.RecommendationRequests.Count;
            await ExecuteAsync(viewModel, viewModel.RecommendationsCommand);
            TestAssert.True(viewModel.IsRecommendationsView && viewModel.Tracks.Count == 50
                && viewModel.RecommendationPageNumber == 1 && viewModel.RecommendationPageCount == 2
                && viewModel.RecommendationTotalCount == 52 && viewModel.BrowseSummary == "52 首推荐"
                && viewModel.NextRecommendationPageCommand.CanExecute(null),
                "A finite deduplicated feed displays its first fifty songs and exposes the remaining local page");
            await ExecuteAsync(viewModel, viewModel.NextRecommendationPageCommand);
            await viewModel.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(viewModel.Tracks.Select(track => track.Id).SequenceEqual(recommended.Skip(50).Select(track => track.Id))
                && viewModel.RecommendationPageNumber == 2 && !viewModel.NextRecommendationPageCommand.CanExecute(null),
                "The final local page contains only the remaining songs and cannot advance beyond the finite feed");
            TestAssert.True(workspace.RecommendationRequests.Skip(recommendationRequestsBeforeRefresh).SequenceEqual(new[] { (first.Id, 0), (first.Id, 1) }),
                "Recommendation paging is finite and tied to its owning endpoint");
            var recommendationRequestsAfterRefresh = workspace.RecommendationRequests.Count;
            viewModel.SelectedTrack = viewModel.Tracks[0];
            await viewModel.PlaySelectedAsync();
            TestAssert.True(playback.Queue.Tracks.Select(track => track.Id).SequenceEqual(recommended.Select(track => track.Id))
                && playback.Queue.Tracks.Count == 52 && backend.OpenCount == 1,
                "Recommendation playback captures the full finite feed across its display pages");
            await ExecuteAsync(viewModel, viewModel.PreviousRecommendationPageCommand);
            await viewModel.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(viewModel.RecommendationPageNumber == 1 && viewModel.Tracks.Count == 50
                && playback.Queue.Tracks.Count == 52 && playback.Queue.Current?.Id == recommended[50].Id && backend.OpenCount == 1,
                "Browsing another recommendation page must retain the playing queue and audio instance");
            await ExecuteAsync(viewModel, viewModel.NextCommand);
            TestAssert.Equal(recommended[51].Id, playback.Queue.Current?.Id,
                "Playback must continue within the finite feed even while another display page is open");
            TestAssert.Equal(recommendationRequestsAfterRefresh, workspace.RecommendationRequests.Count,
                "Queue navigation does not fetch more recommendation pages implicitly");
            viewModel.SelectedPlaylist = viewModel.Playlists.Single(playlist => playlist.Id == "mixed");
            await ExecuteAsync(viewModel, viewModel.OpenPlaylistCommand);
            TestAssert.True(!viewModel.IsRecommendationsView && viewModel.Tracks.Select(track => track.Id).SequenceEqual(mixed.TrackIds),
                "Opening a local playlist exits the recommendation snapshot");

            viewModel.SelectedPlaylist = viewModel.Playlists.Single(playlist => playlist.EndpointId == first.Id);
            await ExecuteAsync(viewModel, viewModel.RecommendationsCommand);
            await ExecuteAsync(viewModel, viewModel.GuestCommand);
            TestAssert.True(viewModel.IsRecommendationsView && viewModel.Tracks.Count == 0 && viewModel.Playlists.All(playlist => playlist.EndpointId != first.Id)
                && viewModel.Playlists.Any(playlist => playlist.EndpointId == second.Id),
                "Switching one source to Guest clears only its cloud and recommendation snapshots");
            TestAssert.True(workspace.LocalPlaylists["mixed"].TrackIds.SequenceEqual(mixed.TrackIds)
                && viewModel.Playlists.Any(playlist => playlist.Id == "mixed") && workspace.Sessions[second.Id].Username == "Bob",
                "Guest changes retain local mixed lists and other endpoint accounts");
            AssertCurrentFavorites(viewModel);
        }

        /// <summary>Checks that the local favorites selection uses the currently exposed immutable projection.</summary>
        /// <param name="viewModel">The initialized shared view model.</param>
        private static void AssertCurrentFavorites(MainViewModel viewModel)
        {
            var favorites = viewModel.Playlists.Single(playlist => playlist.Id == "local:favorites");
            TestAssert.True(ReferenceEquals(viewModel.SelectedPlaylist, favorites),
                "Default or explicit favorites selection points to the current playlist projection");
        }

        /// <summary>Runs one user command and waits for its exclusive operation and current metadata page.</summary>
        /// <param name="viewModel">The command owner.</param>
        /// <param name="command">The currently available user action.</param>
        /// <returns>A task representing command and metadata completion.</returns>
        private static async Task ExecuteAsync(MainViewModel viewModel, AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The requested library action must be available");
            command.Execute(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (viewModel.IsBusy)
            {
                await Task.Delay(1, timeout.Token);
            }

            await viewModel.TransportUpdate.WaitAsync(timeout.Token);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(3));
        }

        /// <summary>Creates the shared view model with an explicitly immediate test dispatcher.</summary>
        /// <param name="workspace">The controlled local and online workspace.</param>
        /// <param name="backend">The controlled audio owner.</param>
        /// <param name="metadata">The local metadata provider used without playback.</param>
        /// <returns>The view model owning its playback lifetime.</returns>
        private static MainViewModel CreateViewModel(ExperienceWorkspace workspace, ExperienceBackend backend, ITrackMetadataProvider metadata)
        {
            return new MainViewModel(workspace,
                new PlaybackService(backend, resolveAudioPath: (track, token) => workspace.ResolveAudioAsync(track, false, token)),
                static action => action(), metadata: metadata);
        }

        /// <summary>Creates a non-routable source configuration for account-selection tests.</summary>
        /// <param name="id">The independent account slot identity.</param>
        /// <param name="name">The displayed source name.</param>
        /// <param name="address">The illustrative API address, never contacted.</param>
        /// <returns>The saved source descriptor.</returns>
        private static EndpointConfiguration Endpoint(string id, string name, string address)
        {
            return new EndpointConfiguration { Id = id, Name = name, BaseUri = new Uri(address) };
        }

        /// <summary>Creates file-independent metadata suitable for library and account workflows.</summary>
        /// <param name="id">The stable library entry identity.</param>
        /// <param name="title">The displayed title.</param>
        /// <param name="endpoint">The optional online owner, otherwise a local-origin entry.</param>
        /// <returns>A music entry whose local audio is never opened outside the fake backend.</returns>
        private static MusicTrack Track(string id, string title, EndpointConfiguration? endpoint = null)
        {
            return new MusicTrack
            {
                Id = id,
                Title = title,
                Artist = "Fixture artist",
                RemoteId = endpoint is null ? null : id,
                EndpointId = endpoint?.Id,
                SourceId = endpoint is null ? "local" : "fixture-server",
                SourceName = endpoint?.Name,
                Revision = endpoint is null ? null : "fixture-revision",
                Duration = endpoint is null ? null : TimeSpan.FromSeconds(120),
                AudioInfo = endpoint is null ? null : new AudioStreamInfo { BitrateBitsPerSecond = 192000, SampleRateHz = 48000 },
                Audio = new MediaResource { LocalPath = Path.Combine(Path.GetTempPath(), "MajMusic-library-fixture", id + ".mp3") }
            };
        }

        /// <summary>Creates same-named account-owned collections to exercise source attribution.</summary>
        /// <param name="endpoint">The independently authenticated owner.</param>
        /// <param name="ids">The ordered online entries belonging to that owner.</param>
        /// <returns>The read-only cloud snapshot.</returns>
        private static MusicPlaylist CloudPlaylist(EndpointConfiguration endpoint, params string[] ids)
        {
            return new MusicPlaylist { Id = "cloud:" + endpoint.Id, Name = "Favorites", EndpointId = endpoint.Id, RemoteId = "favorites", TrackIds = ids };
        }

        /// <summary>Provides a controllable parser that can deliberately return after its caller has canceled.</summary>
        private sealed class ControlledMetadataProvider : ITrackMetadataProvider
        {
            /// <summary>Gets the entry that waits for explicit release.</summary>
            public string? BlockId { get; init; }
            /// <summary>Gets the observed parse requests without requiring audio file access.</summary>
            public ConcurrentQueue<string> RequestedIds { get; } = new();
            /// <summary>Signals that the deliberately blocked parse has started.</summary>
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases a deliberately slow parse even after its cancellation token is canceled.</summary>
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            public async Task<MusicTrack> EnrichAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                RequestedIds.Enqueue(track.Id);
                if (track.Id == BlockId)
                {
                    Started.TrySetResult();
                    await Release.Task;
                }
                else
                {
                    await Task.Yield();
                }

                return track with { Duration = TimeSpan.FromSeconds(120), AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 192000, SampleRateHz = 48000 } };
            }
        }

        /// <summary>Controls persistence and independently authenticated online snapshots without HTTP or disk access.</summary>
        internal class ExperienceWorkspace : IMusicWorkspace, IOnlineMusicWorkspace
        {
            /// <summary>Gets an optional controllable local query for home cancellation cases.</summary>
            public Func<string?, CancellationToken, Task<IReadOnlyList<MusicTrack>>>? SearchOverride { get; set; }
            /// <summary>Gets an optional controllable feed response for home ownership cases.</summary>
            public Func<EndpointConfiguration, int, CancellationToken, Task<IReadOnlyList<MusicTrack>>>? RecommendationOverride { get; set; }
            /// <summary>Gets or sets optional controlled cover resolution without opening source audio.</summary>
            public Func<MusicTrack, CancellationToken, Task<string?>>? ResolveCoverOverride { get; set; }
            /// <summary>Gets or sets the non-secret saved source configurations.</summary>
            public AppSettings Settings { get; set; } = new();
            /// <summary>Gets the library snapshot shared by searches, playlists and recommendation indexing.</summary>
            public List<MusicTrack> Library { get; } = new();
            /// <summary>Gets locally persisted playlists, including arbitrarily mixed members.</summary>
            public Dictionary<string, MusicPlaylist> LocalPlaylists { get; } = new(StringComparer.Ordinal);
            /// <summary>Gets the current independent endpoint account states.</summary>
            public Dictionary<string, EndpointSession> Sessions { get; } = new(StringComparer.Ordinal);
            /// <summary>Gets replaceable account-owned cloud snapshots.</summary>
            public Dictionary<string, IReadOnlyList<MusicPlaylist>> Cloud { get; } = new(StringComparer.Ordinal);
            /// <summary>Gets finite recommendation pages by account slot and page index.</summary>
            public Dictionary<(string EndpointId, int Page), IReadOnlyList<MusicTrack>> Recommendations { get; } = new();
            /// <summary>Gets attempted login destinations without storing passwords.</summary>
            public List<(string EndpointId, string Username)> Logins { get; } = new();
            /// <summary>Gets explicit recommendation page requests.</summary>
            public List<(string EndpointId, int Page)> RecommendationRequests { get; } = new();
            /// <summary>Gets the number of invalid attempts to persist cloud ownership as a local list.</summary>
            public int CloudSaveAttempts { get; private set; }
            /// <inheritdoc />
            public bool IsOnline { get; private set; }
            /// <inheritdoc />
            public string DataDirectory => Path.Combine(Path.GetTempPath(), "MajMusic-library-fixture");

            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Settings);
            }

            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var id in Sessions.Keys.ToArray())
                {
                    var previous = Settings.Endpoints.FirstOrDefault(endpoint => endpoint.Id == id);
                    if (!settings.Endpoints.Any(endpoint => endpoint.Id == id && endpoint.BaseUri == previous?.BaseUri))
                    {
                        Sessions.Remove(id);
                    }
                }

                Settings = settings;
                IsOnline = settings.OnlineEnabled;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled)
            {
                IsOnline = enabled;
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SearchOverride is not null)
                {
                    return SearchOverride(query, cancellationToken);
                }

                return Task.FromResult<IReadOnlyList<MusicTrack>>(Library.Where(track => string.IsNullOrWhiteSpace(query)
                    || track.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
            }

            /// <inheritdoc />
            public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("No fixture import was requested.");
            }

            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("No fixture scan was requested.");
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<MusicPlaylist>>(LocalPlaylists.Values.ToArray());
            }

            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (playlist.IsCloud)
                {
                    CloudSaveAttempts++;
                    throw new InvalidOperationException("Cloud snapshots cannot use local persistence.");
                }

                LocalPlaylists[playlist.Id] = playlist;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            }

            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ResolveCoverOverride?.Invoke(track, cancellationToken) ?? Task.FromResult<string?>(null);
            }

            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(track.Audio.LocalPath ?? throw new InvalidOperationException("Fixture audio path is missing."));
            }

            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public IReadOnlyList<EndpointSession> GetEndpointSessions()
            {
                return Settings.Endpoints.Select(endpoint => Sessions.GetValueOrDefault(endpoint.Id)
                    ?? new EndpointSession { EndpointId = endpoint.Id }).ToArray();
            }

            /// <inheritdoc />
            public Task LoginAsync(EndpointConfiguration endpoint, string username, string password, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logins.Add((endpoint.Id, username));
                Sessions[endpoint.Id] = new EndpointSession { EndpointId = endpoint.Id, Username = username, IsLoggedIn = true };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task UseGuestAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Sessions[endpoint.Id] = new EndpointSession { EndpointId = endpoint.Id };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetCloudPlaylistsAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Cloud.GetValueOrDefault(endpoint.Id) ?? Array.Empty<MusicPlaylist>());
            }

            /// <inheritdoc />
            public async Task<IReadOnlyList<MusicTrack>> GetRecommendationsAsync(EndpointConfiguration endpoint, int page = 0, int pageSize = 50,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecommendationRequests.Add((endpoint.Id, page));
                var tracks = RecommendationOverride is null ? Recommendations.GetValueOrDefault((endpoint.Id, page)) ?? Array.Empty<MusicTrack>()
                    : await RecommendationOverride(endpoint, page, cancellationToken);
                foreach (var track in tracks)
                {
                    if (Library.All(existing => existing.Id != track.Id))
                    {
                        Library.Add(track);
                    }
                }

                return tracks;
            }

            /// <inheritdoc />
            public Task<MusicTrack> EnrichTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(track);
            }

            /// <inheritdoc />
            public Task UpdateTrackMetadataAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = Library.FindIndex(existing => existing.Id == track.Id);
                if (index >= 0)
                {
                    Library[index] = track;
                }

                return Task.CompletedTask;
            }
        }

        /// <summary>Captures playback queue ownership without opening files, requesting media or producing audio.</summary>
        internal sealed class ExperienceBackend : IPlaybackBackend
        {
            /// <summary>Gets the number of explicitly prepared audio entries.</summary>
            public int OpenCount { get; private set; }
            /// <inheritdoc />
            public bool IsAvailable => true;
            /// <inheritdoc />
            public PlaybackCapabilities Capabilities => PlaybackCapabilities.Playback | PlaybackCapabilities.Seeking;
            /// <inheritdoc />
            public PlaybackSnapshot Snapshot { get; private set; } = new();
            /// <inheritdoc />
            public event EventHandler? StateChanged;

            /// <inheritdoc />
            public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OpenCount++;
                Snapshot = new PlaybackSnapshot { State = PlaybackState.Stopped, Duration = TimeSpan.FromSeconds(120) };
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task PlayAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SetStateAsync(PlaybackState.Playing);
            }

            /// <inheritdoc />
            public Task PauseAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SetStateAsync(PlaybackState.Paused);
            }

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SetStateAsync(PlaybackState.Stopped);
            }

            /// <inheritdoc />
            public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Snapshot = Snapshot with { Position = position };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            /// <summary>Publishes a synthetic transport state for the real playback coordinator.</summary>
            /// <param name="state">The new state owned by the controlled backend.</param>
            /// <returns>A completed transport operation.</returns>
            private Task SetStateAsync(PlaybackState state)
            {
                Snapshot = Snapshot with { State = state };
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }
        }
    }
}
