#nullable enable

using System.Diagnostics;
using LibVLCSharp.Shared;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;

namespace MajMusic.Audio.Desktop
{
    /// <summary>Plays complete local audio files through LibVLC with an authoritative native media clock.</summary>
    /// <remarks>All native operations run on managed workers. No playback command runs inside a LibVLC event callback.</remarks>
    public sealed partial class LibVlcPlaybackBackend : IPlaybackBackend, IAudioOutputControl
    {
        /// <summary>Bounds native preparation and transport state transitions.</summary>
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
        /// <summary>Owns the native audio engine.</summary>
        private readonly LibVLC _engine;
        /// <summary>Serializes native access across commands, polling and disposal.</summary>
        private readonly SemaphoreSlim _commands = new(1, 1);
        /// <summary>Wakes the progress worker only while playback is active.</summary>
        private readonly SemaphoreSlim _pollSignal = new(0, 1);
        /// <summary>Cancels work when the desktop owner ends.</summary>
        private readonly CancellationTokenSource _lifetime = new();
        /// <summary>Owns the managed progress worker.</summary>
        private readonly Task _pollTask;
        /// <summary>Protects creation of the single disposal task.</summary>
        private readonly object _disposalGate = new();
        /// <summary>Owns the player of the currently prepared local file.</summary>
        private MediaPlayer? _player;
        /// <summary>Owns the prepared media descriptor.</summary>
        private Media? _media;
        /// <summary>Retains the current file's parsed audio properties without repeated native track or file reads.</summary>
        private AudioStreamInfo? _audioInfo;
        /// <summary>Publishes immutable native-clock state without blocking readers.</summary>
        private PlaybackSnapshot _snapshot = new();
        /// <summary>Shares one shutdown operation across repeated disposal requests.</summary>
        private Task? _disposal;
        /// <summary>Rejects new commands when shutdown begins.</summary>
        private int _disposed;

        /// <summary>Initializes the native local-file playback engine without opening media or network resources.</summary>
        /// <param name="nativeLibraryDirectory">An optional native library directory, otherwise platform discovery is used.</param>
        /// <param name="useDummyAudioOutput">Whether to decode into LibVLC's null output for explicit native regression tests.</param>
        /// <exception cref="DirectoryNotFoundException">The explicit native library directory does not exist.</exception>
        /// <exception cref="VLCException">The native engine cannot be loaded or initialized.</exception>
        public LibVlcPlaybackBackend(string? nativeLibraryDirectory = null, bool useDummyAudioOutput = false)
        {
            if (nativeLibraryDirectory is not null && !Directory.Exists(nativeLibraryDirectory))
            {
                throw new DirectoryNotFoundException("The configured native audio directory does not exist.");
            }

            LibVLCSharp.Shared.Core.Initialize(nativeLibraryDirectory);
            var options = new List<string>
            {
                "--ignore-config", "--no-video", "--no-spu", "--no-sub-autodetect-file", "--no-osd",
                "--no-lua", "--no-metadata-network-access", "--no-auto-preparse", "--no-media-library",
                "--services-discovery=", "--no-stats", "--quiet"
            };
            if (useDummyAudioOutput)
            {
                options.Add("--aout=dummy");
            }

            _engine = new LibVLC(options.ToArray());
            _pollTask = Task.Run(PollAsync);
        }

        /// <inheritdoc />
        public bool IsAvailable => Volatile.Read(ref _disposed) == 0;

        /// <inheritdoc />
        public PlaybackCapabilities Capabilities => IsAvailable
            ? PlaybackCapabilities.Playback | PlaybackCapabilities.Seeking : PlaybackCapabilities.None;

        /// <inheritdoc />
        public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);

        /// <inheritdoc />
        public event EventHandler? StateChanged;

        /// <inheritdoc />
        /// <exception cref="ArgumentException">The input is not a fully qualified local file path.</exception>
        /// <exception cref="FileNotFoundException">The local audio file does not exist.</exception>
        /// <exception cref="InvalidDataException">The local file does not contain a supported audio stream.</exception>
        public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
            if (!Path.IsPathFullyQualified(localPath) || localPath.Contains("://", StringComparison.Ordinal)
                || localPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new ArgumentException("Audio playback requires a fully qualified local file path.", nameof(localPath));
            }

            var fullPath = Path.GetFullPath(localPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("The imported audio file is missing.");
            }

