#nullable enable

using System.Net;
using System.Text;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Application;

namespace MajMusic.Tests
{
    internal static class ChartPreviewAssetTests
    {
        private const string ChartText = "&title=Generated chart\r\n&first=-0.45\r\n&lv_2=4\r\n&inote_2=\r\n(120){4}1,2,E\r\n&inote_4=|| Empty\r\n(120){4}<HS*1.5>,,,E\r\n&lv_5=13+\r\n&inote_5=(120){4}1h[4:1],2-6[4:1],E\r\n&inote_7=\r\n(120){4}C,8,E\r\n";

        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "MajMusic-preview-assets-" + Guid.NewGuid().ToString("N"));
            try
            {
                await ValidateOriginalFilesAsync(root);
                await ValidateSourceFilesAsync(root);
                await ValidateLegacyRoutesAsync(root);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)
                    && Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                    && Path.GetFileName(root).StartsWith("MajMusic-preview-assets-", StringComparison.Ordinal))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static async Task ValidateOriginalFilesAsync(string root)
        {
            var local = Path.Combine(root, "originals", "song");
            Directory.CreateDirectory(local);
            var chart = Path.Combine(local, "Maidata.Txt");
            var audio = Path.Combine(local, "track.mp3");
            var cover = Path.Combine(local, "bg.png");
            var video = Path.Combine(local, "MV.MP4");
            await File.WriteAllTextAsync(chart, ChartText, new UTF8Encoding(true));
            await File.WriteAllTextAsync(audio, "Generated audio.");
            await File.WriteAllTextAsync(cover, "Generated cover.");
            await File.WriteAllTextAsync(video, "Generated PV.");
            var files = Directory.GetFiles(local);
            var originalChartBytes = await File.ReadAllBytesAsync(chart);
            using var handler = new PreviewTransport();
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(Path.Combine(root, "local-data"), client, client);
            await workspace.LoadSettingsAsync();
            var imported = (await workspace.IndexDirectoryAsync(Path.Combine(root, "originals"))).Tracks.Single();
            var expected = new[] { new ChartPreviewDifficulty(2, "4"), new ChartPreviewDifficulty(5, "13+"), new ChartPreviewDifficulty(7, null) };
            var movedAudio = audio + ".unavailable";
            File.Move(audio, movedAudio);
            try
            {
                TestAssert.True((await workspace.GetChartPreviewDifficultiesAsync(imported)).SequenceEqual(expected),
                    "Local difficulty lookup must not open the audio, even when the selected audio file is temporarily unavailable.");
            }
            finally
            {
                File.Move(movedAudio, audio);
            }
            var withVideo = await workspace.ResolveChartPreviewAsync(imported, true);
            TestAssert.Equal(ChartText, withVideo.ChartText, "Preview must preserve all original chart text and its newline format.");
            TestAssert.Equal(audio, withVideo.AudioPath, "The preview must read original audio without copying.");
            TestAssert.Equal(cover, withVideo.CoverPath, "The indexed original cover is the fallback background.");
            TestAssert.Equal(video, withVideo.VideoPath, "MV.MP4 remains the original matched background reference.");
            TestAssert.True(withVideo.Difficulties.SequenceEqual(expected), "Difficulty slots must be selectable even when nonconsecutive or missing a level label.");
            var withoutVideo = await workspace.ResolveChartPreviewAsync(imported, false);
            TestAssert.True(withoutVideo.VideoPath is null && handler.Requests.Count == 0, "Disabling local PV must not perform network requests.");
            var previewedChartBytes = await File.ReadAllBytesAsync(chart);
            TestAssert.True(files.Order().SequenceEqual(Directory.GetFiles(local).Order()) && originalChartBytes.SequenceEqual(previewedChartBytes),
                "Preview must not change files in the imported directory.");
        }

        private static async Task ValidateSourceFilesAsync(string root)
        {
            var data = Path.Combine(root, "online-data");
            using var handler = new PreviewTransport();
            using var client = new HttpClient(handler, false);
            MusicTrack first;
            MusicTrack second;
            var source = new EndpointConfiguration { Id = "preview-a", Name = "Preview A", BaseUri = new Uri("https://preview.test/api/") };
            var other = source with { Id = "preview-b", Name = "Preview B" };
            string? videoPath;
            using (var workspace = new MusicWorkspace(data, client, client))
            {
                var settings = await workspace.LoadSettingsAsync();
                await workspace.SaveSettingsAsync(settings with { OnlineEnabled = true, Endpoints = [source, other] });
                await workspace.LoginAsync(source, "Alice", "test-secret");
                await workspace.LoginAsync(other, "Bob", "test-secret");
                await workspace.SynchronizeAsync(source);
                await workspace.SynchronizeAsync(other);
                var tracks = await workspace.SearchAsync(null);
                first = tracks.Single(track => track.EndpointId == source.Id);
                second = tracks.Single(track => track.EndpointId == other.Id);
                TestAssert.True(!handler.Requests.Any(request => request.Path.Contains("/asset-", StringComparison.Ordinal)),
                    "Synchronizing the music library cannot pull preview-only assets.");

                handler.Requests.Clear();
                var difficulties = await workspace.GetChartPreviewDifficultiesAsync(first);
                TestAssert.True(difficulties.Select(item => item.Number).SequenceEqual([2, 5, 7])
                    && handler.Requests.Count(request => request == ("/api/asset-chart", "Alice")) == 1
                    && handler.Requests.All(request => request.Account == "Alice"
                        && (request.Path.EndsWith("/info", StringComparison.Ordinal) || request.Path == "/api/asset-chart")),
                    "Online difficulty lookup must use the song's account and only request the chart, never the music, cover, or video.");
                var noVideo = await workspace.ResolveChartPreviewAsync(first, false);
                TestAssert.True(noVideo.ChartText.Contains("&title=Alice", StringComparison.Ordinal) && noVideo.VideoPath is null,
                    "The song's owning endpoint session must supply the chart, with no implicit PV download.");
                TestAssert.True(!handler.Requests.Any(request => request.Path.EndsWith("/asset-video", StringComparison.Ordinal)),
                    "Turning video off must not contact the PV route.");
                var withVideo = await workspace.ResolveChartPreviewAsync(first, true);
                videoPath = withVideo.VideoPath;
                TestAssert.True(videoPath is not null && await File.ReadAllTextAsync(videoPath) == "pv-Alice", "PV bytes use the same owning session as the chart.");
                var otherPreview = await workspace.ResolveChartPreviewAsync(second, true);
                TestAssert.True(otherPreview.ChartText.Contains("&title=Bob", StringComparison.Ordinal) && otherPreview.VideoPath is null,
                    "A shared source address must not lend unverified chart or PV bytes across configured accounts.");
                TestAssert.True(handler.Requests.Count(request => request == ("/api/asset-chart", "Alice")) == 1
                    && handler.Requests.Count(request => request == ("/api/asset-chart", "Bob")) == 1,
                    "Warm preview must use the saved Music chart while the other source fetches its own chart.");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => workspace.ResolveChartPreviewAsync(first, true, cancelled.Token));
            }

            handler.Requests.Clear();
            using var reopened = new MusicWorkspace(data, client, client);
            await reopened.LoadSettingsAsync();
            reopened.SetOnlineEnabled(false);
            TestAssert.True((await reopened.GetChartPreviewDifficultiesAsync(first)).Select(item => item.Number).SequenceEqual([2, 5, 7])
                && handler.Requests.Count == 0, "Previously cached chart difficulties must be available offline.");
            var cached = await reopened.ResolveChartPreviewAsync(first, true);
            TestAssert.True(cached.VideoPath == videoPath && cached.ChartText.Contains("&title=Alice", StringComparison.Ordinal) && handler.Requests.Count == 0,
                "Cached chart, audio, and PV must remain usable after restart with networking disabled.");
            await reopened.RemoveTrackMediaAsync(first);
            await TestAssert.ThrowsAsync<FileNotFoundException>(() => reopened.ResolveChartPreviewAsync(first, true));
            TestAssert.True(!File.Exists(videoPath) && (await reopened.ResolveChartPreviewAsync(second, true)).ChartText.Contains("&title=Bob", StringComparison.Ordinal),
                "Deleting this song's local files must remove its PV and chart without disturbing the other account.");
        }

        private static async Task ValidateLegacyRoutesAsync(string root)
        {
            using var handler = new PreviewTransport { ChartInfoStatus = HttpStatusCode.MethodNotAllowed };
            using var client = new HttpClient(handler, false);
            using var workspace = new MusicWorkspace(Path.Combine(root, "legacy-data"), client, client);
            var settings = await workspace.LoadSettingsAsync();
            var source = new EndpointConfiguration { Id = "legacy", Name = "Legacy", BaseUri = new Uri("https://preview.test/api/") };
            await workspace.SaveSettingsAsync(settings with { OnlineEnabled = true, Endpoints = [source] });
            await workspace.LoginAsync(source, "Alice", "test-secret");
            await workspace.SynchronizeAsync(source);
            var track = (await workspace.SearchAsync(null)).Single();
            handler.Requests.Clear();

            var preview = await workspace.ResolveChartPreviewAsync(track, true);
            TestAssert.True(preview.ChartText.Contains("&title=Alice", StringComparison.Ordinal)
                && await File.ReadAllTextAsync(preview.AudioPath) == "audio-Alice"
                && preview.VideoPath is { } video && await File.ReadAllTextAsync(video) == "pv-Alice",
                "An uncached legacy song must resolve chart, track, and video without depending on its unsupported detail route.");
            TestAssert.True(handler.Requests.Count(item => item == ("/api/maichart/same-song/info", "Alice")) == 1
                && handler.Requests.All(item => item.Account == "Alice"),
                "Legacy fallbacks must keep the same scoped session and not requery unsupported details for audio.");

            await workspace.RemoveTrackMediaAsync(track);
            handler.Requests.Clear();
            TestAssert.True((await workspace.GetChartPreviewDifficultiesAsync(track)).Select(item => item.Number).SequenceEqual([2, 5, 7])
                && handler.Requests.SequenceEqual([("/api/maichart/same-song/info", "Alice"), ("/api/maichart/same-song/chart", "Alice")]),
                "Legacy difficulty lookup must only fall back to the chart route.");
            var warmed = await workspace.ResolveChartPreviewAsync(track, false);
            TestAssert.True(warmed.VideoPath is null && await File.ReadAllTextAsync(warmed.AudioPath) == "audio-Alice"
                && handler.Requests.Count(item => item == ("/api/maichart/same-song/chart", "Alice")) == 1,
                "Chart-only warming must still let legacy services open a PV-disabled preview without fetching the chart again.");

            await workspace.RemoveTrackMediaAsync(track);
            handler.ChartInfoStatus = HttpStatusCode.Forbidden;
            handler.Requests.Clear();
            await TestAssert.ThrowsAsync<HttpRequestException>(() => workspace.GetChartPreviewDifficultiesAsync(track));
            TestAssert.True(handler.Requests.SequenceEqual([("/api/maichart/same-song/info", "Alice")]),
                "An authorization failure must stop before any direct media fallback.");
        }

        private sealed class PreviewTransport : HttpMessageHandler
        {
            private const string Meta = """{"id":"same-song","title":"Preview","hash":"revision-a"}""";
            public List<(string Path, string Account)> Requests { get; } = [];
            public HttpStatusCode ChartInfoStatus { get; set; } = HttpStatusCode.OK;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var cookie = request.Headers.TryGetValues("Cookie", out var headers) ? string.Join(";", headers) : string.Empty;
                var account = cookie.Contains("account=Alice", StringComparison.Ordinal) ? "Alice"
                    : cookie.Contains("account=Bob", StringComparison.Ordinal) ? "Bob" : "Guest";
                Requests.Add((path, account));
                if (path == "/api/account/login")
                {
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var username = body.Contains("Alice", StringComparison.Ordinal) ? "Alice" : "Bob";
                    var loggedIn = Json("{\"code\":114514}");
                    loggedIn.Headers.TryAddWithoutValidation("Set-Cookie", $"account={username}; Path=/api; Secure; HttpOnly");
                    return loggedIn;
                }

                return path switch
                {
                    "/api/info" => Json("""{"id":"preview-source","name":"Preview"}"""),
                    "/api/maichart/list" => Json("[" + Meta + "]"),
                    "/api/maichart/same-song/info" when ChartInfoStatus != HttpStatusCode.OK => new HttpResponseMessage(ChartInfoStatus),
                    "/api/maichart/same-song/info" => Json("{\"metadata\":" + Meta
                        + ",\"resources\":{\"maidata\":{\"url\":\"/api/asset-chart\"},\"track\":{},\"thumbnail\":{},\"video\":"
                        + (account == "Alice" ? "{\"url\":\"/api/asset-video\"}" : "null") + "}}"),
                    "/api/asset-chart" => Text(ChartText.Replace("Generated chart", account, StringComparison.Ordinal)),
                    "/api/asset-video" => Text("pv-" + account),
                    "/api/maichart/same-song/chart" => Text(ChartText.Replace("Generated chart", account, StringComparison.Ordinal)),
                    "/api/maichart/same-song/video" => Text("pv-" + account),
                    "/api/maichart/same-song/track" => Text("audio-" + account),
                    "/api/maichart/same-song/image" => Text("cover-" + account),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }

            private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
            private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
        }
    }
}
