#nullable enable

using System.Diagnostics;
using LibVLCSharp.Shared;
using MajMusic.Core.Models;

namespace MajMusic.Audio.Desktop
{
    /// <summary>Controls this player's levels and native routes without changing the current media owner.</summary>
    public sealed partial class LibVlcPlaybackBackend
    {
        /// <summary>Owns an idle native output solely for enumeration before media has been opened.</summary>
        private MediaPlayer? _outputProbe;
        /// <summary>Publishes immutable applied preferences without blocking presentation readers.</summary>
        private AudioOutputPreferences _outputPreferences = new();
        /// <summary>Stores the current route-fallback message independently of decoder errors.</summary>
        private string? _outputDeviceNotice;
        /// <summary>Bounds native route enumeration while a custom device is playing.</summary>
        private long _lastOutputValidation;
        /// <summary>Retains the native backend's enumerated default alias while exposing a portable null preference.</summary>
        private string _nativeDefaultOutputDeviceId = string.Empty;

        /// <inheritdoc />
        public AudioOutputPreferences Preferences => Volatile.Read(ref _outputPreferences);

        /// <inheritdoc />
        public string? OutputDeviceNotice => Volatile.Read(ref _outputDeviceNotice);

        /// <inheritdoc />
        public Task<IReadOnlyList<AudioOutputDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
        {
            return ExecuteOutputAsync<IReadOnlyList<AudioOutputDevice>>(() =>
            {
                var devices = ReadOutputDevices();
                ValidateOutputSelection(devices);
                return devices;
            }, cancellationToken);
        }

        /// <inheritdoc />
        public async Task SetOutputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deviceId is { Length: > 4096 } || deviceId?.Contains('\0') == true)
            {
                throw new ArgumentException("The output device identity is invalid.", nameof(deviceId));
            }

            deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
            await ExecuteOutputAsync(() =>
            {
                var devices = ReadOutputDevices();
                var unavailable = deviceId is not null && !devices.Any(device => device.Id == deviceId);
                var selected = unavailable ? null : deviceId;
                ApplyNativeDevice(selected);
                Volatile.Write(ref _outputPreferences, Preferences with { DeviceId = selected });
                Volatile.Write(ref _outputDeviceNotice, unavailable ? "输出设备不可用，已使用系统默认" : null);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (volume is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(volume));
            }

            await ExecuteOutputAsync(() =>
            {
                var updated = Preferences with { Volume = volume };
                if (_player is not null)
                {
                    _player.Volume = updated.Muted ? 0 : volume;
                }

                Volatile.Write(ref _outputPreferences, updated);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        {
            await ExecuteOutputAsync(() =>
            {
                var updated = Preferences with { Muted = muted };
                if (_player is not null)
                {
                    _player.Mute = muted;
                    _player.Volume = muted ? 0 : updated.Volume;
                }

                Volatile.Write(ref _outputPreferences, updated);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Runs a non-transport operation under the native gate without stopping music when a control is canceled.</summary>
        /// <typeparam name="T">The native operation result.</typeparam>
        /// <param name="action">The synchronous native operation while access is exclusive.</param>
        /// <param name="cancellationToken">Cancels before native access begins.</param>
        /// <returns>The operation result.</returns>
        /// <exception cref="ObjectDisposedException">The audio backend is closing.</exception>
        private Task<T> ExecuteOutputAsync<T>(Func<T> action, CancellationToken cancellationToken)
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
                        return action();
                    }
                    finally
                    {
                        _commands.Release();
                        RaiseStateChanged();
                    }
                }
            });
        }

        /// <summary>Copies device identities from the existing output or an idle, never-played native probe.</summary>
        /// <returns>The single default option and distinct enumerated native devices.</returns>
        private IReadOnlyList<AudioOutputDevice> ReadOutputDevices()
        {
            var player = _player ?? (_outputProbe ??= new MediaPlayer(_engine));
            var devices = new List<AudioOutputDevice> { new() { Id = null, Name = "系统默认" } };
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var native in player.AudioOutputDeviceEnum)
            {
                if (string.IsNullOrWhiteSpace(native.DeviceIdentifier) || native.DeviceIdentifier.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    _nativeDefaultOutputDeviceId = native.DeviceIdentifier ?? string.Empty;
                    continue;
                }

                if (!identities.Add(native.DeviceIdentifier))
                {
                    continue;
                }

                devices.Add(new AudioOutputDevice
                {
                    Id = native.DeviceIdentifier,
                    Name = string.IsNullOrWhiteSpace(native.Description) ? "音频设备" : native.Description
                });
            }

            _lastOutputValidation = Stopwatch.GetTimestamp();
            return devices;
        }

        /// <summary>Rechecks a custom route before playback and periodically while music is playing.</summary>
        /// <param name="force">Whether to bypass the low-frequency validation interval.</param>
        /// <returns>Whether a missing custom route was changed to the system default.</returns>
        private bool ValidateSelectedOutputDevice(bool force)
        {
            if (Preferences.DeviceId is null || !force && Stopwatch.GetElapsedTime(_lastOutputValidation) < TimeSpan.FromSeconds(2))
            {
                return false;
            }

            return ValidateOutputSelection(ReadOutputDevices());
        }

        /// <summary>Falls back only when a configured concrete identity is no longer enumerated.</summary>
        /// <param name="devices">The latest native device snapshot.</param>
        /// <returns>Whether the selection changed.</returns>
        private bool ValidateOutputSelection(IReadOnlyList<AudioOutputDevice> devices)
        {
            if (Preferences.DeviceId is not { } selected || devices.Any(device => device.Id == selected))
            {
                return false;
            }

            ApplyNativeDevice(null);
            Volatile.Write(ref _outputPreferences, Preferences with { DeviceId = null });
            Volatile.Write(ref _outputDeviceNotice, "输出设备不可用，已使用系统默认");
            return true;
        }

        /// <summary>Applies retained levels and an optional route when a new native player is prepared.</summary>
        /// <param name="player">The prepared native player under the command gate.</param>
        /// <param name="applyDevice">Whether to reapply the current route as well as levels.</param>
        private void ApplyOutputPreferences(MediaPlayer player, bool applyDevice)
        {
            var preferences = Preferences;
            if (applyDevice && preferences.DeviceId is not null)
            {
                player.SetOutputDevice(preferences.DeviceId);
            }

            player.Mute = preferences.Muted;
            player.Volume = preferences.Muted ? 0 : preferences.Volume;
        }

        /// <summary>Moves only the current native output route without reopening its media or changing its clock.</summary>
        /// <param name="deviceId">The concrete identity, or null for the system default.</param>
        private void ApplyNativeDevice(string? deviceId)
        {
            (_player ?? _outputProbe)?.SetOutputDevice(deviceId ?? _nativeDefaultOutputDeviceId);
        }
    }
}
