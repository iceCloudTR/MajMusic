#nullable enable

using MajMusic.Infrastructure.Preview;

namespace MajMusic.Tests
{
    /// <summary>Checks packaged renderer access and cancellation.</summary>
    internal static class MajViewRuntimeTests
    {
        public static async Task RunAsync()
        {
            var opened = new List<string>();
            var runtime = new MajViewRuntime("unused", name =>
            {
                opened.Add(name);
                return new MemoryStream();
            });

            await TestAssert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() =>
                runtime.EnsureAvailable(new CancellationToken(canceled: true))));
            TestAssert.Equal(0, opened.Count, "A canceled preview must leave the packaged assets unopened.");

            runtime.EnsureAvailable(default);
            TestAssert.True(opened.SequenceEqual(["Build.loader.js", "Build.framework.js", "Build.data", "Build.wasm"]),
                "The renderer must open each required file from the platform package.");

            var incomplete = new MajViewRuntime("unused", name => throw new FileNotFoundException(name));
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
                incomplete.EnsureAvailable(default)));
        }
    }
}