            return ExecuteAsync(async token =>
            {
                Media? candidate = new Media(_engine, fullPath, FromType.FromPath, ":no-video", ":no-spu", ":no-sub-autodetect-file");
                try
                {
                    // LibVLCSharp completes its parse task from a native callback. Schedule the continuation
                    // onto the managed pool before touching or releasing any further native objects.
                    var parsed = await candidate.Parse(MediaParseOptions.ParseLocal, 5000, token)
                        .ContinueWith(static task => task.GetAwaiter().GetResult(), CancellationToken.None,
                            TaskContinuationOptions.None, TaskScheduler.Default).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    MediaTrack? audioTrack = null;
                    if (parsed == MediaParsedStatus.Done)
                    {
                        foreach (var track in candidate.Tracks)
                        {
                            if (track.TrackType == TrackType.Audio)
                            {
                                audioTrack = track;
                                break;
                            }
                        }
                    }

                    if (audioTrack is not { } parsedAudio)
                    {
                        throw new InvalidDataException("The selected file does not contain a supported audio stream.");
                    }

                    var duration = PositiveDuration(candidate.Duration);
                    var audioInfo = ReadAudioInfo(candidate, parsedAudio, new FileInfo(fullPath).Length, duration);
                    ReleasePlayer();
                    _media = candidate;
                    candidate = null;
                    _audioInfo = audioInfo;
                    _player = new MediaPlayer(_media);
                    ApplyOutputPreferences(_player, true);
                    _outputProbe?.Dispose();
                    _outputProbe = null;
                    SetSnapshot(new PlaybackSnapshot
                    {
                        State = PlaybackState.Stopped,
                        Duration = duration,
                        AudioInfo = audioInfo
                    });
                }
                finally
                {
                    candidate?.Dispose();
                }
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(async token =>
            {
                var player = RequirePlayer();
                ValidateSelectedOutputDevice(true);
                ApplyOutputPreferences(player, true);
                if (!player.Play())
                {
                    throw new IOException("The audio engine could not start the local file.");
                }

                await WaitForStateAsync(player, VLCState.Playing, token).ConfigureAwait(false);
                ApplyOutputPreferences(player, false);
                SetSnapshot(ReadNativeSnapshot(player));
                WakeProgressWorker();
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(async token =>
            {
                var player = RequirePlayer();
                if (player.State == VLCState.Ended)
                {
                    SetSnapshot(ReadNativeSnapshot(player));
                    return;
                }

                if (!player.CanPause)
                {
                    throw new NotSupportedException("The loaded local audio cannot be paused.");
                }

                player.SetPause(true);
                await WaitForStateAsync(player, VLCState.Paused, token).ConfigureAwait(false);
                SetSnapshot(ReadNativeSnapshot(player));
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                if (_player is not null)
                {
                    _player.Stop();
                }

                SetSnapshot(Snapshot with { State = PlaybackState.Stopped, Position = TimeSpan.Zero, Error = null });
                return Task.CompletedTask;
            }, cancellationToken);
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentOutOfRangeException">The requested position is outside the known media duration.</exception>
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            if (position < TimeSpan.Zero || position.TotalMilliseconds > long.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            return ExecuteAsync(async token =>
            {
                var player = RequirePlayer();
                if (Snapshot.Duration is { } duration && position > duration)
                {
                    throw new ArgumentOutOfRangeException(nameof(position));
                }

                if (!player.IsSeekable)
                {
                    throw new NotSupportedException("The loaded local audio does not support seeking.");
                }

                player.Time = (long)position.TotalMilliseconds;
                var deadline = Stopwatch.StartNew();
                while (Math.Abs(player.Time - position.TotalMilliseconds) > 300 && player.State != VLCState.Ended)
                {
                    token.ThrowIfCancellationRequested();
                    if (deadline.Elapsed > CommandTimeout)
                    {
                        throw new IOException("The audio engine did not complete the seek.");
                    }

                    await Task.Delay(20, token).ConfigureAwait(false);
                }

                SetSnapshot(ReadNativeSnapshot(player));
            }, cancellationToken, stopOnCancellation: false);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lock (_disposalGate)
            {
                if (_disposal is null)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    _lifetime.Cancel();
                    _disposal = DisposeCoreAsync();
                }

                return new ValueTask(_disposal);
            }
        }

        /// <summary>Runs a native command on the managed pool and serializes it with polling.</summary>
        /// <param name="action">The operation performed while native access is exclusive.</param>
        /// <param name="cancellationToken">Cancels the caller's operation.</param>
        /// <param name="stopOnCancellation">Whether canceling this command must stop its partially changed native resource.</param>
        /// <returns>The observed command result.</returns>
        /// <exception cref="ObjectDisposedException">The backend is shutting down.</exception>
        private Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken, bool stopOnCancellation = true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            return Task.Run(async () =>
            {
                using (linked)
                {
                    await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        await action(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (stopOnCancellation)
                        {
                            _player?.Stop();
                            SetSnapshot(Snapshot with { State = PlaybackState.Stopped, Position = TimeSpan.Zero, Error = null });
                        }
                        else if (_player is { } player)
                        {
                            SetSnapshot(ReadNativeSnapshot(player));
                        }
                        throw;
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
                    {
                        // Invalid commands do not turn an otherwise valid native playback into an error.
                        throw;
                    }
                    catch (Exception)
                    {
                        _player?.Stop();
                        SetSnapshot(Snapshot with
                        {
                            State = PlaybackState.Failed,
                            Position = TimeSpan.Zero,
                            Error = "Local audio playback could not be completed."
                        });
                        throw;
                    }
                    finally
                    {
                        _commands.Release();
                        RaiseStateChanged();
                    }
                }
            });
        }

        /// <summary>Polls native playback state only while playback is active.</summary>
        /// <returns>A task representing the progress worker lifetime.</returns>
        private async Task PollAsync()
        {
            try
            {
                while (true)
                {
                    await _pollSignal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    while (Snapshot.State is PlaybackState.Playing or PlaybackState.Loading)
                    {
                        await Task.Delay(200, _lifetime.Token).ConfigureAwait(false);
                        var changed = false;
                        await _commands.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                        try
                        {
                            if (_player is not null && Snapshot.State is PlaybackState.Playing or PlaybackState.Loading)
                            {
                                changed = ValidateSelectedOutputDevice(false);
                                var snapshot = ReadNativeSnapshot(_player);
                                changed |= snapshot != Snapshot;
                                SetSnapshot(snapshot);
                            }
                        }
                        finally
                        {
                            _commands.Release();
                        }

                        if (changed)
                        {
                            RaiseStateChanged();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // The desktop owner has requested shutdown.
            }
            catch (Exception)
            {
                await _commands.WaitAsync().ConfigureAwait(false);
                try
                {
                    _player?.Stop();
                }
                catch (Exception exception)
                {
                    Trace.TraceError("Desktop audio error cleanup failed: {0}", exception.GetType().Name);
                }
                finally
                {
                    _commands.Release();
                }

                SetSnapshot(Snapshot with { State = PlaybackState.Failed, Error = "The audio engine stopped responding." });
                RaiseStateChanged();
            }
        }

        /// <summary>Releases native objects after polling and in-flight commands finish.</summary>
        /// <returns>A task representing completed native cleanup.</returns>
        private async Task DisposeCoreAsync()
        {
            await _pollTask.ConfigureAwait(false);
            await _commands.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        ReleasePlayer();
                        _outputProbe?.Dispose();
                        _outputProbe = null;
                    }
                    finally
                    {
                        _engine.Dispose();
                    }
                }).ConfigureAwait(false);
                SetSnapshot(new PlaybackSnapshot { State = PlaybackState.Stopped });
            }
            finally
            {
                _commands.Release();
                _lifetime.Dispose();
            }
        }

        /// <summary>Reads the native media clock and normalizes only native terminal-state conventions.</summary>
        /// <param name="player">The currently owned player under the command gate.</param>
        /// <returns>The native playback state and timeline.</returns>
        private PlaybackSnapshot ReadNativeSnapshot(MediaPlayer player)
        {
            var state = player.State switch
            {
                VLCState.Playing => PlaybackState.Playing,
                VLCState.Paused => PlaybackState.Paused,
                VLCState.Ended => PlaybackState.Completed,
                VLCState.Error => PlaybackState.Failed,
                VLCState.Opening or VLCState.Buffering => PlaybackState.Loading,
                _ => PlaybackState.Stopped
            };
            var duration = PositiveDuration(player.Length) ?? Snapshot.Duration;
            var position = TimeSpan.FromMilliseconds(Math.Max(0, player.Time));
            if (state == PlaybackState.Completed && duration is { } end)
            {
                position = end;
            }

            return new PlaybackSnapshot
            {
                State = state,
                Position = position,
                Duration = duration,
                AudioInfo = _audioInfo,
                Error = state == PlaybackState.Failed ? "The local audio decoder reported a playback error." : null
            };
        }

        /// <summary>Copies native audio metadata once after local parsing and labels any file-average bitrate fallback.</summary>
        /// <param name="media">The parsed local media used to resolve the codec description.</param>
        /// <param name="track">The parsed audio track.</param>
        /// <param name="fileSize">The length of the complete local file.</param>
        /// <param name="duration">The parsed media duration.</param>
        /// <returns>Known stream properties; missing values remain null.</returns>
        private static AudioStreamInfo ReadAudioInfo(Media media, MediaTrack track, long fileSize, TimeSpan? duration)
        {
            long? bitrate = track.Bitrate > 0 ? track.Bitrate : null;
            var estimated = false;
            if (bitrate is null && fileSize > 0 && duration is { TotalSeconds: > 0 } knownDuration)
            {
                var average = fileSize * 8d / knownDuration.TotalSeconds;
                if (double.IsFinite(average) && average is > 0 and < long.MaxValue)
                {
                    bitrate = (long)Math.Round(average, MidpointRounding.AwayFromZero);
                    estimated = true;
                }
            }

            var codec = media.CodecDescription(TrackType.Audio, track.Codec);
            var rate = track.Data.Audio.Rate;
            var channels = track.Data.Audio.Channels;
            return new AudioStreamInfo
            {
                CodecName = string.IsNullOrWhiteSpace(codec) ? null : codec,
                BitrateBitsPerSecond = bitrate,
                IsBitrateEstimated = estimated,
                SampleRateHz = rate is > 0 and <= int.MaxValue ? (int)rate : null,
                Channels = channels is > 0 and <= int.MaxValue ? (int)channels : null,
                FileSizeBytes = fileSize >= 0 ? fileSize : null
            };
        }

        /// <summary>Waits for an explicit native transport state rather than assuming a command succeeded.</summary>
        /// <param name="player">The exclusively owned native player.</param>
        /// <param name="state">The requested native state.</param>
        /// <param name="cancellationToken">Cancels waiting.</param>
        /// <returns>A task representing the observed native transition.</returns>
        /// <exception cref="IOException">The native transition fails or times out.</exception>
        private static async Task WaitForStateAsync(MediaPlayer player, VLCState state, CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            while (player.State != state)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (player.State is VLCState.Error or VLCState.Ended || elapsed.Elapsed > CommandTimeout)
                {
                    throw new IOException("The audio engine did not enter the requested playback state.");
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Gets the player owned under the command gate.</summary>
        /// <returns>The prepared native player.</returns>
        /// <exception cref="InvalidOperationException">No audio has been opened.</exception>
        private MediaPlayer RequirePlayer()
        {
            return _player ?? throw new InvalidOperationException("No local audio file has been opened.");
        }

        /// <summary>Stops and releases the previous player before releasing its media.</summary>
        private void ReleasePlayer()
        {
            var player = _player;
            var media = _media;
            _player = null;
            _media = null;
            _audioInfo = null;
            try
            {
                player?.Stop();
            }
            finally
            {
                try
                {
                    player?.Dispose();
                }
                finally
                {
                    media?.Dispose();
                }
            }
        }

        /// <summary>Publishes an immutable snapshot.</summary>
        /// <param name="snapshot">The observed native state.</param>
        private void SetSnapshot(PlaybackSnapshot snapshot)
        {
            Volatile.Write(ref _snapshot, snapshot);
        }

        /// <summary>Starts progress monitoring without creating an unbounded signal queue.</summary>
        private void WakeProgressWorker()
        {
            try
            {
                _pollSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // An already pending wake-up is sufficient.
            }
        }

        /// <summary>Notifies managed subscribers outside native callbacks and the command gate.</summary>
        private void RaiseStateChanged()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception exception)
                {
                    Trace.TraceError("Desktop audio state subscriber failed: {0}", exception.GetType().Name);
                }
            }
        }

        /// <summary>Converts a positive native duration to its managed representation.</summary>
        /// <param name="milliseconds">The native duration, with nonpositive values indicating unknown duration.</param>
        /// <returns>The known duration or null.</returns>
        private static TimeSpan? PositiveDuration(long milliseconds)
        {
            return milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : null;
        }
    }
}
