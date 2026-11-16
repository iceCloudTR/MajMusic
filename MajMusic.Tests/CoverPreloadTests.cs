#nullable enable

using Avalonia;
using Avalonia.Headless;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Presentation.ViewModels;
using SkiaSharp;

namespace MajMusic.Tests
{
    /// <summary>Checks bounded cover preparation and pre-binding adoption independently of navigation and audio.</summary>
    internal static class CoverPreloadTests
    {
        /// <summary>Runs shared pin, deadline, placeholder, cancellation and routing regressions.</summary>
        /// <returns>A task representing the completed generated-image checks.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-cover-preload-" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(root);
                try
                {
                    var path = Path.Combine(root, "generated.png");
                    using (var image = new SKBitmap(300, 400))
                    {
                        image.Erase(SKColors.CornflowerBlue);
                        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                        await File.WriteAllBytesAsync(path, encoded.ToArray(), timeout.Token);
                    }

                    await VerifyReadyAdoptionAsync(path);
                    await VerifyBoundedPreparationAsync(path);
                    await VerifyDeadlineAndDeferredResumeAsync(path);
                    await VerifyRevisionPlaceholderAsync(path);
                    await VerifyCanceledCallerAsync(path);
                    await VerifyFailedPlaceholderRetryAsync(path);
                    await VerifyAttachedFailureRecoveryAsync(path);
                    return true;
                }
                finally
                {
                    var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(root).StartsWith("MajMusic-cover-preload-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Refusing to remove files outside the generated cover fixture.");
                    }

