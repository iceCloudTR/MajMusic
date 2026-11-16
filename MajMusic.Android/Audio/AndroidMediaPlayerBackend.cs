#nullable enable

using Android.Content;
using Android.Media;
using Android.OS;
using MajMusic.Android.Services;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using OperationCanceledException = System.OperationCanceledException;

namespace MajMusic.Android.Audio
{
    /// <summary>Owns local Android audio on a dedicated native looper independently of activities and views.</summary>
    public sealed class AndroidMediaPlayerBackend : IPlaybackBackend, IAudioOutputControl
    {
        /// <summary>Retains only the application context for native media ownership.</summary>
        private readonly Context _context;
        /// <summary>Serializes media commands and cancellation cleanup.</summary>
        private readonly SemaphoreSlim _commands = new(1, 1);
        /// <summary>Ends pending native operations and timeline polling.</summary>
        private readonly CancellationTokenSource _lifetime = new();
        /// <summary>Owns the native media callback looper.</summary>
        private readonly HandlerThread _thread;
        /// <summary>Dispatches every player operation to its owning looper.</summary>
        private readonly Handler _handler;
        /// <summary>Protects exactly-once asynchronous shutdown.</summary>
        private readonly object _disposalGate = new();
        /// <summary>Polls the authoritative native clock without requiring a visible activity.</summary>
        private readonly Task _polling;
        /// <summary>Stores the current native player, accessed only on the audio looper.</summary>
        private MediaPlayer? _player;
        /// <summary>Completes preparation for the current player.</summary>
        private TaskCompletionSource? _preparation;
        /// <summary>Completes the current native seek.</summary>
        private TaskCompletionSource? _seek;
        /// <summary>Indicates that the current native player accepts transport commands.</summary>
        private bool _prepared;
        /// <summary>Publishes native state without reading JNI properties on the UI thread.</summary>
        private PlaybackSnapshot _snapshot = new() { State = PlaybackState.Empty };
        /// <summary>Publishes application-local volume independently of the system music volume.</summary>
        private AudioOutputPreferences _preferences = new();
        /// <summary>Publishes a system-route fallback after a desktop device preference is restored.</summary>
        private string? _outputNotice;
        /// <summary>Retains the exactly-once shutdown task.</summary>
        private Task? _disposal;
        /// <summary>Rejects new operations after shutdown begins.</summary>
        private int _disposed;

        /// <summary>Starts a process-owned Android audio looper without opening media or requesting network access.</summary>
        /// <param name="context">The application or activity context; only its application context is retained.</param>
        public AndroidMediaPlayerBackend(Context context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _context = context.ApplicationContext ?? context;
            _thread = new HandlerThread("MajMusic audio");
            _thread.Start();
            _handler = new Handler(_thread.Looper!);
            _polling = Task.Run(PollClockAsync);
        }

        /// <inheritdoc />
        public bool IsAvailable => Volatile.Read(ref _disposed) == 0;
        /// <inheritdoc />
        public PlaybackCapabilities Capabilities => PlaybackCapabilities.Playback | PlaybackCapabilities.Seeking;
        /// <inheritdoc />
        public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);
        /// <inheritdoc />
        public AudioOutputPreferences Preferences => Volatile.Read(ref _preferences);
        /// <inheritdoc />
        public string? OutputDeviceNotice => Volatile.Read(ref _outputNotice);
        /// <inheritdoc />
        public event EventHandler? StateChanged;

