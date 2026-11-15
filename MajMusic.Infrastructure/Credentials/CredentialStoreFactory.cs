#nullable enable

using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Abstractions;

namespace MajMusic.Infrastructure.Credentials
{
    /// <summary>Creates an idle protected credential store without reading credentials or opening a vault.</summary>
    public static class CredentialStoreFactory
    {
        /// <summary>Creates the credential adapter for the current desktop platform.</summary>
        /// <returns>A protected platform store, or an adapter that reports unsupported platforms when used.</returns>
        public static ICredentialStore Create()
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsCredentialStore();
            }

            if (OperatingSystem.IsMacOS())
            {
                return new MacKeychainCredentialStore();
            }

            if (OperatingSystem.IsLinux())
            {
                return new LinuxSecretServiceCredentialStore();
            }

            return new UnavailableCredentialStore();
        }
    }

    /// <summary>Normalizes all native target names into this application's private credential namespace.</summary>
    internal static class CredentialIdentity
    {
        /// <summary>Bounds credential data consistently with Windows generic-credential storage.</summary>
        internal const int MaximumSecretBytes = 2560;

        /// <summary>Builds a non-secret fixed-length target that cannot escape the application namespace.</summary>
        /// <param name="key">The endpoint/account/origin identity supplied by the application.</param>
        /// <returns>The deterministic native credential target.</returns>
        /// <exception cref="ArgumentException">The key is empty or excessively long.</exception>
        internal static string Target(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (key.Length > 4096)
            {
                throw new ArgumentException("The credential identity is too long.", nameof(key));
            }

            return "MajMusic:credential:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        }

        /// <summary>Encodes a bounded secret without putting it in an exception message.</summary>
        /// <param name="secret">The credential to encode.</param>
        /// <returns>The caller-owned UTF-8 bytes that must be cleared after use.</returns>
        /// <exception cref="ArgumentException">The secret is null or exceeds the supported vault size.</exception>
        internal static byte[] Encode(string secret)
        {
            ArgumentNullException.ThrowIfNull(secret);
            if (Encoding.UTF8.GetByteCount(secret) > MaximumSecretBytes)
            {
                throw new ArgumentException("The credential exceeds the system vault's supported size.", nameof(secret));
            }

            return Encoding.UTF8.GetBytes(secret);
        }
    }

    /// <summary>Reports the absence of a protected vault without falling back to a settings file.</summary>
    internal sealed class UnavailableCredentialStore : ICredentialStore
    {
        /// <inheritdoc />
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CredentialIdentity.Target(key);
            return Task.FromException<string?>(Unavailable());
        }

        /// <inheritdoc />
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CredentialIdentity.Target(key);
            var bytes = CredentialIdentity.Encode(secret);
            CryptographicOperations.ZeroMemory(bytes);
            return Task.FromException(Unavailable());
        }

        /// <inheritdoc />
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CredentialIdentity.Target(key);
            return Task.FromException(Unavailable());
        }

        /// <summary>Creates a non-secret operational error for unsupported hosts.</summary>
        /// <returns>The error describing the missing protected vault.</returns>
        private static InvalidOperationException Unavailable()
        {
            return new InvalidOperationException("当前平台没有可用的系统密码存储。");
        }
    }
}
