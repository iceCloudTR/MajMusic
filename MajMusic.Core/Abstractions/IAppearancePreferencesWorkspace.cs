#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Persists an appearance choice without replacing account, audio or directory preferences.</summary>
    public interface IAppearancePreferencesWorkspace
    {
        /// <summary>Atomically changes only the application appearance preference.</summary>
        /// <param name="appearance">The explicit light, dark or system choice.</param>
        /// <param name="cancellationToken">Cancels preference persistence.</param>
        /// <returns>The current preferences after the narrow update.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The appearance is not a supported preference.</exception>
        /// <exception cref="OperationCanceledException">The requested local write is canceled before publication.</exception>
        Task<AppSettings> UpdateAppearanceAsync(AppearancePreference appearance, CancellationToken cancellationToken = default);
    }
}
