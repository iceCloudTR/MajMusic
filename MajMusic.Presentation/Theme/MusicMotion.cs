#nullable enable

using Avalonia.Animation.Easings;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Defines the shared surface timing and easing used by music navigation and search.</summary>
    public static class MusicMotion
    {
        /// <summary>Defines one complete outgoing and incoming surface transition.</summary>
        public static readonly TimeSpan SurfaceDuration = TimeSpan.FromMilliseconds(360);
        /// <summary>Uses an explicit symmetric slow, fast, slow curve rather than Arc's default ease-out.</summary>
        public static readonly Easing InOut = new SplineEasing(0.42, 0, 0.58, 1);

    }
}
