#nullable enable

using Avalonia.Headless;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;
using SkiaSharp;

namespace MajMusic.Tests
{
    /// <summary>Checks that concurrent reads of the same home mode cannot undo a completed playlist edit.</summary>
    internal static class CatalogReadOrderingTests
    {
        /// <summary>Exercises delayed catalog metadata and delayed first-screen artwork without changing home ownership.</summary>
        /// <returns>A task representing both same-mode publication-order regressions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await session.Dispatch<bool>(async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "MajMusic-catalog-order-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var cover = Path.Combine(directory, "generated.png");
                try
                {
                    using (var image = new SKBitmap(32, 32))
                    {
                        image.Erase(SKColors.CornflowerBlue);
                        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                        await File.WriteAllBytesAsync(cover, encoded.ToArray(), timeout.Token);
                    }

                    await ValidateLateQueryAsync(cover, timeout.Token);
                    await ValidateLateCoverAsync(cover, timeout.Token);
                    return true;
                }
                finally
                {
                    File.Delete(cover);
                    Directory.Delete(directory);
                }
            }, timeout.Token);
        }

        /// <summary>Rejects old membership and stream metadata returned after a newer same-mode read has published.</summary>
        /// <param name="cover">The generated image used to detect unnecessary stale artwork preparation.</param>
        /// <param name="token">Cancels a stalled fixture.</param>
        /// <returns>A task representing late-query rejection and stable prepared-preview reuse.</returns>
        private static async Task ValidateLateQueryAsync(string cover, CancellationToken token)
        {
            var workspace = new OrderingWorkspace();
            await using var model = await CreateModelAsync(workspace, token);
            model.SetBrowseViewportSize(900, 480);
            await model.BrowsePreloadUpdate.WaitAsync(token);
            var oldEntries = ObsoleteSnapshot(workspace, cover);
            var query = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken oldToken = default;
            workspace.Inner.SearchOverride = (_, cancellationToken) =>
            {
                oldToken = cancellationToken;
                started.TrySetResult();
                return query.Task;
            };
            try
            {
                model.ShowPlaylistsModeCommand.Execute(null);
                var oldRead = model.HomeUpdate;
                await started.Task.WaitAsync(token);
                workspace.Inner.SearchOverride = null;
                await RemoveAndPublishNewerAsync(model, workspace, token);
                TestAssert.True(!oldToken.IsCancellationRequested && !oldRead.IsCompleted,
                    "A playlist edit must overtake a still-live same-mode query rather than relying on a mode-change cancellation.");
                query.TrySetResult(oldEntries);
                await oldRead.WaitAsync(token);
                AssertCurrentContents(model);
                TestAssert.Equal(0, workspace.StaleCoverRequests,
                    "An obsolete metadata query must be rejected before preparing its stale first-screen artwork.");
                await AssertPreparedRevisitAsync(model, token);
            }
            finally
            {
                workspace.Inner.SearchOverride = null;
                query.TrySetResult(oldEntries);
                model.SetBrowseViewportSize(0, 0);
            }
        }

        /// <summary>Rejects a previously current read that loses ownership while its first-screen cover is resolving.</summary>
        /// <param name="cover">The generated local artwork returned by the deliberately held source.</param>
        /// <param name="token">Cancels a stalled fixture.</param>
        /// <returns>A task representing post-cover ordering and preview-cache protection.</returns>
        private static async Task ValidateLateCoverAsync(string cover, CancellationToken token)
        {
            var workspace = new OrderingWorkspace();
            await using var model = await CreateModelAsync(workspace, token);
            model.SetBrowseViewportSize(900, 480);
            await model.BrowsePreloadUpdate.WaitAsync(token);
            var oldEntries = ObsoleteSnapshot(workspace, cover);
            var artwork = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.ResolveStaleCover = (_, _) =>
            {
                started.TrySetResult();
                return artwork.Task;
            };
            workspace.Inner.SearchOverride = (_, _) => Task.FromResult(oldEntries);
            try
            {
                model.ShowPlaylistsModeCommand.Execute(null);
                var oldRead = model.HomeUpdate;
                await started.Task.WaitAsync(token);
                workspace.Inner.SearchOverride = null;
                await RemoveAndPublishNewerAsync(model, workspace, token);
                TestAssert.True(!oldRead.IsCompleted,
                    "The newer playlist read must publish while the original foreground preparation is still inside its 400 ms deadline.");
                TestAssert.Equal(1, workspace.StaleCoverRequests,
                    "An active browse viewport must have actually started the deliberately delayed foreground cover request.");
                artwork.TrySetResult(cover);
                await oldRead.WaitAsync(token);
                AssertCurrentContents(model);
                await AssertPreparedRevisitAsync(model, token);
            }
            finally
            {
                workspace.Inner.SearchOverride = null;
                artwork.TrySetResult(cover);
                model.SetBrowseViewportSize(0, 0);
            }
        }

        /// <summary>Creates the initial two-song playlist before enabling any speculative cover work.</summary>
        /// <param name="workspace">The isolated mutable catalog and membership fixture.</param>
        /// <param name="token">Cancels a stalled initial read.</param>
        /// <returns>An initialized model displaying both original playlist members.</returns>
        private static async Task<MainViewModel> CreateModelAsync(OrderingWorkspace workspace, CancellationToken token)
        {
            var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()));
            try
            {
                await model.InitializeAsync();
                model.SelectedHomePlaylist = model.Playlists.Single(item => item.Id == OrderingWorkspace.PlaylistId);
                await model.HomeUpdate.WaitAsync(token);
                model.SelectedTrack = model.Tracks.Single(item => item.Id == "removed");
                TestAssert.True(model.Tracks.Select(item => item.Id).SequenceEqual(["removed", "remaining"]),
                    "The ordering fixture must begin in the actual editable two-song playlist.");
                return model;
            }
            catch
            {
                await model.DisposeAsync();
                throw;
            }
        }

        /// <summary>Removes the inspected member and lets its newer catalog read and preview warm-up finish first.</summary>
        /// <param name="model">The current playlist presentation.</param>
        /// <param name="workspace">The local catalog whose stream metadata changes before the newer read.</param>
        /// <param name="token">Cancels a stalled command or warm-up.</param>
        /// <returns>A task representing publication of the surviving current metadata.</returns>
        private static async Task RemoveAndPublishNewerAsync(MainViewModel model, OrderingWorkspace workspace, CancellationToken token)
        {
            workspace.Inner.Library[1] = workspace.Inner.Library[1] with
            {
                Title = "Current metadata",
                Duration = TimeSpan.FromSeconds(240),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 320000 }
            };
            var transition = model.BrowseTransitionVersion;
            TestAssert.True(model.RemoveFromPlaylistCommand.CanExecute(null),
                "A pending home query must leave the local playlist removal action available.");
            model.RemoveFromPlaylistCommand.Execute(null);
            while (model.IsBusy)
            {
                await Task.Delay(1, token);
            }

            await model.BrowsePreloadUpdate.WaitAsync(token);
            TestAssert.Equal(transition, model.BrowseTransitionVersion,
                "Removing a member must preserve the current mode rather than mask ordering with another navigation generation.");
            TestAssert.True(workspace.Inner.LocalPlaylists[OrderingWorkspace.PlaylistId].TrackIds.SequenceEqual(["remaining"]),
                "The actual local edit must complete before the deliberately older read is released.");
            AssertCurrentContents(model);
        }

        /// <summary>Checks the actual catalog and card models after all older work is allowed to finish.</summary>
        /// <param name="model">The current same-mode presentation.</param>
        private static void AssertCurrentContents(MainViewModel model)
        {
            TestAssert.True(model.IsPlaylistsMode && model.Tracks.Count == 1 && model.Tracks[0].Id == "remaining"
                && model.Tracks[0].Title == "Current metadata" && model.Tracks[0].Duration == TimeSpan.FromSeconds(240)
                && model.Tracks[0].AudioInfo?.BitrateBitsPerSecond == 320000
                && model.CardTracks.Count == 1 && model.CardTracks[0].Track == model.Tracks[0],
                "An older same-mode read cannot resurrect the removed song or overwrite the newer title, duration, bitrate and card snapshot.");
        }

        /// <summary>Reads the synchronous prepared prefix before its queued complete query can repair a poisoned cache.</summary>
        /// <param name="model">The model retaining first-screen previews.</param>
        /// <param name="token">Cancels a stalled revisit.</param>
        /// <returns>A task representing a stable ready-preview revisit and its full read.</returns>
        private static async Task AssertPreparedRevisitAsync(MainViewModel model, CancellationToken token)
        {
            model.ShowLibraryModeCommand.Execute(null);
            await model.HomeUpdate.WaitAsync(token);
            model.ShowPlaylistsModeCommand.Execute(null);
            AssertCurrentContents(model);
            await model.HomeUpdate.WaitAsync(token);
            AssertCurrentContents(model);
        }

        /// <summary>Creates an older metadata result whose removed member requires an independently identifiable cover.</summary>
        /// <param name="workspace">The current initial two-song catalog.</param>
        /// <param name="cover">The generated local artwork.</param>
        /// <returns>The old query's immutable membership and metadata snapshot.</returns>
        private static IReadOnlyList<MusicTrack> ObsoleteSnapshot(OrderingWorkspace workspace, string cover)
        {
            return
            [
                workspace.Inner.Library[0] with { Cover = new MediaResource { LocalPath = cover, CacheVersion = "stale-read" } },
                workspace.Inner.Library[1] with { Title = "Obsolete metadata", Duration = TimeSpan.FromSeconds(30), AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 64000 } }
            ];
        }

        /// <summary>Provides in-memory catalog edits and controlled cover resolution without source or audio access.</summary>
        private sealed class OrderingWorkspace : IMusicWorkspace, IPlaylistManagementWorkspace
        {
            /// <summary>Identifies the editable playlist used by both races.</summary>
            public const string PlaylistId = "ordering-playlist";
            /// <summary>Gets the isolated metadata and playlist store.</summary>
            public LibraryExperienceTests.ExperienceWorkspace Inner { get; } = new();
            /// <summary>Gets or sets the source gate for artwork belonging only to the obsolete read.</summary>
            public Func<MusicTrack, CancellationToken, Task<string?>>? ResolveStaleCover { get; set; }
            /// <summary>Gets the number of real stale-artwork requests observed by the shared cover cache.</summary>
            public int StaleCoverRequests { get; private set; }

            /// <summary>Creates a two-song catalog whose files are never opened.</summary>
            public OrderingWorkspace()
            {
                foreach (var id in new[] { "removed", "remaining" })
                {
                    Inner.Library.Add(new MusicTrack
                    {
                        Id = id,
                        Title = id,
                        Duration = TimeSpan.FromSeconds(120),
                        AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 192000 },
                        Audio = new MediaResource { LocalPath = "unused-ordering-audio" }
                    });
                }

                Inner.LocalPlaylists[PlaylistId] = new MusicPlaylist { Id = PlaylistId, Name = "Ordering", TrackIds = ["removed", "remaining"] };
            }

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
                throw new NotSupportedException("The catalog-ordering fixture cannot import music.");
            }
            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("The catalog-ordering fixture cannot scan music.");
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
                throw new NotSupportedException("The catalog-ordering fixture cannot contact a source.");
            }
            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                if (track.Cover?.CacheVersion == "stale-read")
                {
                    StaleCoverRequests++;
                    return ResolveStaleCover?.Invoke(track, cancellationToken) ?? Task.FromResult(track.Cover.LocalPath);
                }

                return Task.FromResult(track.Cover?.LocalPath);
            }
            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Catalog ordering must not resolve audio.");
            }
            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task DeletePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("The catalog-ordering fixture only removes an existing member.");
            }
            /// <inheritdoc />
            public Task RemoveFromPlaylistAsync(string playlistId, MusicTrack track, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var playlist = Inner.LocalPlaylists[playlistId];
                Inner.LocalPlaylists[playlistId] = playlist with { TrackIds = playlist.TrackIds.Where(id => id != track.Id).ToArray() };
                return Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task<bool> IsPlaylistDeletedAsync(string playlistId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(false);
            }
        }
    }
}
