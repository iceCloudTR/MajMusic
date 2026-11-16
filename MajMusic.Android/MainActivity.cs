#nullable enable

using Avalonia.Android;
using global::Android.App;
using global::Android.Content.PM;
using global::Android.OS;

namespace MajMusic.Android
{
    /// <summary>Hosts the responsive music interface without owning or stopping background playback.</summary>
    [Activity(Name = "com.icecloud.majmusic.MainActivity", Label = "MajMusic", Icon = "@drawable/app_icon",
        Theme = "@style/MajMusicTheme", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize
            | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.Density)]
    public sealed class MainActivity : AvaloniaMainActivity
    {
        /// <summary>Restores the native host and asks for media-notification visibility on supported devices.</summary>
        /// <param name="savedInstanceState">The previous Android activity state, when available.</param>
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            if (OperatingSystem.IsAndroidVersionAtLeast(33)
                && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 1001);
            }
        }
    }
}