        /// <inheritdoc />
        public async Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
            using var contentUri = IsContentUri(localPath) ? global::Android.Net.Uri.Parse(localPath) : null;
            if (contentUri is null && (!Path.IsPathFullyQualified(localPath) || !File.Exists(localPath)))
            {
                throw new FileNotFoundException("The local audio file is unavailable.", localPath);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
            MediaPlayer? opening = null;
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await InvokeAsync(() =>
                {
                    ReleasePlayer();
                    opening = new MediaPlayer();
                    _player = opening;
                    _preparation = prepared;
                    opening.SetAudioAttributes(new AudioAttributes.Builder()!
                        .SetUsage(AudioUsageKind.Media)!
                        .SetContentType(AudioContentType.Music)!
                        .Build());
                    opening.SetWakeMode(_context, WakeLockFlags.Partial);
                    opening.Prepared += OnPrepared;
                    opening.Completion += OnCompleted;
                    opening.Error += OnPlayerError;
                    opening.SeekComplete += OnSeekCompleted;
                    if (contentUri is not null)
                    {
                        opening.SetDataSource(_context, contentUri);
                    }
                    else
                    {
                        opening.SetDataSource(localPath);
                    }
                    ApplyVolume();
                    Publish(new PlaybackSnapshot { State = PlaybackState.Loading });
                    opening.PrepareAsync();
                }, linked.Token).ConfigureAwait(false);
                await prepared.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch
            {
                await InvokeAsync(() =>
                {
                    if (opening is not null && ReferenceEquals(_player, opening))
                    {
                        ReleasePlayer();
                        Publish(new PlaybackSnapshot { State = PlaybackState.Empty });
                    }
                }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            finally
            {
                _commands.Release();
            }
        }

        /// <inheritdoc />
        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(async token =>
            {
                await PlaybackMediaService.EnsureForegroundAsync(token).ConfigureAwait(false);
                if (!await PlaybackMediaService.AcquireAudioFocusAsync(token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Audio focus is unavailable.");
                }

                await InvokeAsync(() =>
                {
                    EnsurePrepared();
                    _player!.Start();
                    Publish(Snapshot with { State = PlaybackState.Playing, Position = ReadPosition(), Error = null });
                }, token).ConfigureAwait(false);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(token => InvokeAsync(() =>
            {
                EnsurePrepared();
                if (_player!.IsPlaying)
                {
                    _player.Pause();
                }

                Publish(Snapshot with { State = PlaybackState.Paused, Position = ReadPosition() });
            }, token), cancellationToken);
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(token => InvokeAsync(() =>
            {
                if (_prepared && _player is { } player)
                {
                    if (player.IsPlaying)
                    {
                        player.Pause();
                    }

                    // Retain Prepared state so a later Resume does not need to reopen the file.
                    player.SeekTo(0);
                }

                Publish(Snapshot with { State = PlaybackState.Stopped, Position = TimeSpan.Zero });
            }, token), cancellationToken);
        }

        /// <inheritdoc />
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            if (position < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            return ExecuteAsync(async token =>
            {
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await InvokeAsync(() =>
                {
                    EnsurePrepared();
                    _seek?.TrySetCanceled();
                    _seek = completed;
                    var milliseconds = (int)Math.Clamp(position.TotalMilliseconds, 0, _player!.Duration);
                    _player.SeekTo(milliseconds);
                }, token).ConfigureAwait(false);
                try
                {
                    await completed.Task.WaitAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    await InvokeAsync(() =>
                    {
                        if (ReferenceEquals(_seek, completed))
                        {
                            _seek = null;
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AudioOutputDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AudioOutputDevice>>([new AudioOutputDevice { Name = "系统默认" }]);
        }

        /// <inheritdoc />
        public Task SetOutputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            Volatile.Write(ref _preferences, Preferences with { DeviceId = null });
            Volatile.Write(ref _outputNotice, string.IsNullOrWhiteSpace(deviceId) ? null : "已使用系统默认输出");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
        {
            if (volume is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(volume));
            }

            return ExecuteAsync(token => InvokeAsync(() =>
            {
                Volatile.Write(ref _preferences, Preferences with { Volume = volume });
                ApplyVolume();
            }, token), cancellationToken);
        }

        /// <inheritdoc />
        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(token => InvokeAsync(() =>
            {
                Volatile.Write(ref _preferences, Preferences with { Muted = muted });
                ApplyVolume();
            }, token), cancellationToken);
        }

        /// <summary>Runs one command while retaining the native looper through cancellation cleanup.</summary>
        /// <param name="command">The command owning any native preparation or seek completion.</param>
        /// <param name="token">Cancels the caller's interest.</param>
        /// <returns>A task representing the serialized command.</returns>
        private async Task ExecuteAsync(Func<CancellationToken, Task> command, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                await command(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _commands.Release();
            }
        }

        /// <summary>Dispatches a short native operation to the sole media owner.</summary>
        /// <param name="action">The native operation, which must not wait on another looper callback.</param>
        /// <param name="token">Cancels queued work before it accesses native media.</param>
        /// <returns>A task completing after the queued native operation.</returns>
        private async Task InvokeAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            if (!_handler.Post(() =>
            {
                if (token.IsCancellationRequested)
                {
                    completion.TrySetCanceled(token);
                    return;
                }

                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
            {
                throw new ObjectDisposedException(nameof(AndroidMediaPlayerBackend));
            }

            await completion.Task.ConfigureAwait(false);
        }

        /// <summary>Completes the current asynchronous preparation on its native callback looper.</summary>
        /// <param name="sender">The prepared player.</param>
        /// <param name="args">The native completion arguments.</param>
        private void OnPrepared(object? sender, EventArgs args)
        {
            if (!ReferenceEquals(sender, _player))
            {
                return;
            }

            _prepared = true;
            Publish(new PlaybackSnapshot { State = PlaybackState.Paused, Duration = TimeSpan.FromMilliseconds(_player!.Duration) });
            _preparation?.TrySetResult();
            _preparation = null;
        }

        /// <summary>Publishes natural completion for the Core queue worker.</summary>
        /// <param name="sender">The completed player.</param>
        /// <param name="args">The native completion arguments.</param>
        private void OnCompleted(object? sender, EventArgs args)
        {
            if (ReferenceEquals(sender, _player))
            {
                Publish(Snapshot with { State = PlaybackState.Completed, Position = Snapshot.Duration ?? ReadPosition() });
            }
        }

        /// <summary>Completes a native seek without blocking either the main or audio looper.</summary>
        /// <param name="sender">The seeking player.</param>
        /// <param name="args">The native completion arguments.</param>
        private void OnSeekCompleted(object? sender, EventArgs args)
        {
            if (ReferenceEquals(sender, _player))
            {
                Publish(Snapshot with { Position = ReadPosition() });
                _seek?.TrySetResult();
                _seek = null;
            }
        }

        /// <summary>Converts a native media error into a bounded non-secret failure.</summary>
        /// <param name="sender">The player reporting the error.</param>
        /// <param name="args">The native error arguments.</param>
        private void OnPlayerError(object? sender, MediaPlayer.ErrorEventArgs args)
        {
            args.Handled = true;
            if (!ReferenceEquals(sender, _player))
            {
                return;
            }

            _prepared = false;
            var exception = new IOException("Android could not decode this audio file.");
            _preparation?.TrySetException(exception);
            _seek?.TrySetException(exception);
            _preparation = null;
            _seek = null;
            Publish(Snapshot with { State = PlaybackState.Failed, Error = exception.Message });
        }

        /// <summary>Reads timeline snapshots at a bounded frequency while actual media is playing.</summary>
        /// <returns>A task ending before the native looper is released.</returns>
        private async Task PollClockAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    if (Snapshot.State == PlaybackState.Playing)
                    {
                        await InvokeAsync(() =>
                        {
                            if (_prepared && Snapshot.State == PlaybackState.Playing)
                            {
                                Publish(Snapshot with { Position = ReadPosition() });
                            }
                        }, _lifetime.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Shutdown owns cancellation and waits for this loop before releasing the looper.
            }
        }

        /// <summary>Reads the current position only from the native audio looper.</summary>
        /// <returns>The nonnegative native media clock.</returns>
        private TimeSpan ReadPosition()
        {
            return TimeSpan.FromMilliseconds(Math.Max(0, _player?.CurrentPosition ?? 0));
        }

        /// <summary>Recognizes a persisted Storage Access Framework reference without treating it as a filesystem path.</summary>
        /// <param name="path">The resolved local resource reference.</param>
        /// <returns>Whether the resource is an Android content-provider URI.</returns>
        private static bool IsContentUri(string path)
        {
            return path.StartsWith("content://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Checks that preparation succeeded before invoking a native transport command.</summary>
        /// <exception cref="InvalidOperationException">No prepared local media is available.</exception>
        private void EnsurePrepared()
        {
            if (!_prepared || _player is null)
            {
                throw new InvalidOperationException("No audio resource is prepared.");
            }
        }

        /// <summary>Applies only this player's gain without modifying system music volume.</summary>
        private void ApplyVolume()
        {
            var preferences = Preferences;
            var gain = preferences.Muted ? 0 : preferences.Volume / 100f;
            _player?.SetVolume(gain, gain);
        }

        /// <summary>Publishes a cached immutable state and isolates native callbacks from observer failures.</summary>
        /// <param name="snapshot">The authoritative media state.</param>
        private void Publish(PlaybackSnapshot snapshot)
        {
            Volatile.Write(ref _snapshot, snapshot);
            if (StateChanged is { } changed)
            {
                foreach (EventHandler observer in changed.GetInvocationList())
                {
                    try
                    {
                        observer(this, EventArgs.Empty);
                    }
                    catch (Exception)
                    {
                        // One UI observer cannot break native playback callbacks.
                    }
                }
            }
        }

        /// <summary>Releases native resources and completes their outstanding tasks before replacing the player.</summary>
        private void ReleasePlayer()
        {
            var player = _player;
            _player = null;
            _prepared = false;
            _preparation?.TrySetCanceled();
            _seek?.TrySetCanceled();
            _preparation = null;
            _seek = null;
            if (player is null)
            {
                return;
            }

            player.Prepared -= OnPrepared;
            player.Completion -= OnCompleted;
            player.Error -= OnPlayerError;
            player.SeekComplete -= OnSeekCompleted;
            try
            {
                player.Release();
            }
            finally
            {
                player.Dispose();
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lock (_disposalGate)
            {
                if (_disposal is null)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    _disposal = DisposeCoreAsync();
                }

                return new ValueTask(_disposal);
            }
        }

        /// <summary>Drains canceled operations before releasing the sole native media thread.</summary>
        /// <returns>A task representing complete native shutdown.</returns>
        private async Task DisposeCoreAsync()
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _polling.ConfigureAwait(false);
            await _commands.WaitAsync().ConfigureAwait(false);
            try
            {
                await InvokeAsync(ReleasePlayer, CancellationToken.None).ConfigureAwait(false);
                PlaybackMediaService.Disconnect();
                _thread.QuitSafely();
                await Task.Run(() => _thread.Join()).ConfigureAwait(false);
                _handler.Dispose();
                _thread.Dispose();
                _lifetime.Dispose();
            }
            finally
            {
                _commands.Release();
                _commands.Dispose();
            }
        }
    }
}
