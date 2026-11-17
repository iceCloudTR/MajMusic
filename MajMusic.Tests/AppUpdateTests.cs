#nullable enable

using System.Net;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Network;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks the public update entry without using real accounts or services.</summary>
    internal static class AppUpdateTests
    {
        /// <summary>Exercises startup, newer and equal versions, missing releases and retry.</summary>
        /// <returns>The completed release and settings projection checks.</returns>
        public static async Task RunAsync()
        {
            using var transport = new ReleaseTransport();
            using var client = new HttpClient(transport);
            var source = new GitHubAppReleaseSource(client);
            var workspace = new LibraryExperienceTests.ExperienceWorkspace();
            await using var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()),
                static action => action(), releases: source);
            await model.InitializeAsync();

            var assemblyVersion = typeof(MainViewModel).Assembly.GetName().Version!;
            var installed = assemblyVersion.ToString(3);
            var newer = new Version(assemblyVersion.Major + 1, 0, 0);
            TestAssert.Equal("版本 " + installed, model.AppVersionText, "Settings must show the installed release version.");
            TestAssert.True(transport.Requests == 0 && !workspace.IsOnline && !model.OnlineEnabled,
                "Offline startup must not contact the release endpoint or a music source.");

            transport.Location = new Uri($"/iceCloudTR/MajMusic/releases/tag/v{newer}", UriKind.Relative);
            await TestAssert.ExecuteAsync(model.CheckAppUpdateCommand);
            TestAssert.Equal($"发现新版本 {newer}", model.AppUpdateStatus, "A newer GitHub release must appear in settings.");
            TestAssert.Equal(new Uri($"https://github.com/iceCloudTR/MajMusic/releases/tag/v{newer}"), model.AvailableReleasePage,
                "The download entry must point to the checked tag in the official repository.");

            transport.Location = new Uri($"https://github.com/iceCloudTR/MajMusic/releases/tag/{installed}");
            await TestAssert.ExecuteAsync(model.CheckAppUpdateCommand);
            TestAssert.True(model.AppUpdateStatus == "已是最新版本" && model.AvailableReleasePage is null,
                "The installed version must compare as equal and clear a previous update link.");

            transport.Location = new Uri("https://github.com/iceCloudTR/MajMusic/releases");
            await TestAssert.ExecuteAsync(model.CheckAppUpdateCommand);
            TestAssert.Equal("暂无正式版本", model.AppUpdateStatus, "An empty release list must have its own status.");

            transport.Status = HttpStatusCode.TooManyRequests;
            await TestAssert.ExecuteAsync(model.CheckAppUpdateCommand);
            TestAssert.Equal("无法检查更新，请稍后重试", model.AppUpdateStatus, "A rate limit should keep the check available for retry.");

            transport.Status = HttpStatusCode.Found;
            transport.Location = new Uri($"https://github.com/iceCloudTR/MajMusic/releases/tag/v{newer}-preview");
            await TestAssert.ThrowsAsync<InvalidDataException>(() => source.GetLatestAsync());
            transport.Location = new Uri("https://example.invalid/iceCloudTR/MajMusic/releases/tag/v999.0.0");
            await TestAssert.ThrowsAsync<InvalidDataException>(() => source.GetLatestAsync());
            transport.Location = new Uri($"https://github.com/iceCloudTR/MajMusic/releases/tag/v{newer}");
            await TestAssert.ExecuteAsync(model.CheckAppUpdateCommand);
            TestAssert.True(model.AvailableReleasePage is not null && !workspace.IsOnline && !model.OnlineEnabled && !workspace.Settings.OnlineEnabled,
                "A successful retry must preserve the independent offline music preference.");
        }

        /// <summary>Records the fixed anonymous GitHub request and returns a controlled release.</summary>
        private sealed class ReleaseTransport : HttpMessageHandler
        {
            public int Requests { get; private set; }
            public Uri? Location { get; set; }
            public HttpStatusCode Status { get; set; } = HttpStatusCode.Found;

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests++;
                TestAssert.True(request.Method == HttpMethod.Head
                    && request.RequestUri == new Uri("https://github.com/iceCloudTR/MajMusic/releases/latest")
                    && request.Headers.UserAgent.ToString() == "MajMusic"
                    && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"),
                    "Release lookup must contact only the public GitHub release link without a music-source account.");
                return Task.FromResult(new HttpResponseMessage(Status)
                {
                    Headers = { Location = Location }
                });
            }
        }
    }
}
