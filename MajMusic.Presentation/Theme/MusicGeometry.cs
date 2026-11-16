#nullable enable

using Avalonia;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Shares the outline geometry between control templates and artwork layout.</summary>
    public static class MusicGeometry
    {
        /// <summary>Defines a consistent outline that remains visible through antialiasing on rounded corners.</summary>
        public const double OutlineWidth = 1.25;

        /// <summary>Gets the uniform frame thickness used by both desktop and mobile surfaces.</summary>
        public static Thickness OutlineThickness { get; } = new(OutlineWidth);
    }
}
