#nullable enable

using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Abstractions;
using global::Android.Content;
using global::Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;

namespace MajMusic.Android.Security
{
    /// <summary>Encrypts saved credentials with an application-specific key held by Android Keystore.</summary>
    internal sealed class AndroidCredentialStore : ICredentialStore
    {
        /// <summary>Identifies the nonexportable application encryption key.</summary>
        private const string KeyAlias = "MajMusic.Credentials.v1";
        /// <summary>Stores only encrypted credential blobs in app-private preferences.</summary>
        private readonly ISharedPreferences _preferences;
        /// <summary>Serializes key creation and atomic preference writes.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Opens the private encrypted-blob preferences without reading credentials.</summary>
        /// <param name="context">The application context owning the preferences.</param>
        public AndroidCredentialStore(Context context)
        {
            _preferences = context.GetSharedPreferences("MajMusic.Credentials", FileCreationMode.Private)
                ?? throw new InvalidOperationException("Android credential storage is unavailable.");
        }

        /// <inheritdoc />
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            return RunAsync(key, () =>
            {
                var stored = _preferences.GetString(Target(key), null);
                if (stored is null)
                {
                    return null;
                }

                var bytes = Convert.FromBase64String(stored);
                if (bytes.Length < 29)
                {
                    throw new InvalidDataException("The encrypted Android credential is invalid.");
                }

                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ?? throw new CryptographicException("The Android cipher is unavailable.");
                using var secretKey = GetKey();
                using var parameters = new GCMParameterSpec(128, bytes[..12]);
                cipher.Init(CipherMode.DecryptMode, secretKey, parameters);
                var plaintext = cipher.DoFinal(bytes[12..]) ?? throw new CryptographicException("The Android credential could not be decrypted.");
                try
                {
                    return Encoding.UTF8.GetString(plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }, cancellationToken);
        }

        /// <inheritdoc />
        public async Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(secret);
            await RunAsync(key, () =>
            {
                var plaintext = Encoding.UTF8.GetBytes(secret);
                try
                {
                    using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ?? throw new CryptographicException("The Android cipher is unavailable.");
                    using var secretKey = GetKey();
                    cipher.Init(CipherMode.EncryptMode, secretKey);
                    var encrypted = cipher.DoFinal(plaintext) ?? throw new CryptographicException("The Android credential could not be encrypted.");
                    var nonce = cipher.GetIV() ?? throw new CryptographicException("The Android credential nonce is unavailable.");
                    if (nonce.Length != 12)
                    {
                        throw new CryptographicException("The Android credential nonce has an invalid length.");
                    }

                    using var editor = _preferences.Edit() ?? throw new IOException("Android credential preferences are unavailable.");
                    editor.PutString(Target(key), Convert.ToBase64String(nonce.Concat(encrypted).ToArray()));
                    if (!editor.Commit())
                    {
                        throw new IOException("The Android credential could not be saved.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            await RunAsync(key, () =>
            {
                using var editor = _preferences.Edit() ?? throw new IOException("Android credential preferences are unavailable.");
                editor.Remove(Target(key));
                if (!editor.Commit())
                {
                    throw new IOException("The saved Android credential could not be removed.");
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Runs one bounded credential operation on a managed worker under its vault lock.</summary>
        /// <typeparam name="T">The operation result type.</typeparam>
        /// <param name="key">The source- and account-scoped opaque credential identity.</param>
        /// <param name="action">The protected native storage operation.</param>
        /// <param name="token">Cancels waiting or returning a result.</param>
        /// <returns>The operation's protected result.</returns>
        private async Task<T> RunAsync<T>(string key, Func<T> action, CancellationToken token)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return action();
                }, token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Obtains the app's existing protected AES key or creates it in Android Keystore.</summary>
        /// <returns>A scoped native key handle whose bytes cannot be exported.</returns>
        private static ISecretKey GetKey()
        {
            using var store = KeyStore.GetInstance("AndroidKeyStore") ?? throw new CryptographicException("Android Keystore is unavailable.");
            store.Load(null);
            if (store.GetKey(KeyAlias, null) is ISecretKey existing)
            {
                return existing;
            }

            using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")
                ?? throw new CryptographicException("The Android key generator is unavailable.");
            using var parameters = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetRandomizedEncryptionRequired(true)
                .SetKeySize(256)
                .Build();
            generator.Init(parameters);
            return generator.GenerateKey() ?? throw new CryptographicException("The Android key could not be created.");
        }

        /// <summary>Converts opaque account identities into stable preference names.</summary>
        /// <param name="key">The original account-scoped identity.</param>
        /// <returns>A digest containing no password or account name.</returns>
        private static string Target(string key)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        }
    }
}
