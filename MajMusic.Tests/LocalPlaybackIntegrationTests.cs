#nullable enable

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using MajMusic.Audio.Desktop;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Metadata;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Views;

namespace MajMusic.Tests
{
    /// <summary>Checks imported music through the actual workspace, shared view and native decoder without a device.</summary>
    internal static class LocalPlaybackIntegrationTests
    {
        /// <summary>Exercises the complete local playback path while rejecting every attempted HTTP request.</summary>
        /// <returns>A task representing local import, playback, seek and queue integration.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch(async () =>
            {
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MajMusic-local-playback-" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(root);
                using var handler = new OfflineTransport();
                using var transport = new HttpClient(handler);
                using var workspace = new MusicWorkspace(Path.Combine(root, "data"), transport, transport);
                var playback = new PlaybackService(new LibVlcPlaybackBackend(useDummyAudioOutput: true),
                    resolveAudioPath: (track, token) => workspace.ResolveAudioAsync(track, false, token));
                var metadata = new CachedTrackMetadataProvider(new LocalAudioMetadataReader(), Path.Combine(root, "metadata"));
                await using var viewModel = new MainViewModel(workspace, playback, metadata: metadata);
                var window = new Window
                {
                    Content = new MainView { DataContext = viewModel },
                    Width = 1100,
                    Height = 780,
                    Title = "MajMusic"
                };
                try
                {
                    await viewModel.InitializeAsync();
                    window.Show();
                    var firstDirectory = CreateSong(root, "Alpha", 6);
                    var secondDirectory = CreateSong(root, "Beta", 7);
                    await viewModel.ImportDirectoryAsync(firstDirectory);
                    await viewModel.ImportDirectoryAsync(secondDirectory);
                    await viewModel.MetadataRefresh;
                    TestAssert.Equal(2, viewModel.Tracks.Count, "Two distinct imports must appear in the shared view.");
                    TestAssert.True(!workspace.IsOnline && !viewModel.OnlineEnabled, "Import and native initialization must remain offline.");
                    TestAssert.True(viewModel.VisibleTracks.All(track => track.Duration is { TotalSeconds: > 5 }
                        && track.AudioInfo is { SampleRateHz: 44100, Channels: 2 }),
                        "Every visible library row must have real metadata before playback or selection.");
                    TestAssert.True(playback.Snapshot.Track is null, "Background row metadata must not open the playback owner.");
                    viewModel.SelectedTrack = null;
                    var libraryOutput = Path.Combine(Environment.CurrentDirectory, ".work");
                    Directory.CreateDirectory(libraryOutput);
                    using (var libraryFrame = window.CaptureRenderedFrame()
                        ?? throw new InvalidOperationException("The library metadata view did not render."))
                    {
                        libraryFrame.Save(Path.Combine(libraryOutput, "ui-library-metadata.png"), PngBitmapEncoderOptions.Default);
                    }

                    var first = viewModel.Tracks.Single(track => track.Title == "Alpha");
                    var second = viewModel.Tracks.Single(track => track.Title == "Beta");
                    viewModel.SelectedTrack = first;
                    Execute(viewModel.TogglePlayPauseCommand);
                    await WaitUntilAsync(() => playback.Snapshot.State == PlaybackState.Playing && viewModel.CanSeek
                        && viewModel.PositionSeconds > 0.2 && !viewModel.IsBusy, deadline.Token);
                    TestAssert.True(playback.Snapshot.Duration is { TotalSeconds: > 5.8 and < 6.2 },
                        "The imported WAV duration must come from LibVLC.");
                    TestAssert.True(playback.Snapshot.AudioInfo is { SampleRateHz: 44100, Channels: 2 },
                        "The real indexed stream must expose parsed sample rate and channels.");
                    TestAssert.True(viewModel.AudioDetails.Contains("kHz", StringComparison.Ordinal),
                        "Parsed stream information must reach the actual shared view.");
                    TestAssert.True(first.Audio.LocalPath!.StartsWith(firstDirectory, StringComparison.Ordinal)
                        && !Directory.Exists(Path.Combine(workspace.DataDirectory, "Library")),
                        "The complete local playback flow must use original files without creating managed copies.");

                    Execute(viewModel.TogglePlayPauseCommand);
                    await WaitUntilAsync(() => playback.Snapshot.State == PlaybackState.Paused && !viewModel.IsBusy, deadline.Token);
                    var paused = playback.Snapshot.Position;
                    await Task.Delay(300, deadline.Token);
                    TestAssert.True(Math.Abs((playback.Snapshot.Position - paused).TotalMilliseconds) < 100,
                        "Pausing through the actual view command must stop the native clock.");
                    TestAssert.True(viewModel.BeginSeek(), "A paused imported file must allow timeline dragging.");
                    viewModel.PositionSeconds = 2;
                    await viewModel.CommitSeekAsync();
                    TestAssert.True(Math.Abs(playback.Snapshot.Position.TotalSeconds - 2) < 0.35,
                        "Committing a view timeline preview must seek the actual native player.");
                    TestAssert.Equal(PlaybackState.Paused, playback.Snapshot.State, "Paused seeking must preserve pause.");

                    Execute(viewModel.TogglePlayPauseCommand);
                    await WaitUntilAsync(() => playback.Snapshot.State == PlaybackState.Playing && !viewModel.IsBusy, deadline.Token);
                    Execute(viewModel.NextCommand);
                    await WaitUntilAsync(() => playback.Snapshot.Track?.Id == second.Id && viewModel.PlaybackTitle == "Beta"
                        && playback.Snapshot.State == PlaybackState.Playing && !viewModel.IsBusy, deadline.Token);
                    Execute(viewModel.TogglePlayPauseCommand);
                    await WaitUntilAsync(() => playback.Snapshot.State == PlaybackState.Paused && !viewModel.IsBusy, deadline.Token);
                    Execute(viewModel.TogglePlayPauseCommand);
                    await WaitUntilAsync(() => playback.Snapshot.State == PlaybackState.Playing && !viewModel.IsBusy, deadline.Token);
                    TestAssert.Equal(second.Id, playback.Snapshot.Track?.Id,
                        "Resuming after next-track navigation must keep the paused file instead of the browsing selection.");
                    Execute(viewModel.PreviousCommand);
                    await WaitUntilAsync(() => playback.Snapshot.Track?.Id == first.Id && viewModel.PlaybackTitle == "Alpha"
                        && viewModel.CanSeek && !viewModel.IsBusy, deadline.Token);

                    TestAssert.True(viewModel.BeginSeek(), "A playing imported file must support a new timeline preview.");
                    viewModel.PositionSeconds = 5.5;
                    await viewModel.CommitSeekAsync();
                    await WaitUntilAsync(() => playback.Snapshot.Track?.Id == second.Id && viewModel.PlaybackTitle == "Beta"
                        && playback.Snapshot.State == PlaybackState.Playing && !viewModel.IsBusy, deadline.Token);
                    Execute(viewModel.TogglePlayPauseCommand);
                    await WaitUntilAsync(() => playback.Snapshot.State == PlaybackState.Paused && !viewModel.IsBusy, deadline.Token);
                    TestAssert.True(viewModel.BeginSeek(), "The automatically advanced file must retain a working timeline.");
                    viewModel.PositionSeconds = 3;
                    await viewModel.CommitSeekAsync();

                    var output = Path.Combine(Environment.CurrentDirectory, ".work");
                    Directory.CreateDirectory(output);
                    using var frame = window.CaptureRenderedFrame()
                        ?? throw new InvalidOperationException("The native playback view did not render.");
                    frame.Save(Path.Combine(output, "ui-local-native-playback.png"), PngBitmapEncoderOptions.Default);
                    TestAssert.Equal(0, handler.RequestCount, "The complete imported local playback flow must send zero HTTP requests.");
                    TestAssert.True(!workspace.IsOnline, "Playback must not enable online sources.");
                    Console.WriteLine("Imported local music passed native/UI playback, pause, drag seek and manual/automatic switching with zero HTTP requests (dummy output).");
                    return true;
                }
                finally
                {
                    window.Close();
                    await viewModel.DisposeAsync();
                    workspace.Dispose();
                    var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(root).StartsWith("MajMusic-local-playback-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Refusing to clean outside the local playback test directory.");
                    }

                    Directory.Delete(root, true);
                }
            }, deadline.Token);
        }

        /// <summary>Creates an original local audio fixture with minimal song metadata.</summary>
        /// <param name="root">The isolated validation directory.</param>
        /// <param name="title">The fixture title.</param>
        /// <param name="seconds">The unique fixture duration.</param>
        /// <returns>The importable song directory.</returns>
        private static string CreateSong(string root, string title, int seconds)
        {
            var directory = Path.Combine(root, title);
            Directory.CreateDirectory(directory);
            DesktopAudioTests.WriteQuietWave(Path.Combine(directory, "track.wav"), seconds);
            File.WriteAllText(Path.Combine(directory, "maidata.txt"), $"&title={title}\n&artist=Generated local fixture\n");
            return directory;
        }

        /// <summary>Executes a view command only when its real availability predicate permits it.</summary>
        /// <param name="command">The actual shared-view command.</param>
        private static void Execute(AsyncCommand command)
        {
            TestAssert.True(command.CanExecute(null), "The requested playback action must be available in the shared view.");
            command.Execute(null);
        }

        /// <summary>Waits for observable integration state without simulating media time.</summary>
        /// <param name="condition">The state that must become true.</param>
        /// <param name="cancellationToken">Cancels the enclosing validation.</param>
        /// <returns>A task representing the observed state.</returns>
        /// <exception cref="InvalidOperationException">The integration state does not arrive within five seconds.</exception>
        private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition())
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                {
                    throw new InvalidOperationException("The expected local playback integration state did not arrive.");
                }

                await Task.Delay(25, cancellationToken);
            }
        }

        /// <summary>Rejects and counts any unexpected source or download request.</summary>
        private sealed class OfflineTransport : HttpMessageHandler
        {
            /// <summary>Counts attempted requests across worker threads.</summary>
            private int _requestCount;

            /// <summary>Gets the number of attempted network operations.</summary>
            public int RequestCount => Volatile.Read(ref _requestCount);

            /// <summary>Fails every network operation rather than contacting a real endpoint.</summary>
            /// <param name="request">The unexpected outgoing request.</param>
            /// <param name="cancellationToken">The request cancellation token.</param>
            /// <returns>A failed task describing the offline regression.</returns>
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requestCount);
                return Task.FromException<HttpResponseMessage>(new InvalidOperationException("Local playback attempted a network request."));
            }
        }
    }
}
