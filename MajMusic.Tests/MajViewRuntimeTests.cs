#nullable enable

using System.Net;
using MajMusic.Infrastructure.Preview;

namespace MajMusic.Tests
{
    /// <summary>Checks local runtime verification and cancellation without external requests.</summary>
    internal static class MajViewRuntimeTests
    {
        /// <summary>Exercises missing, damaged, and canceled installs against the real install path.</summary>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-MajView-" + Guid.NewGuid().ToString("N"));
            var handler = new DownloadFixture();
            using var client = new HttpClient(handler);
            var runtime = new MajViewRuntime(root, client);
            var runtimeRoot = Path.Combine(root, "Runtime", "MajView");
            try
            {
                await TestAssert.ThrowsAsync<InvalidOperationException>(() => runtime.EnsureAsync(false));
                TestAssert.True(!runtime.IsInstalled && !Directory.Exists(root) && handler.Requests == 0,
                    "Opening without a cached runtime offline must not create files or contact GitHub.");

                await TestAssert.ThrowsAsync<InvalidDataException>(() => runtime.EnsureAsync(true));
                TestAssert.True(!runtime.IsInstalled && !Directory.EnumerateFileSystemEntries(runtimeRoot).Any(),
                    "A same-size response with a mismatched hash must not leave a partial install.");
                TestAssert.Equal(new Uri($"https://raw.githubusercontent.com/TeamMajdata/MajdataNet/{MajViewRuntime.Revision}/public/WebGLBuild/Build.loader.js"),
                    handler.LastRequest, "Runtime requests must use the pinned public revision.");

                handler.BlockRequest = true;
                using var cancellation = new CancellationTokenSource();
                var installing = runtime.EnsureAsync(true, cancellation.Token);
                await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await TestAssert.ThrowsAsync<OperationCanceledException>(async () =>
                {
                    cancellation.Cancel();
                    await installing;
                });
                TestAssert.True(!runtime.IsInstalled && !Directory.EnumerateFileSystemEntries(runtimeRoot).Any(),
                    "Canceling a pending installation must not leave staging files.");
            }
            finally
            {
                foreach (var path in new[] { runtimeRoot, Path.GetDirectoryName(runtimeRoot)!, root })
                {
                    if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                    {
                        Directory.Delete(path);
                    }
                }
            }
        }

        private sealed class DownloadFixture : HttpMessageHandler
        {
            public bool BlockRequest { get; set; }
            public int Requests { get; private set; }
            public Uri? LastRequest { get; private set; }
            public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                LastRequest = request.RequestUri;
                if (BlockRequest)
                {
                    RequestStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[26_982]) };
            }
        }
    }
}
