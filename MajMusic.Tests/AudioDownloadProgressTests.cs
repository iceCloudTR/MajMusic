#nullable enable

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Infrastructure.Storage;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks track-scoped audio download observations using isolated HTTP handlers and local fixtures.</summary>
    internal static class AudioDownloadProgressTests
    {
        /// <summary>Runs real-transfer, cache-hit, cancellation, failure and overlapping-source progress cases.</summary>
        /// <returns>A task representing completed audio progress validation.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-audio-progress-" + Guid.NewGuid().ToString("N"));
            try
            {
                await ValidateSuccessAndCacheAsync(Path.Combine(root, "success"));
                await ValidateUnknownLengthAsync(Path.Combine(root, "unknown"));
                await ValidateCancelledAsync(Path.Combine(root, "cancelled"));
                await ValidateFailedAsync(Path.Combine(root, "failed"));
                await ValidateIndependentSourcesAsync(Path.Combine(root, "sources"));
                await ValidatePlaybackProgressAsync(Path.Combine(root, "playback"));
                await ValidateUnknownPlaybackAndCancellationAsync(Path.Combine(root, "unknown-playback"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var resolved = Path.GetFullPath(root);
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("MajMusic-audio-progress-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to remove files outside the generated audio-progress fixture.");
                }

                if (Directory.Exists(resolved))
                {
                    Directory.Delete(resolved, true);
                }
            }
        }

        /// <summary>Reports only actual audio bytes and keeps cached audio, local audio, covers and metadata silent.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing transfer and cache-hit observations.</returns>
        private static async Task ValidateSuccessAndCacheAsync(string root)
        {
            var bytes = Encoding.UTF8.GetBytes(new string('a', 180000));
            using var transport = new FixtureTransport((_, _) => Task.FromResult(Response(new ByteArrayContent(bytes))));
            using var client = new HttpClient(transport);
            using var workspace = await CreateWorkspaceAsync(root, client);
            var events = new ConcurrentQueue<AudioDownloadProgress>();
            workspace.AudioDownloadProgressChanged += (_, _) => throw new InvalidOperationException("Generated observer failure");
            workspace.AudioDownloadProgressChanged += (_, progress) => events.Enqueue(progress);
            var track = Track("one", "source-a", "https://audio.example.invalid/one");
            var path = await workspace.ResolveAudioAsync(track, true);
            var first = events.ToArray();
            AssertLifecycle(first, track, AudioDownloadState.Completed);
            TestAssert.True(first.Any(item => item.State == AudioDownloadState.Downloading), "An actual audio response must publish download progress.");
            TestAssert.True(first.Last().Transfer is { IsCompleted: true } final && final.ReceivedBytes == bytes.Length && final.TotalBytes == bytes.Length,
                "Completed audio must report its actual received and expected byte counts.");
            TestAssert.True(ReferenceEquals(first.Last(), workspace.CurrentAudioDownloadProgress), "The optional current snapshot must retain the latest published state.");
            TestAssert.True(File.Exists(path), "Observer failures cannot prevent publication of the validated audio file.");
            while (events.TryDequeue(out _))
            {
            }

            var requests = transport.Requests;
            await workspace.ResolveAudioAsync(track, false);
            var cached = events.ToArray();
            AssertLifecycle(cached, track, AudioDownloadState.Completed);
            TestAssert.True(cached.Length == 2 && cached.All(item => item.Transfer is null),
                "Cached audio must publish preparation and completion without inventing downloaded bytes.");
            TestAssert.True(first[0].OperationId != cached[0].OperationId, "Repeated requests for one song must still carry distinct operation identities.");
            TestAssert.Equal(requests, transport.Requests, "A cache-hit operation cannot send another HTTP request.");
            while (events.TryDequeue(out _))
            {
            }

            await workspace.ResolveCoverAsync(track with { Id = "cover-only", Cover = new MediaResource { RemoteUri = new Uri("https://audio.example.invalid/image") } });
            await workspace.EnrichTrackMetadataAsync(track);
            TestAssert.True(events.IsEmpty, "Cover loading and metadata enrichment cannot publish audio download progress.");
            var local = Path.Combine(root, "original-local.mp3");
            await File.WriteAllBytesAsync(local, "local original bytes"u8.ToArray());
            var localTrack = track with { Id = "local", Audio = new MediaResource { LocalPath = local } };
            await workspace.ResolveAudioAsync(localTrack, false);
            TestAssert.True(events.Count == 2 && events.All(item => item.State != AudioDownloadState.Downloading && item.Transfer is null),
                "Referencing original local audio cannot report a transfer or copy it into the cache.");
        }

        /// <summary>Preserves an unknown total size instead of reporting a fabricated percentage denominator.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing chunked-response observations.</returns>
        private static async Task ValidateUnknownLengthAsync(string root)
        {
            var bytes = "unknown length fixture body"u8.ToArray();
            using var transport = new FixtureTransport((_, _) => Task.FromResult(Response(new UnknownLengthContent(bytes))));
            using var client = new HttpClient(transport);
            using var workspace = await CreateWorkspaceAsync(root, client);
            var events = new ConcurrentQueue<AudioDownloadProgress>();
            workspace.AudioDownloadProgressChanged += (_, progress) => events.Enqueue(progress);
            var track = Track("unknown", "source", "https://audio.example.invalid/unknown");
            await workspace.ResolveAudioAsync(track, false);
            AssertLifecycle(events.ToArray(), track, AudioDownloadState.Completed);
            TestAssert.True(events.Any(item => item.State == AudioDownloadState.Downloading && item.Transfer?.ReceivedBytes > 0),
                "A response without Content-Length must still report received bytes.");
            TestAssert.True(events.Where(item => item.Transfer is not null).All(item => item.Transfer!.TotalBytes is null),
                "An unknown response length must remain unknown in all audio progress snapshots.");
        }

        /// <summary>Ends a canceled resolution once and keeps exception details out of its progress state.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing cancellation notifications.</returns>
        private static async Task ValidateCancelledAsync(string root)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var transport = new FixtureTransport(async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Response(new ByteArrayContent(Array.Empty<byte>()));
            });
            using var client = new HttpClient(transport);
            using var workspace = await CreateWorkspaceAsync(root, client);
            var events = new ConcurrentQueue<AudioDownloadProgress>();
            workspace.AudioDownloadProgressChanged += (_, progress) => events.Enqueue(progress);
            using var cancellation = new CancellationTokenSource();
            var track = Track("cancel", "source", "https://audio.example.invalid/cancel");
            var resolving = workspace.ResolveAudioAsync(track, false, cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => resolving);
            AssertLifecycle(events.ToArray(), track, AudioDownloadState.Cancelled);
            TestAssert.True(events.All(item => item.State != AudioDownloadState.Completed), "A canceled request cannot report completed audio.");
        }

        /// <summary>Reports a safe failed state without turning source error text into user-facing progress data.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing failed-transfer notifications.</returns>
        private static async Task ValidateFailedAsync(string root)
        {
            using var transport = new FixtureTransport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Generated private source error")
            }));
            using var client = new HttpClient(transport);
            using var workspace = await CreateWorkspaceAsync(root, client);
            var events = new ConcurrentQueue<AudioDownloadProgress>();
            workspace.AudioDownloadProgressChanged += (_, progress) => events.Enqueue(progress);
            var track = Track("failure", "source", "https://audio.example.invalid/failure");
            await TestAssert.ThrowsAsync<HttpRequestException>(() => workspace.ResolveAudioAsync(track, false));
            AssertLifecycle(events.ToArray(), track, AudioDownloadState.Failed);
            TestAssert.True(events.All(item => item.Title == track.Title && item.TrackId == track.Id),
                "Failure observations must retain only requested-track ownership instead of source exception text.");
        }

        /// <summary>Keeps operation identities and byte counts independent while different sources finish out of order.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing source and operation isolation.</returns>
        private static async Task ValidateIndependentSourcesAsync(string root)
        {
            var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var transport = new FixtureTransport(async (request, token) =>
            {
                var sourceA = request.RequestUri!.Host == "a.example.invalid";
                (sourceA ? startedA : startedB).TrySetResult();
                await (sourceA ? releaseA : releaseB).Task.WaitAsync(token);
                return Response(new ByteArrayContent(new byte[sourceA ? 35000 : 70000]));
            });
            using var client = new HttpClient(transport);
            using var workspace = await CreateWorkspaceAsync(root, client);
            var events = new ConcurrentQueue<AudioDownloadProgress>();
            workspace.AudioDownloadProgressChanged += (_, progress) => events.Enqueue(progress);
            var first = Track("source-a:one", "source-a", "https://a.example.invalid/audio");
            var second = Track("source-b:one", "source-b", "https://b.example.invalid/audio");
            var one = workspace.ResolveAudioAsync(first, false);
            var two = workspace.ResolveAudioAsync(second, true);
            try
            {
                await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(TimeSpan.FromSeconds(3));
                releaseB.TrySetResult();
                await two.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(!one.IsCompleted, "A slow source cannot prevent another source's independent audio operation from completing.");
                releaseA.TrySetResult();
                await one.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                releaseA.TrySetResult();
                releaseB.TrySetResult();
                await Task.WhenAll(one, two);
            }

            var grouped = events.GroupBy(item => item.OperationId).ToArray();
            TestAssert.Equal(2, grouped.Length, "Overlapping sources must retain two independent audio operation identities.");
            var eventsA = events.Where(item => item.TrackId == first.Id).ToArray();
            var eventsB = events.Where(item => item.TrackId == second.Id).ToArray();
            AssertLifecycle(eventsA, first, AudioDownloadState.Completed);
            AssertLifecycle(eventsB, second, AudioDownloadState.Completed);
            TestAssert.True(eventsA.Last().Transfer?.ReceivedBytes == 35000 && eventsB.Last().Transfer?.ReceivedBytes == 70000,
                "Another source's bytes or completion state cannot overwrite a track's own progress snapshots.");
        }

        /// <summary>Keeps playback controls available and rejects an obsolete download's late terminal state.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing two overlapping playback intents.</returns>
        private static async Task ValidatePlaybackProgressAsync(string root)
        {
            using var firstBody = new ControlledBodyStream(true);
            using var secondBody = new ControlledBodyStream(false, 43);
            using var transport = new FixtureTransport((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/info"
                ? SourceInfoResponse() : Response(new ControlledContent(request.RequestUri.AbsolutePath == "/first" ? firstBody : secondBody, true))));
            using var client = new HttpClient(transport);
            using var workspace = await CreateWorkspaceAsync(root, client);
            await ConfigurePlaybackEndpointAsync(workspace);
            var first = OnlineTrack("playing-first", "https://audio.example.invalid/first");
            var second = OnlineTrack("playing-second", "https://audio.example.invalid/second", 43);
            await new SqliteLibraryRepository(Path.Combine(root, "library.db")).UpsertTracksAsync([first, second]);
            var (view, playback) = await CreatePlaybackViewAsync(workspace);
            await using var ownedView = view;
            var firstCard = view.CardTracks.Single(card => card.Track.Id == first.Id);
            var secondCard = view.CardTracks.Single(card => card.Track.Id == second.Id);
            view.SelectedTrack = view.Tracks.Single(track => track.Id == first.Id);
            var oldPlay = view.PlaySelectedAsync();
            Task? currentPlay = null;
            try
            {
                await firstBody.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(firstCard.IsDownloading && !firstCard.IsDownloaded,
                    "An actual playback download must update its own song card.");
                TestAssert.True(Math.Abs(firstCard.DownloadPercent - 25) < 0.001 && !firstCard.IsDownloadIndeterminate,
                    "The song card must show actual received bytes as a known-length percentage.");
                TestAssert.True(firstCard.DownloadDetails.Contains("40.0 KB / 160.0 KB", StringComparison.Ordinal)
                    && firstCard.DownloadDetails.Contains("/s", StringComparison.Ordinal),
                    "The song card must expose downloaded size, expected size and measured receive speed.");
                TestAssert.True(view.IsIdle && view.PlayCommand.CanExecute(null) && view.SearchCommand.CanExecute(null),
                    "A slow audio response cannot hold the presentation's global busy state or disable later user intents.");
                view.SelectedTrack = view.Tracks.Single(track => track.Id == second.Id);
                currentPlay = view.PlaySelectedAsync();
                await secondBody.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(secondCard.IsDownloading && Math.Abs(secondCard.DownloadPercent - 25) < 0.001,
                    "A new playback intent must update the newly requested song's card.");
                firstBody.Release.TrySetResult();
                await ObserveCancelledPlayAsync(oldPlay);
                TestAssert.True(secondCard.IsDownloading && Math.Abs(secondCard.DownloadPercent - 25) < 0.001
                    && !firstCard.IsDownloading,
                    "An obsolete operation's delayed cancellation cannot clear or replace another song card's progress.");
                secondBody.Release.TrySetResult();
                await currentPlay.WaitAsync(TimeSpan.FromSeconds(3));
                await WaitForAsync(() => playback.Snapshot.Track?.Id == second.Id && secondCard.IsDownloaded && secondCard.DownloadPercent == 100);
                TestAssert.True(view.IsIdle, "The completed preparation must leave the presentation interactive.");
            }
            finally
            {
                firstBody.Release.TrySetResult();
                secondBody.Release.TrySetResult();
                await ObserveCancelledPlayAsync(oldPlay);
                if (currentPlay is not null)
                {
                    await ObserveCancelledPlayAsync(currentPlay);
                }
            }
        }

        /// <summary>Shows unknown lengths, hides cache hits and clears canceled playback downloads.</summary>
        /// <param name="root">The explicit generated application-data directory.</param>
        /// <returns>A task representing unknown-length, cache and stop behavior.</returns>
        private static async Task ValidateUnknownPlaybackAndCancellationAsync(string root)
        {
            using var unknownBody = new ControlledBodyStream(false, 44);
            using var canceledBody = new ControlledBodyStream(false, 45);
            using var transport = new FixtureTransport((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/info"
                ? SourceInfoResponse() : Response(new ControlledContent(request.RequestUri.AbsolutePath == "/unknown-play" ? unknownBody : canceledBody,
                    request.RequestUri.AbsolutePath != "/unknown-play"))));
            using var client = new HttpClient(transport);
            using var workspace = await CreateWorkspaceAsync(root, client);
            await ConfigurePlaybackEndpointAsync(workspace);
            var unknown = OnlineTrack("unknown-playback", "https://audio.example.invalid/unknown-play", 44);
            var canceled = OnlineTrack("canceled-playback", "https://audio.example.invalid/cancel-play", 45);
            await new SqliteLibraryRepository(Path.Combine(root, "library.db")).UpsertTracksAsync([unknown, canceled]);
            var downloads = 0;
            workspace.AudioDownloadProgressChanged += (_, progress) =>
            {
                if (progress.State == AudioDownloadState.Downloading)
                {
                    Interlocked.Increment(ref downloads);
                }
            };
            var (view, playback) = await CreatePlaybackViewAsync(workspace);
            await using var ownedView = view;
            var unknownCard = view.CardTracks.Single(card => card.Track.Id == unknown.Id);
            var canceledCard = view.CardTracks.Single(card => card.Track.Id == canceled.Id);
            view.SelectedTrack = view.Tracks.Single(track => track.Id == unknown.Id);
            var playing = view.PlaySelectedAsync();
            Task? stopping = null;
            try
            {
                await unknownBody.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(unknownCard.IsDownloading && unknownCard.IsDownloadIndeterminate
                    && unknownCard.DownloadDetails.Contains("40.0 KB", StringComparison.Ordinal),
                    "An unknown-length response must display received bytes on its own card.");
                TestAssert.True(view.IsIdle, "Unknown resource size cannot make the user interface globally busy.");
                unknownBody.Release.TrySetResult();
                await playing.WaitAsync(TimeSpan.FromSeconds(3));
                await WaitForAsync(() => unknownCard.IsDownloaded && unknownCard.DownloadPercent == 100);
                var downloadEvents = Volatile.Read(ref downloads);
                await playback.StopAsync();
                await view.PlaySelectedAsync();
                await WaitForAsync(() => unknownCard.IsDownloaded && !unknownCard.IsDownloading);
                TestAssert.Equal(downloadEvents, Volatile.Read(ref downloads), "Cached playback cannot display invented download-byte updates.");
                view.SelectedTrack = view.Tracks.Single(track => track.Id == canceled.Id);
                stopping = view.PlaySelectedAsync();
                await canceledBody.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
                TestAssert.True(canceledCard.IsDownloading, "The newly requested uncached song must own its card's download state.");
                await playback.StopAsync();
                await ObserveCancelledPlayAsync(stopping);
                await WaitForAsync(() => !canceledCard.IsDownloading && !canceledCard.HasDownloadStatus);
                TestAssert.True(view.IsIdle && unknownCard.IsDownloaded,
                    "Canceling one audio preparation must clear only its own card while retaining other completed downloads.");
            }
            finally
            {
                unknownBody.Release.TrySetResult();
                canceledBody.Release.TrySetResult();
                await ObserveCancelledPlayAsync(playing);
                if (stopping is not null)
                {
                    await ObserveCancelledPlayAsync(stopping);
                }
            }
        }

        /// <summary>Configures the isolated source used by real online-card playback tests.</summary>
        /// <param name="workspace">The generated application workspace.</param>
        /// <returns>A task representing local source configuration.</returns>
        private static async Task ConfigurePlaybackEndpointAsync(MusicWorkspace workspace)
        {
            var settings = await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(settings with
            {
                Endpoints = [new EndpointConfiguration { Id = "source", Name = "Source", BaseUri = new Uri("https://audio.example.invalid/api/") }]
            });
        }

        /// <summary>Creates an online card whose audio integrity is known without requiring an extra track-info request.</summary>
        /// <param name="id">The source-scoped track identity.</param>
        /// <param name="address">The isolated generated response route.</param>
        /// <param name="fill">The generated response byte used to distinguish actual audio resources.</param>
        /// <returns>The complete online descriptor.</returns>
        private static MusicTrack OnlineTrack(string id, string address, byte fill = 42)
        {
            var bytes = new byte[ControlledBodyStream.TotalLength];
            Array.Fill(bytes, fill);
            var track = Track(id, "source", address);
            return track with
            {
                RemoteId = id,
                Revision = "v1",
                Duration = TimeSpan.FromSeconds(120),
                AudioInfo = new AudioStreamInfo { BitrateBitsPerSecond = 192000 },
                Audio = track.Audio with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }
            };
        }

        /// <summary>Returns the isolated online source identity without a real connection.</summary>
        /// <returns>The generated server-info response.</returns>
        private static HttpResponseMessage SourceInfoResponse()
        {
            return Response(new StringContent("{\"id\":\"source\",\"name\":\"Source\"}", Encoding.UTF8, "application/json"));
        }

        /// <summary>Creates the real download-to-playback bridge with a managed fake audio backend.</summary>
        /// <param name="workspace">The generated source and media cache.</param>
        /// <returns>The presentation and playback owners of this case.</returns>
        private static async Task<(MainViewModel View, PlaybackService Playback)> CreatePlaybackViewAsync(MusicWorkspace workspace)
        {
            var playback = new PlaybackService(new LibraryExperienceTests.ExperienceBackend(),
                resolveAudioPath: (track, token) => workspace.ResolveAudioAsync(track, false, token));
            var view = new MainViewModel(workspace, playback, static action => action());
            await view.InitializeAsync();
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate;
            await view.MetadataRefresh;
            return (view, playback);
        }

        /// <summary>Observes the normal cancellation of a superseded playback intent.</summary>
        /// <param name="playing">The superseded or stopped request.</param>
        /// <returns>A task representing its completed cleanup.</returns>
        private static async Task ObserveCancelledPlayAsync(Task playing)
        {
            try
            {
                await playing.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException)
            {
                // The current playback intent owns the next state.
            }
        }

        /// <summary>Waits for asynchronously dispatched media state without assuming event order.</summary>
        /// <param name="condition">The expected final presentation state.</param>
        /// <returns>A task representing the observed state.</returns>
        private static async Task WaitForAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!condition())
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        /// <summary>Checks unique ownership and one terminal notification for a complete audio operation.</summary>
        /// <param name="events">The captured observations for one operation.</param>
        /// <param name="track">The entry owned by that operation.</param>
        /// <param name="terminal">The expected final lifecycle state.</param>
        private static void AssertLifecycle(IReadOnlyList<AudioDownloadProgress> events, MusicTrack track, AudioDownloadState terminal)
        {
            TestAssert.True(events.Count >= 2 && events[0].State == AudioDownloadState.Preparing && events[^1].State == terminal,
                "An audio operation must start in preparation and finish in its matching terminal state.");
            TestAssert.True(events.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count() == 1
                && Guid.TryParseExact(events[0].OperationId, "N", out _), "Every observation must retain its operation's unique identity.");
            TestAssert.True(events.All(item => item.TrackId == track.Id && item.Title == track.Title),
                "Every observation must retain the requested source-scoped track and title.");
            TestAssert.True(events.All(item => item.SourceId == track.SourceId && item.EndpointId == track.EndpointId && item.Revision == track.Revision),
                "Every observation must retain the requested server, endpoint and source revision.");
            TestAssert.True(events.Where(item => item.State != AudioDownloadState.Completed).All(item => item.LocalAudioPath is null),
                "A pending or unsuccessful operation cannot claim a validated local audio path.");
            if (terminal == AudioDownloadState.Completed)
            {
                TestAssert.True(events[^1].LocalAudioPath is { } path && Path.IsPathFullyQualified(path) && File.Exists(path),
                    "Successful completion must expose the validated complete local path for card availability checks.");
            }

            TestAssert.Equal(1, events.Count(item => item.State is AudioDownloadState.Completed or AudioDownloadState.Cancelled or AudioDownloadState.Failed),
                "An operation cannot publish duplicate or contradictory terminal states.");
        }

        /// <summary>Creates an explicitly online test workspace whose requests remain inside the supplied fake handler.</summary>
        /// <param name="root">The generated local application-data directory.</param>
        /// <param name="client">The isolated source-byte transport.</param>
        /// <returns>The caller-owned test workspace.</returns>
        private static async Task<MusicWorkspace> CreateWorkspaceAsync(string root, HttpClient client)
        {
            var workspace = new MusicWorkspace(root, client, client, credentialStore: new RejectingCredentials());
            await workspace.LoadSettingsAsync();
            await workspace.UpdateOnlineEnabledAsync(true);
            return workspace;
        }

        /// <summary>Creates one indexed external-resource descriptor without a Maj metadata dependency.</summary>
        /// <param name="id">The source-scoped track identity.</param>
        /// <param name="source">The independently identified source.</param>
        /// <param name="address">The illustrative audio route handled only by the fixture transport.</param>
        /// <returns>The generated playable entry.</returns>
        private static MusicTrack Track(string id, string source, string address)
        {
            return new MusicTrack { Id = id, Title = "Title " + id, SourceId = source, EndpointId = source, Audio = new MediaResource { RemoteUri = new Uri(address), CacheVersion = "v1" } };
        }

        /// <summary>Creates a successful response without publishing any actual HTTP request.</summary>
        /// <param name="content">The generated response body.</param>
        /// <returns>The isolated HTTP response.</returns>
        private static HttpResponseMessage Response(HttpContent content)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        /// <summary>Delegates requests to deterministic in-memory source behavior.</summary>
        private sealed class FixtureTransport : HttpMessageHandler
        {
            /// <summary>Stores the isolated response implementation.</summary>
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;
            /// <summary>Counts requests independently of callback threads.</summary>
            private int _requests;
            /// <summary>Creates an in-memory media source.</summary>
            /// <param name="response">The generated response behavior.</param>
            public FixtureTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
            {
                _response = response;
            }
            /// <summary>Gets how many requests reached this fixture.</summary>
            public int Requests => Volatile.Read(ref _requests);
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requests);
                return _response(request, cancellationToken);
            }
        }

        /// <summary>Produces response bytes without a Content-Length header.</summary>
        private sealed class UnknownLengthContent : HttpContent
        {
            /// <summary>Stores the generated response bytes.</summary>
            private readonly byte[] _bytes;
            /// <summary>Creates one unknown-length response body.</summary>
            /// <param name="bytes">The generated source body.</param>
            public UnknownLengthContent(byte[] bytes)
            {
                _bytes = bytes;
            }
            /// <inheritdoc />
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                return stream.WriteAsync(_bytes).AsTask();
            }
            /// <inheritdoc />
            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                return Task.FromResult<Stream>(new MemoryStream(_bytes, false));
            }
            /// <inheritdoc />
            protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult<Stream>(new MemoryStream(_bytes, false));
            }
            /// <inheritdoc />
            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }

        /// <summary>Exposes a controllable stream without prebuffering it or manufacturing a response length.</summary>
        private sealed class ControlledContent : HttpContent
        {
            /// <summary>Retains the body owned by this response.</summary>
            private readonly ControlledBodyStream _body;
            /// <summary>Determines whether this response declares its size.</summary>
            private readonly bool _knownLength;
            /// <summary>Creates one controllable response body.</summary>
            /// <param name="body">The body whose remaining bytes wait for explicit release.</param>
            /// <param name="knownLength">Whether to expose the complete size through Content-Length.</param>
            public ControlledContent(ControlledBodyStream body, bool knownLength)
            {
                _body = body;
                _knownLength = knownLength;
            }
            /// <inheritdoc />
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                throw new NotSupportedException("The fixture body must be consumed as a stream.");
            }
            /// <inheritdoc />
            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                return Task.FromResult<Stream>(_body);
            }
            /// <inheritdoc />
            protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult<Stream>(_body);
            }
            /// <inheritdoc />
            protected override bool TryComputeLength(out long length)
            {
                length = ControlledBodyStream.TotalLength;
                return _knownLength;
            }
        }

        /// <summary>Supplies one measured quarter of a response before waiting for release or cancellation.</summary>
        private sealed class ControlledBodyStream : Stream
        {
            /// <summary>Defines a known body size whose first complete chunk is exactly twenty-five percent.</summary>
            public const int TotalLength = 160000;
            /// <summary>Allows a deliberately late obsolete-source result for playback ownership tests.</summary>
            private readonly bool _ignoreBlockedCancellation;
            /// <summary>Distinguishes the generated response's content identity.</summary>
            private readonly byte _fill;
            /// <summary>Tracks bytes already returned to the download owner.</summary>
            private int _position;
            /// <summary>Creates a body with an explicitly controlled remainder.</summary>
            /// <param name="ignoreBlockedCancellation">Whether an obsolete read completes only after explicit release.</param>
            /// <param name="fill">The generated response byte.</param>
            public ControlledBodyStream(bool ignoreBlockedCancellation, byte fill = 42)
            {
                _ignoreBlockedCancellation = ignoreBlockedCancellation;
                _fill = fill;
            }
            /// <summary>Signals that the first measured chunk has been consumed and the remainder is pending.</summary>
            public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Releases the remaining generated response bytes.</summary>
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <inheritdoc />
            public override bool CanRead => true;
            /// <inheritdoc />
            public override bool CanSeek => false;
            /// <inheritdoc />
            public override bool CanWrite => false;
            /// <inheritdoc />
            public override long Length => TotalLength;
            /// <inheritdoc />
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }
            /// <inheritdoc />
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position == 0)
                {
                    await Task.Delay(130, cancellationToken);
                    buffer.Span[..40000].Fill(_fill);
                    _position = 40000;
                    return 40000;
                }

                if (_position == 40000)
                {
                    Blocked.TrySetResult();
                    if (_ignoreBlockedCancellation)
                    {
                        await Release.Task;
                    }
                    else
                    {
                        await Release.Task.WaitAsync(cancellationToken);
                    }
                }

                var count = Math.Min(buffer.Length, TotalLength - _position);
                buffer.Span[..count].Fill(_fill);
                _position += count;
                return count;
            }
            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
            /// <inheritdoc />
            public override void Flush()
            {
            }
            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }
            /// <inheritdoc />
            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }
            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
            /// <inheritdoc />
            protected override void Dispose(bool disposing)
            {
                Release.TrySetResult();
                base.Dispose(disposing);
            }
        }

        /// <summary>Rejects system-vault access during media-transfer observations.</summary>
        private sealed class RejectingCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Audio progress cannot read credentials.");
            }
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Audio progress cannot write credentials.");
            }
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Audio progress cannot delete credentials.");
            }
        }
    }
}
