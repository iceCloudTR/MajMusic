#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Persists non-secret preferences independently of their storage location.</summary>
    public interface ISettingsStore
    {
        /// <summary>Loads preferences or returns offline defaults when no configuration exists.</summary>
        /// <param name="cancellationToken">Cancels reading.</param>
        /// <returns>The saved preferences or offline defaults.</returns>
        Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

        /// <summary>Atomically persists a complete preference snapshot.</summary>
        /// <param name="settings">The non-secret preferences to persist.</param>
        /// <param name="cancellationToken">Cancels publication.</param>
        /// <returns>A task representing the completed write.</returns>
        Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    }
}
