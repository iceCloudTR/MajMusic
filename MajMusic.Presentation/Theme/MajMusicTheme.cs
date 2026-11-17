#nullable enable

using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using MajMusic.Core.Models;
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
            .Token("EdgeThickness", "1.25")
            .ForDark(dark => dark
                .Token("Background", "#141416").Token("Surface", "#1C1C1F")
                .Token("SurfaceRaised", "#27272C").Token("SurfaceMuted", "#242428")
                .Token("Border", "#36363D").Token("Foreground", "#F4F4F5")
                .Token("TextSecondary", "#A1A1AA").Token("TextMuted", "#92929C")
                .Token("Accent", "#8B8CF8").Token("AccentSubtle", "#2D2B48")
                .Token("AccentForeground", "#161625").Token("ControlOn", "#8B8CF8")
                .Token("ControlFill", "#8B8CF8").Token("ControlThumbOn", "#161625"))
            .ForLight(light => light
                .Token("Background", "#F5F5F7").Token("Surface", "#FFFFFF")
                .Token("SurfaceRaised", "#EEEEF1").Token("SurfaceMuted", "#F0F0F3")
                .Token("Border", "#DCDCE2").Token("Foreground", "#202024")
                .Token("TextSecondary", "#66666F").Token("TextMuted", "#72727C")
                .Token("Accent", "#5B5BD6").Token("AccentSubtle", "#EBEAFB")
                .Token("AccentForeground", "#FFFFFF").Token("ControlOn", "#5B5BD6")
                .Token("ControlFill", "#5B5BD6").Token("ControlThumbOn", "#FFFFFF")));

        /// <summary>Installs Arc's controls, mobile components, language resources and the shared music palette.</summary>
        /// <param name="builder">The Avalonia host being configured.</param>
        /// <param name="motion">The application motion policy; rendering checks can disable motion.</param>
        /// <param name="appearance">The initial dark, light or system appearance.</param>
        /// <returns>The configured Avalonia builder.</returns>
        public static AppBuilder UseMajMusicArc(this AppBuilder builder, ArcMotionLevel motion = ArcMotionLevel.Full,
            ArcAppearance appearance = ArcAppearance.Dark)
        {
            return builder.UseArc(arc => arc.UseArcControls().UseArcMobile()
                .UseLanguages(LanguageTags.ZhCN, [LanguageTags.ZhCN, LanguageTags.EnUS])
                .WithTheme(theme => theme.Appearance(appearance).Accent(ArcAccentPreset.Violet).MotionLevel(motion).Preset(Palette)))
                .AfterSetup(_ => InstallScrollGutters());
        }

        /// <summary>Applies the same reserved scroll tracks to every window, mobile surface and popup root.</summary>
        private static void InstallScrollGutters()
        {
            if (Application.Current is not { } application)
            {
                return;
            }

            var source = new Uri("avares://MajMusic.Presentation/Styles/ScrollGutterStyles.axaml");
            if (!application.Styles.OfType<StyleInclude>().Any(style => style.Source == source))
            {
                application.Styles.Add(new StyleInclude(source) { Source = source });
            }
        }

        /// <summary>Applies a persisted appearance while preserving the registered palette and motion policy.</summary>
        /// <param name="appearance">The toolkit-independent light, dark or system preference.</param>
        public static void ApplyAppearance(AppearancePreference appearance)
        {
            if (ArcThemeManager.Current is not { } theme)
            {
                return;
            }

            var requested = appearance switch
            {
                AppearancePreference.Light => ArcAppearance.Light,
                AppearancePreference.System => ArcAppearance.System,
                _ => ArcAppearance.Dark
            };
            if (theme.State.Appearance != requested)
            {
                theme.Apply(builder => builder.Appearance(requested));
            }
        }
    }
}
