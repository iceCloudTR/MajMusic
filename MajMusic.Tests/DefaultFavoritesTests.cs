#nullable enable

using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Collections;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;
using Microsoft.Data.Sqlite;

namespace MajMusic.Tests
{
    /// <summary>Checks explicit favorites creation and conservative retirement of the old empty placeholder.</summary>
    internal static class DefaultFavoritesTests
    {
        /// <summary>Runs isolated local migrations and source-list presentation without accessing real accounts or music.</summary>
        /// <returns>A task representing empty, imported, manual and cloud-backed favorite lifetimes.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-default-favorites-" + Guid.NewGuid().ToString("N")));
            try
            {
                await ValidateExplicitCreationAsync(Path.Combine(root, "fresh"));
                await ValidateLegacyRemovalAsync(Path.Combine(root, "legacy"));
                await ValidateRetainedListsAsync(Path.Combine(root, "retained"));
                await ValidateCloudBindingAsync(Path.Combine(root, "bound"));
                await ValidateCloudPresentationAsync();
            }
            finally
            {
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                TestAssert.True(root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(root).StartsWith("MajMusic-default-favorites-", StringComparison.Ordinal),
                    "Favorite fixture cleanup must remain inside its generated temporary root.");
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>Keeps fresh storage and playlist mode genuinely empty until a user explicitly creates favorites.</summary>
        /// <param name="root">The generated application data directory.</param>
        /// <returns>A task representing command availability, null selections and explicit persistence.</returns>
        private static async Task ValidateExplicitCreationAsync(string root)
        {
            using var transport = new NoNetwork();
            using var http = new HttpClient(transport);
            using var workspace = new MusicWorkspace(root, http, http);
            var track = new MusicTrack { Id = "generated-track", Title = "Generated song", Audio = new MediaResource { LocalPath = "generated-audio" } };
            await new SqliteLibraryRepository(Path.Combine(root, "library.db")).UpsertTracksAsync([track]);
            await using var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await view.InitializeAsync();
            view.SelectedTrack = track;
            TestAssert.True(view.Playlists.Count == 0 && (await workspace.GetPlaylistsAsync()).Count == 0 && !view.HasFavorites
                && !view.ToggleFavoriteCommand.CanExecute(null) && !view.OpenFavoritesCommand.CanExecute(null),
                "A fresh library must not create a playlist or enable a favorite action that would create one implicitly.");
            view.ToggleFavoriteCommand.Execute(null);
            await view.ToggleTrackFavoriteFromMenuAsync(track);
            TestAssert.Equal(0, (await workspace.GetPlaylistsAsync()).Count, "Unavailable inspector and captured-menu actions cannot synthesize favorites.");
            view.ShowPlaylistsModeCommand.Execute(null);
            await view.HomeUpdate;
            TestAssert.True(view.IsPlaylistsMode && view.SelectedPlaylist is null && view.SelectedHomePlaylist is null && view.Tracks.Count == 0,
                "An empty playlist page must remain in its chosen mode with null selections and no stale library rows.");
            view.NewPlaylistName = "我喜欢";
            await TestAssert.ExecuteAsync(view.CreatePlaylistCommand);
            var created = view.Playlists.Single();
            TestAssert.True(created.IsFavorite && created.Id != MajCollectionService.FavoritesPlaylistId && view.HasFavorites
                && view.ToggleFavoriteCommand.CanExecute(null),
                "Explicitly creating favorites must assign a user-owned identity and enable the existing favorite action.");
            await TestAssert.ExecuteAsync(view.ToggleFavoriteCommand);
            TestAssert.True(view.IsSelectedFavorite && (await workspace.GetPlaylistsAsync()).Single().TrackIds.SequenceEqual([track.Id]),
                "The first explicit favorite action must edit the collection the user created.");
            await TestAssert.ExecuteAsync(view.ToggleFavoriteCommand);
            TestAssert.True((await workspace.GetPlaylistsAsync()).Single().Id == created.Id && view.HasFavorites,
                "Removing a manual collection's final favorite cannot retire that user-created empty collection.");
            TestAssert.Equal(0, transport.Count, "Fresh initialization, manual favorites and empty browsing remain offline.");
        }

        /// <summary>Retires only the old generated empty row without suppressing a later explicit Maj import.</summary>
        /// <param name="root">The generated application data directory.</param>
        /// <returns>A task representing durable removal and unchanged source-document import.</returns>
        private static async Task ValidateLegacyRemovalAsync(string root)
        {
            var repository = new SqliteLibraryRepository(Path.Combine(root, "library.db"));
            await repository.SavePlaylistAsync(Legacy());
            using var transport = new NoNetwork();
            using var http = new HttpClient(transport);
            using var workspace = new MusicWorkspace(root, http, http);
            TestAssert.Equal(0, (await workspace.GetPlaylistsAsync()).Count, "The unowned legacy empty default must be retired.");
            TestAssert.Equal(0, (await repository.GetPlaylistsAsync()).Count, "Retirement must persist instead of merely hiding a stored row.");
            TestAssert.True(!await repository.IsPlaylistDeletedAsync(MajCollectionService.FavoritesPlaylistId),
                "An automatic placeholder migration cannot record a user deletion that blocks later MyFavorites discovery.");
            var source = Path.Combine(root, "source", MajCollectionService.FavoritesFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            const string document = """{"Name":"Maj favorites","SongHashs":[],"IsPlayList":false}""";
            await File.WriteAllTextAsync(source, document);
            var imported = await new MajCollectionExchange(workspace).ImportFileAsync(source);
            TestAssert.True(imported.IsFavorite && (await workspace.GetPlaylistsAsync()).Single().MajCollection?.SourceFilePath == source,
                "A subsequent explicit empty Maj favorite import must remain available with its real source ownership.");
            TestAssert.Equal(document, await File.ReadAllTextAsync(source), "Retiring the placeholder and importing favorites cannot modify the original Maj file.");
            TestAssert.Equal(0, transport.Count, "Legacy retirement and Maj imports need no source request.");
        }

        /// <summary>Preserves every list that differs from the exact unowned generated placeholder.</summary>
        /// <param name="root">The parent for independent generated migration cases.</param>
        /// <returns>A task representing retained content, user identities and external ownership.</returns>
        private static async Task ValidateRetainedListsAsync(string root)
        {
            var cases = new[]
            {
                Legacy() with { TrackIds = ["unavailable-but-retained"] },
                Legacy() with { Id = Guid.NewGuid().ToString("N") },
                Legacy() with { Id = Guid.NewGuid().ToString("N"), IsFavorite = false },
                Legacy() with { Name = "My renamed favorite" },
                Legacy() with { EndpointId = "retained-source" },
                Legacy() with { MajCollection = new MajCollectionDefinition { SourceFileName = "MyFavorites.json" } },
                Legacy() with { MajCollection = new MajCollectionDefinition { Entries = [new MajCollectionEntry { Hash = "unresolved-portable-reference" }] } }
            };
            for (var index = 0; index < cases.Length; index++)
            {
                var directory = Path.Combine(root, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var repository = new SqliteLibraryRepository(Path.Combine(directory, "library.db"));
                await repository.SavePlaylistAsync(cases[index]);
                using var transport = new NoNetwork();
                using var http = new HttpClient(transport);
                using var workspace = new MusicWorkspace(directory, http, http);
                var retained = (await workspace.GetPlaylistsAsync()).Single();
                TestAssert.True(retained.Id == cases[index].Id && retained.Name == cases[index].Name
                    && retained.TrackIds.SequenceEqual(cases[index].TrackIds) && (retained.MajCollection is not null) == (cases[index].MajCollection is not null),
                    "Nonempty, renamed, manually created and imported lists must retain their data even when currently unavailable or empty.");
            }
        }

        /// <summary>Protects a legacy backing owned by any account, including a pending remote creation with no returned ID.</summary>
        /// <param name="root">The isolated generated application data directory.</param>
        /// <returns>A task representing persistent all-scope ownership protection without authentication.</returns>
        private static async Task ValidateCloudBindingAsync(string root)
        {
            await new SqliteLibraryRepository(Path.Combine(root, "library.db")).SavePlaylistAsync(Legacy());
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "cloud-collections.db"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE bindings (scope TEXT NOT NULL, playlist TEXT NOT NULL, document TEXT NOT NULL, PRIMARY KEY(scope, playlist));
                    INSERT INTO bindings VALUES ('inactive-generated-account', 'local:favorites', '{"Scope":"inactive-generated-account","PlaylistId":"local:favorites","Marker":"0123456789abcdef0123456789abcdef","CreationPending":true}');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using var transport = new NoNetwork();
            using var http = new HttpClient(transport);
            using var workspace = new MusicWorkspace(root, http, http);
            TestAssert.Equal(MajCollectionService.FavoritesPlaylistId, (await workspace.GetPlaylistsAsync()).Single().Id,
                "Any durable account binding must protect its local favorite backing from triggering a remote deletion later.");
            TestAssert.Equal(0, transport.Count, "Ownership protection must work offline without inspecting an account or authenticating.");
        }

        /// <summary>Shows an existing Maj collection directly without adding an empty local favorite or conflating same-name ownership.</summary>
        /// <returns>A task representing authenticated cloud visibility and absence of local writes.</returns>
        private static async Task ValidateCloudPresentationAsync()
        {
            var endpoint = new EndpointConfiguration { Id = "generated-maj", Name = "Maj", BaseUri = new Uri("https://favorite.example.invalid/api/") };
            var workspace = new LibraryExperienceTests.ExperienceWorkspace
            {
                Settings = new AppSettings { OnlineEnabled = true, ActiveEndpointId = endpoint.Id, Endpoints = [endpoint] }
            };
            workspace.Cloud[endpoint.Id] = [new MusicPlaylist { Id = "cloud:generated-maj:existing", Name = "我喜欢", EndpointId = endpoint.Id, RemoteId = "existing" }];
            await using var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await view.InitializeAsync();
            view.LoginUsername = "GeneratedUser";
            view.LoginPassword = "Generated fixture password";
            await ExecuteLoginAsync(view);
            await TestAssert.ExecuteAsync(view.RefreshCloudPlaylistsCommand);
            TestAssert.True(view.Playlists.Count == 1 && view.Playlists[0].IsCloud && view.Playlists[0].RemoteId == "existing"
                && workspace.LocalPlaylists.Count == 0,
                "An account's existing Maj list must appear directly without synthesizing another local list with the same name.");
            view.SelectedHomePlaylist = view.Playlists[0];
            await view.HomeUpdate;
            TestAssert.True(view.IsPlaylistsMode && view.SelectedHomePlaylist?.RemoteId == "existing",
                "The actual Maj collection must remain selectable in home playlist mode even when no local favorite exists.");
        }

        /// <summary>Observes explicit login completion even though its consumed password disables the button afterward.</summary>
        /// <param name="view">The generated account presentation owner.</param>
        /// <returns>A task representing the completed finite login operation.</returns>
        private static async Task ExecuteLoginAsync(MainViewModel view)
        {
            TestAssert.True(view.LoginCommand.CanExecute(null), "The generated explicit login must start available.");
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainViewModel.IsIdle) && view.IsIdle)
                {
                    completed.TrySetResult();
                }
            }

            view.PropertyChanged += OnChanged;
            try
            {
                view.LoginCommand.Execute(null);
                if (view.IsIdle)
                {
                    completed.TrySetResult();
                }

                await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.EndpointProfiles.Single().IsLoggedIn, "The fixture account must actually authenticate before reading Maj collections.");
            }
            finally
            {
                view.PropertyChanged -= OnChanged;
            }
        }

        /// <summary>Creates the exact empty favorite generated by old presentation initialization.</summary>
        /// <returns>The unowned fixed-identity legacy row.</returns>
        private static MusicPlaylist Legacy()
        {
            return new MusicPlaylist { Id = MajCollectionService.FavoritesPlaylistId, Name = "我喜欢", IsFavorite = true };
        }

        /// <summary>Rejects every unexpected request from these strictly local migration fixtures.</summary>
        private sealed class NoNetwork : HttpMessageHandler
        {
            /// <summary>Gets the number of unexpected source requests.</summary>
            public int Count { get; private set; }
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Count++;
                throw new InvalidOperationException("Favorite migration must not contact a source.");
            }
        }
    }
}
