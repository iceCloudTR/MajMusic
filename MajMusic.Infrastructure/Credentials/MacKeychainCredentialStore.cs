#nullable enable

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Abstractions;

namespace MajMusic.Infrastructure.Credentials
{
    /// <summary>Stores generic-password items through macOS Keychain Services without a shell or password arguments.</summary>
    internal sealed class MacKeychainCredentialStore : ICredentialStore
    {
        /// <summary>Locates the system Security framework.</summary>
        private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
        /// <summary>Locates the system Core Foundation framework.</summary>
        private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        /// <summary>Identifies an absent Keychain item.</summary>
        private const int ItemNotFound = -25300;
        /// <summary>Identifies an item concurrently created before an add operation.</summary>
        private const int DuplicateItem = -25299;
        /// <summary>Defers framework loading until the user actually accesses a credential.</summary>
        private static readonly Lazy<FrameworkSymbols> Symbols = new(() => new FrameworkSymbols());

        /// <inheritdoc />
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = CredentialIdentity.Target(key);
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var query = CreateQuery(target);
                query.Set("kSecReturnData", Symbols.Value.CoreConstant("kCFBooleanTrue"));
                query.Set("kSecMatchLimit", Symbols.Value.SecurityConstant("kSecMatchLimitOne"));
                var status = SecItemCopyMatching(query.Handle, out var data);
                if (status == ItemNotFound)
                {
                    return null;
                }

                EnsureSuccess(status);
                if (data == IntPtr.Zero)
                {
                    throw new InvalidOperationException("系统密码存储中的凭据无效。");
                }

