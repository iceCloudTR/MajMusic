#nullable enable

using global::Android.Content;
using global::Android.Content.PM;
using global::Android.OS;
using global::Android.Provider;

namespace MajMusic.Android.Storage
{
    /// <summary>Requests external-file access only for explicit user actions and resumes the same action after system settings return.</summary>
    internal sealed class AndroidStorageAccess
    {
        /// <summary>Separates legacy file grants from Avalonia pickers and notification permission requests.</summary>
        internal const int LegacyPermissionRequest = 41021;
        /// <summary>Shares permission coordination across activity recreation without retaining an old activity.</summary>
        public static AndroidStorageAccess Instance { get; } = new();
        /// <summary>Protects the current weak activity and one user-requested permission operation.</summary>
        private readonly object _gate = new();
        /// <summary>References only the activity currently able to launch a native permission page.</summary>
        private WeakReference<MainActivity>? _activity;
        /// <summary>Completes when the user returns from the explicitly opened system permission UI.</summary>
        private TaskCompletionSource<bool>? _pending;
        /// <summary>Distinguishes the all-files settings UI from a legacy runtime dialog.</summary>
        private bool _waitingForSettings;
        /// <summary>Prevents an unrelated initial resume from completing a newly scheduled settings request.</summary>
        private bool _settingsWasPaused;

