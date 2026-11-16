#nullable enable

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using MajMusic.Core.Playback;
using CorePlaybackState = MajMusic.Core.Playback.PlaybackState;
using NativePlaybackState = Android.Media.Session.PlaybackState;
using OperationCanceledException = System.OperationCanceledException;

namespace MajMusic.Android.Services
{
    /// <summary>Keeps actual playback alive independently of activities and exposes Android media controls.</summary>
    [Service(Name = "com.icecloud.majmusic.PlaybackMediaService", Exported = false,
        ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
    public sealed class PlaybackMediaService : Service
    {
        /// <summary>Identifies this application's low-importance playback notification channel.</summary>
        private const string ChannelId = "majmusic.playback";
        /// <summary>Identifies the single ongoing media notification.</summary>
        private const int NotificationId = 1001;
        /// <summary>Starts the service without changing the Core transport state.</summary>
        private const string ActionEnsure = "com.icecloud.majmusic.media.ENSURE";
        /// <summary>Toggles playback from the notification.</summary>
        private const string ActionToggle = "com.icecloud.majmusic.media.TOGGLE";
        /// <summary>Advances the Core queue from the notification.</summary>
        private const string ActionNext = "com.icecloud.majmusic.media.NEXT";
        /// <summary>Moves backward in the Core queue from the notification.</summary>
        private const string ActionPrevious = "com.icecloud.majmusic.media.PREVIOUS";
        /// <summary>Stops playback and the foreground service explicitly.</summary>
        private const string ActionStop = "com.icecloud.majmusic.media.STOP";
        /// <summary>Protects application/service connection changes without retaining an activity.</summary>
        private static readonly object ConnectionGate = new();
        /// <summary>Retains the process-owned Core playback service, never an activity or view.</summary>
        private static PlaybackService? _playback;
        /// <summary>Locates the currently running Android service.</summary>
        private static PlaybackMediaService? _current;
        /// <summary>Completes after StartForeground has actually published its notification.</summary>
        private static TaskCompletionSource _foregroundReady = NewCompletion();
        /// <summary>Observes the last bounded stop triggered by Android service destruction.</summary>
        private static Task _shutdown = Task.CompletedTask;
        /// <summary>Owns service media-command cancellation.</summary>
        private readonly CancellationTokenSource _lifetime = new();
        /// <summary>Tracks every asynchronous media command until it completes or is canceled.</summary>
        private readonly HashSet<Task> _operations = new();
        /// <summary>Protects asynchronous command registration and completion.</summary>
        private readonly object _operationsGate = new();
        /// <summary>Dispatches notification and session updates to Android's main looper.</summary>
        private Handler? _main;
        /// <summary>Owns this service's Android media session.</summary>
        private MediaSession? _session;
        /// <summary>Retains the media-session callback while native controls can invoke it.</summary>
        private SessionCallback? _callback;
        /// <summary>Accesses Android's process audio-focus manager.</summary>
        private AudioManager? _audioManager;
        /// <summary>Retains the native focus listener while focus is requested.</summary>
        private FocusListener? _focusListener;
        /// <summary>Retains the modern audio-focus request until abandonment.</summary>
        private AudioFocusRequestClass? _focusRequest;
        /// <summary>Pauses playback when a headset route disconnects.</summary>
        private NoisyReceiver? _noisy;
        /// <summary>Retains the Core instance currently observed by this native service.</summary>
        private PlaybackService? _observedPlayback;
        /// <summary>Indicates that foreground playback requirements have been satisfied.</summary>
        private bool _foreground;
        /// <summary>Rejects queued callbacks after native service destruction.</summary>
        private bool _destroyed;
        /// <summary>Indicates ownership of Android audio focus.</summary>
        private bool _hasFocus;
        /// <summary>Tracks an outstanding focus registration even while another application temporarily owns focus.</summary>
        private bool _focusRequested;
        /// <summary>Retains the song interrupted by a transient focus loss.</summary>
        private string? _interruptedTrackId;
        /// <summary>Distinguishes the service's interruption pause from a subsequent explicit user action.</summary>
        private bool _focusPauseInProgress;
        /// <summary>Indicates that a temporary focus loss may resume the same paused song.</summary>
        private bool _transientLoss;
        /// <summary>Suppresses duplicate notification rebuilding during quarter-second backend polling.</summary>
        private (string? Track, CorePlaybackState State)? _notified;
        /// <summary>Stores the native session's last authoritative position.</summary>
        private TimeSpan _sessionPosition;
        /// <summary>Stores the elapsed real time corresponding to the last native session update.</summary>
        private long _sessionUpdatedAt;
        /// <summary>Stores the native session's last published transport state.</summary>
        private CorePlaybackState _sessionState;

        /// <summary>Connects the application-owned playback lifetime without starting a service or opening audio.</summary>
        /// <param name="playback">The sole Core playback service created by Android application composition.</param>
        public static void Connect(PlaybackService playback)
        {
            ArgumentNullException.ThrowIfNull(playback);
            PlaybackMediaService? service;
            lock (ConnectionGate)
            {
                if (_playback is not null && !ReferenceEquals(_playback, playback))
                {
                    throw new InvalidOperationException("An Android playback owner is already connected.");
                }

                _playback = playback;
                service = _current;
            }

            service?._main?.Post(service.ObservePlayback);
        }

        /// <summary>Starts foreground media ownership only after an actual Play request.</summary>
        /// <param name="token">Cancels waiting for foreground service startup.</param>
        /// <returns>A task completing after the ongoing notification has been published.</returns>
        public static async Task EnsureForegroundAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Task ready;
            lock (ConnectionGate)
            {
                if (_playback is null)
                {
                    throw new InvalidOperationException("The Android playback service is not connected.");
                }

                if (_current is { _foreground: true, _destroyed: false })
                {
                    return;
                }

                if (_foregroundReady.Task.IsCompleted)
                {
                    _foregroundReady = NewCompletion();
                }

                ready = _foregroundReady.Task;
            }

            var context = global::Android.App.Application.Context;
            using var intent = new Intent(context, typeof(PlaybackMediaService)).SetAction(ActionEnsure);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }

            await ready.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }

        /// <summary>Requests focus only after foreground playback ownership is active.</summary>
        /// <param name="token">Cancels focus acquisition before dispatch.</param>
        /// <returns>Whether Android granted immediate audio focus.</returns>
        public static async Task<bool> AcquireAudioFocusAsync(CancellationToken token)
        {
            await EnsureForegroundAsync(token).ConfigureAwait(false);
            PlaybackMediaService service;
            lock (ConnectionGate)
            {
                service = _current ?? throw new InvalidOperationException("The Android media service is unavailable.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, service._lifetime.Token);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
            service._main!.Post(() =>
            {
                if (linked.IsCancellationRequested || service._destroyed)
                {
                    completion.TrySetCanceled(new CancellationToken(true));
                    return;
                }

                try
                {
                    completion.TrySetResult(service.RequestFocus());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            return await completion.Task.ConfigureAwait(false);
        }

        /// <summary>Releases the native service when application-owned audio is being disposed.</summary>
        public static void Disconnect()
        {
            PlaybackMediaService? service;
            lock (ConnectionGate)
            {
                _playback = null;
                _foregroundReady.TrySetCanceled();
                service = _current;
            }

            service?._main?.Post(() =>
            {
                if (!service._destroyed)
                {
                    service.RemoveForeground();
                    service.StopSelf();
                }
            });
        }

        /// <summary>Creates the native session and notification channel without starting media automatically.</summary>
        public override void OnCreate()
        {
            base.OnCreate();
            _main = new Handler(Looper.MainLooper!);
            _audioManager = GetSystemService(AudioService) as AudioManager;
            _focusListener = new FocusListener(this);
            _session = new MediaSession(this, "MajMusic");
            _callback = new SessionCallback(this);
            _session.SetCallback(_callback, _main);
            _session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
            _session.Active = true;
            _noisy = new NoisyReceiver(this);
            using var filter = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                RegisterReceiver(_noisy, filter, ReceiverFlags.NotExported);
            }
            else
            {
                RegisterReceiver(_noisy, filter);
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                using var channel = new NotificationChannel(ChannelId, "音乐播放", NotificationImportance.Low);
                channel.SetSound(null, null);
                (GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
            }

            lock (ConnectionGate)
            {
                _current = this;
            }

            ObservePlayback();
        }

        /// <summary>Publishes foreground ownership before dispatching a notification transport command.</summary>
        /// <param name="intent">The explicit service action.</param>
        /// <param name="flags">Android delivery flags.</param>
        /// <param name="startId">The native service request identity.</param>
        /// <returns>A nonsticky result so Android never restarts an empty or outdated queue automatically.</returns>
        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            using var notification = BuildNotification();
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }

            lock (ConnectionGate)
            {
                _foreground = true;
                _foregroundReady.TrySetResult();
            }

            switch (intent?.Action)
            {
                case ActionToggle:
                    RunCommand(ToggleAsync);
                    break;
                case ActionNext:
                    ClearInterruption();
                    RunCommand(token => RequirePlayback().NextAsync(token));
                    break;
                case ActionPrevious:
                    ClearInterruption();
                    RunCommand(token => RequirePlayback().PreviousAsync(token));
                    break;
                case ActionStop:
                    StopFromControls();
                    break;
            }

            UpdatePlayback();
            return StartCommandResult.NotSticky;
        }

        /// <summary>Does not expose an activity-bound binder; Android media controls use the active MediaSession.</summary>
        /// <param name="intent">The unused bind request.</param>
        /// <returns>No binder.</returns>
        public override IBinder? OnBind(Intent? intent)
        {
            return null;
        }

        /// <summary>Releases listeners and focus, then safely stops media if Android destroys the owning service.</summary>
        public override void OnDestroy()
        {
            _destroyed = true;
            _foreground = false;
            _lifetime.Cancel();
            var playback = _observedPlayback;
            if (playback is not null)
            {
                playback.StateChanged -= OnPlaybackChanged;
            }

            _observedPlayback = null;
            ClearInterruption();
            AbandonFocus();
            if (_noisy is not null)
            {
                UnregisterReceiver(_noisy);
                _noisy.Dispose();
                _noisy = null;
            }

            if (_session is not null)
            {
                _session.Active = false;
                _session.SetCallback(null);
                _session.Release();
                _session.Dispose();
                _session = null;
            }

            _callback?.Dispose();
            _callback = null;
            _focusListener?.Dispose();
            _focusListener = null;
            _focusRequest?.Dispose();
            _focusRequest = null;
            _audioManager?.Dispose();
            _audioManager = null;
            _main?.RemoveCallbacksAndMessages(null);
            _main?.Dispose();
            _main = null;
            lock (ConnectionGate)
            {
                if (ReferenceEquals(_current, this))
                {
                    _current = null;
                    _foregroundReady.TrySetCanceled();
                    _foregroundReady = NewCompletion();
                }

            }

            Task[] operations;
            lock (_operationsGate)
            {
                operations = _operations.ToArray();
            }

            _shutdown = StopAfterServiceDestructionAsync(playback, operations, _lifetime);

            base.OnDestroy();
        }

        /// <summary>Connects to the process-owned queue without changing activity or application ownership.</summary>
        private void ObservePlayback()
        {
            if (_destroyed)
            {
                return;
            }

            PlaybackService? playback;
            lock (ConnectionGate)
            {
                playback = _playback;
            }

            if (ReferenceEquals(_observedPlayback, playback))
            {
                return;
            }

            if (_observedPlayback is not null)
            {
                _observedPlayback.StateChanged -= OnPlaybackChanged;
            }

            _observedPlayback = playback;
            if (playback is not null)
            {
                playback.StateChanged += OnPlaybackChanged;
            }

            UpdatePlayback();
        }

        /// <summary>Marshals Core events to the Android session owner.</summary>
        /// <param name="sender">The Core playback owner.</param>
        /// <param name="args">The state notification.</param>
        private void OnPlaybackChanged(object? sender, EventArgs args)
        {
            _main?.Post(UpdatePlayback);
        }

        /// <summary>Updates session metadata and extrapolated transport state without rebuilding notifications on every clock tick.</summary>
        private void UpdatePlayback()
        {
            if (_destroyed || _observedPlayback is not { } playback || _session is null)
            {
                return;
            }

            var snapshot = playback.Snapshot;
            if (_interruptedTrackId is not null && (snapshot.Track?.Id != _interruptedTrackId
                || snapshot.State is CorePlaybackState.Stopped or CorePlaybackState.Empty or CorePlaybackState.Failed
                || snapshot.State == CorePlaybackState.Paused && !_focusPauseInProgress && !_transientLoss))
            {
                ClearInterruption();
            }

            if (!_transientLoss && snapshot.State is CorePlaybackState.Paused or CorePlaybackState.Stopped or CorePlaybackState.Completed
                or CorePlaybackState.Empty or CorePlaybackState.Failed)
            {
                AbandonFocus();
            }

            var now = SystemClock.ElapsedRealtime();
            var expectedPosition = _sessionPosition + (_sessionState == CorePlaybackState.Playing
                ? TimeSpan.FromMilliseconds(now - _sessionUpdatedAt) : TimeSpan.Zero);
            var identity = (snapshot.Track?.Id, snapshot.State);
            var changed = _notified != identity;
            if (changed || Math.Abs((snapshot.Position - expectedPosition).TotalMilliseconds) > 750)
            {
                var nativeState = snapshot.State switch
                {
                    CorePlaybackState.Playing => PlaybackStateCode.Playing,
                    CorePlaybackState.Paused => PlaybackStateCode.Paused,
                    CorePlaybackState.Loading => PlaybackStateCode.Buffering,
                    CorePlaybackState.Failed => PlaybackStateCode.Error,
                    CorePlaybackState.Empty => PlaybackStateCode.None,
                    _ => PlaybackStateCode.Stopped
                };
                using var state = new NativePlaybackState.Builder()!
                    .SetActions(NativePlaybackState.ActionPlay | NativePlaybackState.ActionPause | NativePlaybackState.ActionPlayPause
                        | NativePlaybackState.ActionStop | NativePlaybackState.ActionSkipToNext | NativePlaybackState.ActionSkipToPrevious
                        | NativePlaybackState.ActionSeekTo)!
                    .SetState(nativeState, (long)snapshot.Position.TotalMilliseconds,
                        snapshot.State == CorePlaybackState.Playing ? 1f : 0f, now)!
                    .Build();
                _session.SetPlaybackState(state);
                _sessionPosition = snapshot.Position;
                _sessionUpdatedAt = now;
                _sessionState = snapshot.State;
            }

            if (changed)
            {
                using var metadata = new MediaMetadata.Builder()!
                    .PutString(MediaMetadata.MetadataKeyTitle, snapshot.Track?.Title ?? "MajMusic")!
                    .PutString(MediaMetadata.MetadataKeyArtist, snapshot.Track?.Artist ?? string.Empty)!
                    .PutLong(MediaMetadata.MetadataKeyDuration, (long)(snapshot.Duration?.TotalMilliseconds ?? 0))!
                    .Build();
                _session.SetMetadata(metadata);
                _notified = identity;
                if (_foreground)
                {
                    using var notification = BuildNotification();
                    (GetSystemService(NotificationService) as NotificationManager)?.Notify(NotificationId, notification);
                }
            }
        }

        /// <summary>Creates real media controls with explicit immutable service intents.</summary>
        /// <returns>The caller-owned media notification.</returns>
        private Notification BuildNotification()
        {
            var snapshot = _observedPlayback?.Snapshot;
            var playing = snapshot?.State == CorePlaybackState.Playing;
            using var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? new Notification.Builder(this, ChannelId) : new Notification.Builder(this);
            builder.SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
                .SetContentTitle(snapshot?.Track?.Title ?? "MajMusic")!
                .SetContentText(snapshot?.Track?.Artist ?? "准备播放")!
                .SetOnlyAlertOnce(true)!
                .SetVisibility(NotificationVisibility.Public)!
                .SetOngoing(playing)!
                .SetShowWhen(false);
            AddControl(builder, global::Android.Resource.Drawable.IcMediaPrevious, "上一首", ActionPrevious, 1);
            AddControl(builder, playing ? global::Android.Resource.Drawable.IcMediaPause : global::Android.Resource.Drawable.IcMediaPlay,
                playing ? "暂停" : "播放", ActionToggle, 2);
            AddControl(builder, global::Android.Resource.Drawable.IcMediaNext, "下一首", ActionNext, 3);
            AddControl(builder, global::Android.Resource.Drawable.IcMenuCloseClearCancel, "停止", ActionStop, 4);
            using var style = new Notification.MediaStyle();
            style.SetShowActionsInCompactView(0, 1, 2);
            if (_session?.SessionToken is { } token)
            {
                style.SetMediaSession(token);
            }

            builder.SetStyle(style);
            using var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
            if (launch is not null)
            {
                builder.SetContentIntent(PendingIntent.GetActivity(this, 5, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
            }

            return builder.Build()!;
        }

        /// <summary>Creates one explicit immutable notification action.</summary>
        /// <param name="action">The transport action private to this application.</param>
        /// <param name="requestCode">The distinct immutable intent identity.</param>
        /// <returns>The native pending intent.</returns>
        private PendingIntent ControlIntent(string action, int requestCode)
        {
            using var intent = new Intent(this, typeof(PlaybackMediaService)).SetAction(action);
            return PendingIntent.GetService(this, requestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
        }

        /// <summary>Adds a modern notification action while releasing its temporary managed native wrappers.</summary>
        /// <param name="builder">The notification receiving the control.</param>
        /// <param name="resource">The monochrome system icon resource.</param>
        /// <param name="title">The short action label.</param>
        /// <param name="action">The explicit private service action.</param>
        /// <param name="requestCode">The immutable pending-intent identity.</param>
        private void AddControl(Notification.Builder builder, int resource, string title, string action, int requestCode)
        {
            using var icon = Icon.CreateWithResource(this, resource);
            using var pending = ControlIntent(action, requestCode);
            using var actionBuilder = new Notification.Action.Builder(icon, title, pending);
            using var control = actionBuilder.Build();
            builder.AddAction(control);
        }

        /// <summary>Requests immediate media focus on the native service looper.</summary>
        /// <returns>Whether this process currently owns Android audio focus.</returns>
        private bool RequestFocus()
        {
            if (_hasFocus)
            {
                return true;
            }

            if (_audioManager is null || _focusListener is null)
            {
                return false;
            }

            ClearInterruption();
            AudioFocusRequest result;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                using var attributes = new AudioAttributes.Builder()!
                    .SetUsage(AudioUsageKind.Media)!
                    .SetContentType(AudioContentType.Music)!
                    .Build() ?? throw new InvalidOperationException("Android media audio attributes are unavailable.");
                _focusRequest ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)!
                    .SetAudioAttributes(attributes)!
                    .SetAcceptsDelayedFocusGain(false)!
                    .SetWillPauseWhenDucked(true)!
                    .SetOnAudioFocusChangeListener(_focusListener, _main!)!
                    .Build() ?? throw new InvalidOperationException("Android audio focus is unavailable.");
                result = _audioManager.RequestAudioFocus(_focusRequest);
            }
            else
            {
                result = _audioManager.RequestAudioFocus(_focusListener, global::Android.Media.Stream.Music, AudioFocus.Gain);
            }

            _hasFocus = result == AudioFocusRequest.Granted;
            _focusRequested = _hasFocus;
            return _hasFocus;
        }

        /// <summary>Abandons focus without changing system volume or another application's player.</summary>
        private void AbandonFocus()
        {
            if (!_focusRequested || _audioManager is null || _focusListener is null)
            {
                return;
            }

            _hasFocus = false;
            _focusRequested = false;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                if (_focusRequest is not null)
                {
                    _audioManager.AbandonAudioFocusRequest(_focusRequest);
                }
            }
            else
            {
                _audioManager.AbandonAudioFocus(_focusListener);
            }
        }

        /// <summary>Pauses interrupted playback and resumes only the same temporarily interrupted song after focus returns.</summary>
        /// <param name="change">The native focus transition.</param>
        private void OnFocusChanged(AudioFocus change)
        {
            if (_destroyed || _observedPlayback is not { } playback)
            {
                return;
            }

            if (change == AudioFocus.Gain)
            {
                _hasFocus = true;
                var interrupted = _interruptedTrackId;
                var resume = _transientLoss && interrupted is not null && playback.Snapshot.Track?.Id == interrupted
                    && playback.Snapshot.State == CorePlaybackState.Paused;
                ClearInterruption();
                if (resume)
                {
                    RunCommand(token => playback.ResumeAsync(token));
                }

                return;
            }

            if (change is not (AudioFocus.Loss or AudioFocus.LossTransient or AudioFocus.LossTransientCanDuck))
            {
                return;
            }

            _hasFocus = false;
            var snapshot = playback.Snapshot;
            _transientLoss = change != AudioFocus.Loss;
            _interruptedTrackId = _transientLoss && snapshot.State == CorePlaybackState.Playing ? snapshot.Track?.Id : null;
            if (snapshot.State == CorePlaybackState.Playing)
            {
                RunCommand(async token =>
                {
                    _focusPauseInProgress = true;
                    try
                    {
                        await playback.PauseAsync(token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _focusPauseInProgress = false;
                    }
                });
            }
        }

        /// <summary>Clears transient-resume permission after an explicit user or route action.</summary>
        private void ClearInterruption()
        {
            _interruptedTrackId = null;
            _transientLoss = false;
        }

        /// <summary>Pauses a disconnected headset route without automatically resuming through the speaker.</summary>
        private void OnBecomingNoisy()
        {
            ClearInterruption();
            if (_observedPlayback?.Snapshot.State == CorePlaybackState.Playing)
            {
                RunCommand(token => RequirePlayback().PauseAsync(token));
            }
        }

        /// <summary>Toggles the same Core transport used by the application UI.</summary>
        /// <param name="token">Cancels the requested transport action.</param>
        /// <returns>A task representing pause or resume completion.</returns>
        private Task ToggleAsync(CancellationToken token)
        {
            ClearInterruption();
            var playback = RequirePlayback();
            return playback.Snapshot.State == CorePlaybackState.Playing ? playback.PauseAsync(token) : playback.ResumeAsync(token);
        }

        /// <summary>Stops both playback and the ongoing foreground session on an explicit Stop action.</summary>
        private void StopFromControls()
        {
            ClearInterruption();
            RunCommand(async token =>
            {
                await RequirePlayback().StopAsync(token).ConfigureAwait(false);
                _main?.Post(() =>
                {
                    if (!_destroyed)
                    {
                        _foreground = false;
                        RemoveForeground();
                        StopSelf();
                    }
                });
            });
        }

        /// <summary>Removes foreground ownership using the appropriate API for the minimum supported Android version.</summary>
        private void RemoveForeground()
        {
            _foreground = false;
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                StopForeground(StopForegroundFlags.Remove);
            }
            else
            {
                StopForeground(true);
            }
        }

        /// <summary>Gets the connected application owner while preventing accidental fallback to an unrelated session.</summary>
        /// <returns>The sole process playback owner.</returns>
        /// <exception cref="InvalidOperationException">No application playback owner is connected.</exception>
        private PlaybackService RequirePlayback()
        {
            return _observedPlayback ?? throw new InvalidOperationException("No Android playback owner is connected.");
        }

        /// <summary>Registers, observes and cancels native control commands with the service lifetime.</summary>
        /// <param name="command">The Core transport operation.</param>
        private void RunCommand(Func<CancellationToken, Task> command)
        {
            if (_destroyed)
            {
                return;
            }

            var completion = NewCompletion();
            lock (_operationsGate)
            {
                _operations.Add(completion.Task);
            }

            _ = RunCommandCoreAsync(command, completion);
        }

        /// <summary>Observes one service command without letting native callbacks surface unhandled exceptions.</summary>
        /// <param name="command">The Core transport operation.</param>
        /// <param name="completion">The registered lifetime completion.</param>
        /// <returns>A task whose errors are handled and whose completion releases the service registration.</returns>
        private async Task RunCommandCoreAsync(Func<CancellationToken, Task> command, TaskCompletionSource completion)
        {
            try
            {
                await command(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Service shutdown or a newer Core playback intent canceled this control request.
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning("Android media control failed: {0}", exception.GetType().Name);
            }
            finally
            {
                lock (_operationsGate)
                {
                    _operations.Remove(completion.Task);
                }

                completion.TrySetResult();
            }
        }

        /// <summary>Observes Core stopping after Android ends native foreground ownership.</summary>
        /// <param name="playback">The former native session's process-owned playback service.</param>
        /// <returns>A bounded observed shutdown operation.</returns>
        /// <param name="operations">The canceled native commands that must release their service tokens first.</param>
        /// <param name="lifetime">The service cancellation owner released after its operations finish.</param>
        private static async Task StopAfterServiceDestructionAsync(PlaybackService? playback, Task[] operations, CancellationTokenSource lifetime)
        {
            try
            {
                await Task.WhenAll(operations).ConfigureAwait(false);
                if (playback is not null)
                {
                    await playback.StopAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning("Android media shutdown ended: {0}", exception.GetType().Name);
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        /// <summary>Creates asynchronously continued native-command completion sources.</summary>
        /// <returns>A completion source that never runs arbitrary continuations on a native callback stack.</returns>
        private static TaskCompletionSource NewCompletion()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Routes platform transport controls into the same application-owned Core queue.</summary>
        private sealed class SessionCallback : MediaSession.Callback
        {
            /// <summary>Retains the service while its native session is active.</summary>
            private readonly PlaybackMediaService _owner;
            /// <summary>Creates the callback for one service lifetime.</summary>
            /// <param name="owner">The native media-session owner.</param>
            public SessionCallback(PlaybackMediaService owner)
            {
                _owner = owner;
            }

            /// <inheritdoc />
            public override void OnPlay()
            {
                _owner.ClearInterruption();
                _owner.RunCommand(token => _owner.RequirePlayback().ResumeAsync(token));
            }

            /// <inheritdoc />
            public override void OnPause()
            {
                _owner.ClearInterruption();
                _owner.RunCommand(token => _owner.RequirePlayback().PauseAsync(token));
            }

            /// <inheritdoc />
            public override void OnSkipToNext()
            {
                _owner.ClearInterruption();
                _owner.RunCommand(token => _owner.RequirePlayback().NextAsync(token));
            }

            /// <inheritdoc />
            public override void OnSkipToPrevious()
            {
                _owner.ClearInterruption();
                _owner.RunCommand(token => _owner.RequirePlayback().PreviousAsync(token));
            }

            /// <inheritdoc />
            public override void OnStop()
            {
                _owner.StopFromControls();
            }

            /// <inheritdoc />
            public override void OnSeekTo(long pos)
            {
                _owner.RunCommand(token => _owner.RequirePlayback().SeekAsync(TimeSpan.FromMilliseconds(Math.Max(0, pos)), token));
            }
        }

        /// <summary>Routes native focus changes to the main-looper service lifetime.</summary>
        private sealed class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
        {
            /// <summary>Retains the focus owner while registered with AudioManager.</summary>
            private readonly PlaybackMediaService _owner;
            /// <summary>Creates one listener for the service lifetime.</summary>
            /// <param name="owner">The native focus owner.</param>
            public FocusListener(PlaybackMediaService owner)
            {
                _owner = owner;
            }

            /// <inheritdoc />
            public void OnAudioFocusChange(AudioFocus focusChange)
            {
                _owner._main?.Post(() => _owner.OnFocusChanged(focusChange));
            }
        }

        /// <summary>Observes only the system's private-to-this-process headset-route broadcast registration.</summary>
        private sealed class NoisyReceiver : BroadcastReceiver
        {
            /// <summary>Retains the service until its dynamic receiver is unregistered.</summary>
            private readonly PlaybackMediaService _owner;
            /// <summary>Creates a headset-route listener for one native service.</summary>
            /// <param name="owner">The native playback owner.</param>
            public NoisyReceiver(PlaybackMediaService owner)
            {
                _owner = owner;
            }

            /// <inheritdoc />
            public override void OnReceive(Context? context, Intent? intent)
            {
                if (intent?.Action == AudioManager.ActionAudioBecomingNoisy)
                {
                    _owner.OnBecomingNoisy();
                }
            }
        }
    }
}
