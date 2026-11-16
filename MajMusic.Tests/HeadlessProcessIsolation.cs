#nullable enable

using System.Diagnostics;

namespace MajMusic.Tests
{
    /// <summary>Gives additional mobile UI regressions a complete process-owned application and dispatcher lifetime.</summary>
    internal static class HeadlessProcessIsolation
    {
        /// <summary>Identifies the one test group selected in a generated child process.</summary>
        private const string WorkerEnvironmentKey = "MAJMUSIC_HEADLESS_GROUP_WORKER";

        /// <summary>Runs a real UI group without sharing third-party process-global geometry caches with other dispatchers.</summary>
        /// <param name="group">The registered validation group that the child process must execute.</param>
        /// <param name="worker">The complete real UI assertions executed in that child.</param>
        /// <returns>A task completing only after the child passed, with all failures and timeouts propagated.</returns>
        public static async Task RunAsync(string group, Func<Task> worker)
        {
            if (Environment.GetEnvironmentVariable(WorkerEnvironmentKey) == group)
            {
                await worker();
                return;
            }

            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException("The validation executable is unavailable."),
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(typeof(HeadlessProcessIsolation).Assembly.Location);
            }

            start.ArgumentList.Add(group);
            start.Environment[WorkerEnvironmentKey] = group;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The mobile UI validation worker could not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                await Task.WhenAll(output, error);
                throw;
            }

            var diagnostics = (await output) + (await error);
            TestAssert.True(process.ExitCode == 0, "The isolated " + group + " regression must pass. " + diagnostics);
        }
    }
}
