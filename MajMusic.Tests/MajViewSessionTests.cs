#nullable enable

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Preview;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    internal static class MajViewSessionTests
    {
        public static async Task RunAsync()
        {
            var audio = Enumerable.Range(0, 120).Select(value => (byte)value).ToArray();
            const string maidata = "&title=Fixture\n&inote_2=(120){4}1,2,E\n&inote_5=(120){4}8,7,E\n";
            var assets = new ChartPreviewAssets(maidata, "granted-track", null, "granted-video",
                [new(2, "6"), new(5, "12+")]);
            var loader = MajViewRuntime.Files.Single(file => file.Name == "Build.loader.js");
            var loaderBytes = new byte[loader.Length];
            Array.Fill(loaderBytes, (byte)42);
            var runtime = new MajViewRuntime("unused", _ => new PackageAssetStream(loaderBytes));
            await using var preview = new MajViewPreviewSession(runtime, "题目 <script>", assets,
                (path, _) => Task.FromResult<Stream>(path is "granted-track" or "granted-video"
                    ? new MemoryStream(audio, false) : throw new FileNotFoundException()));
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = preview.Address, Timeout = TimeSpan.FromSeconds(5) };
            TestAssert.True(preview.Address.IsLoopback, "The viewer must bind only to the loopback interface.");
            TestAssert.Equal(maidata, await client.GetStringAsync("chart"), "Unity must receive the complete original chart text.");
            using var renderer = await client.GetAsync("runtime/Build.loader.js");
            TestAssert.True(renderer.Content.Headers.ContentLength == loaderBytes.Length
                && (await renderer.Content.ReadAsByteArrayAsync()).SequenceEqual(loaderBytes),
                "Unity must receive the renderer bytes and length even when the package asset does not support seeking.");
            using var metadata = JsonDocument.Parse(await client.GetStringAsync("song.json"));
            TestAssert.Equal(MajViewRuntime.EmbeddedViewport, metadata.RootElement.GetProperty("embeddedViewport").GetBoolean(),
                "The viewer must receive the viewport mode of the packaged renderer.");
            TestAssert.True(metadata.RootElement.GetProperty("difficulties").EnumerateArray()
                .Select(level => level.GetProperty("number").GetInt32()).SequenceEqual([2, 5]),
                "The browser must receive the real sparse difficulty numbers.");
            using var range = new HttpRequestMessage(HttpMethod.Get, "video.mp4");
            range.Headers.Range = new RangeHeaderValue(50, 59);
            using var video = await client.SendAsync(range);
            TestAssert.True(video.StatusCode == HttpStatusCode.PartialContent && video.Content.Headers.ContentRange?.Length == audio.Length
                && (await video.Content.ReadAsByteArrayAsync()).SequenceEqual(audio[50..60]),
                "Video seeks must return exactly the requested byte interval.");
            using var unknown = await client.GetAsync("runtime/anything-else");
            TestAssert.Equal(HttpStatusCode.NotFound, unknown.StatusCode, "Only declared routes may expose files.");
            using var foreign = await client.GetAsync(new Uri(preview.Address, "/other-session/track"));
            TestAssert.Equal(HttpStatusCode.NotFound, foreign.StatusCode, "Another session token must not expose the song.");
            var intent = new ChartIntent(3, 1, 1, 4.25, false, new ChartPracticeOptions(), 0.75, true);
            preview.SetIntent(intent);
            using var command = JsonDocument.Parse(await client.GetStringAsync("transport?g=3&ack=-1"));
            TestAssert.Equal(4.25, command.RootElement.GetProperty("position").GetDouble(), "The document must receive the absolute seek target.");
            using var staleRequest = await client.GetAsync("transport?g=2&ack=1");
            TestAssert.Equal(HttpStatusCode.Gone, staleRequest.StatusCode, "A replaced difficulty must not control the current clock.");
            var clockReceived = new TaskCompletionSource<ChartFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            preview.FrameReceived += frame => clockReceived.TrySetResult(frame);
            var waiting = client.GetAsync("transport?g=3&ack=1&time=4.25&len=20&play=0");
            var frame = await clockReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            TestAssert.Equal(4.25, frame.Position, "The parked renderer must report its exact clock.");
            preview.SetIntent(intent with { Revision = 2, Playing = true });
            using var resumed = await waiting;
            TestAssert.Equal(HttpStatusCode.OK, resumed.StatusCode, "Native play must immediately wake a sleeping chart.");
            using var malformed = await client.PostAsync("notes?g=3&len=20", new StringContent("[[1],[],[],[],[-7]]"));
            TestAssert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode, "Malformed analysis must not enter the UI.");
            ChartNoteTimeline? timeline = null;
            preview.FrameReceived += update => timeline = update.Notes;
            using var analysis = await client.PostAsync("notes?g=3&len=20", new StringContent("[[5001,5000,5000],[],[6250],[],[]]"));
            TestAssert.True(analysis.StatusCode == HttpStatusCode.NoContent && timeline is not null &&
                timeline.Totals.SequenceEqual([3, 0, 1, 0, 0]) && timeline.Density.Count == 640 && timeline.Density.Sum() == 4
                && timeline.CountAt(0, 5) == 2 && timeline.CountAt(0, 4.999) == 0 && timeline.CountAt(0, 5.002) == 3,
                "Simultaneous judgements and nearby notes in one density bin must have independently seekable, exact counts.");
            using var close = await client.PostAsync("closed", null);
            TestAssert.Equal(HttpStatusCode.NoContent, close.StatusCode, "Normal page close must acknowledge session removal.");
            await preview.DisposeAsync();
            await TestAssert.ThrowsAsync<HttpRequestException>(async () => await client.GetAsync("chart"));
            await ValidatePlaybackHandoffAsync();
            await ValidateFirstChartAsync();
            await ValidatePendingChartClockAsync();
        }

        private static async Task ValidatePendingChartClockAsync()
        {
            var workspace = new PreviewWorkspace();
            var song = new MusicTrack { Id = "one-minute-in", Title = "Parked at 1:02", Audio = new() { LocalPath = "audio" } };
            var playback = new PlaybackService(new LibraryExperienceTests.ExperienceBackend(), resolveAudioPath: (_, _) => Task.FromResult("audio"));
            await using var model = new MainViewModel(workspace, playback, static work => work());
            await model.InitializeAsync();
            model.SelectedTrack = song;
            model.IsSongDetailsExpanded = true;
            await model.EnsureMajViewDifficultiesAsync();
            await playback.SetQueueAsync([song]);
            await playback.SeekAsync(TimeSpan.FromSeconds(62));
            workspace.Ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opening = model.SelectMajViewDifficultyAsync(2);
            TestAssert.True(model.DurationSeconds == 120 && model.PositionSeconds == 62 && model.DurationText == "02:00" && model.BeginSeek(),
                "Session preparation must keep the song's real seek range and clock, even before there is a renderer.");
            model.PositionSeconds = 75;
            await model.CommitSeekAsync();
            workspace.Ready.SetResult();
            await opening;
            TestAssert.Equal(75d, workspace.Pending.Intent!.Position, "The mounted document must start at the seek queued during preparation.");
            TestAssert.True(model.BeginSeek(), "Loading WebGL must still accept seeks.");
            model.PositionSeconds = 90;
            await model.CommitSeekAsync();
            var latest = workspace.Pending.Intent!;
            workspace.Pending.Report(new(latest.Generation, latest.Revision - 1, 75, 120, false));
            TestAssert.True(model.PositionSeconds == 90 && model.DurationSeconds == 120, "A late clock cannot undo a loading-time seek.");
            var notes = ChartNoteTimeline.Create([[1000, 10000], [90000], [], [2000], []], 120)!;
            workspace.Pending.Report(new(latest.Generation, -1, 0, 120, false, Notes: notes));
            workspace.Pending.Report(new(latest.Generation, latest.Revision, 90, 120, false));
            TestAssert.True(model.PassedNotes.SequenceEqual([2, 1, 0, 1, 0]) && model.ChartNotes.Contains("HOLD 1/1"), "The legend must include the exact cursor and totals.");
            TestAssert.True(model.BeginSeek(), "A second drag must take ownership of the cursor.");
            model.PositionSeconds = 1;
            TestAssert.True(model.PassedNotes.SequenceEqual([1, 0, 0, 0, 0]), "Dragging backwards must update counts before the renderer round trip.");
            await model.CommitSeekAsync();

            var other = song with { Id = "different" };
            model.SelectedTrack = other;
            await model.EnsureMajViewDifficultiesAsync();
            workspace.Pending = new PreviewSession();
            await model.SelectMajViewDifficultyAsync(2);
            TestAssert.True(model.IsChartDurationPending && !model.CanSeek && model.PositionSeconds == 0 && model.DurationText == "--:--",
                "An unrelated song with unknown duration must get a neutral loading rail, never the parked song's range or progress.");
        }

        private static async Task ValidateFirstChartAsync()
        {
            var workspace = new PreviewWorkspace();
            var track = new MusicTrack { Id = "first", Title = "First practice", Audio = new() { LocalPath = "song" } };
            var backend = new LibraryExperienceTests.ExperienceBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: (song, _) => Task.FromResult(song.Audio.LocalPath!));
            await using var model = new MainViewModel(workspace, playback, static work => work());
            await model.InitializeAsync();
            model.SelectedTrack = track;
            model.IsSongDetailsExpanded = true;
            await model.EnsureMajViewDifficultiesAsync();
            await model.SelectMajViewDifficultyAsync(2);
            var intent = workspace.Pending.Intent!;
            workspace.Pending.Report(new(intent.Generation, intent.Revision, 17.5, 120, false));
            model.CloseMajView();
            TestAssert.True(backend.OpenCount == 0 && !model.IsPlaying && model.TransportTitle == track.Title && model.PositionSeconds == 17.5,
                "Returning from the first chart must present its song and parked clock without starting audio.");
            model.TogglePlayPauseCommand.Execute(null);
            await model.TransportUpdate;
            TestAssert.True(backend.OpenCount == 1 && playback.Queue.Current?.Id == track.Id && playback.Snapshot.Position.TotalSeconds == 17.5,
                "The first native play must pick up the parked chart position.");
        }

        private sealed class PackageAssetStream(byte[] bytes) : MemoryStream(bytes, writable: false)
        {
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
        }

        private static async Task ValidatePlaybackHandoffAsync()
        {
            var workspace = new PreviewWorkspace();
            var song = new MusicTrack { Id = "current", Title = "当前歌曲", Audio = new() { LocalPath = "mock-source" } };
            var other = song with { Id = "other", Title = "另一首歌" };
            var backend = new LibraryExperienceTests.ExperienceBackend();
            var playback = new PlaybackService(backend, resolveAudioPath: (track, _) => Task.FromResult(track.Audio.LocalPath!));
            await using var model = new MainViewModel(workspace, playback, static work => work());
            await model.InitializeAsync();
            model.SelectedTrack = song;
            model.IsSongDetailsExpanded = true;
            await model.EnsureMajViewDifficultiesAsync();
            TestAssert.True(model.MajViewDifficulties.Select(item => item.Number).SequenceEqual([2, 5, 7])
                && workspace.Created == 0 && model.MajViewAddress is null,
                "Opening details must list only playable slots without starting the viewer.");
            await playback.SetQueueAsync([song, other]);
            await playback.SeekAsync(TimeSpan.FromSeconds(5));
            model.ShowPreviewVideo = false;
            await model.SelectMajViewDifficultyAsync(7);
            TestAssert.True(model.MajViewAddress?.Query.StartsWith("?level=7&direction=1&g=") == true && playback.Snapshot.State == PlaybackState.Paused
                && playback.Snapshot.Position == TimeSpan.FromSeconds(5) && playback.Queue.Tracks.SequenceEqual([song, other]),
                "Mounting MajView must park the original playback and retain the queue and position.");
            TestAssert.True(!workspace.IncludeVideo && model.MajViewStatus.Length == 0, "PV selection must apply to that exact preview.");
            var original = workspace.Pending.Intent!;
            TestAssert.Equal(5d, original.Position, "The chart must open at the parked song clock.");
            workspace.Pending.Report(new(original.Generation, original.Revision, 5, 120, false));
            TestAssert.True(model.CanSeek && model.BeginSeek(), "The bottom bar must seek the chart when ready.");
            model.PositionSeconds = 31.5;
            await model.CommitSeekAsync();
            var sought = workspace.Pending.Intent!;
            workspace.Pending.Report(new(original.Generation, original.Revision, 0, 120, true));
            TestAssert.True(model.PositionSeconds == 31.5 && !model.IsPlaying, "A late pre-seek frame cannot rewind the thumb or unpause it.");
            TestAssert.True(sought.SeekRevision > original.SeekRevision && sought.Position == 31.5 && playback.Snapshot.Position.TotalSeconds == 5,
                "Seeking the chart must not play or seek the parked backend.");
            model.TogglePlayPauseCommand.Execute(null);
            await model.TransportUpdate;
            TestAssert.True(workspace.Pending.Intent!.Playing && playback.Snapshot.State == PlaybackState.Paused,
                "The center button must only start the active source.");
            await model.SelectMajViewDifficultyAsync(5);
            TestAssert.True(workspace.Pending.Intent is { Playing:false, Position:31.5 }, "Changing difficulty must retain time and pause.");
            workspace.Pending.Report(new(original.Generation, original.Revision, 99, 120, true));
            TestAssert.True(model.CanSeek && !model.TogglePlayPauseCommand.CanExecute(null) && model.DurationSeconds == 120 && model.PositionSeconds == 31.5, "A new difficulty must retain the seek range while ignoring the old playback clock.");
            await model.SelectMajViewDifficultyAsync(3);
            TestAssert.True(model.MajViewAddress?.Query.StartsWith("?level=5&direction=-1&g=") == true
                && model.MajViewAddress.GetLeftPart(UriPartial.Path) == workspace.Pending.Address.AbsoluteUri
                && workspace.Created == 1 && playback.Snapshot.State == PlaybackState.Paused,
                "Difficulty switching must navigate the original session without accepting empty slots or restarting the music queue.");
            var latest = workspace.Pending.Intent!;
            workspace.Pending.Report(new(latest.Generation, latest.Revision, 41, 120, false));
            model.CloseMajView();
            await model.TransportUpdate;
            TestAssert.True(model.PositionSeconds == 41 && !model.IsPlaying && playback.Queue.Current == song,
                "Returning to the song must hand back chart time while paused.");
            model.PreviousCommand.Execute(null);
            await model.TransportUpdate;
            TestAssert.True(playback.Queue.Current == song && model.PositionSeconds == 0 && model.IsPlaying,
                "After three seconds the left button must restart the same song.");
            model.NextCommand.Execute(null);
            await model.TransportUpdate;
            TestAssert.Equal(other.Id, playback.Snapshot.Track!.Id, "Next must advance the queue.");
            await playback.SeekAsync(TimeSpan.FromSeconds(2.99));
            model.PreviousCommand.Execute(null);
            await model.TransportUpdate;
            TestAssert.Equal(song.Id, playback.Snapshot.Track!.Id, "Before three seconds the left button must use the previous song.");
            workspace.Pending = new PreviewSession();
            await model.SelectMajViewDifficultyAsync(5);
            await playback.ResumeAsync();
            await workspace.Pending.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(playback.Snapshot.State == PlaybackState.Playing && model.MajViewAddress is null,
                "Resuming music must remove the active preview.");

            await playback.SeekAsync(TimeSpan.FromSeconds(12));
            model.SelectedTrack = other;
            await model.EnsureMajViewDifficultiesAsync();
            workspace.Pending = new PreviewSession();
            await model.SelectMajViewDifficultyAsync(2);
            var foreign = workspace.Pending.Intent!;
            workspace.Pending.Report(new(foreign.Generation, foreign.Revision, 45, 120, false));
            model.CloseMajView();
            await model.TransportUpdate;
            TestAssert.True(model.PositionSeconds == 12 && model.TransportTitle == song.Title && !model.IsPlaying,
                "Reviewing another chart must retain the original queue's parked position.");
            var options = new ChartPracticeOptions(8, 9, 0.4, 35, 0.3, 0.75);
            await model.SaveChartOptionsAsync(options);
            TestAssert.Equal(options, (await workspace.LoadSettingsAsync()).ChartPractice, "Native settings must survive the session.");
            model.SelectedTrack = song;
            await model.EnsureMajViewDifficultiesAsync();
            workspace.Pending = new PreviewSession();
            workspace.Ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stale = model.SelectMajViewDifficultyAsync(2);
            await model.SelectMajViewDifficultyAsync(7);
            model.SelectedTrack = other;
            workspace.Ready.SetResult();
            await stale.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(workspace.Pending.Closed.Task.IsCompleted && model.MajViewAddress is null && model.MajViewStatus.Length == 0,
                "A slow preview completing after selection changes must release its session without mounting.");
        }

        private sealed class PreviewWorkspace : LibraryExperienceTests.ExperienceWorkspace, IMajViewPreviewHost, IChartPreviewWorkspace
        {
            public bool IncludeVideo { get; private set; }
            public int Created { get; private set; }
            public PreviewSession Pending { get; set; } = new();
            public TaskCompletionSource? Ready { get; set; }

            public Task<IReadOnlyList<ChartPreviewDifficulty>> GetChartPreviewDifficultiesAsync(MusicTrack track,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ChartPreviewDifficulty>>([new(7, "?"), new(2, "6"), new(5, "12+")]);

            public Task<ChartPreviewAssets> ResolveChartPreviewAsync(MusicTrack track, bool includeVideo,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public async Task<IMajViewPreviewSession> CreateMajViewPreviewAsync(MusicTrack track, bool includeVideo,
                CancellationToken cancellationToken = default)
            {
                IncludeVideo = includeVideo;
                Created++;
                if (Ready is { } ready)
                {
                    await ready.Task;
                }
                return Pending;
            }
        }

        private sealed class PreviewSession : IMajViewPreviewSession
        {
            public event Action<ChartFrame>? FrameReceived;
            public ChartIntent? Intent { get; private set; }
            public void SetIntent(ChartIntent intent) => Intent = intent;
            public void Report(ChartFrame frame) => FrameReceived?.Invoke(frame);
            public Uri Address { get; } = new("http://127.0.0.1:1234/");
            public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ValueTask DisposeAsync()
            {
                Closed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }
}
