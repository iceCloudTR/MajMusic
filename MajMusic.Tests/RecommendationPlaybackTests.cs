#nullable enable

using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks continuous audio and queue ownership while later recommendation pages arrive.</summary>
    internal static class RecommendationPlaybackTests
    {
        /// <summary>Exercises append-only ordering, stale ownership, shuffle, rollback and early first-page playback.</summary>
        /// <returns>A task representing the finite-feed playback regressions.</returns>
        public static async Task RunAsync()
        {
            await ValidateStableContinuationAsync();
            await ValidateShuffleHistoryAsync();
            await ValidatePendingRollbackAsync();
            await ValidateEarlyPlaybackAsync();
        }

        /// <summary>Extends a paused resource without changing its audio clock or accepting an obsolete queue view.</summary>
        /// <returns>A task representing stable resource and replacement ownership checks.</returns>
        private static async Task ValidateStableContinuationAsync()
        {
            var backend = new LibraryExperienceTests.ExperienceBackend();
            await using var service = new PlaybackService(backend, resolveAudioPath: static (_, _) => Task.FromResult("generated-audio"));
            var tracks = Enumerable.Range(0, 4).Select(index => Track(index)).ToArray();
            await service.SetQueueAsync(tracks.Take(2), 1);
            await service.SeekAsync(TimeSpan.FromSeconds(30));
            await service.PauseAsync();
            var prefix = service.Queue.Tracks;
            TestAssert.True(service.TryExtendQueue(prefix, tracks), "A matching feed continuation must extend its active queue.");
            TestAssert.True(service.Queue.Tracks.Count == 4 && service.Queue.Current?.Id == tracks[1].Id
                && service.Snapshot.State == PlaybackState.Paused && service.Snapshot.Position == TimeSpan.FromSeconds(30)
                && backend.OpenCount == 1, "Appending recommendations must preserve the resource, pause state and playback clock.");
            TestAssert.True(!service.TryExtendQueue(prefix, tracks), "A previously consumed queue view cannot append twice.");
            var extended = service.Queue.Tracks;
            await service.SetQueueAsync(tracks, 2);
            TestAssert.True(!service.TryExtendQueue(extended, tracks.Append(Track(4)).ToArray()),
                "Starting even an identical local membership must retire the previous recommendation queue's ownership.");
            TestAssert.Equal(tracks[2].Id, service.Queue.Current?.Id, "A stale append cannot change the replacement queue's selection.");
            await service.PreviousAsync();
            TestAssert.Equal(tracks[1].Id, service.Queue.Current?.Id, "The original playback order remains navigable after extension.");
        }

        /// <summary>Inserts new shuffled entries only after the current item while retaining visited navigation history.</summary>
        /// <returns>A task representing stable shuffle and complete future traversal.</returns>
        private static async Task ValidateShuffleHistoryAsync()
        {
            var backend = new LibraryExperienceTests.ExperienceBackend();
            await using var service = new PlaybackService(backend, new PlaybackQueue(new Random(123)),
                static (_, _) => Task.FromResult("generated-shuffle-audio"));
            var tracks = Enumerable.Range(0, 10).Select(index => Track(index)).ToArray();
            await service.SetQueueAsync(tracks.Take(5));
            await service.SetShuffleAsync(true);
            var visited = new HashSet<string> { service.Queue.Current!.Id };
            await service.NextAsync();
            var previous = service.Queue.Current!.Id;
            visited.Add(previous);
            await service.NextAsync();
            var current = service.Queue.Current!.Id;
            visited.Add(current);
            TestAssert.True(service.TryExtendQueue(service.Queue.Tracks, tracks), "A shuffled active feed accepts its matching continuation.");
            TestAssert.Equal(current, service.Queue.Current?.Id, "New shuffled entries cannot displace the playing item.");
            await service.PreviousAsync();
            TestAssert.Equal(previous, service.Queue.Current?.Id, "Appending must retain the already visited history.");
            await service.NextAsync();
            TestAssert.Equal(current, service.Queue.Current?.Id, "Forward history must still return to the same playing item.");
            for (var index = 0; index < 7; index++)
            {
                await service.NextAsync();
                TestAssert.True(visited.Add(service.Queue.Current!.Id), "Each unplayed shuffled continuation must appear once.");
            }

            TestAssert.Equal(10, visited.Count, "Every original and appended recommendation must remain reachable in shuffle mode.");
        }

        /// <summary>Retains newly received recommendations when a pending next-song resolution rolls back.</summary>
        /// <returns>A task representing queue rollback independently of native opens.</returns>
        private static async Task ValidatePendingRollbackAsync()
        {
            var backend = new LibraryExperienceTests.ExperienceBackend();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tracks = Enumerable.Range(0, 3).Select(index => Track(index)).ToArray();
            await using var service = new PlaybackService(backend, resolveAudioPath: (track, token) =>
            {
                if (track.Id == tracks[1].Id)
                {
                    started.TrySetResult();
                    return release.Task.WaitAsync(token);
                }

                return Task.FromResult("generated-rollback-audio");
            });
            await service.SetQueueAsync(tracks.Take(2));
            var next = service.NextAsync();
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(service.TryExtendQueue(service.Queue.Tracks, tracks),
                    "A matching pending navigation must receive later recommendations without canceling its audio preparation.");
                var continued = service.Queue.Tracks;
                release.TrySetException(new IOException("Generated preparation failure."));
                await TestAssert.ThrowsAsync<IOException>(() => next);
                TestAssert.True(service.Queue.Tracks.Count == 3 && service.Queue.Current?.Id == tracks[0].Id && backend.OpenCount == 1,
                    "Failed navigation must restore the previous song while retaining the new queue continuation.");
                TestAssert.True(service.TryExtendQueue(continued, tracks.Append(Track(3)).ToArray())
                    && service.Queue.Tracks.Count == 4 && service.Queue.Current?.Id == tracks[0].Id && backend.OpenCount == 1,
                    "A restored queue must retain feed ownership so the final batch can still arrive without opening audio.");
            }
            finally
            {
                release.TrySetResult("generated-rollback-audio");
            }
        }

        /// <summary>Starts the first available page and continues across its boundary after the remaining source pages finish.</summary>
        /// <returns>A task representing real model, feed and playback-coordinator integration without HTTP or audio.</returns>
        private static async Task ValidateEarlyPlaybackAsync()
        {
            var endpoint = new EndpointConfiguration { Id = "recommendation-audio", Name = "Source", BaseUri = new Uri("https://example.invalid/api/") };
            var workspace = new LibraryExperienceTests.ExperienceWorkspace
            {
                Settings = new AppSettings { OnlineEnabled = true, ActiveEndpointId = endpoint.Id, Endpoints = [endpoint] }
            };
            workspace.Sessions[endpoint.Id] = new EndpointSession { EndpointId = endpoint.Id, Username = "Alice", IsLoggedIn = true };
            var tracks = Enumerable.Range(0, 150).Select(index => Track(index) with
            {
                EndpointId = endpoint.Id, SourceId = "source", RemoteId = "remote-" + index
            }).ToArray();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IReadOnlyList<MusicTrack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.RecommendationOverride = (_, page, token) =>
            {
                if (page == 1)
                {
                    started.TrySetResult();
                    return release.Task.WaitAsync(token);
                }

                return Task.FromResult<IReadOnlyList<MusicTrack>>(tracks.Skip(page * 50).Take(50).ToArray());
            };
            var backend = new LibraryExperienceTests.ExperienceBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: static (_, _) => Task.FromResult("generated-early-audio"));
            await using var model = new MainViewModel(workspace, playback, static action => action());
            await model.InitializeAsync();
            var batch = model.EnsureHomeRecommendationsAsync();
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.Equal(50, model.Tracks.Count, "The first page must be playable before a slow later page finishes.");
                model.SelectedTrack = model.Tracks[49];
                await model.PlaySelectedAsync();
                TestAssert.True(playback.Queue.Tracks.Count == 50 && backend.OpenCount == 1,
                    "First-page playback must start immediately without waiting for the complete batch.");
                release.TrySetResult(tracks.Skip(50).Take(50).ToArray());
                await batch.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(playback.Queue.Tracks.Count == 150 && playback.Queue.Current?.Id == tracks[49].Id
                    && playback.Snapshot.State == PlaybackState.Playing && backend.OpenCount == 1,
                    "Later recommendation pages must extend the same playing resource without restarting it.");
                await playback.NextAsync();
                TestAssert.Equal(tracks[50].Id, playback.Queue.Current?.Id, "Playback must cross the first display-page boundary.");
                model.NextRecommendationPageCommand.Execute(null);
                await model.HomeUpdate.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(playback.Queue.Current?.Id == tracks[50].Id && backend.OpenCount == 2
                    && workspace.RecommendationRequests.Count == 3,
                    "Display pagination cannot reopen the current audio or fetch a fourth source page.");
            }
            finally
            {
                release.TrySetResult(tracks.Skip(50).Take(50).ToArray());
            }
        }

        /// <summary>Creates a source-independent entry that only a synthetic backend may open.</summary>
        /// <param name="index">The stable batch position.</param>
        /// <returns>A generated music descriptor.</returns>
        private static MusicTrack Track(int index)
        {
            return new MusicTrack { Id = "queue-extension-" + index, Title = "Generated " + index,
                Audio = new MediaResource { LocalPath = "generated-unused-audio" } };
        }
    }
}
