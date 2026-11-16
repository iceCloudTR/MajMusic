#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Keeps song artwork and loading placeholders square inside the same outlined card geometry.</summary>
    internal static class SongCardGeometry
    {
        /// <summary>Resolves a transient zero-width virtualized measure without retaining an outline-only card height.</summary>
        /// <param name="availableWidth">The parent's current measurement constraint.</param>
        /// <param name="arrangedWidth">The width already assigned by the real viewport.</param>
        /// <param name="layout">The preferred density used only before a real width becomes available.</param>
        /// <returns>A positive width replaced by the actual viewport on its first arrangement.</returns>
        public static double GetMeasureWidth(double availableWidth, double arrangedWidth, SongCardLayout layout)
        {
            if (double.IsFinite(availableWidth) && availableWidth > MusicGeometry.OutlineWidth * 2)
            {
                return availableWidth;
            }

            if (double.IsFinite(arrangedWidth) && arrangedWidth > MusicGeometry.OutlineWidth * 2)
            {
                return arrangedWidth;
            }

            return layout == SongCardLayout.Small ? 160 : 320;
        }

        /// <summary>Calculates the outlined card height while preserving its square artwork column.</summary>
        /// <param name="width">The available outer card width in logical pixels.</param>
        /// <param name="layout">The preferred artwork and information proportions.</param>
        /// <returns>The height including both outline edges.</returns>
        public static double GetHeight(double width, SongCardLayout layout)
        {
            var outline = MusicGeometry.OutlineWidth * 2;
            return Math.Max(0, width - outline) / (layout == SongCardLayout.Compact ? 3 : 2) + outline;
        }
    }
}