        /// <summary>Gets whether ordinary shared-storage paths can currently be accessed by this application.</summary>
        public bool IsGranted => OperatingSystem.IsAndroidVersionAtLeast(30)
            ? global::Android.OS.Environment.IsExternalStorageManager
            : global::Android.App.Application.Context.CheckSelfPermission(global::Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted
                && global::Android.App.Application.Context.CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;

        /// <summary>Asks once for required access and lets the caller continue its original save or scan on return.</summary>
        /// <param name="reference">The user-selected ordinary path or granted document-tree reference.</param>
        /// <returns>Whether direct filesystem access or an existing writable SAF fallback is available.</returns>
        public async Task<bool> EnsureAccessAsync(string reference)
        {
            if (!RequiresExternalAccess(reference) || IsGranted || HasWritableDocumentGrant(reference))
            {
                return true;
            }
            return await RequestAllFilesAccessAsync().ConfigureAwait(false);
        }

        /// <summary>Opens the explicit all-files permission action independently of the current media root.</summary>
        /// <returns>Whether broad external file access is granted after the user returns.</returns>
        public Task<bool> RequestAllFilesAccessAsync()
        {
            if (IsGranted)
            {
                return Task.FromResult(true);
            }
            Task<bool> operation;
            MainActivity? activity = null;
            TaskCompletionSource<bool>? request = null;
            lock (_gate)
            {
                if (_pending is not null)
                {
                    operation = _pending.Task;
                }
                else
                {
                    if (_activity is null || !_activity.TryGetTarget(out var current) || current.IsFinishing || current.IsDestroyed)
                    {
                        return Task.FromResult(false);
                    }
                    activity = current;
                    request = _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    operation = request.Task;
                }
            }

            if (request is not null)
            {
                var currentActivity = activity!;
                var currentRequest = request;
                currentActivity.RunOnUiThread(() => BeginRequest(currentActivity, currentRequest));
            }
            return operation;
        }

        /// <summary>Converts only a known external-storage document into its ordinary path after broad access has actually been granted.</summary>
        /// <param name="reference">The original selected path or document URI.</param>
        /// <returns>The accessible ordinary path, or the unchanged SAF reference when conversion is not applicable.</returns>
        public string ResolveFileSystemPath(string reference)
        {
            return IsGranted && TryMapExternalDocument(reference) is { } path ? path : reference;
        }

        /// <summary>Refreshes the weak host and completes only a request returning from its own settings page.</summary>
        /// <param name="activity">The activity that has resumed after its Avalonia base lifecycle.</param>
        public void OnActivityResumed(MainActivity activity)
        {
            TaskCompletionSource<bool>? request;
            lock (_gate)
            {
                _activity = new WeakReference<MainActivity>(activity);
                request = _waitingForSettings && _settingsWasPaused ? _pending : null;
            }
            if (request is not null)
            {
                Complete(request, IsGranted);
            }
        }

        /// <summary>Records the departure caused by an already explicit settings request.</summary>
        /// <param name="activity">The activity paused after its Avalonia base lifecycle.</param>
        public void OnActivityPaused(MainActivity activity)
        {
            lock (_gate)
            {
                if (_waitingForSettings && _activity?.TryGetTarget(out var current) == true && ReferenceEquals(current, activity))
                {
                    _settingsWasPaused = true;
                }
            }
        }

        /// <summary>Releases a destroyed host while allowing configuration recreation to retain the pending user action.</summary>
        /// <param name="activity">The activity whose native lifecycle ended.</param>
        public void OnActivityDestroyed(MainActivity activity)
        {
            TaskCompletionSource<bool>? request = null;
            lock (_gate)
            {
                if (_activity?.TryGetTarget(out var current) == true && ReferenceEquals(current, activity))
                {
                    _activity = null;
                    if (activity.IsFinishing)
                    {
                        request = _pending;
                    }
                }
            }
            if (request is not null)
            {
                Complete(request, false);
            }
        }

        /// <summary>Completes an explicit Android 6–10 file permission dialog without reopening it after denial.</summary>
        /// <param name="requestCode">The request code returned after the activity's base callback.</param>
        public void OnLegacyPermissionsResult(int requestCode)
        {
            if (requestCode != LegacyPermissionRequest)
            {
                return;
            }
            TaskCompletionSource<bool>? request;
            lock (_gate)
            {
                request = _pending;
            }
            if (request is not null)
            {
                Complete(request, IsGranted);
            }
        }

        /// <summary>Launches native permission UI solely from a caller's explicit save or permission-button action.</summary>
        /// <param name="activity">The live host retained only during this short launch.</param>
        /// <param name="request">The operation permitted to launch a permission page.</param>
        private void BeginRequest(MainActivity activity, TaskCompletionSource<bool> request)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_pending, request))
                {
                    return;
                }
                _waitingForSettings = OperatingSystem.IsAndroidVersionAtLeast(30);
                _settingsWasPaused = false;
            }
            if (activity.IsFinishing || activity.IsDestroyed)
            {
                Complete(request, false);
                return;
            }
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    using var package = global::Android.Net.Uri.Parse("package:" + activity.PackageName);
                    using var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, package);
                    try
                    {
                        activity.StartActivity(intent);
                    }
                    catch (ActivityNotFoundException)
                    {
                        using var fallback = new Intent(Settings.ActionManageAllFilesAccessPermission);
                        activity.StartActivity(fallback);
                    }
                }
                else
                {
                    activity.RequestPermissions(
                        [global::Android.Manifest.Permission.ReadExternalStorage, global::Android.Manifest.Permission.WriteExternalStorage], LegacyPermissionRequest);
                }
            }
            catch (Exception)
            {
                Complete(request, false);
            }
        }

        /// <summary>Completes the current permission intent exactly once and never starts another permission UI automatically.</summary>
        /// <param name="request">The request receiving the native result.</param>
        /// <param name="granted">Whether ordinary file access is now available.</param>
        private void Complete(TaskCompletionSource<bool> request, bool granted)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_pending, request))
                {
                    return;
                }
                _pending = null;
                _waitingForSettings = false;
                _settingsWasPaused = false;
            }
            request.TrySetResult(granted);
        }

        /// <summary>Excludes private application storage and unrelated document providers from broad-file permission requests.</summary>
        /// <param name="reference">The edited ordinary path or document reference.</param>
        /// <returns>Whether this location belongs to shared external storage.</returns>
        private static bool RequiresExternalAccess(string reference)
        {
            if (reference.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            {
                return TryMapExternalDocument(reference) is not null;
            }
            var context = global::Android.App.Application.Context;
            using var files = context.GetExternalFilesDir(null);
            if (files?.AbsolutePath is { } privateExternal
                && (reference == privateExternal || reference.StartsWith(privateExternal + "/", StringComparison.Ordinal)))
            {
                return false;
            }
            return reference.StartsWith("/storage/", StringComparison.Ordinal) || reference == "/storage"
                || reference.StartsWith("/sdcard", StringComparison.Ordinal) || reference.StartsWith("/mnt/", StringComparison.Ordinal);
        }

        /// <summary>Preserves the separately granted SAF fallback when the user declines broad storage access.</summary>
        /// <param name="reference">The user-selected document reference.</param>
        /// <returns>Whether Android still grants read and write access to that original document tree.</returns>
        private static bool HasWritableDocumentGrant(string reference)
        {
            if (!reference.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            try
            {
                using var uri = global::Android.Net.Uri.Parse(reference);
                return uri is not null && global::Android.App.Application.Context.CheckUriPermission(uri,
                    global::Android.OS.Process.MyPid(), global::Android.OS.Process.MyUid(),
                    ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission) == Permission.Granted;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Maps only external-storage-provider IDs using constrained volume roots and nonescaping relative components.</summary>
        /// <param name="reference">The original selected document-tree URI.</param>
        /// <returns>The ordinary external path, or null when the provider or document ID has no safe filesystem mapping.</returns>
        private static string? TryMapExternalDocument(string reference)
        {
            if (!Uri.TryCreate(reference, UriKind.Absolute, out var address) || address.Scheme != "content"
                || address.Authority != "com.android.externalstorage.documents")
            {
                return null;
            }
            try
            {
                using var uri = global::Android.Net.Uri.Parse(reference);
                if (uri is null)
                {
                    return null;
                }
                var context = global::Android.App.Application.Context;
                var id = DocumentsContract.IsDocumentUri(context, uri) ? DocumentsContract.GetDocumentId(uri) : DocumentsContract.GetTreeDocumentId(uri);
                if (id is null)
                {
                    return null;
                }
                var separator = id.IndexOf(':');
                if (separator < 1)
                {
                    return null;
                }
                var volume = id[..separator];
                string root;
                if (volume == "primary")
                {
                    using var files = context.GetExternalFilesDir(null);
                    var external = files?.AbsolutePath;
                    var marker = external?.IndexOf("/Android/", StringComparison.Ordinal) ?? -1;
                    root = marker > 0 ? external![..marker] : "/storage/emulated/0";
                }
                else if (volume.Length is > 0 and <= 64 && volume.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                {
                    root = "/storage/" + volume;
                }
                else
                {
                    return null;
                }
                var relative = id[(separator + 1)..];
                if (relative.StartsWith('/') || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0')
                    || relative.Split('/').Any(part => part is "." or ".."))
                {
                    return null;
                }
                var path = Path.GetFullPath(Path.Combine(root, relative));
                return path == root || path.StartsWith(root + "/", StringComparison.Ordinal) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
