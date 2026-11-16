#nullable enable

using Avalonia.Android;
using global::Android.App;
using global::Android.Content.PM;
using global::Android.OS;
using MajMusic.Android.Storage;

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

        /// <summary>Continues only an explicitly requested storage operation after returning from system settings.</summary>
        protected override void OnResume()
        {
            base.OnResume();
            AndroidStorageAccess.Instance.OnActivityResumed(this);
        }

        /// <summary>Records departure to the system storage page without starting another permission request.</summary>
        protected override void OnPause()
        {
            base.OnPause();
            AndroidStorageAccess.Instance.OnActivityPaused(this);
        }

        /// <summary>Releases this native host while preserving pending work across configuration recreation.</summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();
            AndroidStorageAccess.Instance.OnActivityDestroyed(this);
        }

        /// <summary>Forwards legacy storage grants after Avalonia handles its own permission results.</summary>
        /// <param name="requestCode">The native permission request identifier.</param>
        /// <param name="permissions">The requested Android permission names.</param>
        /// <param name="grantResults">The user's individual permission decisions.</param>
        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            AndroidStorageAccess.Instance.OnLegacyPermissionsResult(requestCode);
        }
    }
}
