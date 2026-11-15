#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks automatically saved online choices without real credentials or network access.</summary>
    internal static class OnlinePreferencesTests
    {
        /// <summary>Runs restart, rapid-choice, concurrent-edit and shutdown regressions.</summary>
        /// <returns>A task representing the completed preference cases.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-online-preferences-" + Guid.NewGuid().ToString("N"));
            try
            {
                await ValidateRestartAsync(Path.Combine(root, "restart"));
                await ValidateRapidChoicesAsync(Path.Combine(root, "rapid"));
                await ValidateConcurrentEditsAsync(Path.Combine(root, "concurrent"));
                await ValidateCloseFlushAsync(Path.Combine(root, "close"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var resolved = Path.GetFullPath(root);
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("MajMusic-online-preferences-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated online-preference fixture.");
                }

                if (Directory.Exists(resolved))
                {
                    Directory.Delete(resolved, true);
                }
            }
        }

        /// <summary>Restores saved enabled and disabled choices while preserving an independent Guest selection.</summary>
        /// <param name="root">The explicit isolated application-data location.</param>
        /// <returns>A task representing restart persistence.</returns>
        private static async Task ValidateRestartAsync(string root)
        {
            using var transport = new RejectingTransport();
            using var client = new HttpClient(transport);
            var vault = new RejectingCredentials();
            var store = new TrackingStore(Path.Combine(root, "settings.json"));
            using (var workspace = new MusicWorkspace(root, client, client, store, vault))
            {
                var settings = await workspace.LoadSettingsAsync();
                TestAssert.True(!settings.OnlineEnabled && !workspace.IsOnline, "A new installation must still start offline.");
                var endpoint = Endpoint() with
                {
                    Accounts = [new SavedEndpointAccount { Id = "remembered", Username = "Remembered", RememberPassword = true, AutoLogin = true }],
                    SelectedAccountId = null
                };
                await workspace.SaveSettingsAsync(settings with { Endpoints = [endpoint] });
                var savesBeforeInitialization = store.SaveCount;
                await using var view = CreateView(workspace);
                await view.InitializeAsync();
                TestAssert.Equal(savesBeforeInitialization, store.SaveCount, "Loading the checkbox cannot save its default over existing preferences.");
                view.OnlineEnabled = true;
                await view.OnlineSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True((await store.LoadAsync()).OnlineEnabled, "Enabling online mode must save without an additional save action.");
            }

            using (var workspace = new MusicWorkspace(root, client, client, store, vault))
            {
                await using var view = CreateView(workspace);
                await view.InitializeAsync();
                await view.AutomaticLogin.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.OnlineEnabled && workspace.IsOnline, "Restart must restore an explicitly saved enabled choice.");
                TestAssert.True((await workspace.LoadSettingsAsync()).Endpoints.Single().SelectedAccountId is null,
                    "Restoring online mode must retain Guest instead of selecting a remembered automatic account.");
                view.OnlineEnabled = false;
                await view.OnlineSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            }

            using (var workspace = new MusicWorkspace(root, client, client, store, vault))
            {
                await using var view = CreateView(workspace);
                await view.InitializeAsync();
                TestAssert.True(!view.OnlineEnabled && !workspace.IsOnline, "Restart must also restore an explicitly saved disabled choice.");
            }

            TestAssert.True(transport.Requests == 0 && vault.Calls == 0,
                "Saving and restoring an online Guest choice cannot send requests or access the system credential vault.");
        }

        /// <summary>Preserves the final checkbox value when a previous save is still writing.</summary>
        /// <param name="root">The explicit isolated application-data location.</param>
        /// <returns>A task representing merged rapid edits.</returns>
        private static async Task ValidateRapidChoicesAsync(string root)
        {
            using var transport = new RejectingTransport();
            using var client = new HttpClient(transport);
            var store = new TrackingStore(Path.Combine(root, "settings.json"));
            using var workspace = new MusicWorkspace(root, client, client, store, new RejectingCredentials());
            await using var view = CreateView(workspace);
            await view.InitializeAsync();
            store.PauseNextSave();
            view.OnlineEnabled = true;
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                for (var index = 0; index < 60; index++)
                {
                    view.OnlineEnabled = index % 2 == 0;
                }

                view.OnlineEnabled = false;
                TestAssert.True(!workspace.IsOnline, "Disabling online mode must apply immediately while an older disk write is blocked.");
            }
            finally
            {
                store.Release.TrySetResult();
            }

            await view.OnlineSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(!(await store.LoadAsync()).OnlineEnabled && !view.OnlineEnabled && !workspace.IsOnline,
                "The final rapid checkbox choice must own both the saved and active permission.");
            TestAssert.True(store.SaveCount <= 3, "A burst of checkbox changes must use one bounded worker instead of one save per event.");
        }

        /// <summary>Saves online mode alongside independent audio, account and directory edits without replacing their snapshots.</summary>
        /// <param name="root">The explicit isolated application-data location.</param>
        /// <returns>A task representing serialized narrow settings updates.</returns>
        private static async Task ValidateConcurrentEditsAsync(string root)
        {
            using var transport = new RejectingTransport();
            using var client = new HttpClient(transport);
            var vault = new RejectingCredentials();
            var store = new TrackingStore(Path.Combine(root, "settings.json"));
            using var workspace = new MusicWorkspace(root, client, client, store, vault);
            var loaded = await workspace.LoadSettingsAsync();
            var endpoint = Endpoint();
            var original = new LocalDirectorySource { Id = "original", Path = Path.Combine(root, "original-songs") };
            await workspace.SaveSettingsAsync(loaded with
            {
                Endpoints = [endpoint],
                LocalDirectorySources = [original],
                AudioOutput = new AudioOutputPreferences { Volume = 81 }
            });
            await using var view = CreateView(workspace);
            await view.InitializeAsync();
            store.PauseNextSave();
            view.OnlineEnabled = true;
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var audio = ((IPlaybackPreferencesWorkspace)workspace).UpdateAudioOutputAsync(new AudioOutputPreferences { Volume = 37, Muted = true });
            var account = workspace.SaveAccountAsync(endpoint, "Independent account", null, false, false);
            view.OnlineEnabled = false;
            view.OnlineEnabled = true;
            store.Release.TrySetResult();
            await Task.WhenAll(audio, account, view.OnlineSettingsUpdate).WaitAsync(TimeSpan.FromSeconds(3));
            var current = await workspace.LoadSettingsAsync();
            var index = new LocalDirectorySource { Id = "index", Path = Path.Combine(root, "portable-index"), Kind = LocalDirectoryKind.CollectionIndex };
            var directorySave = workspace.SaveSettingsAsync(current with { LocalDirectorySources = [original, index] });
            var onlineSave = ((IOnlinePreferencesWorkspace)workspace).UpdateOnlineEnabledAsync(false);
            await Task.WhenAll(directorySave, onlineSave).WaitAsync(TimeSpan.FromSeconds(3));
            var persisted = await store.LoadAsync();
            TestAssert.True(!persisted.OnlineEnabled && persisted.AudioOutput is { Volume: 37, Muted: true },
                "Online-mode persistence cannot overwrite independently accepted audio preferences.");
            TestAssert.Equal("Independent account", persisted.Endpoints.Single().Accounts.Single().Username,
                "Online-mode persistence cannot replace an independently saved account snapshot.");
            TestAssert.True(persisted.LocalDirectorySources.Select(item => item.Id).SequenceEqual(["original", "index"]),
                "Online-mode persistence cannot discard a directory update queued alongside it.");
            TestAssert.True(transport.Requests == 0 && vault.Calls == 0,
                "Concurrent local preferences must not authenticate, fetch media or read real credentials.");
        }

        /// <summary>Drains the final local save even when the view closes immediately after a checkbox edit.</summary>
        /// <param name="root">The explicit isolated application-data location.</param>
        /// <returns>A task representing close-time persistence.</returns>
        private static async Task ValidateCloseFlushAsync(string root)
        {
            using var transport = new RejectingTransport();
            using var client = new HttpClient(transport);
            var store = new TrackingStore(Path.Combine(root, "settings.json"));
            using var workspace = new MusicWorkspace(root, client, client, store, new RejectingCredentials());
            var view = CreateView(workspace);
            await view.InitializeAsync();
            view.OnlineEnabled = true;
            await view.DisposeAsync();
            TestAssert.True((await store.LoadAsync()).OnlineEnabled, "Closing the view must drain its final online preference save.");
        }

        /// <summary>Creates a preference-only view without a native audio backend.</summary>
        /// <param name="workspace">The explicit test workspace.</param>
        /// <returns>The test-owned presentation model.</returns>
        private static MainViewModel CreateView(MusicWorkspace workspace)
        {
            return new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
        }

        /// <summary>Creates an illustrative endpoint without a network connection.</summary>
        /// <returns>The saved endpoint descriptor.</returns>
        private static EndpointConfiguration Endpoint()
        {
            return new EndpointConfiguration { Id = "source", Name = "Source", BaseUri = new Uri("https://example.invalid/api/") };
        }

        /// <summary>Blocks one local save to expose ordering and coalescing without timing assumptions.</summary>
        private sealed class TrackingStore : ISettingsStore
        {
            /// <summary>Writes actual atomic fixture settings files.</summary>
            private readonly JsonSettingsStore _inner;
            /// <summary>Counts local publication attempts.</summary>
            private int _saveCount;
            /// <summary>Marks the next publication for explicit release.</summary>
            private int _pause;
            /// <summary>Creates a store beneath the explicit fixture directory.</summary>
            /// <param name="path">The generated settings path.</param>
            public TrackingStore(string path)
            {
                _inner = new JsonSettingsStore(path);
            }
            /// <summary>Gets the number of local save attempts.</summary>
            public int SaveCount => Volatile.Read(ref _saveCount);
            /// <summary>Signals that the requested blocked save has started.</summary>
            public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the requested blocked save.</summary>
            public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Configures one deterministic blocked publication.</summary>
            public void PauseNextSave()
            {
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                Interlocked.Increment(ref _saveCount);
                if (Interlocked.Exchange(ref _pause, 0) == 1)
                {
                    Started.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }

                await _inner.SaveAsync(settings, cancellationToken);
            }
        }

        /// <summary>Rejects requests so no fixture can contact a real endpoint.</summary>
        private sealed class RejectingTransport : HttpMessageHandler
        {
            /// <summary>Gets the number of unexpected request attempts.</summary>
            public int Requests { get; private set; }
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                throw new InvalidOperationException("Local online preferences attempted a network request.");
            }
        }

        /// <summary>Rejects vault access so settings tests never touch real credentials.</summary>
        private sealed class RejectingCredentials : ICredentialStore
        {
            /// <summary>Gets the number of unexpected credential operations.</summary>
            public int Calls { get; private set; }
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                Calls++;
                throw new InvalidOperationException("Local online preferences attempted a credential read.");
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                Calls++;
                throw new InvalidOperationException("Local online preferences attempted a credential write.");
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                Calls++;
                throw new InvalidOperationException("Local online preferences attempted a credential deletion.");
            }
        }
    }
}
