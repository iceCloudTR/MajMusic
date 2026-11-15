#nullable enable

using MajMusic.Infrastructure.Credentials;

namespace MajMusic.Tests
{
    /// <summary>Checks protected-vault boundaries without reading or changing existing user credentials.</summary>
    internal static class CredentialStoreTests
    {
        /// <summary>Checks invalid input and pre-cancellation before any native vault is accessed.</summary>
        /// <returns>A task representing the hardware-independent contract checks.</returns>
        public static async Task RunAsync()
        {
            var store = CredentialStoreFactory.Create();
            await TestAssert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(" "));
            await TestAssert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(new string('x', 4097)));
            await TestAssert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("MajMusic/tests/validation", new string('x', 2561)));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var key = "MajMusic/tests/canceled-" + Guid.NewGuid().ToString("N");
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => store.ReadAsync(key, canceled.Token));
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => store.WriteAsync(key, "unused-test-value", canceled.Token));
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => store.DeleteAsync(key, canceled.Token));
        }

        /// <summary>Writes, reopens, updates and removes only newly generated Windows test credentials.</summary>
        /// <returns>A task representing real native vault validation, without using real account credentials.</returns>
        /// <exception cref="PlatformNotSupportedException">The check is run outside Windows.</exception>
        public static async Task RunNativeAsync()
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The native vault acceptance check currently targets Windows.");
            }

            var prefix = "MajMusic/tests/native-vault-" + Guid.NewGuid().ToString("N");
            var firstKey = prefix + "/endpoint-a/account-a";
            var secondKey = prefix + "/endpoint-a/account-b";
            var otherEndpointKey = prefix + "/endpoint-b/account-a";
            if (!firstKey.StartsWith("MajMusic/tests/native-vault-", StringComparison.Ordinal)
                || !secondKey.StartsWith(prefix, StringComparison.Ordinal) || !otherEndpointKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to touch a credential outside the generated test namespace.");
            }

            var store = CredentialStoreFactory.Create();
            var secret = "Generated 测试\n\"/\\ secret " + Guid.NewGuid().ToString("N");
            var replacement = "Replacement 测试 " + Guid.NewGuid().ToString("N");
            try
            {
                TestAssert.True(await store.ReadAsync(firstKey) is null, "A generated credential identity starts absent.");
                await store.WriteAsync(firstKey, secret);
                var reopened = CredentialStoreFactory.Create();
                TestAssert.True(await reopened.ReadAsync(firstKey) == secret, "The protected vault persists exact Unicode data across adapter instances.");
                TestAssert.True(await reopened.ReadAsync(secondKey) is null, "Different accounts cannot read each other's credential key.");
                TestAssert.True(await reopened.ReadAsync(otherEndpointKey) is null, "Different endpoints cannot read each other's credential key.");
                await reopened.WriteAsync(firstKey, replacement);
                TestAssert.True(await store.ReadAsync(firstKey) == replacement, "Updating a protected credential replaces only its own target.");
                await reopened.WriteAsync(secondKey, secret);
                await reopened.DeleteAsync(firstKey);
                TestAssert.True(await store.ReadAsync(firstKey) is null, "A deleted credential must be absent.");
                TestAssert.True(await store.ReadAsync(secondKey) == secret, "Removing one account credential must retain the other account.");
                await store.DeleteAsync(firstKey);
            }
            finally
            {
                await store.DeleteAsync(firstKey);
                await store.DeleteAsync(secondKey);
                await store.DeleteAsync(otherEndpointKey);
            }
        }
    }
}
