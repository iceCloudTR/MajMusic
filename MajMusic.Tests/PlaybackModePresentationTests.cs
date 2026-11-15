#nullable enable

using System.Collections.Concurrent;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks that presentation mode actions delegate to the live queue without interrupting audio.</summary>
    internal static class PlaybackModePresentationTests
    {
        /// <summary>Runs queue-preference, navigation and unavailable-backend cases.</summary>
        /// <returns>A task representing completed presentation regressions.</returns>
        public static async Task RunAsync()
        {
            await ValidateLiveModesAsync();
            await ValidateUnavailableModesAsync();
        }

        /// <summary>Changes order and repeat preferences during a paused resource without preparing it again.</summary>
        /// <returns>A task representing mode notification and shuffle navigation checks.</returns>
        private static async Task ValidateLiveModesAsync()
        {
            var workspace = new LibraryExperienceTests.ExperienceWorkspace();
            var backend = new LibraryExperienceTests.ExperienceBackend();
            var service = new PlaybackService(backend, new PlaybackQueue(new Random(7)));
            await using var view = new MainViewModel(workspace, service, static action => action());
            await view.InitializeAsync();
            TestAssert.True(!view.IsShuffle && !view.IsRepeating && !view.IsRepeatOne,
                "A fresh player must start with sequential navigation and no repeat.");
            TestAssert.Equal("顺序播放", view.RepeatModeText, "The initial repeat label must describe the active behavior.");
            TestAssert.Equal("顺序播放", view.ShuffleModeText, "The initial shuffle label must describe the active behavior.");
            var tracks = Enumerable.Range(0, 8).Select(index => new MusicTrack
            {
                Id = $"track-{index}",
                Title = $"Track {index}",
                Audio = new MediaResource { LocalPath = $"track-{index}.mp3" }
            }).ToArray();
            await service.SetQueueAsync(tracks);
            await service.SeekAsync(TimeSpan.FromSeconds(31));
            await ExecuteAsync(view, view.PauseCommand);
            var loaded = service.Snapshot.Track;
            var opened = backend.OpenCount;
            var changed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            view.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is { } name)
                {
                    changed.TryAdd(name, 0);
                }
            };

            await ExecuteAsync(view, view.ToggleShuffleCommand);
            TestAssert.True(view.IsShuffle && service.Queue.Shuffle, "The shuffle action must update the active service queue.");
            TestAssert.Equal("随机播放", view.ShuffleModeText, "The shuffle label must follow the active queue state.");
            await ExecuteAsync(view, view.CycleRepeatModeCommand);
            TestAssert.True(view.IsRepeating && !view.IsRepeatOne && service.Queue.RepeatMode == RepeatMode.All,
                "The first repeat action must select playlist repeat.");
            TestAssert.Equal("列表循环", view.RepeatModeText, "Playlist repeat must have the matching presentation label.");
            await ExecuteAsync(view, view.CycleRepeatModeCommand);
            TestAssert.True(view.IsRepeating && view.IsRepeatOne && service.Queue.RepeatMode == RepeatMode.One,
                "The second repeat action must select single-track repeat.");
            TestAssert.Equal("单曲循环", view.RepeatModeText, "Single-track repeat must have the matching presentation label.");
            await ExecuteAsync(view, view.CycleRepeatModeCommand);
            TestAssert.True(!view.IsRepeating && !view.IsRepeatOne && service.Queue.RepeatMode == RepeatMode.None,
                "The third repeat action must restore no repeat.");
            TestAssert.Equal("顺序播放", view.RepeatModeText, "Cycling repeat must restore the original label.");
            TestAssert.True(ReferenceEquals(loaded, service.Snapshot.Track) && ReferenceEquals(loaded, service.Queue.Current),
                "Changing playback modes must preserve the resource owner and selected queue entry.");
            TestAssert.Equal(opened, backend.OpenCount, "Changing playback modes cannot prepare the loaded resource again.");
            TestAssert.Equal(TimeSpan.FromSeconds(31), service.Snapshot.Position, "Changing playback modes cannot reset the media clock.");
            TestAssert.Equal(PlaybackState.Paused, service.Snapshot.State, "Changing playback modes cannot resume paused audio.");
            foreach (var property in new[] { nameof(MainViewModel.IsShuffle), nameof(MainViewModel.IsRepeating), nameof(MainViewModel.IsRepeatOne),
                nameof(MainViewModel.RepeatModeText), nameof(MainViewModel.ShuffleModeText) })
            {
                TestAssert.True(changed.ContainsKey(property), "Mode changes must notify the binding for " + property + ".");
            }

            var visited = new List<string> { service.Snapshot.Track!.Id };
            for (var index = 1; index < tracks.Length; index++)
            {
                await ExecuteAsync(view, view.NextCommand);
                visited.Add(service.Snapshot.Track!.Id);
            }

            TestAssert.True(visited.Order(StringComparer.Ordinal).SequenceEqual(tracks.Select(item => item.Id).Order(StringComparer.Ordinal)),
                "Next-track actions must traverse every shuffled entry once without repetition or omission.");
            TestAssert.True(!visited.SequenceEqual(tracks.Select(item => item.Id)),
                "The deterministic shuffled queue must affect actual next-track navigation.");
            loaded = service.Snapshot.Track;
            opened = backend.OpenCount;
            await ExecuteAsync(view, view.ToggleShuffleCommand);
            TestAssert.True(!view.IsShuffle && !service.Queue.Shuffle, "A second shuffle action must restore sequential queue navigation.");
            TestAssert.Equal("顺序播放", view.ShuffleModeText, "Disabling shuffle restores its sequential label.");
            TestAssert.True(ReferenceEquals(loaded, service.Snapshot.Track) && backend.OpenCount == opened,
                "Disabling shuffle must preserve the currently playing resource without another preparation.");
        }

        /// <summary>Disables queue preference controls when no playback backend is available.</summary>
        /// <returns>A task representing backend availability checks.</returns>
        private static async Task ValidateUnavailableModesAsync()
        {
            var workspace = new LibraryExperienceTests.ExperienceWorkspace();
            var view = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action());
            await view.InitializeAsync();
            TestAssert.True(!view.ToggleShuffleCommand.CanExecute(null) && !view.CycleRepeatModeCommand.CanExecute(null),
                "An unavailable audio backend must disable shuffle and repeat actions.");
            view.ToggleShuffleCommand.Execute(null);
            view.CycleRepeatModeCommand.Execute(null);
            TestAssert.True(!view.IsShuffle && !view.IsRepeating, "Disabled mode actions cannot mutate the queue preferences.");
            await view.DisposeAsync();
            TestAssert.True(!view.ToggleShuffleCommand.CanExecute(null) && !view.CycleRepeatModeCommand.CanExecute(null),
                "Disposed presentation commands must remain disabled.");
        }

        /// <summary>Runs an available observed presentation action and waits for its completion.</summary>
        /// <param name="view">The action owner.</param>
        /// <param name="command">The public playback or preference action.</param>
        /// <returns>A task representing the completed action.</returns>
        private static async Task ExecuteAsync(MainViewModel view, AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The requested playback mode action must be available.");
            command.Execute(null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (view.IsBusy)
            {
                await Task.Delay(1, cancellation.Token);
            }

            await view.TransportUpdate.WaitAsync(cancellation.Token);
            TestAssert.True(string.IsNullOrEmpty(view.Status), "Playback mode operations must complete without an operation error.");
        }
    }
}
