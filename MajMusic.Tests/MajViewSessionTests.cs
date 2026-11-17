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
            using var close = await client.PostAsync("closed", null);
            TestAssert.Equal(HttpStatusCode.NoContent, close.StatusCode, "Normal page close must acknowledge session removal.");
            await preview.DisposeAsync();
            await TestAssert.ThrowsAsync<HttpRequestException>(async () => await client.GetAsync("chart"));
            await ValidatePlaybackHandoffAsync();
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
            TestAssert.True(model.MajViewAddress?.Query == "?level=7&direction=1" && playback.Snapshot.State == PlaybackState.Paused
                && playback.Snapshot.Position == TimeSpan.FromSeconds(5) && playback.Queue.Tracks.SequenceEqual([song, other]),
                "Mounting MajView must park the original playback and retain the queue and position.");
            TestAssert.True(!workspace.IncludeVideo && model.MajViewStatus.Length == 0, "PV selection must apply to that exact preview.");
            await model.SelectMajViewDifficultyAsync(5);
            await model.SelectMajViewDifficultyAsync(3);
            TestAssert.True(model.MajViewAddress?.Query == "?level=5&direction=-1"
                && model.MajViewAddress.GetLeftPart(UriPartial.Path) == workspace.Pending.Address.AbsoluteUri
                && workspace.Created == 1 && playback.Snapshot.State == PlaybackState.Paused,
                "Difficulty switching must navigate the original session without accepting empty slots or restarting the music queue.");
            await playback.ResumeAsync();
            await workspace.Pending.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(playback.Snapshot.State == PlaybackState.Playing && model.MajViewAddress is null,
                "Resuming music must remove the active preview.");

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