                    Directory.Delete(root, true);
                }
            }, timeout.Token);
        }

        /// <summary>Adopts a shared decoded bitmap before attachment and retains independent card ownership.</summary>
        /// <param name="path">The generated local artwork.</param>
        /// <returns>A task representing synchronous first-frame availability and independent release.</returns>
        private static async Task VerifyReadyAdoptionAsync(string path)
        {
            var workspace = new CoverWorkspace();
            await using var cache = new CoverThumbnailCache(workspace, CancellationToken.None);
            var first = Track("first", path, "same");
            var second = Track("second", path, "same");
            using var batch = await cache.PreloadAsync([first, second], TimeSpan.FromSeconds(2), CancellationToken.None);
            TestAssert.True(batch.Count == 1 && workspace.CoverRequests == 1 && workspace.AudioRequests == 0,
                "Preparing duplicate first-screen artwork must resolve one cover and never inspect audio.");
            using var firstCard = new SongCardViewModel(first, cache);
            using var secondCard = new SongCardViewModel(second, cache);
            TestAssert.True(firstCard.AdoptPreloadedCover(batch) && secondCard.AdoptPreloadedCover(batch)
                && firstCard.Cover is not null && ReferenceEquals(firstCard.Cover, secondCard.Cover),
                "Both unattached cards must already expose the same prepared image before their first visual binding.");
            var bitmap = firstCard.Cover!;
            batch.Dispose();
            using (bitmap.CreateScaledBitmap(new PixelSize(1, 1)))
            {
            }

            var emptyNotifications = 0;
            firstCard.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SongCardViewModel.Cover) && firstCard.Cover is null)
                {
                    emptyNotifications++;
                }
            };
            await firstCard.ActivateAsync();
            firstCard.UpdateTrack(first with { Duration = TimeSpan.FromSeconds(120) });
            firstCard.ResumeCoverLoading();
            await firstCard.ResourceCompletion;
            TestAssert.True(ReferenceEquals(bitmap, firstCard.Cover) && emptyNotifications == 0 && workspace.CoverRequests == 1,
                "Attaching, metadata refresh and a settled-transition notification cannot clear or rerequest a prepared image.");
            firstCard.Deactivate();
            TestAssert.True(firstCard.Cover is null && ReferenceEquals(bitmap, secondCard.Cover),
                "Releasing one card cannot release the other card's adopted image.");
            using (secondCard.Cover!.CreateScaledBitmap(new PixelSize(1, 1)))
            {
            }

            using var differentSource = new SongCardViewModel(first with { EndpointId = "other-account-route" }, cache);
            using var differentRevision = new SongCardViewModel(first with { Cover = first.Cover! with { CacheVersion = "changed" } }, cache);
            using var freshBatch = await cache.PreloadAsync([first], TimeSpan.FromSeconds(2), CancellationToken.None);
            TestAssert.True(!differentSource.AdoptPreloadedCover(freshBatch) && !differentRevision.AdoptPreloadedCover(freshBatch),
                "A preparation cannot be adopted by another endpoint or a changed cover revision.");
            differentSource.Dispose();
            TestAssert.True(!differentSource.AdoptPreloadedCover(freshBatch), "Disposed cards cannot adopt late navigation resources.");
            TestAssert.True(!firstCard.AdoptPreloadedCover(batch), "A released preparation batch cannot create new pins.");
            TestAssert.Equal(0, workspace.AudioRequests, "All adoption and routing checks must remain covers-only.");
        }

        /// <summary>Limits preloading to twelve entries and uses the existing four shared worker slots.</summary>
        /// <param name="path">The generated local artwork.</param>
        /// <returns>A task representing bounded concurrency and catalog scope.</returns>
        private static async Task VerifyBoundedPreparationAsync(string path)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = 0;
            var maximum = 0;
            var workspace = new CoverWorkspace
            {
                Resolve = async (_, token) =>
                {
                    var current = Interlocked.Increment(ref active);
                    maximum = Math.Max(maximum, current);
                    if (current == 4)
                    {
                        started.TrySetResult();
                    }

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
            await using var cache = new CoverThumbnailCache(workspace, CancellationToken.None);
            var tracks = Enumerable.Range(0, 20).Select(index => Track("song-" + index, path, "cover-" + index)).ToArray();
            var pending = cache.PreloadAsync(tracks, TimeSpan.FromSeconds(4), CancellationToken.None);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.Equal(4, workspace.CoverRequests, "A held first viewport must start at most four cover resolutions.");
            }
            finally
            {
                release.TrySetResult();
            }

            using var batch = await pending;
            TestAssert.True(batch.Count == 12 && workspace.CoverRequests == 12 && maximum == 4,
                "Preparing a large catalog must resolve only its first twelve covers with four concurrent workers.");
            TestAssert.True(!batch.TryAcquire(tracks[12], out _), "Tracks beyond the bounded first viewport cannot own a prepared result.");
            TestAssert.Equal(0, workspace.AudioRequests, "Bounded preparation cannot probe any track audio.");
        }

        /// <summary>Returns on a deadline while a shared slow request continues and freezes the card until explicit resume.</summary>
        /// <param name="path">The generated local artwork returned after the gate opens.</param>
        /// <returns>A task representing a stable placeholder and independent request ownership.</returns>
        private static async Task VerifyDeadlineAndDeferredResumeAsync(string path)
        {
            var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken sharedToken = default;
            var workspace = new CoverWorkspace
            {
                Resolve = (_, token) =>
                {
                    sharedToken = token;
                    return release.Task;
                }
            };
            await using var cache = new CoverThumbnailCache(workspace, CancellationToken.None);
            var track = Track("deadline", path, "deadline");
            var otherConsumer = cache.AcquireAsync(track, CancellationToken.None);
            try
            {
                using var batch = await cache.PreloadAsync([track], TimeSpan.FromMilliseconds(70), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(!release.Task.IsCompleted && !otherConsumer.IsCompleted && !sharedToken.IsCancellationRequested,
                    "A short navigation deadline must return while another consumer retains its uncanceled slow request.");
                using var card = new SongCardViewModel(track, cache);
                TestAssert.True(card.AdoptPreloadedCover(batch) && card.Cover is null,
                    "A timed-out preparation must adopt an explicit stable placeholder before attachment.");
                await card.ActivateAsync();
                var laterNotifications = 0;
                card.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(SongCardViewModel.Cover))
                    {
                        laterNotifications++;
                    }
                };
                release.TrySetResult(path);
                using var held = await otherConsumer;
                TestAssert.True(card.Cover is null && laterNotifications == 0 && workspace.CoverRequests == 1,
                    "A shared cover completing halfway through a slide cannot replace its frozen placeholder.");
                TestAssert.True(batch.TryAcquire(track, out var frozen) && frozen is null,
                    "A prepared placeholder snapshot must remain frozen after the shared request completes.");
                using var refreshed = cache.CaptureReady([track, Track("unrequested", path, "unrequested")]);
                using var revisited = new SongCardViewModel(track, cache);
                TestAssert.True(revisited.AdoptPreloadedCover(refreshed) && revisited.Cover is not null
                    && workspace.CoverRequests == 1 && refreshed.TryAcquire(Track("unrequested", path, "unrequested"), out var absent) && absent is null,
                    "A cache-only revisit must adopt the newly ready image immediately while leaving cold identities unrequested.");
                TestAssert.True(batch.TryAcquire(track, out frozen) && frozen is null && card.Cover is null && laterNotifications == 0,
                    "Refreshing a revisit snapshot cannot mutate the original active transition or publish its late image.");
                card.ResumeCoverLoading();
                await card.ResourceCompletion;
                TestAssert.True(card.Cover is not null && laterNotifications == 1 && workspace.CoverRequests == 1,
                    "Only the settled-transition notification may publish the ready shared cover without another request.");
                var resolved = card.Cover;
                TestAssert.True(card.AdoptPreloadedCover(batch) && ReferenceEquals(resolved, card.Cover) && laterNotifications == 1,
                    "Readopting an older placeholder snapshot cannot downgrade or notify an empty state for the same already-ready cover.");
                held?.Dispose();
                using (card.Cover!.CreateScaledBitmap(new PixelSize(1, 1)))
                {
                }
            }
            finally
            {
                release.TrySetResult(path);
                using var drain = await otherConsumer;
            }

            TestAssert.Equal(0, workspace.AudioRequests, "Deadline and resume work cannot request or inspect audio.");
        }

        /// <summary>Preserves a ready matching image while rejecting an old revision as a replacement's placeholder.</summary>
        /// <param name="path">The generated image shared by the isolated cover revisions.</param>
        /// <returns>A task representing exact-identity preservation and retry after replacement.</returns>
        private static async Task VerifyRevisionPlaceholderAsync(string path)
        {
            var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workspace = new CoverWorkspace();
            await using var cache = new CoverThumbnailCache(workspace, CancellationToken.None);
            var original = Track("revision", path, "original");
            var changed = original with { Cover = original.Cover! with { CacheVersion = "changed" } };
            using var placeholders = cache.CaptureReady([original, changed]);
            using var prepared = await cache.PreloadAsync([original], TimeSpan.FromSeconds(2), CancellationToken.None);
            using var card = new SongCardViewModel(original, cache);
            TestAssert.True(card.AdoptPreloadedCover(prepared) && card.Cover is not null,
                "The revision fixture must adopt its initial ready cover before attachment.");
            var initialBitmap = card.Cover;
            await card.ActivateAsync();
            TestAssert.True(card.AdoptPreloadedCover(placeholders) && ReferenceEquals(initialBitmap, card.Cover),
                "A matching ready pin must survive an older cache-only placeholder snapshot.");
            workspace.Resolve = (_, token) => release.Task.WaitAsync(token);
            try
            {
                card.UpdateTrack(changed);
                TestAssert.True(ReferenceEquals(initialBitmap, card.Cover),
                    "Normal attached replacement may retain the prior bitmap while its new revision loads.");
                TestAssert.True(card.AdoptPreloadedCover(placeholders) && card.Cover is null,
                    "A prepared placeholder for a new revision must clear the previous revision rather than preserve unrelated artwork.");
                card.ResumeCoverLoading();
                release.TrySetResult(path);
                await card.ResourceCompletion;
                TestAssert.True(card.Cover is not null && !ReferenceEquals(initialBitmap, card.Cover),
                    "Resuming the changed revision must acquire its own decoded bitmap after the old request was canceled.");
                TestAssert.Equal(0, workspace.AudioRequests, "Revision replacement must remain covers-only.");
            }
            finally
            {
                release.TrySetResult(path);
            }
        }

        /// <summary>Cancels an isolated navigation without waiting for a resolver that ignores cancellation.</summary>
        /// <param name="path">The generated cover used to drain the obsolete request.</param>
        /// <returns>A task representing prompt cancellation and retry after request retirement.</returns>
        private static async Task VerifyCanceledCallerAsync(string path)
        {
            var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken obsoleteToken = default;
            var workspace = new CoverWorkspace
            {
                Resolve = (_, token) =>
                {
                    obsoleteToken = token;
                    started.TrySetResult();
                    return release.Task;
                }
            };
            await using var cache = new CoverThumbnailCache(workspace, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var track = Track("canceled", path, "canceled");
            var pending = cache.PreloadAsync([track], TimeSpan.FromSeconds(4), cancellation.Token);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                cancellation.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(async () =>
                {
                    using var unexpected = await pending.WaitAsync(TimeSpan.FromSeconds(3));
                });
                TestAssert.True(!release.Task.IsCompleted && obsoleteToken.IsCancellationRequested,
                    "Canceled navigation must release its sole pending cover interest without waiting for late source completion.");
            }
            finally
            {
                release.TrySetResult(path);
            }

            workspace.Resolve = (_, _) => Task.FromResult<string?>(path);
            using var retry = await cache.PreloadAsync([track], TimeSpan.FromSeconds(2), CancellationToken.None);
            using var card = new SongCardViewModel(track, cache);
            TestAssert.True(card.AdoptPreloadedCover(retry) && card.Cover is not null && workspace.CoverRequests == 2,
                "A canceled preparation must retire its result and allow the next navigation to prepare a fresh image.");
        }

        /// <summary>Freezes a failed image until resume and permits normal retry after viewport detachment.</summary>
        /// <param name="path">The generated valid image used by a later attempt.</param>
        /// <returns>A task representing failed-placeholder retry behavior.</returns>
        private static async Task VerifyFailedPlaceholderRetryAsync(string path)
        {
            var workspace = new CoverWorkspace { Resolve = (_, _) => Task.FromResult<string?>(null) };
            await using var cache = new CoverThumbnailCache(workspace, CancellationToken.None);
            var track = Track("missing", path, "missing");
            using var batch = await cache.PreloadAsync([track], TimeSpan.FromSeconds(2), CancellationToken.None);
            using var card = new SongCardViewModel(track, cache);
            TestAssert.True(card.AdoptPreloadedCover(batch), "Missing artwork must still form an explicit prepared placeholder.");
            workspace.Resolve = (_, _) => Task.FromResult<string?>(path);
            await card.ActivateAsync();
            card.UpdateTrack(track with { Duration = TimeSpan.FromSeconds(99) });
            TestAssert.True(card.Cover is null && workspace.CoverRequests == 1,
                "Attachment and unrelated metadata cannot replace a placeholder during the active transition.");
            card.ResumeCoverLoading();
            await card.ResourceCompletion;
            TestAssert.True(card.Cover is not null && workspace.CoverRequests == 2,
                "A completed transition must allow a previously failed prepared cover to retry.");
            card.Deactivate();
            await card.ActivateAsync();
            TestAssert.True(card.Cover is not null && workspace.CoverRequests == 2,
                "A subsequent viewport activation must reuse the successfully retried cached image.");
            TestAssert.Equal(0, workspace.AudioRequests, "Retrying image placeholders cannot probe audio.");
        }

        /// <summary>Allows an attached failed card to recover the same image identity when a download refreshes its audio descriptor.</summary>
        /// <param name="path">The generated valid image made available after the simulated source failure.</param>
        /// <returns>A task representing retry without detaching either card or canceling a successful peer.</returns>
        private static async Task VerifyAttachedFailureRecoveryAsync(string path)
        {
            var available = false;
            var workspace = new CoverWorkspace
            {
                Resolve = (_, _) => available ? Task.FromResult<string?>(path)
                    : Task.FromException<string?>(new HttpRequestException("The isolated cover is temporarily inaccessible."))
            };
            await using var cache = new CoverThumbnailCache(workspace, CancellationToken.None);
            var track = Track("attached-retry", path, "unchanged-cover");
            using var card = new SongCardViewModel(track, cache);
            await card.ActivateAsync();
            TestAssert.True(card.Cover is null && workspace.CoverRequests == 1,
                "An attached card must display a placeholder after the actual cover request fails.");
            available = true;
            var downloaded = track with { Audio = new MediaResource { LocalPath = path } };
            TestAssert.Equal(CoverThumbnailCache.Identity(track), CoverThumbnailCache.Identity(downloaded),
                "The download refresh must exercise the same cover identity rather than bypassing a failed cache entry.");
            card.UpdateTrack(downloaded);
            await card.ResourceCompletion;
            TestAssert.True(card.Cover is not null && workspace.CoverRequests == 2,
                "A completed download or resource refresh must retry an attached failed cover without requiring viewport detachment.");
            using var peer = new SongCardViewModel(track, cache);
            await peer.ActivateAsync();
            TestAssert.True(ReferenceEquals(card.Cover, peer.Cover) && workspace.CoverRequests == 2,
                "Successful recovery remains shared and does not trigger another source read for a peer.");
            card.Deactivate();
            using (peer.Cover!.CreateScaledBitmap(new PixelSize(1, 1)))
            {
            }

            TestAssert.Equal(0, workspace.AudioRequests, "Recovery of the same image identity cannot request audio.");
        }

        /// <summary>Creates a source-scoped cover descriptor with no audio-file ownership.</summary>
        /// <param name="id">The generated song identity.</param>
        /// <param name="path">The generated artwork path.</param>
        /// <param name="version">The controlled cover-resource identity.</param>
        /// <returns>The isolated descriptor.</returns>
        private static MusicTrack Track(string id, string path, string version)
        {
            return new MusicTrack { Id = id, Title = id, Audio = new MediaResource(), Cover = new MediaResource { LocalPath = path, CacheVersion = version } };
        }

        /// <summary>Delegates local fixture bookkeeping while rejecting any audio operation.</summary>
        private sealed class CoverWorkspace : IMusicWorkspace
        {
            /// <summary>Counts concurrent image requests.</summary>
            private int _coverRequests;
            /// <summary>Counts forbidden audio resource requests.</summary>
            private int _audioRequests;
            /// <summary>Owns unrelated in-memory workspace methods.</summary>
            private readonly LibraryExperienceTests.ExperienceWorkspace _inner = new();
            /// <summary>Gets or sets the controlled cover-only resolver.</summary>
            public Func<MusicTrack, CancellationToken, Task<string?>> Resolve { get; set; } = (track, _) => Task.FromResult(track.Cover?.LocalPath);
            /// <summary>Gets the number of image resolutions.</summary>
            public int CoverRequests => Volatile.Read(ref _coverRequests);
            /// <summary>Gets the number of forbidden audio resolutions.</summary>
            public int AudioRequests => Volatile.Read(ref _audioRequests);
            /// <inheritdoc />
            public bool IsOnline => _inner.IsOnline;
            /// <inheritdoc />
            public string DataDirectory => _inner.DataDirectory;
            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                return _inner.LoadSettingsAsync(cancellationToken);
            }
            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                return _inner.SaveSettingsAsync(settings, cancellationToken);
            }
            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled)
            {
                _inner.SetOnlineEnabled(enabled);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                return _inner.SearchAsync(query, cancellationToken);
            }
            /// <inheritdoc />
            public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                return _inner.ImportDirectoryAsync(directory, cancellationToken);
            }
            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                return _inner.IndexDirectoryAsync(directory, cancellationToken);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
            {
                return _inner.GetPlaylistsAsync(cancellationToken);
            }
            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                return _inner.SavePlaylistAsync(playlist, cancellationToken);
            }
            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return _inner.SynchronizeAsync(endpoint, cancellationToken);
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
                throw new InvalidOperationException("Cover preparation cannot request audio.");
            }
            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default)
            {
                return _inner.ClearCacheAsync(cancellationToken);
            }
        }
    }
}
