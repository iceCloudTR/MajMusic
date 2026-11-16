#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Persists a card layout independently of account, audio and appearance preferences.</summary>
    public interface ICardLayoutPreferencesWorkspace
    {
        /// <summary>Atomically updates only the music-list card layout.</summary>
        /// <param name="layout">The selected spacious, compact or small card arrangement.</param>
        /// <param name="cancellationToken">Cancels the local preference write.</param>
        /// <returns>The complete preferences after the narrow update.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The layout is not a supported value.</exception>
        /// <exception cref="OperationCanceledException">The local preference update is canceled.</exception>
        Task<AppSettings> UpdateCardLayoutAsync(SongCardLayout layout, CancellationToken cancellationToken = default);
    }
}
