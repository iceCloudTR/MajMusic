#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Updates chart practice controls without replacing unrelated settings.</summary>
    public interface IChartPracticePreferencesWorkspace
    {
        /// <summary>Persists only chart practice preferences and returns the resulting settings.</summary>
        Task<AppSettings> UpdateChartPracticeAsync(ChartPracticeOptions options, CancellationToken cancellationToken = default);
    }

    /// <summary>Updates application audio preferences without replacing independently edited accounts or directories.</summary>
    public interface IPlaybackPreferencesWorkspace
    {
        /// <summary>Atomically replaces only the application's audio-output preferences.</summary>
        /// <param name="preferences">The actual audio controls accepted by the playback backend.</param>
        /// <param name="cancellationToken">Cancels persistence.</param>
        /// <returns>The current settings snapshot after the local update.</returns>
        /// <exception cref="InvalidOperationException">The workspace preferences have not been loaded.</exception>
        Task<AppSettings> UpdateAudioOutputAsync(AudioOutputPreferences preferences, CancellationToken cancellationToken = default);
    }
}
