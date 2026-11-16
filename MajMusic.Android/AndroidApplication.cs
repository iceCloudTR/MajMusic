#nullable enable

using Avalonia;
using Avalonia.Android;
using MajMusic.Presentation.Theme;
using global::Android.Runtime;

namespace MajMusic.Android
{
    /// <summary>Initializes Avalonia once for all Android activities and the background playback service.</summary>
    [global::Android.App.Application]
    public sealed class AndroidApplication : AvaloniaAndroidApplication<MobileApplication>
    {
        /// <summary>Creates the Android application wrapper from its Java peer.</summary>
        /// <param name="javaReference">The native Java application handle.</param>
        /// <param name="transfer">The ownership of the native handle.</param>
        public AndroidApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        /// <summary>Applies the shared fonts, controls and mobile design tokens to the Android renderer.</summary>
        /// <param name="builder">The Android-specific Avalonia builder.</param>
        /// <returns>The configured application builder.</returns>
        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder).WithInterFont().UseMajMusicArc();
        }
    }
}
