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
            await using var preview = new MajViewPreviewSession("unused", "题目 <script>", assets,
                (path, _) => Task.FromResult<Stream>(path is "granted-track" or "granted-video"
                    ? new MemoryStream(audio, false) : throw new FileNotFoundException()));
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = preview.Address, Timeout = TimeSpan.FromSeconds(5) };
            TestAssert.True(preview.Address.IsLoopback, "The viewer must bind only to the loopback interface.");
            TestAssert.Equal(maidata, await client.GetStringAsync("chart"), "Unity must receive the complete original chart text.");
            using var metadata = JsonDocument.Parse(await client.GetStringAsync("song.json"));
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
            await playback.SetQueueAsync([song, other]);
            await playback.SeekAsync(TimeSpan.FromSeconds(5));
            model.ShowPreviewVideo = false;
            await model.OpenMajViewAsync();
            TestAssert.True(model.MajViewAddress == workspace.Pending.Address && playback.Snapshot.State == PlaybackState.Paused
                && playback.Snapshot.Position == TimeSpan.FromSeconds(5) && playback.Queue.Tracks.SequenceEqual([song, other]),
                "Mounting MajView must park the original playback and retain the queue and position.");
            TestAssert.True(!workspace.IncludeVideo && model.MajViewStatus.Length == 0, "PV selection must apply to that exact preview.");
            await playback.ResumeAsync();
            await workspace.Pending.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(playback.Snapshot.State == PlaybackState.Playing && model.MajViewAddress is null,
                "Resuming music must remove the active preview.");

            workspace.Pending = new PreviewSession();
            workspace.Ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stale = model.OpenMajViewAsync();
            model.SelectedTrack = other;
            workspace.Ready.SetResult();
            await stale.WaitAsync(TimeSpan.FromSeconds(3));
            TestAssert.True(workspace.Pending.Closed.Task.IsCompleted && model.MajViewAddress is null && model.MajViewStatus.Length == 0,
                "A slow preview completing after selection changes must release its session without mounting.");
        }

        private sealed class PreviewWorkspace : LibraryExperienceTests.ExperienceWorkspace, IMajViewPreviewHost
        {
            public bool IsMajViewInstalled => true;
            public bool IncludeVideo { get; private set; }
            public PreviewSession Pending { get; set; } = new();
            public TaskCompletionSource? Ready { get; set; }

            public async Task<IMajViewPreviewSession> CreateMajViewPreviewAsync(MusicTrack track, bool includeVideo,
                bool allowRuntimeDownload, CancellationToken cancellationToken = default)
            {
                IncludeVideo = includeVideo;
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
