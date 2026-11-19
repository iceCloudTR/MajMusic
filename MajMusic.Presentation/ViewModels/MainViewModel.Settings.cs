#nullable enable

using System.Collections.ObjectModel;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Core.Abstractions;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Applies real application audio controls independently of source-account and directory editors.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Provides optional native output controls.</summary>
        private IAudioOutputControl? AudioOutput => _playback.AudioOutput;
        /// <summary>Serializes ownership of the bounded output-settings worker.</summary>
        private readonly object _audioUpdateGate = new();
        /// <summary>Tracks the newest user edit without creating a task for each slider event.</summary>
        private long _audioEditVersion;
        /// <summary>Indicates whether one worker owns pending output edits.</summary>
        private bool _audioWorkerRunning;
        /// <summary>Identifies the worker permitted to release the current batch ownership.</summary>
        private long _audioWorkerId;
        /// <summary>Suppresses user-write scheduling while loading actual backend state.</summary>
        private bool _suppressAudioEdits;
        /// <summary>Prevents initialization from persisting default controls over saved preferences.</summary>
        private bool _audioSettingsReady;
        /// <summary>Exposes completion of the current merged update batch.</summary>
        private Task _audioSettingsUpdate = Task.CompletedTask;
        /// <summary>Stores the application volume in slider units.</summary>
        private double _volume = 100;
        /// <summary>Stores the application mute choice.</summary>
        private bool _isMuted;
        /// <summary>Stores the device selected from the native route list.</summary>
        private AudioOutputDevice? _selectedOutputDevice;

        /// <summary>Gets real output routes including the system default.</summary>
        public ObservableCollection<AudioOutputDevice> OutputDevices { get; } = new();
        /// <summary>Gets whether the current backend supports actual output configuration.</summary>
        public bool CanConfigureAudio => AudioOutput is not null && _playback.IsAvailable;
        /// <summary>Gets the application volume label.</summary>
        public string VolumeText => $"{Math.Round(Volume):0}%";
        /// <summary>Gets an actionable native route fallback notice, or empty text.</summary>
        public string AudioOutputNotice => AudioOutput?.OutputDeviceNotice ?? string.Empty;
        /// <summary>Gets whether the output route currently has a notice.</summary>
        public bool HasAudioOutputNotice => !string.IsNullOrEmpty(AudioOutputNotice);
        /// <summary>Gets completion of the most recently scheduled merged audio update.</summary>
        public Task AudioSettingsUpdate => _audioSettingsUpdate;
        /// <summary>Gets the command refreshing actual native output routes.</summary>
        public AsyncCommand RefreshOutputDevicesCommand { get; private set; } = null!;
        /// <summary>Gets an explicit flush command for hosts that require a save action.</summary>
        public AsyncCommand SaveAudioSettingsCommand { get; private set; } = null!;
        /// <summary>Gets the shared play/pause action.</summary>
        public AsyncCommand TogglePlayPauseCommand { get; private set; } = null!;
        /// <summary>Gets the action label of the compact transport button.</summary>
        public string TransportButtonText => IsPlaying ? "暂停" : CanResume ? "继续" : "播放";
        /// <summary>Gets whether the authoritative playback owner is currently playing.</summary>
        public bool IsPlaying => IsSongChartPreviewSelected ? _chartPlaying : _playback.Snapshot.State == PlaybackState.Playing;

        /// <summary>Gets or sets application volume without changing the operating system's global volume.</summary>
        public double Volume
        {
            get => _volume;
            set
            {
                if (double.IsFinite(value) && SetField(ref _volume, Math.Clamp(value, 0, 100)))
                {
                    OnPropertyChanged(nameof(VolumeText));
                    PublishChartIntent();
                    QueueAudioSettingsUpdate();
                }
            }
        }

        /// <summary>Gets or sets application mute.</summary>
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (SetField(ref _isMuted, value))
                {
                    PublishChartIntent();
                    QueueAudioSettingsUpdate();
                }
            }
        }

        /// <summary>Gets or sets one of the available native output routes.</summary>
        public AudioOutputDevice? SelectedOutputDevice
        {
            get => _selectedOutputDevice;
            set
            {
                if (!ReferenceEquals(_selectedOutputDevice, value))
                {
                    _selectedOutputDevice = value;
                    OnPropertyChanged();
                    QueueAudioSettingsUpdate();
                }
            }
        }

        /// <summary>Creates audio and transport actions without opening any media.</summary>
        private void InitializeSettingsCommands()
        {
            RefreshOutputDevicesCommand = CreateCommand(RefreshOutputDevicesAsync, () => CanConfigureAudio);
            SaveAudioSettingsCommand = CreateCommand(async () =>
            {
                QueueAudioSettingsUpdate();
                await AudioSettingsUpdate;
            }, () => CanConfigureAudio);
            TogglePlayPauseCommand = CreateTransportCommand(async () =>
            {
                if (IsSongChartPreviewSelected)
                {
                    ToggleChartPlayback();
                    return;
                }
                await _returnToMusic;
                if (PendingMusic is { } pending)
                {
                    await PlayPendingMusicAsync(pending);
                    return;
                }
                if (_playback.Snapshot.State is PlaybackState.Playing or PlaybackState.Loading)
                {
                    await _playback.PauseAsync(_lifetime.Token);
                }
                else if ((_playback.Snapshot.Track is not null || _playback.Queue.Current is not null)
                    && _playback.Snapshot.State is PlaybackState.Paused or PlaybackState.Stopped or PlaybackState.Completed)
                {
                    await _playback.ResumeAsync(_lifetime.Token);
                }
                else
                {
                    await PlayAsync();
                }
            }, () => IsSongChartPreviewSelected ? _chartReady : CanPlay && (PendingMusic is not null || SelectedTrack is not null || _playback.Snapshot.Track is not null));
        }

        /// <summary>Restores saved application controls before opening any music.</summary>
        /// <returns>A task representing native configuration and read-only route discovery.</returns>
        private async Task InitializeAudioSettingsAsync()
        {
            _audioSettingsReady = false;
            if (AudioOutput is not { } output)
            {
                OutputDevices.Clear();
                OutputDevices.Add(new AudioOutputDevice { Name = "系统默认" });
                SelectedOutputDevice = OutputDevices[0];
                OnPropertyChanged(nameof(CanConfigureAudio));
                return;
            }

            var saved = _settings.AudioOutput;
            await output.SetVolumeAsync(saved.Volume, _lifetime.Token);
            await output.SetMutedAsync(saved.Muted, _lifetime.Token);
            await output.SetOutputDeviceAsync(saved.DeviceId, _lifetime.Token);
            UpdateOutputDevices(await output.GetOutputDevicesAsync(_lifetime.Token));
            SyncAudioOutputState();
            _audioSettingsReady = true;
            if (output.Preferences != saved)
            {
                await PersistAudioPreferencesAsync(output.Preferences, _lifetime.Token);
            }

            OnPropertyChanged(nameof(CanConfigureAudio));
        }

        /// <summary>Refreshes available routes without opening or restarting media.</summary>
        /// <returns>A task representing native route discovery.</returns>
        private async Task RefreshOutputDevicesAsync()
        {
            if (AudioOutput is not { } output)
            {
                return;
            }

            await AudioSettingsUpdate;
            UpdateOutputDevices(await output.GetOutputDevicesAsync(_lifetime.Token));
            SyncAudioOutputState();
            if (output.Preferences != _settings.AudioOutput)
            {
                await PersistAudioPreferencesAsync(output.Preferences, _lifetime.Token);
            }
        }

        /// <summary>Rebinds native routes while preserving pending user edits.</summary>
        /// <param name="devices">The actual routes returned by the audio backend.</param>
        private void UpdateOutputDevices(IReadOnlyList<AudioOutputDevice> devices)
        {
            var selectedId = _audioWorkerRunning ? SelectedOutputDevice?.Id : AudioOutput?.Preferences.DeviceId;
            _suppressAudioEdits = true;
            try
            {
                OutputDevices.Clear();
                foreach (var device in devices)
                {
                    OutputDevices.Add(device);
                }

                if (!OutputDevices.Any(item => item.Id is null))
                {
                    OutputDevices.Insert(0, new AudioOutputDevice { Name = "系统默认" });
                }

                SelectedOutputDevice = OutputDevices.FirstOrDefault(item => item.Id == selectedId)
                    ?? OutputDevices.First(item => item.Id is null);
            }
            finally
            {
                _suppressAudioEdits = false;
            }
        }

        /// <summary>Reflects an actual route fallback without overwriting an in-progress volume drag.</summary>
        private void SyncAudioOutputState()
        {
            OnPropertyChanged(nameof(AudioOutputNotice));
            OnPropertyChanged(nameof(HasAudioOutputNotice));
            if (AudioOutput is not { } output || _audioWorkerRunning)
            {
                return;
            }

            _suppressAudioEdits = true;
            try
            {
                Volume = output.Preferences.Volume;
                IsMuted = output.Preferences.Muted;
                SelectedOutputDevice = OutputDevices.FirstOrDefault(item => item.Id == output.Preferences.DeviceId)
                    ?? OutputDevices.FirstOrDefault(item => item.Id is null);
            }
            finally
            {
                _suppressAudioEdits = false;
            }
        }

        /// <summary>Coalesces slider and route edits into one observed bounded worker.</summary>
        private void QueueAudioSettingsUpdate()
        {
            if (_suppressAudioEdits || !_audioSettingsReady || !CanConfigureAudio || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            long workerId;
            lock (_audioUpdateGate)
            {
                _audioEditVersion++;
                if (_audioWorkerRunning)
                {
                    return;
                }

                _audioWorkerRunning = true;
                workerId = ++_audioWorkerId;
            }

            var pending = RunTrackedAsync(() => ApplyAudioSettingsAsync(workerId));
            lock (_audioUpdateGate)
            {
                if (_audioWorkerId == workerId)
                {
                    _audioSettingsUpdate = pending;
                }
            }
        }

        /// <summary>Applies the latest controls and saves only settled state rather than each slider tick.</summary>
        /// <param name="workerId">The owner of this update batch.</param>
        /// <returns>A task representing the current merged output-control batch.</returns>
        private async Task ApplyAudioSettingsAsync(long workerId)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(50, _lifetime.Token);
                    long version;
                    lock (_audioUpdateGate)
                    {
                        version = _audioEditVersion;
                    }

                    var requested = new AudioOutputPreferences
                    {
                        Volume = (int)Math.Round(Volume, MidpointRounding.AwayFromZero),
                        Muted = IsMuted,
                        DeviceId = SelectedOutputDevice?.Id
                    };
                    var output = AudioOutput!;
                    if (output.Preferences.DeviceId != requested.DeviceId)
                    {
                        await output.SetOutputDeviceAsync(requested.DeviceId, _lifetime.Token);
                    }

                    if (output.Preferences.Volume != requested.Volume)
                    {
                        await output.SetVolumeAsync(requested.Volume, _lifetime.Token);
                    }

                    if (output.Preferences.Muted != requested.Muted)
                    {
                        await output.SetMutedAsync(requested.Muted, _lifetime.Token);
                    }

                    lock (_audioUpdateGate)
                    {
                        if (version != _audioEditVersion)
                        {
                            continue;
                        }
                    }

                    await PersistAudioPreferencesAsync(output.Preferences, _lifetime.Token);
                    lock (_audioUpdateGate)
                    {
                        if (version != _audioEditVersion)
                        {
                            continue;
                        }

                        _audioWorkerRunning = false;
                    }

                    SyncAudioOutputState();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // The view lifetime owns and drains all queued native output operations.
            }
            catch (Exception exception)
            {
                ReportError(exception);
            }
            finally
            {
                lock (_audioUpdateGate)
                {
                    if (_audioWorkerId == workerId)
                    {
                        _audioWorkerRunning = false;
                    }
                }
            }
        }

        /// <summary>Persists only accepted audio settings while retaining current account and directory state.</summary>
        /// <param name="preferences">The native controls that actually took effect.</param>
        /// <param name="cancellationToken">Cancels the local preference update.</param>
        /// <returns>A task representing persistence.</returns>
        private async Task PersistAudioPreferencesAsync(AudioOutputPreferences preferences, CancellationToken cancellationToken)
        {
            if (_workspace is IPlaybackPreferencesWorkspace patch)
            {
                await patch.UpdateAudioOutputAsync(preferences, cancellationToken);
            }
            else
            {
                var current = await _workspace.LoadSettingsAsync(cancellationToken);
                await _workspace.SaveSettingsAsync(current with { AudioOutput = preferences }, cancellationToken);
            }

            _settings = _settings with { AudioOutput = preferences };
        }
    }
}
