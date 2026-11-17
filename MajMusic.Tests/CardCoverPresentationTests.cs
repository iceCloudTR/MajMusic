#nullable enable

using Avalonia;
using Avalonia.Headless;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using SkiaSharp;

namespace MajMusic.Tests
{
    /// <summary>Checks automatic visible-card thumbnails independently of selection and audio preparation.</summary>
    internal static class CardCoverPresentationTests
    {
        /// <summary>Runs shared loading, cancellation, bounded concurrency, eviction and shutdown regressions.</summary>
        /// <returns>A task representing the completed thumbnail cases.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await session.Dispatch(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "MajMusic-card-covers-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var portrait = WriteCover(root, "portrait.png", 320, 1024);
                    var landscape = WriteCover(root, "landscape.jpg", 1024, 320);
                    var unsupported = Path.Combine(root, "unsupported.bin");
                    await File.WriteAllTextAsync(unsupported, "Unsupported image fixture");
                    await ValidateSharedAndStableAsync(portrait);
                    await ValidateMissingCoverRetryAsync(portrait, unsupported);
                    await ValidateConcurrencyAsync(landscape);
                    await ValidateDetachedCancellationAsync(portrait);
                    await ValidateEvictionAsync(portrait);
                    await ValidateShutdownAsync(portrait);
                    return true;
                }
                finally
                {
                    var resolved = Path.GetFullPath(root);
                    var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(resolved).StartsWith("MajMusic-card-covers-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Refusing to remove files outside the generated card-cover fixture.");
                    }

