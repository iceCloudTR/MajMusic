#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Playback
{
    /// <summary>Provides optional application-volume and native-route controls without changing media ownership.</summary>
    public interface IAudioOutputControl
    {
        /// <summary>Gets the latest applied application audio preferences.</summary>
        AudioOutputPreferences Preferences { get; }

        /// <summary>Gets an actionable route-fallback message, or null when no fallback occurred.</summary>
        string? OutputDeviceNotice { get; }

        /// <summary>Reads available routes and validates the selected route without starting playback.</summary>
        /// <param name="cancellationToken">Cancels device enumeration.</param>
        /// <returns>The system-default option followed by known native devices.</returns>
        Task<IReadOnlyList<AudioOutputDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default);

        /// <summary>Selects a native route while retaining the current media and timeline.</summary>
        /// <param name="deviceId">The selected native identity, or null for the system default route.</param>
        /// <param name="cancellationToken">Cancels the change.</param>
        /// <returns>A task representing the applied selection or fallback.</returns>
        Task SetOutputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default);

        /// <summary>Changes only this application's volume.</summary>
        /// <param name="volume">The volume from zero through one hundred percent.</param>
        /// <param name="cancellationToken">Cancels the change.</param>
        /// <returns>A task representing the applied application volume.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The volume is outside zero through one hundred.</exception>
        Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default);

        /// <summary>Changes only this application's mute state.</summary>
        /// <param name="muted">Whether the application's audio should be muted.</param>
        /// <param name="cancellationToken">Cancels the change.</param>
        /// <returns>A task representing the applied mute state.</returns>
        Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
    }
}
