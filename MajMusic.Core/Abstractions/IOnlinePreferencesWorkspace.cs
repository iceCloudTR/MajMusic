#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Persists network permission without replacing independently edited application preferences.</summary>
    public interface IOnlinePreferencesWorkspace
    {
        /// <summary>Atomically updates only the saved online-mode choice while applying it immediately.</summary>
        /// <param name="enabled">The latest explicit online-mode choice.</param>
        /// <param name="cancellationToken">Cancels local persistence.</param>
        /// <returns>The current settings after the choice has been persisted.</returns>
        /// <exception cref="InvalidOperationException">The initial application preferences have not been loaded.</exception>
        Task<AppSettings> UpdateOnlineEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    }
}