                try
                {
                    var length = CFDataGetLength(data);
                    if (length < 0 || length > CredentialIdentity.MaximumSecretBytes)
                    {
                        throw new InvalidOperationException("系统密码存储中的凭据无效。");
                    }

                    var bytes = new byte[(int)length];
                    try
                    {
                        if (bytes.Length > 0)
                        {
                            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        return Encoding.UTF8.GetString(bytes);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }
                finally
                {
                    CFRelease(data);
                }
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = CredentialIdentity.Target(key);
            var bytes = CredentialIdentity.Encode(secret);
            return WriteCoreAsync(target, bytes, cancellationToken);
        }

        /// <inheritdoc />
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = CredentialIdentity.Target(key);
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var query = CreateQuery(target);
                var status = SecItemDelete(query.Handle);
                if (status != ItemNotFound)
                {
                    EnsureSuccess(status);
                }
            }, cancellationToken);
        }

        /// <summary>Owns secret bytes through a worker-based synchronous Keychain write.</summary>
        /// <param name="target">The normalized application target.</param>
        /// <param name="bytes">The credential bytes cleared before completion.</param>
        /// <param name="cancellationToken">Cancels before the protected write begins.</param>
        /// <returns>A task representing the completed protected write.</returns>
        private static async Task WriteCoreAsync(string target, byte[] bytes, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var query = CreateQuery(target);
                    using var update = new OwnedDictionary();
                    var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
                    if (data == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("无法准备系统密码存储。");
                    }

                    try
                    {
                        update.Set("kSecValueData", data);
                        cancellationToken.ThrowIfCancellationRequested();
                        var status = SecItemUpdate(query.Handle, update.Handle);
                        if (status == ItemNotFound)
                        {
                            query.Set("kSecValueData", data);
                            status = SecItemAdd(query.Handle, IntPtr.Zero);
                            if (status == DuplicateItem)
                            {
                                using var retry = CreateQuery(target);
                                status = SecItemUpdate(retry.Handle, update.Handle);
                            }
                        }

                        EnsureSuccess(status);
                    }
                    finally
                    {
                        CFRelease(data);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        /// <summary>Creates an exact generic-password query limited to the MajMusic service and requested identity.</summary>
        /// <param name="target">The normalized application target.</param>
        /// <returns>A caller-owned Core Foundation query dictionary.</returns>
        private static OwnedDictionary CreateQuery(string target)
        {
            var query = new OwnedDictionary();
            try
            {
                query.Set("kSecClass", Symbols.Value.SecurityConstant("kSecClassGenericPassword"));
                query.SetString("kSecAttrService", "MajMusic");
                query.SetString("kSecAttrAccount", target);
                return query;
            }
            catch
            {
                query.Dispose();
                throw;
            }
        }

        /// <summary>Reports failed OSStatus values without disclosing credential contents or identities.</summary>
        /// <param name="status">The signed 32-bit Keychain status.</param>
        /// <exception cref="InvalidOperationException">The system operation failed.</exception>
        private static void EnsureSuccess(int status)
        {
            if (status != 0)
            {
                throw new InvalidOperationException("无法访问 macOS 钥匙串。");
            }
        }

        /// <summary>Owns one mutable CFDictionary using retaining CFType callbacks on 64-bit desktop ABIs.</summary>
        private sealed class OwnedDictionary : IDisposable
        {
            /// <summary>Creates a dictionary that retains inserted keys and values.</summary>
            public OwnedDictionary()
            {
                Handle = CFDictionaryCreateMutable(IntPtr.Zero, 0,
                    Symbols.Value.CoreAddress("kCFTypeDictionaryKeyCallBacks"),
                    Symbols.Value.CoreAddress("kCFTypeDictionaryValueCallBacks"));
                if (Handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("无法准备系统密码存储。");
                }
            }

            /// <summary>Gets the owned dictionary reference.</summary>
            public IntPtr Handle { get; private set; }

            /// <summary>Sets one retained Security-framework constant key.</summary>
            /// <param name="key">The exported Security-framework key name.</param>
            /// <param name="value">The retained Core Foundation value.</param>
            public void Set(string key, IntPtr value)
            {
                CFDictionarySetValue(Handle, Symbols.Value.SecurityConstant(key), value);
            }

            /// <summary>Sets a copied UTF-8 string and releases the temporary string reference.</summary>
            /// <param name="key">The exported Security-framework key name.</param>
            /// <param name="value">The non-secret service or credential identity.</param>
            public void SetString(string key, string value)
            {
                var text = CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100);
                if (text == IntPtr.Zero)
                {
                    throw new InvalidOperationException("无法准备系统密码存储。");
                }

                try
                {
                    Set(key, text);
                }
                finally
                {
                    CFRelease(text);
                }
            }

            /// <inheritdoc />
            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    CFRelease(Handle);
                    Handle = IntPtr.Zero;
                }
            }
        }

        /// <summary>Keeps two fixed framework handles alive while exported CF constants may be referenced.</summary>
        private sealed class FrameworkSymbols
        {
            /// <summary>Owns the process-lifetime Security framework handle.</summary>
            private readonly IntPtr _security = NativeLibrary.Load(SecurityFramework);
            /// <summary>Owns the process-lifetime Core Foundation framework handle.</summary>
            private readonly IntPtr _core = NativeLibrary.Load(CoreFoundationFramework);

            /// <summary>Reads a CF object pointer from an exported Security constant variable.</summary>
            /// <param name="name">The exported constant name.</param>
            /// <returns>The borrowed CF object reference.</returns>
            public IntPtr SecurityConstant(string name)
            {
                return Marshal.ReadIntPtr(NativeLibrary.GetExport(_security, name));
            }

            /// <summary>Reads a CF object pointer from an exported Core Foundation constant variable.</summary>
            /// <param name="name">The exported constant name.</param>
            /// <returns>The borrowed CF object reference.</returns>
            public IntPtr CoreConstant(string name)
            {
                return Marshal.ReadIntPtr(CoreAddress(name));
            }

            /// <summary>Resolves an exported callback structure by address rather than dereferencing it as a CF object.</summary>
            /// <param name="name">The exported symbol name.</param>
            /// <returns>The borrowed symbol address.</returns>
            public IntPtr CoreAddress(string name)
            {
                return NativeLibrary.GetExport(_core, name);
            }
        }

        /// <summary>Creates a mutable CF dictionary; CFIndex uses native pointer width.</summary>
        /// <param name="allocator">The default allocator when null.</param>
        /// <param name="capacity">The initial native-width entry capacity.</param>
        /// <param name="keyCallbacks">The address of the retaining key callback structure.</param>
        /// <param name="valueCallbacks">The address of the retaining value callback structure.</param>
        /// <returns>An owned mutable dictionary reference.</returns>
        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);

        /// <summary>Retains one key/value pair in a mutable CF dictionary.</summary>
        /// <param name="dictionary">The mutable dictionary.</param>
        /// <param name="key">The borrowed CF key.</param>
        /// <param name="value">The borrowed CF value to retain.</param>
        [DllImport(CoreFoundationFramework)]
        private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);

        /// <summary>Creates an owned CF string from a null-terminated UTF-8 service or account value.</summary>
        /// <param name="allocator">The default allocator when null.</param>
        /// <param name="value">The non-secret UTF-8 input.</param>
        /// <param name="encoding">The explicit UTF-8 encoding identifier.</param>
        /// <returns>An owned string reference.</returns>
        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

        /// <summary>Creates owned CF data by copying the bounded password bytes.</summary>
        /// <param name="allocator">The default allocator when null.</param>
        /// <param name="bytes">The bounded source secret bytes.</param>
        /// <param name="length">The native-width byte count.</param>
        /// <returns>An owned CFData reference.</returns>
        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

        /// <summary>Reads a CFData byte length as a native-width signed CFIndex.</summary>
        /// <param name="data">The borrowed CFData reference.</param>
        /// <returns>The native-width signed byte count.</returns>
        [DllImport(CoreFoundationFramework)]
        private static extern nint CFDataGetLength(IntPtr data);

        /// <summary>Reads a borrowed byte pointer valid for the lifetime of its CFData owner.</summary>
        /// <param name="data">The owning CFData reference.</param>
        /// <returns>The borrowed immutable byte pointer.</returns>
        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr data);

        /// <summary>Releases a single owned Core Foundation reference.</summary>
        /// <param name="reference">The owned reference to release.</param>
        [DllImport(CoreFoundationFramework)]
        private static extern void CFRelease(IntPtr reference);

        /// <summary>Copies the exact matching password data into a caller-owned CFData reference.</summary>
        /// <param name="query">The exact service/account query requesting password data.</param>
        /// <param name="result">Receives the owned result.</param>
        /// <returns>The signed 32-bit Keychain status.</returns>
        [DllImport(SecurityFramework)]
        private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

        /// <summary>Adds one exact generic-password item; OSStatus is signed 32-bit.</summary>
        /// <param name="attributes">The service, account and secret data attributes.</param>
        /// <param name="result">The optional result pointer, unused when null.</param>
        /// <returns>The signed 32-bit Keychain status.</returns>
        [DllImport(SecurityFramework)]
        private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

        /// <summary>Updates the exact matching generic-password data.</summary>
        /// <param name="query">The exact service/account query.</param>
        /// <param name="attributesToUpdate">The dictionary containing only new secret data.</param>
        /// <returns>The signed 32-bit Keychain status.</returns>
        [DllImport(SecurityFramework)]
        private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

        /// <summary>Deletes the exact application service/account match.</summary>
        /// <param name="query">The exact service/account query.</param>
        /// <returns>The signed 32-bit Keychain status.</returns>
        [DllImport(SecurityFramework)]
        private static extern int SecItemDelete(IntPtr query);
    }
}
