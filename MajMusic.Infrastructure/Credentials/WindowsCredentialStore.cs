#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using MajMusic.Core.Abstractions;

namespace MajMusic.Infrastructure.Credentials
{
    /// <summary>Stores UTF-8 generic credentials in Windows Credential Manager for the current user.</summary>
    internal sealed class WindowsCredentialStore : ICredentialStore
    {
        /// <summary>Identifies application-defined generic credentials.</summary>
        private const uint GenericCredential = 1;
        /// <summary>Persists across logon sessions on this computer for this Windows user.</summary>
        private const uint LocalMachinePersistence = 2;
        /// <summary>Identifies an absent credential without hiding other vault failures.</summary>
        private const int NotFound = 1168;

        /// <inheritdoc />
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = CredentialIdentity.Target(key);
            return Task.Run(() => Read(target, cancellationToken), cancellationToken);
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
                if (!CredDelete(target, GenericCredential, 0) && Marshal.GetLastPInvokeError() != NotFound)
                {
                    throw Failure();
                }
            }, cancellationToken);
        }

        /// <summary>Reads and releases only the requested native credential allocation.</summary>
        /// <param name="target">The normalized application target.</param>
        /// <param name="cancellationToken">Cancels before reading or returning a secret.</param>
        /// <returns>The stored secret, or null when absent.</returns>
        private static string? Read(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CredRead(target, GenericCredential, 0, out var allocation))
            {
                if (Marshal.GetLastPInvokeError() == NotFound)
                {
                    return null;
                }

                throw Failure();
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(allocation);
                if (credential.CredentialBlobSize > CredentialIdentity.MaximumSecretBytes
                    || credential.CredentialBlobSize > 0 && credential.CredentialBlob == IntPtr.Zero)
                {
                    throw new InvalidOperationException("系统密码存储中的凭据无效。");
                }

                var bytes = new byte[credential.CredentialBlobSize];
                try
                {
                    if (bytes.Length > 0)
                    {
                        Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    ClearNative(credential.CredentialBlob, bytes.Length);
                }
            }
            finally
            {
                CredFree(allocation);
            }
        }

        /// <summary>Owns encoded secret bytes even when cancellation prevents the worker from starting.</summary>
        /// <param name="target">The normalized application target.</param>
        /// <param name="bytes">The credential bytes, cleared before completion.</param>
        /// <param name="cancellationToken">Cancels before the native write begins.</param>
        /// <returns>A task representing the completed protected write.</returns>
        private static async Task WriteCoreAsync(string target, byte[] bytes, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
                    try
                    {
                        if (bytes.Length > 0)
                        {
                            Marshal.Copy(bytes, 0, blob, bytes.Length);
                        }

                        var credential = new Credential
                        {
                            Type = GenericCredential,
                            TargetName = target,
                            CredentialBlobSize = (uint)bytes.Length,
                            CredentialBlob = blob,
                            Persist = LocalMachinePersistence,
                            UserName = "MajMusic"
                        };
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!CredWrite(ref credential, 0))
                        {
                            throw Failure();
                        }
                    }
                    finally
                    {
                        ClearNative(blob, bytes.Length);
                        Marshal.FreeHGlobal(blob);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        /// <summary>Clears caller-owned native secret storage before its allocator releases it.</summary>
        /// <param name="pointer">The allocation containing secret bytes.</param>
        /// <param name="length">The bounded number of initialized bytes.</param>
        private static void ClearNative(IntPtr pointer, int length)
        {
            for (var index = 0; index < length; index++)
            {
                Marshal.WriteByte(pointer, index, 0);
            }
        }

        /// <summary>Creates an operational error that never contains a target or secret.</summary>
        /// <returns>The sanitized vault access failure.</returns>
        private static InvalidOperationException Failure()
        {
            return new InvalidOperationException("无法访问 Windows 密码存储。");
        }

        /// <summary>Matches CREDENTIALW: DWORD fields are 32-bit, FILETIME is 8 bytes, pointers use the current ABI width.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            /// <summary>Contains reserved flags.</summary>
            public uint Flags;
            /// <summary>Identifies the generic credential type.</summary>
            public uint Type;
            /// <summary>Contains the normalized target.</summary>
            public string? TargetName;
            /// <summary>Contains an optional non-secret comment.</summary>
            public string? Comment;
            /// <summary>Contains the native last-write timestamp.</summary>
            public FILETIME LastWritten;
            /// <summary>Contains the secret byte count.</summary>
            public uint CredentialBlobSize;
            /// <summary>Points to the application-defined secret bytes.</summary>
            public IntPtr CredentialBlob;
            /// <summary>Defines the current user's persistence scope.</summary>
            public uint Persist;
            /// <summary>Contains the optional attribute count.</summary>
            public uint AttributeCount;
            /// <summary>Points to optional native attributes.</summary>
            public IntPtr Attributes;
            /// <summary>Contains an optional alias.</summary>
            public string? TargetAlias;
            /// <summary>Contains the non-secret application identity.</summary>
            public string? UserName;
        }

        /// <summary>Reads a single generic credential into one CredFree-owned allocation.</summary>
        /// <param name="target">The credential target.</param>
        /// <param name="type">The credential type.</param>
        /// <param name="flags">Reserved; zero.</param>
        /// <param name="credential">Receives the native allocation.</param>
        /// <returns>Whether Windows returned the credential.</returns>
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        /// <summary>Writes a generic credential into the current user's protected vault.</summary>
        /// <param name="credential">The native credential descriptor.</param>
        /// <param name="flags">Reserved; zero.</param>
        /// <returns>Whether Windows stored the credential.</returns>
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        /// <summary>Deletes the exact generic-credential target.</summary>
        /// <param name="target">The credential target.</param>
        /// <param name="type">The credential type.</param>
        /// <param name="flags">Reserved; zero.</param>
        /// <returns>Whether the credential was removed.</returns>
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        /// <summary>Releases the complete allocation returned by CredReadW.</summary>
        /// <param name="buffer">The exact native allocation.</param>
        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr buffer);
    }
}
