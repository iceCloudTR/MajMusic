#nullable enable

using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Localization;
using TKCode.Arc.Mobile;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Configures one Arc design system for desktop, mobile and deterministic rendering hosts.</summary>
    public static class MajMusicTheme
    {
        /// <summary>Retains the music palette and consistent corner sizes across Arc controls.</summary>
        private static readonly ArcThemePreset Palette = ArcThemePreset.Create("MajMusic", palette => palette
            .Token("RadiusControl", "12")
            .Token("RadiusPanel", "12")
            .Token("RadiusSurface", "12")
            .ForDark(dark => dark
                .Token("Background", "#11161C").Token("Surface", "#19212A")
                .Token("SurfaceRaised", "#222D38").Token("SurfaceMuted", "#222D38")
                .Token("Border", "#2B3743").Token("Foreground", "#E8EDF2")
                .Token("TextSecondary", "#97A7B7").Token("TextMuted", "#748493")
                .Token("Accent", "#7AD8C1").Token("AccentSubtle", "#243E44")
                .Token("AccentForeground", "#102820").Token("ControlOn", "#7AD8C1")
                .Token("ControlFill", "#7AD8C1").Token("ControlThumbOn", "#102820"))
            .ForLight(light => light
                .Token("Background", "#F4F6F9").Token("Surface", "#FFFFFF")
                .Token("SurfaceRaised", "#E9EEF4").Token("SurfaceMuted", "#E9EEF4")
                .Token("Border", "#DDE4EC").Token("Foreground", "#243344")
                .Token("TextSecondary", "#687C90").Token("TextMuted", "#8290A0")
                .Token("Accent", "#177F71").Token("AccentSubtle", "#E0F0EE")
                .Token("AccentForeground", "#FFFFFF").Token("ControlOn", "#177F71")
                .Token("ControlFill", "#177F71").Token("ControlThumbOn", "#FFFFFF")));

        /// <summary>Installs Arc's controls, mobile components, language resources and the shared music palette.</summary>
        /// <param name="builder">The Avalonia host being configured.</param>
        /// <param name="motion">The application motion policy; rendering checks can disable motion.</param>
        /// <param name="appearance">The initial dark, light or system appearance.</param>
        /// <returns>The configured Avalonia builder.</returns>
        public static AppBuilder UseMajMusicArc(this AppBuilder builder, ArcMotionLevel motion = ArcMotionLevel.Reduced,
            ArcAppearance appearance = ArcAppearance.Dark)
        {
            return builder.UseArc(arc => arc.UseArcControls().UseArcMobile()
                .UseLanguages(LanguageTags.ZhCN, [LanguageTags.ZhCN, LanguageTags.EnUS])
                .WithTheme(theme => theme.Appearance(appearance).Accent(ArcAccentPreset.Green).MotionLevel(motion).Preset(Palette)));
        }
    }
}