                    Directory.Delete(resolved, true);
                }
            }, timeout.Token);
        }

        /// <summary>Shares one image across visible cards and keeps it while audio metadata and selection change.</summary>
        /// <param name="path">The generated portrait cover.</param>
        /// <returns>A task representing sharing and bitmap continuity.</returns>
        private static async Task ValidateSharedAndStableAsync(string path)
        {
            var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workspace = new CoverWorkspace { Resolve = (_, token) => release.Task.WaitAsync(token) };
            workspace.Inner.Library.AddRange([Track("first", path, "shared"), Track("second", path, "shared")]);
            for (var index = 2; index <= MainViewModel.ResidentCardLimit; index++)
            {
                workspace.Inner.Library.Add(Track("shared-" + index, path, "shared"));
            }
            var backend = new LibraryExperienceTests.ExperienceBackend();
            await using var view = await CreateViewAsync(workspace, backend);
            TestAssert.Equal(0, workspace.CoverRequests, "Unattached card records cannot prefetch images outside the viewport.");
            var first = view.CardTracks[0];
            var second = view.CardTracks[1];
            var outsideResidentPrefix = view.CardTracks[MainViewModel.ResidentCardLimit];
            var firstLoad = first.ActivateAsync();
            var secondLoad = second.ActivateAsync();
            TestAssert.Equal(1, workspace.CoverRequests, "Identical visible cover resources must share a pending resolution.");
            release.TrySetResult(path);
            await Task.WhenAll(firstLoad, secondLoad);
            var bitmap = first.Cover ?? throw new InvalidOperationException("The card did not decode its cover.");
            TestAssert.True(ReferenceEquals(bitmap, second.Cover), "Identical visible covers must share their decoded bitmap.");
            TestAssert.True(bitmap.PixelSize.Width <= 256 && bitmap.PixelSize.Height == 256,
                "A portrait cover must decode with its long edge bounded to 256 pixels.");
            var emptyTransitions = 0;
            first.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SongCardViewModel.Cover) && first.Cover is null)
                {
                    emptyTransitions++;
                }
            };
            workspace.Inner.Library[0] = workspace.Inner.Library[0] with { Duration = TimeSpan.FromSeconds(123), AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 192000 } };
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate;
            TestAssert.True(ReferenceEquals(first, view.CardTracks[0]) && first.Track.Duration == TimeSpan.FromSeconds(123),
                "Refreshing audio metadata must reuse the stable card instance and publish its current properties.");
            TestAssert.True(ReferenceEquals(bitmap, first.Cover) && emptyTransitions == 0 && workspace.CoverRequests == 1,
                "Audio metadata replacement cannot clear, decode or resolve an unchanged cover again.");
            TestAssert.True(view.SelectedTrack is null && backend.OpenCount == 0 && workspace.AudioRequests == 0,
                "Visible thumbnails must load before selection and never prepare or request audio.");
            first.Deactivate();
            TestAssert.True(ReferenceEquals(bitmap, first.Cover) && ReferenceEquals(bitmap, second.Cover),
                "A resident card retains its shared artwork when its control leaves the viewport.");
            await outsideResidentPrefix.ActivateAsync();
            TestAssert.True(ReferenceEquals(bitmap, outsideResidentPrefix.Cover),
                "A card outside the resident prefix can still share the visible bitmap.");
            outsideResidentPrefix.Deactivate();
            TestAssert.True(outsideResidentPrefix.Cover is null && ReferenceEquals(bitmap, second.Cover),
                "Detaching a nonresident card releases only its own interest in a shared bitmap.");
            using var scaled = second.Cover!.CreateScaledBitmap(new PixelSize(1, 1));
            second.Deactivate();
            await first.ActivateAsync();
            TestAssert.True(ReferenceEquals(bitmap, first.Cover) && workspace.CoverRequests == 1,
                "Reattaching a resident card must reuse its retained bitmap.");
        }

        /// <summary>Retries unavailable and undecodable thumbnails after the card leaves and returns to the viewport.</summary>
        /// <param name="path">The valid generated image used by the second attempt.</param>
        /// <param name="unsupported">The generated unsupported image used to exercise a null decoding result.</param>
        /// <returns>A task representing both missing-resource and decoding retries.</returns>
        private static async Task ValidateMissingCoverRetryAsync(string path, string unsupported)
        {
            foreach (var initial in new string?[] { null, unsupported })
            {
                var attempt = 0;
                var workspace = new CoverWorkspace
                {
                    Resolve = (_, _) => Task.FromResult(++attempt == 1 ? initial : path)
                };
                workspace.Inner.Library.Add(Track("retry", path, "retry"));
                await using var view = await CreateViewAsync(workspace);
                var card = view.CardTracks.Single();
                await card.ActivateAsync();
                TestAssert.True(card.Cover is null && workspace.CoverRequests == 1,
                    "A missing or unsupported image must leave a placeholder after its first attempt.");
                card.Deactivate();
                await card.ActivateAsync();
                TestAssert.True(card.Cover is not null && workspace.CoverRequests == 2,
                    "An unavailable thumbnail cannot be retained as a successful empty cache entry after detachment.");
            }
        }

        /// <summary>Limits resolution and decoding to four concurrent visible requests.</summary>
        /// <param name="path">The generated landscape JPEG.</param>
        /// <returns>A task representing the worker bound.</returns>
        private static async Task ValidateConcurrencyAsync(string path)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = 0;
            var maximum = 0;
            var workspace = new CoverWorkspace
            {
                Resolve = async (_, token) =>
                {
                    var current = Interlocked.Increment(ref active);
                    maximum = Math.Max(maximum, current);
                    try
                    {
                        await release.Task.WaitAsync(token);
                        return path;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                }
            };
            workspace.Inner.Library.AddRange(Enumerable.Range(0, 12).Select(index => Track("parallel-" + index, path, "cover-" + index)));
            await using var view = await CreateViewAsync(workspace);
            var loads = view.CardTracks.Select(card => card.ActivateAsync()).ToArray();
            TestAssert.Equal(4, workspace.CoverRequests, "Only four image resolutions may start while previous requests remain pending.");
            release.TrySetResult();
            await Task.WhenAll(loads);
            TestAssert.True(maximum <= 4 && workspace.CoverRequests == 12, "All visible requests must finish without exceeding the worker limit.");
            TestAssert.True(view.CardTracks.All(card => card.Cover is { PixelSize.Width: 256, PixelSize.Height: <= 256 }),
                "Landscape JPEG thumbnails must also keep both decoded dimensions bounded.");
        }

        /// <summary>Cancels a detached card's pending request and permits a later activation to retry.</summary>
        /// <param name="path">The generated local cover.</param>
        /// <returns>A task representing viewport cancellation and retry.</returns>
        private static async Task ValidateDetachedCancellationAsync(string path)
        {
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workspace = new CoverWorkspace
            {
                Resolve = async (_, token) =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                        return path;
                    }
                    catch (OperationCanceledException)
                    {
                        canceled.TrySetResult();
                        throw;
                    }
                }
            };
            workspace.Inner.Library.Add(Track("detach", path, "detach"));
            await using var view = await CreateViewAsync(workspace);
            var card = view.CardTracks.Single();
            var loading = card.ActivateAsync();
            card.Deactivate();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await loading;
            TestAssert.True(card.Cover is null, "A detached card must not publish a late thumbnail.");
            workspace.Resolve = (_, _) => Task.FromResult<string?>(path);
            await card.ActivateAsync();
            TestAssert.True(card.Cover is not null && workspace.CoverRequests == 2, "A canceled viewport request must be retryable on reattachment.");
        }

        /// <summary>Evicts old unused thumbnails while keeping an actively displayed bitmap alive.</summary>
        /// <param name="path">The generated shared image bytes.</param>
        /// <returns>A task representing bounded retention and active pinning.</returns>
        private static async Task ValidateEvictionAsync(string path)
        {
            var workspace = new CoverWorkspace();
            workspace.Inner.Library.AddRange(Enumerable.Range(0, 110).Select(index => Track("cache-" + index, path, "cache-" + index)));
            await using var view = await CreateViewAsync(workspace);
            var pinned = view.CardTracks[0];
            await pinned.ActivateAsync();
            var bitmap = pinned.Cover;
            foreach (var card in view.CardTracks.Skip(1))
            {
                await card.ActivateAsync();
                card.Deactivate();
            }

            using var scaled = pinned.Cover!.CreateScaledBitmap(new PixelSize(1, 1));
            TestAssert.True(ReferenceEquals(bitmap, pinned.Cover), "Cache pressure cannot dispose a bitmap still held by a visible card.");
            var requests = workspace.CoverRequests;
            await view.CardTracks[1].ActivateAsync();
            TestAssert.Equal(requests + 1, workspace.CoverRequests, "Old unused thumbnails must be evicted instead of growing the cache indefinitely.");
        }

        /// <summary>Waits for a canceled resolver that has not returned before releasing the view.</summary>
        /// <param name="path">The generated local cover path.</param>
        /// <returns>A task representing asynchronous shutdown ownership.</returns>
        private static async Task ValidateShutdownAsync(string path)
        {
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workspace = new CoverWorkspace
            {
                Resolve = async (_, token) =>
                {
                    using var registration = token.Register(() => canceled.TrySetResult());
                    await release.Task;
                    return path;
                }
            };
            workspace.Inner.Library.Add(Track("shutdown", path, "shutdown"));
            var view = await CreateViewAsync(workspace);
            var loading = view.CardTracks.Single().ActivateAsync();
            var closing = view.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(!closing.IsCompleted, "Closing the view must wait for thumbnail media access that has not returned.");
            release.TrySetResult();
            await Task.WhenAll(closing, loading).WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.Equal(0, view.CardTracks.Count, "A closed view must release its card and bitmap ownership.");
        }

        /// <summary>Creates a loaded local browse snapshot without constructing a card control or selecting music.</summary>
        /// <param name="workspace">The controllable cover-only workspace.</param>
        /// <param name="backend">An optional controlled audio backend.</param>
        /// <returns>The view model owned by the test.</returns>
        private static async Task<MainViewModel> CreateViewAsync(CoverWorkspace workspace, LibraryExperienceTests.ExperienceBackend? backend = null)
        {
            var view = new MainViewModel(workspace, new PlaybackService(backend ?? new LibraryExperienceTests.ExperienceBackend()), static action => action());
            await view.InitializeAsync();
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate;
            return view;
        }

        /// <summary>Creates a source-scoped image descriptor without any real audio resource.</summary>
        /// <param name="id">The song identity.</param>
        /// <param name="path">The local cover fixture path.</param>
        /// <param name="version">The image resource identity used to control sharing.</param>
        /// <returns>The generated entry.</returns>
        private static MusicTrack Track(string id, string path, string version)
        {
            return new MusicTrack { Id = id, Title = id, Audio = new MediaResource { LocalPath = "unopened.mp3" }, Cover = new MediaResource { LocalPath = path, CacheVersion = version } };
        }

        /// <summary>Writes an image whose source size is larger than the allowed thumbnail edge.</summary>
        /// <param name="root">The test-owned temporary directory.</param>
        /// <param name="name">The generated image filename.</param>
        /// <param name="width">The source pixel width.</param>
        /// <param name="height">The source pixel height.</param>
        /// <returns>The complete image path.</returns>
        private static string WriteCover(string root, string name, int width, int height)
        {
            using var image = new SKBitmap(width, height);
            image.Erase(SKColors.CornflowerBlue);
            using var data = image.Encode(name.EndsWith(".jpg", StringComparison.Ordinal) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 85);
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }

        /// <summary>Delegates local bookkeeping while intercepting only image and audio resource requests.</summary>
        private sealed class CoverWorkspace : IMusicWorkspace
        {
            /// <summary>Counts cover requests across worker threads.</summary>
            private int _coverRequests;
            /// <summary>Counts forbidden audio resource requests.</summary>
            private int _audioRequests;
            /// <summary>Gets the isolated in-memory library and playlist persistence.</summary>
            public LibraryExperienceTests.ExperienceWorkspace Inner { get; } = new();
            /// <summary>Gets or sets the controllable cover resolver.</summary>
            public Func<MusicTrack, CancellationToken, Task<string?>> Resolve { get; set; } = (track, _) => Task.FromResult(track.Cover?.LocalPath);
            /// <summary>Gets how many image requests reached the workspace.</summary>
            public int CoverRequests => Volatile.Read(ref _coverRequests);
            /// <summary>Gets how many audio requests reached the workspace.</summary>
            public int AudioRequests => Volatile.Read(ref _audioRequests);
            /// <inheritdoc />
            public bool IsOnline => Inner.IsOnline;
            /// <inheritdoc />
            public string DataDirectory => Inner.DataDirectory;
            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                return Inner.LoadSettingsAsync(cancellationToken);
            }
            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                return Inner.SaveSettingsAsync(settings, cancellationToken);
            }
            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled)
            {
                Inner.SetOnlineEnabled(enabled);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                return Inner.SearchAsync(query, cancellationToken);
            }
            /// <inheritdoc />
            public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                return Inner.ImportDirectoryAsync(directory, cancellationToken);
            }
            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                return Inner.IndexDirectoryAsync(directory, cancellationToken);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
            {
                return Inner.GetPlaylistsAsync(cancellationToken);
            }
            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                return Inner.SavePlaylistAsync(playlist, cancellationToken);
            }
            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return Inner.SynchronizeAsync(endpoint, cancellationToken);
            }
            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _coverRequests);
                return Resolve(track, cancellationToken);
            }
            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _audioRequests);
                throw new InvalidOperationException("A thumbnail requested audio.");
            }
            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default)
            {
                return Inner.ClearCacheAsync(cancellationToken);
            }
        }
    }
}
