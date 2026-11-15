#nullable enable

using Avalonia.Headless;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using SkiaSharp;

namespace MajMusic.Tests
{
    /// <summary>Checks cover continuity and request ownership independently of changing music-row metadata.</summary>
    internal static class CoverPresentationTests
    {
        /// <summary>Validates loaded and pending reuse, version replacement, retry, stale results and shutdown cleanup.</summary>
        /// <returns>A task representing the completed cover regressions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "MajMusic-cover-ui-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var first = WriteCover(root, "first.png", SKColors.CornflowerBlue);
                    var second = WriteCover(root, "second.png", SKColors.Coral);
                    await ValidateLoadedReuseAsync(first);
                    await ValidatePendingReuseAsync(first);
                    await ValidateVersionReplacementAsync(first, second);
                    await ValidateRemovedCoverAsync(first);
                    await ValidateRetryAsync(first);
                    await ValidateStaleSelectionAsync(first, second);
                    await ValidateSharedShutdownAsync();
                    return true;
                }
                finally
                {
                    var fullRoot = Path.GetFullPath(root);
                    var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!fullRoot.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(fullRoot).StartsWith("MajMusic-cover-ui-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Refusing to clean outside the cover-test directory.");
                    }

                    Directory.Delete(fullRoot, true);
                }
            }, timeout.Token);
        }

        /// <summary>Clears a same-song image when its refreshed descriptor no longer provides a cover.</summary>
        /// <param name="path">The original generated cover.</param>
        /// <returns>A task representing an explicitly removed cover.</returns>
        private static async Task ValidateRemovedCoverAsync(string path)
        {
            var workspace = new CoverWorkspace((track, _) => Task.FromResult(track.Cover is null ? null : path));
            await using var view = CreateView(workspace);
            view.SelectedTrack = Track("removed", path);
            await view.LoadCoverAsync();
            TestAssert.True(view.Cover is not null, "The original descriptor must display its image.");
            view.SelectedTrack = view.SelectedTrack! with { Cover = null };
            await view.LoadCoverAsync();
            TestAssert.True(view.Cover is null, "A removed cover must leave the inspector empty instead of retaining an obsolete image.");
            TestAssert.Equal(2, workspace.ResolveCount, "Removing the cover changes its resource identity once.");
        }

        /// <summary>Retains the same bitmap when audio metadata changes for the inspected song.</summary>
        /// <param name="path">The local generated cover fixture.</param>
        /// <returns>A task representing loaded-cover reuse and explicit clearing.</returns>
        private static async Task ValidateLoadedReuseAsync(string path)
        {
            var workspace = new CoverWorkspace((_, _) => Task.FromResult<string?>(path));
            await using var view = CreateView(workspace);
            view.SelectedTrack = Track("same", path);
            await view.LoadCoverAsync();
            var bitmap = view.Cover ?? throw new InvalidOperationException("The fixture did not produce a cover.");
            var emptyTransitions = 0;
            view.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.Cover) && view.Cover is null)
                {
                    emptyTransitions++;
                }
            };
            for (var index = 0; index < 4; index++)
            {
                view.SelectedTrack = view.SelectedTrack! with
                {
                    Title = "Refreshed title " + index,
                    Duration = TimeSpan.FromSeconds(120 + index),
                    AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 192000 + index },
                    Cover = view.SelectedTrack!.Cover! with { }
                };
                await view.LoadCoverAsync();
            }

            TestAssert.Equal(1, workspace.ResolveCount, "Metadata replacements must not resolve the same loaded cover again.");
            TestAssert.True(ReferenceEquals(bitmap, view.Cover), "Metadata replacements must retain the already decoded bitmap.");
            TestAssert.Equal(0, emptyTransitions, "A loaded cover must not flash empty between metadata updates.");
            view.SelectedTrack = null;
            await view.LoadCoverAsync();
            TestAssert.True(view.Cover is null, "Clearing the inspected song must clear its cover.");
            TestAssert.Equal(1, emptyTransitions, "Explicitly clearing selection produces one empty transition.");
        }

        /// <summary>Shares an unfinished cover read across repeated selection notifications.</summary>
        /// <param name="path">The local generated cover fixture.</param>
        /// <returns>A task representing coalesced request completion.</returns>
        private static async Task ValidatePendingReuseAsync(string path)
        {
            var started = Signal();
            var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken requestToken = default;
            var workspace = new CoverWorkspace((_, token) =>
            {
                requestToken = token;
                started.TrySetResult();
                return release.Task.WaitAsync(token);
            });
            await using var view = CreateView(workspace);
            try
            {
                view.SelectedTrack = Track("pending", path);
                var first = view.LoadCoverAsync();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                view.SelectedTrack = view.SelectedTrack! with { Duration = TimeSpan.FromSeconds(83) };
                var second = view.LoadCoverAsync();
                TestAssert.Equal(1, workspace.ResolveCount, "The same pending cover must have one resource request.");
                TestAssert.True(!first.IsCompleted && !second.IsCompleted && !requestToken.IsCancellationRequested,
                    "A duplicate notification must join the pending read rather than cancel it.");
                release.TrySetResult(path);
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.Cover is not null, "The shared request must publish its bitmap once ready.");
            }
            finally
            {
                release.TrySetResult(path);
            }
        }

        /// <summary>Replaces a changed descriptor without clearing the current song's valid image while loading.</summary>
        /// <param name="firstPath">The first cover version.</param>
        /// <param name="secondPath">The replacement cover version.</param>
        /// <returns>A task representing an atomic same-song cover replacement.</returns>
        private static async Task ValidateVersionReplacementAsync(string firstPath, string secondPath)
        {
            var started = Signal();
            var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workspace = new CoverWorkspace((track, token) =>
            {
                if (track.Cover?.CacheVersion == "v2")
                {
                    started.TrySetResult();
                    return release.Task.WaitAsync(token);
                }

                return Task.FromResult<string?>(firstPath);
            });
            await using var view = CreateView(workspace);
            try
            {
                view.SelectedTrack = Track("versioned", firstPath);
                await view.LoadCoverAsync();
                var original = view.Cover;
                view.SelectedTrack = view.SelectedTrack! with
                {
                    Cover = new MediaResource { LocalPath = secondPath, CacheVersion = "v2" }
                };
                var replacing = view.LoadCoverAsync();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(original is not null && ReferenceEquals(original, view.Cover),
                    "A same-song version update must retain the previous bitmap until its replacement is decoded.");
                release.TrySetResult(secondPath);
                await replacing.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(view.Cover is not null && !ReferenceEquals(original, view.Cover),
                    "A changed cover descriptor must publish a fresh bitmap.");
                TestAssert.Equal(2, workspace.ResolveCount, "Each actual cover version must be read once.");
            }
            finally
            {
                release.TrySetResult(secondPath);
            }
        }

        /// <summary>Permits a failed cover to be requested again without changing the song identity.</summary>
        /// <param name="path">The successful retry fixture.</param>
        /// <returns>A task representing failure recovery.</returns>
        private static async Task ValidateRetryAsync(string path)
        {
            var attempts = 0;
            var workspace = new CoverWorkspace((_, _) => ++attempts == 1
                ? Task.FromException<string?>(new IOException("Generated cover read failure.")) : Task.FromResult<string?>(path));
            await using var view = CreateView(workspace);
            view.SelectedTrack = Track("retry", path);
            await view.LoadCoverAsync();
            TestAssert.True(view.Cover is null, "A failed read must leave an empty cover when no prior version exists.");
            await view.LoadCoverAsync();
            TestAssert.True(view.Cover is not null && workspace.ResolveCount == 2, "A failed cover read must remain retryable.");
        }

        /// <summary>Rejects a late result from a previously selected song even when its transport ignores cancellation.</summary>
        /// <param name="firstPath">The old song's late cover.</param>
        /// <param name="secondPath">The current song's cover.</param>
        /// <returns>A task representing stale selection rejection.</returns>
        private static async Task ValidateStaleSelectionAsync(string firstPath, string secondPath)
        {
            var started = Signal();
            var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workspace = new CoverWorkspace((track, _) =>
            {
                if (track.Id == "old")
                {
                    started.TrySetResult();
                    return release.Task;
                }

                return Task.FromResult<string?>(secondPath);
            });
            await using var view = CreateView(workspace);
            try
            {
                view.SelectedTrack = Track("old", firstPath);
                var obsolete = view.LoadCoverAsync();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                view.SelectedTrack = Track("current", secondPath);
                await view.LoadCoverAsync();
                var current = view.Cover;
                release.TrySetResult(firstPath);
                await obsolete.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(current is not null && ReferenceEquals(current, view.Cover),
                    "A late old-song cover cannot replace the image belonging to the current selection.");
            }
            finally
            {
                release.TrySetResult(firstPath);
            }
        }

        /// <summary>Drains the owner and all joined requests after cancellation cleanup completes.</summary>
        /// <returns>A task representing coalesced shutdown ownership.</returns>
        private static async Task ValidateSharedShutdownAsync()
        {
            var started = Signal();
            var canceled = Signal();
            var release = Signal();
            var workspace = new CoverWorkspace(async (_, token) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    await release.Task;
                    throw;
                }
            });
            var view = CreateView(workspace);
            try
            {
                view.SelectedTrack = Track("closing", "generated-cover.png");
                var first = view.LoadCoverAsync();
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var duplicate = view.LoadCoverAsync();
                var closing = view.DisposeAsync().AsTask();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(!closing.IsCompleted, "Shutdown must wait for the shared cover request's cleanup.");
                release.TrySetResult();
                await Task.WhenAll(first, duplicate, closing).WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.Equal(1, workspace.ResolveCount, "Joined cover requests must not create duplicate shutdown work.");
                TestAssert.True(view.Cover is null, "Shutdown must release the displayed bitmap binding.");
            }
            finally
            {
                release.TrySetResult();
                await view.DisposeAsync();
            }
        }

        /// <summary>Creates an isolated view model with no native playback engine.</summary>
        /// <param name="workspace">The controlled cover resolver.</param>
        /// <returns>The view model owned by the caller.</returns>
        private static MainViewModel CreateView(CoverWorkspace workspace)
        {
            return new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), action => action());
        }

        /// <summary>Creates an entry with a stable cover descriptor.</summary>
        /// <param name="id">The song identity.</param>
        /// <param name="path">The generated cover path.</param>
        /// <returns>The metadata fixture.</returns>
        private static MusicTrack Track(string id, string path)
        {
            return new MusicTrack
            {
                Id = id,
                Title = id,
                Audio = new MediaResource { LocalPath = "generated-audio.wav" },
                Cover = new MediaResource { LocalPath = path, CacheVersion = "v1" }
            };
        }

        /// <summary>Creates an asynchronously continued signal for controlled request lifetimes.</summary>
        /// <returns>The new completion signal.</returns>
        private static TaskCompletionSource Signal()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Writes a small generated PNG to the isolated test directory.</summary>
        /// <param name="directory">The test directory.</param>
        /// <param name="name">The fixture filename.</param>
        /// <param name="color">The generated bitmap color.</param>
        /// <returns>The absolute fixture path.</returns>
        private static string WriteCover(string directory, string name, SKColor color)
        {
            var path = Path.Combine(directory, name);
            using var bitmap = new SKBitmap(8, 8);
            bitmap.Erase(color);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            encoded.SaveTo(stream);
            return path;
        }

        /// <summary>Implements only the local contract needed to exercise cover ownership.</summary>
        private sealed class CoverWorkspace : IMusicWorkspace
        {
            /// <summary>Controls cover completion without starting network requests.</summary>
            private readonly Func<MusicTrack, CancellationToken, Task<string?>> _resolve;
            /// <summary>Counts resolver entries across request continuations.</summary>
            private int _resolveCount;

            /// <summary>Creates a controlled local cover resolver.</summary>
            /// <param name="resolve">The fixture read behavior.</param>
            public CoverWorkspace(Func<MusicTrack, CancellationToken, Task<string?>> resolve)
            {
                _resolve = resolve;
            }

            /// <summary>Gets the number of actual resolver calls.</summary>
            public int ResolveCount => Volatile.Read(ref _resolveCount);
            /// <inheritdoc />
            public bool IsOnline => false;
            /// <inheritdoc />
            public string DataDirectory => Path.GetTempPath();
            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AppSettings());
            }
            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled)
            {
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<MusicTrack>>(Array.Empty<MusicTrack>());
            }
            /// <inheritdoc />
            public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<MusicPlaylist>>(Array.Empty<MusicPlaylist>());
            }
            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _resolveCount);
                return _resolve(track, cancellationToken);
            }
            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }
    }
}
