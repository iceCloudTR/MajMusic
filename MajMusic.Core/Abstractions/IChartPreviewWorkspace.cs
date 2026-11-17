#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Abstractions
{
    /// <summary>Loads chart files on demand without coupling the library to a particular renderer.</summary>
    public interface IChartPreviewWorkspace
    {
        /// <summary>Reads the selected song's actual populated difficulty slots without loading its media or renderer.</summary>
        /// <param name="track">The selected local or source-owned song.</param>
        /// <param name="cancellationToken">Cancels chart retrieval or parsing.</param>
        /// <returns>The numbered slots in the chart's original order.</returns>
        Task<IReadOnlyList<ChartPreviewDifficulty>> GetChartPreviewDifficultiesAsync(MusicTrack track, CancellationToken cancellationToken = default);

        /// <summary>Loads the current source revision's chart and media.</summary>
        /// <param name="track">The selected local or source-owned song.</param>
        /// <param name="includeVideo">Whether to resolve its optional PV.</param>
        /// <param name="cancellationToken">Cancels asset resolution.</param>
        /// <returns>The original chart text and playable resources.</returns>
        /// <exception cref="FileNotFoundException">The chart or music is unavailable.</exception>
        /// <exception cref="InvalidDataException">No playable difficulty could be read.</exception>
        Task<ChartPreviewAssets> ResolveChartPreviewAsync(MusicTrack track, bool includeVideo, CancellationToken cancellationToken = default);
    }
}
