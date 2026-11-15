#nullable enable

using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks independent directory configuration through the real shared view model and persistent workspace.</summary>
    internal static class DirectoryPresentationTests
    {
        /// <summary>Runs explicit source-role, editing, removal, restart and delayed-binding UI regressions.</summary>
        /// <returns>A task representing all directory presentation checks.</returns>
        public static async Task RunAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-directory-ui-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                await ValidateIndependentConfigurationAsync(Path.Combine(root, "configuration"));
                await ValidateIndexBeforeMusicAsync(Path.Combine(root, "reverse-order"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("MajMusic-directory-ui-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the directory-presentation fixture.");
                }

                Directory.Delete(root, true);
            }
        }

        /// <summary>Uses explicit directory controls to isolate music discovery from index discovery and retain user data.</summary>
        /// <param name="root">The isolated source and application-data fixture.</param>
        /// <returns>A task representing role editing, persistence and source preservation.</returns>
        private static async Task ValidateIndependentConfigurationAsync(string root)
        {
            var music = Path.Combine(root, "source", "music");
            var song = Path.Combine(music, "nested", "song");
            var hash = CreateSong(song, "Directory UI song");
            var index = Path.Combine(music, "majcollection");
            Directory.CreateDirectory(index);
            await WriteCollectionAsync(Path.Combine(index, "embedded.json"), "Explicit embedded list", hash);
            var before = Snapshot(Path.Combine(root, "source"));
            var data = Path.Combine(root, "data");
            using var handler = new RejectingTransport();
            using var transport = new HttpClient(handler);
            var vault = new RejectingCredentialStore();

            using (var workspace = new MusicWorkspace(data, transport, transport, credentialStore: vault))
            {
                await using var viewModel = CreateViewModel(workspace);
                await viewModel.InitializeAsync();
                viewModel.ShowLibraryModeCommand.Execute(null);
                await viewModel.HomeUpdate;
                TestAssert.True(!viewModel.OnlineEnabled && !workspace.IsOnline, "The directory page must open in offline mode.");

                await ConfigureDirectoryAsync(viewModel, music, LocalDirectoryKind.MusicLibrary);
                TestAssert.Equal(1, viewModel.DirectorySources.Count, "Saving a music directory creates exactly one explicit configuration.");
                TestAssert.Equal(LocalDirectoryKind.MusicLibrary, viewModel.DirectorySources.Single().Kind,
                    "Saving a music root cannot infer a collection-index configuration.");
                TestAssert.Equal(1, viewModel.Tracks.Count, "The music-only directory appears in the actual shared library.");
                TestAssert.True(!viewModel.Playlists.Any(item => item.Name == "Explicit embedded list"),
                    "An embedded majcollection must remain unread until it has an explicit index configuration.");
                TestAssert.Equal(Path.Combine(song, "track.mp3"), viewModel.Tracks.Single().Audio.LocalPath,
                    "The directory UI retains original source audio references.");

                await ConfigureDirectoryAsync(viewModel, index, LocalDirectoryKind.CollectionIndex);
                TestAssert.Equal(2, viewModel.DirectorySources.Count, "A separate index source is configured independently of its music root.");
                var imported = viewModel.Playlists.Single(item => item.Name == "Explicit embedded list");
                TestAssert.Equal(1, imported.TrackIds.Count, "An explicitly configured index binds the indexed music.");
                TestAssert.Equal(PlaylistContentKind.Local, imported.ContentKind, "The imported playlist receives its current local-origin badge.");
                var indexId = viewModel.DirectorySources.Single(item => item.Kind == LocalDirectoryKind.CollectionIndex).Id;
                var edited = imported with { Name = "Locally edited list", TrackIds = Array.Empty<string>() };
                await workspace.SavePlaylistAsync(edited);

                viewModel.SelectedDirectory = viewModel.DirectorySources.Single(item => item.Id == indexId);
                viewModel.SelectedDirectoryKind = viewModel.DirectoryKinds.Single(item => item.Kind == LocalDirectoryKind.MusicLibrary);
                await ExecuteAsync(viewModel, viewModel.SaveDirectoryCommand);
                TestAssert.Equal(LocalDirectoryKind.MusicLibrary, viewModel.DirectorySources.Single(item => item.Id == indexId).Kind,
                    "Editing a configured source type preserves its stable configuration identity.");
                var retained = viewModel.Playlists.Single(item => item.Id == imported.Id);
                TestAssert.Equal("Locally edited list", retained.Name, "Changing a directory's role cannot replace a locally edited playlist name.");
                TestAssert.Equal(0, retained.TrackIds.Count, "Changing a role cannot restore music deliberately removed from a local playlist.");
                await ExecuteAsync(viewModel, viewModel.RescanCommand);
                TestAssert.Equal("Locally edited list", viewModel.Playlists.Single(item => item.Id == imported.Id).Name,
                    "Rescanning music-only sources cannot replay embedded index documents.");

                while (viewModel.DirectorySources.Count > 0)
                {
                    viewModel.SelectedDirectory = viewModel.DirectorySources[0];
                    await ExecuteAsync(viewModel, viewModel.RemoveDirectoryCommand);
                }

                TestAssert.Equal(0, viewModel.DirectorySources.Count, "Removing all directory configurations leaves an empty source list.");
                TestAssert.Equal(1, viewModel.Tracks.Count, "Removing source configuration cannot delete indexed music.");
                TestAssert.Equal("Locally edited list", viewModel.Playlists.Single(item => item.Id == imported.Id).Name,
                    "Removing source configuration cannot delete locally edited lists.");
                TestAssert.Equal(0, (await workspace.LoadSettingsAsync()).LocalMusicDirectories.Count,
                    "The directory editor clears migrated legacy paths after publishing the independent source layout.");
                TestAssert.True(!Directory.Exists(Path.Combine(data, "Library")), "The directory UI cannot create copied audio imports.");
            }

            using (var reopened = new MusicWorkspace(data, transport, transport, credentialStore: vault))
            {
                await using var viewModel = CreateViewModel(reopened);
                await viewModel.InitializeAsync();
                viewModel.ShowLibraryModeCommand.Execute(null);
                await viewModel.HomeUpdate;
                TestAssert.Equal(0, viewModel.DirectorySources.Count, "Removed source configurations cannot reappear after restart.");
                TestAssert.Equal(1, viewModel.Tracks.Count, "Indexed original-file references survive source removal and restart.");
                var local = viewModel.Playlists.Single(item => item.Name == "Locally edited list");
                TestAssert.Equal(0, local.TrackIds.Count, "Locally edited playlist contents survive restart unchanged.");
                TestAssert.True(!viewModel.OnlineEnabled && !reopened.IsOnline, "Restoring directory configuration cannot enable online mode.");
            }

            TestAssert.True(before.SequenceEqual(Snapshot(Path.Combine(root, "source"))),
                "Directory configuration, scans, edits and removals leave original source bytes, paths, timestamps and attributes unchanged.");
            TestAssert.Equal(0, handler.RequestCount, "The complete directory UI workflow must send zero HTTP requests.");
            TestAssert.Equal(0, vault.CallCount, "Directory configuration cannot access an account credential vault.");
        }

        /// <summary>Binds a list configured before its music through the same public directory controls.</summary>
        /// <param name="root">The reverse-order source fixture.</param>
        /// <returns>A task representing saved-reference binding after a later music source is configured.</returns>
        private static async Task ValidateIndexBeforeMusicAsync(string root)
        {
            var music = Path.Combine(root, "music");
            var hash = CreateSong(Path.Combine(music, "song"), "Configured later");
            var index = Path.Combine(root, "portable-lists");
            Directory.CreateDirectory(index);
            var json = Path.Combine(index, "before-music.json");
            await WriteCollectionAsync(json, "List before music", hash);
            using var handler = new RejectingTransport();
            using var transport = new HttpClient(handler);
            var vault = new RejectingCredentialStore();
            using var workspace = new MusicWorkspace(Path.Combine(root, "data"), transport, transport, credentialStore: vault);
            await using var viewModel = CreateViewModel(workspace);
            await viewModel.InitializeAsync();
            viewModel.ShowLibraryModeCommand.Execute(null);
            await viewModel.HomeUpdate;
            await ConfigureDirectoryAsync(viewModel, index, LocalDirectoryKind.CollectionIndex);
            TestAssert.Equal(0, viewModel.Tracks.Count, "An index-only configuration cannot discover its future music source.");
            var unknown = viewModel.Playlists.Single(item => item.Name == "List before music");
            TestAssert.Equal(0, unknown.TrackIds.Count, "A preconfigured index retains its unavailable music reference.");
            TestAssert.Equal(1, unknown.UnresolvedCount, "The list badge counts the unavailable reference before music indexing.");

            await File.WriteAllTextAsync(json, "{ invalid current fixture to detect an unintended index reread");
            var invalidSource = File.ReadAllBytes(json);
            await ConfigureDirectoryAsync(viewModel, music, LocalDirectoryKind.MusicLibrary);
            var bound = viewModel.Playlists.Single(item => item.Id == unknown.Id);
            TestAssert.Equal(1, bound.TrackIds.Count, "Adding a music source rebinds the stored unavailable reference.");
            TestAssert.Equal(0, bound.UnresolvedCount, "The shared list badge is refreshed after delayed source binding.");
            TestAssert.Equal(PlaylistContentKind.Local, bound.ContentKind, "The newly bound list is classified as local music.");
            TestAssert.Equal("List before music", bound.Name, "Music indexing uses saved index metadata instead of rereading a changed source JSON.");
            TestAssert.True(invalidSource.SequenceEqual(File.ReadAllBytes(json)), "Delayed list binding cannot rewrite the index source.");
            TestAssert.Equal(0, handler.RequestCount, "Configuring indexes before music remains entirely offline.");
            TestAssert.Equal(0, vault.CallCount, "Delayed local list binding does not read account credentials.");
        }

        /// <summary>Composes the actual directory service into a view model with no available audio device.</summary>
        /// <param name="workspace">The isolated persistent application workspace.</param>
        /// <returns>The test-owned shared view model.</returns>
        private static MainViewModel CreateViewModel(MusicWorkspace workspace)
        {
            var exchange = new MajCollectionExchange(workspace);
            return new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), action => action(),
                collections: exchange, directories: new LocalDirectoryService(workspace, exchange));
        }

        /// <summary>Uses the same new/type/path/save controls as the directory editor.</summary>
        /// <param name="viewModel">The shared view model.</param>
        /// <param name="path">The selected absolute source directory.</param>
        /// <param name="kind">The explicitly selected source role.</param>
        /// <returns>A task representing configuration and its first scan.</returns>
        private static async Task ConfigureDirectoryAsync(MainViewModel viewModel, string path, LocalDirectoryKind kind)
        {
            await ExecuteAsync(viewModel, viewModel.NewDirectoryCommand);
            viewModel.DirectoryPath = path;
            viewModel.SelectedDirectoryKind = viewModel.DirectoryKinds.Single(item => item.Kind == kind);
            await ExecuteAsync(viewModel, viewModel.SaveDirectoryCommand);
            await viewModel.MetadataRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        }

        /// <summary>Waits for one observed asynchronous UI command without polling or bypassing its availability rules.</summary>
        /// <param name="viewModel">The command owner.</param>
        /// <param name="command">The public command requested by the case.</param>
        /// <returns>A task representing the command's return to idle.</returns>
        private static async Task ExecuteAsync(MainViewModel viewModel, AsyncCommand command)
        {
            var availability = System.Diagnostics.Stopwatch.StartNew();
            while (!command.CanExecute(null) && availability.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(1);
            }

            TestAssert.True(command.CanExecute(null), "The directory editor must permit the requested command.");
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainViewModel.IsBusy) && !viewModel.IsBusy)
                {
                    idle.TrySetResult();
                }
            }

            viewModel.PropertyChanged += OnChanged;
            try
            {
                command.Execute(null);
                if (!viewModel.IsBusy)
                {
                    idle.TrySetResult();
                }

                await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
                TestAssert.True(!viewModel.Status.StartsWith("操作未完成", StringComparison.Ordinal),
                    "The directory command must complete rather than only reporting a swallowed operation failure.");
            }
            finally
            {
                viewModel.PropertyChanged -= OnChanged;
            }
        }

        /// <summary>Creates a read-only indexing fixture whose audio need not be decoded.</summary>
        /// <param name="directory">The test-owned song directory.</param>
        /// <param name="title">The authoritative maidata title.</param>
        /// <returns>The exact portable maidata hash.</returns>
        private static string CreateSong(string directory, string title)
        {
            Directory.CreateDirectory(directory);
            var bytes = Encoding.UTF8.GetBytes("&title=" + title + "\n&artist=Directory fixture\n");
            File.WriteAllBytes(Path.Combine(directory, "maidata.txt"), bytes);
            File.WriteAllBytes(Path.Combine(directory, "track.mp3"), "indexed audio fixture"u8.ToArray());
            return Convert.ToBase64String(MD5.HashData(bytes));
        }

        /// <summary>Creates a portable Maj document inside a generated source fixture.</summary>
        /// <param name="path">The fixture file path.</param>
        /// <param name="name">The list name.</param>
        /// <param name="hash">The exact maidata identity.</param>
        /// <returns>A task representing test fixture creation.</returns>
        private static Task WriteCollectionAsync(string path, string name, string hash)
        {
            return File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Name = name, SongHashs = new[] { hash }, IsPlayList = true }));
        }

        /// <summary>Captures content and metadata signatures of the small generated source tree.</summary>
        /// <param name="root">The test-owned source tree.</param>
        /// <returns>Stable source signatures suitable for exact read-only comparisons.</returns>
        private static string[] Snapshot(string root)
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            {
                var file = new FileInfo(path);
                return Path.GetRelativePath(root, path) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks + "|"
                    + file.Attributes + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }).ToArray();
        }

        /// <summary>Rejects all network activity during purely local directory UI checks.</summary>
        private sealed class RejectingTransport : HttpMessageHandler
        {
            /// <summary>Gets how many attempted requests reached the transport.</summary>
            public int RequestCount { get; private set; }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                throw new InvalidOperationException("A directory presentation operation attempted a network request.");
            }
        }

        /// <summary>Rejects credential access so local directory operations cannot touch a real system vault.</summary>
        private sealed class RejectingCredentialStore : ICredentialStore
        {
            /// <summary>Gets the number of unexpected credential operations.</summary>
            public int CallCount { get; private set; }

            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                CallCount++;
                throw new InvalidOperationException("Directory configuration attempted to read an account credential.");
            }

            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                CallCount++;
                throw new InvalidOperationException("Directory configuration attempted to store an account credential.");
            }

            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                CallCount++;
                throw new InvalidOperationException("Directory configuration attempted to remove an account credential.");
            }
        }
    }
}
