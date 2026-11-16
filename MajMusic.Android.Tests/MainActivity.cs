#nullable enable

using System.Security.Cryptography;
using MajMusic.Android.Security;
using global::Android.App;
using global::Android.Content;
using global::Android.OS;
using global::Android.Widget;

namespace MajMusic.Android.NativeTests
{
    /// <summary>Runs isolated Android Keystore checks under the test application's independent UID.</summary>
    [Activity(Name = "com.icecloud.majmusic.tests.MainActivity", Label = "MajMusic Native Tests",
        MainLauncher = true, Exported = true, Theme = "@android:style/Theme.Material.Light.NoActionBar")]
    public sealed class MainActivity : Activity
    {
        /// <summary>Identifies safe test-result messages without including keys, ciphertext or secret values.</summary>
        private const string LogTag = "MajMusic.CredTests";

        /// <summary>Starts actual native credential checks whenever the test activity is launched.</summary>
        /// <param name="savedInstanceState">The previous Android activity state, when available.</param>
        protected override async void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            var suppliedRun = Intent?.GetStringExtra("run-id");
            var runId = Guid.TryParseExact(suppliedRun, "N", out var parsed) ? parsed.ToString("N") : Guid.NewGuid().ToString("N");
            var output = new TextView(this) { Text = "Running native credential checks…" };
            output.SetPadding(24, 48, 24, 24);
            SetContentView(output);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var stage = "initialization";
            try
            {
                var count = await RunCredentialChecksAsync(ApplicationContext!, value => stage = value, timeout.Token);
                var result = $"PASS run={runId} checks={count}";
                global::Android.Util.Log.Info(LogTag, result);
                RunOnUiThread(() => output.Text = "PASS: native credential checks");
            }
            catch (Exception exception)
            {
                var cause = exception.InnerException?.GetType().Name ?? "none";
                global::Android.Util.Log.Error(LogTag,
                    $"FAIL run={runId} stage={stage} exception={exception.GetType().Name} cause={cause}");
                RunOnUiThread(() => output.Text = "FAIL: " + exception.GetType().Name);
            }
        }

        /// <summary>Checks real encrypted persistence while cleaning up only this run's synthetic logical keys.</summary>
        /// <param name="context">The test application's private application context.</param>
        /// <param name="setStage">Records the current non-secret operation for a failed test.</param>
        /// <param name="token">Cancels stalled credential operations.</param>
        /// <returns>The number of successful cross-instance persistence assertions.</returns>
        private static async Task<int> RunCredentialChecksAsync(Context context, Action<string> setStage, CancellationToken token)
        {
            var firstKey = "native-test-first-" + Guid.NewGuid().ToString("N");
            var secondKey = "native-test-second-" + Guid.NewGuid().ToString("N");
            var firstSecret = RandomSecret();
            var secondSecret = RandomSecret();
            var updatedSecret = RandomSecret();
            var first = new AndroidCredentialStore(context);
            var count = 0;
            try
            {
                setStage("first-write-read");
                await first.WriteAsync(firstKey, firstSecret, token);
                Require(await first.ReadAsync(firstKey, token) == firstSecret);
                count++;

                setStage("new-instance-read");
                var second = new AndroidCredentialStore(context);
                Require(await second.ReadAsync(firstKey, token) == firstSecret);
                count++;

                setStage("second-write-retains-first");
                await second.WriteAsync(secondKey, secondSecret, token);
                Require(await first.ReadAsync(firstKey, token) == firstSecret);
                Require(await first.ReadAsync(secondKey, token) == secondSecret);
                count += 2;

                setStage("cross-instance-update");
                var third = new AndroidCredentialStore(context);
                await third.WriteAsync(firstKey, updatedSecret, token);
                Require(await first.ReadAsync(firstKey, token) == updatedSecret);
                Require(await second.ReadAsync(firstKey, token) == updatedSecret);
                Require(await third.ReadAsync(secondKey, token) == secondSecret);
                count += 3;

                setStage("cross-instance-delete");
                await third.DeleteAsync(firstKey, token);
                Require(await first.ReadAsync(firstKey, token) is null);
                Require(await second.ReadAsync(secondKey, token) == secondSecret);
                count += 2;

                setStage("reopen-and-delete-last");
                var reopened = new AndroidCredentialStore(context);
                Require(await reopened.ReadAsync(secondKey, token) == secondSecret);
                await reopened.DeleteAsync(secondKey, token);
                Require(await first.ReadAsync(secondKey, token) is null);
                Require(await reopened.ReadAsync(firstKey, token) is null);
                return count + 3;
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await first.DeleteAsync(firstKey, cleanup.Token);
                await first.DeleteAsync(secondKey, cleanup.Token);
            }
        }

        /// <summary>Creates a synthetic value without reusing an account or credential from any application.</summary>
        /// <returns>A randomly generated test-only secret.</returns>
        private static string RandomSecret()
        {
            return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        }

        /// <summary>Rejects a native persistence mismatch without logging either compared secret.</summary>
        /// <param name="condition">Whether the synthetic persistence expectation holds.</param>
        /// <exception cref="InvalidOperationException">The native credential store did not preserve the expected value.</exception>
        private static void Require(bool condition)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Native credential persistence assertion failed.");
            }
        }
    }
}
