#nullable enable

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using MajMusic.Core.Abstractions;

namespace MajMusic.Infrastructure.Credentials
{
    /// <summary>Uses the desktop Secret Service through libsecret's system tool, sending passwords only through stdin.</summary>
    internal sealed class LinuxSecretServiceCredentialStore : ICredentialStore
    {
        /// <summary>Bounds a missing or locked desktop Secret Service rather than hanging application shutdown.</summary>
        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

        /// <inheritdoc />
        public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = CredentialIdentity.Target(key);
            var result = await RunToolAsync("lookup", target, null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 1 && !result.HasError)
            {
                return null;
            }

            EnsureSuccess(result.ExitCode);
            if (System.Text.Encoding.UTF8.GetByteCount(result.Output) > CredentialIdentity.MaximumSecretBytes)
            {
                throw new InvalidOperationException("系统密码存储中的凭据无效。");
            }

            return result.Output;
        }

        /// <inheritdoc />
        public async Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = CredentialIdentity.Target(key);
            var bytes = CredentialIdentity.Encode(secret);
            try
            {
                var result = await RunToolAsync("store", target, bytes, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result.ExitCode);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = CredentialIdentity.Target(key);
            var result = await RunToolAsync("clear", target, null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 1 || result.HasError)
            {
                EnsureSuccess(result.ExitCode);
            }
        }

        /// <summary>Runs the fixed system libsecret utility without a shell, password arguments or inherited terminal input.</summary>
        /// <param name="operation">The fixed lookup, store or clear operation.</param>
        /// <param name="target">The normalized application credential target.</param>
        /// <param name="input">Optional bounded secret bytes sent exactly, without an added newline.</param>
        /// <param name="cancellationToken">Cancels the child operation and terminates its process.</param>
        /// <returns>The result and captured output; stderr contents are never exposed.</returns>
        /// <exception cref="InvalidOperationException">The system tool or Secret Service is unavailable.</exception>
        private static async Task<(int ExitCode, string Output, bool HasError)> RunToolAsync(
            string operation, string target, byte[]? input, CancellationToken cancellationToken)
        {
            var executable = File.Exists("/usr/bin/secret-tool") ? "/usr/bin/secret-tool"
                : File.Exists("/bin/secret-tool") ? "/bin/secret-tool" : null;
            if (executable is null)
            {
                throw new InvalidOperationException("系统密码存储不可用，请安装 libsecret-tools 并启用桌面密码服务。");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(OperationTimeout);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add(operation);
            if (operation == "store")
            {
                process.StartInfo.ArgumentList.Add("--label=MajMusic");
            }

            process.StartInfo.ArgumentList.Add("application");
            process.StartInfo.ArgumentList.Add("MajMusic");
            process.StartInfo.ArgumentList.Add("credential");
            process.StartInfo.ArgumentList.Add(target);
            Task<string>? output = null;
            Task<string>? error = null;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!process.Start())
                {
                    throw new InvalidOperationException("无法打开系统密码存储。");
                }

                output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                error = process.StandardError.ReadToEndAsync(deadline.Token);
                if (input is not null)
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
                }

                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                var capturedOutput = await output.ConfigureAwait(false);
                var capturedError = await error.ConfigureAwait(false);
                return (process.ExitCode, capturedOutput, capturedError.Length > 0);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("系统密码存储响应超时。");
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException("无法打开系统密码存储，请检查桌面密码服务。");
            }
            catch (IOException)
            {
                throw new InvalidOperationException("无法访问系统密码存储，请检查桌面密码服务。");
            }
            finally
            {
                deadline.Cancel();
                TryTerminate(process);
                await ObservePipeCompletionAsync(output).ConfigureAwait(false);
                await ObservePipeCompletionAsync(error).ConfigureAwait(false);
            }
        }

        /// <summary>Observes redirected pipe completion even when cancellation or startup failure bypasses normal awaits.</summary>
        /// <param name="completion">The optional scoped pipe read.</param>
        /// <returns>A task representing observed pipe shutdown.</returns>
        private static async Task ObservePipeCompletionAsync(Task<string>? completion)
        {
            if (completion is null)
            {
                return;
            }

            try
            {
                await completion.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The scoped utility has been canceled or ended; no pipe exception is left unobserved.
            }
        }

        /// <summary>Terminates only the child launched for this credential request when it has not already exited.</summary>
        /// <param name="process">The scoped libsecret child process.</param>
        private static void TryTerminate(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // There is no launched process, or the child already ended during cancellation.
            }
        }

        /// <summary>Reports failure without returning libsecret stderr or credential contents to the UI.</summary>
        /// <param name="exitCode">The scoped utility's exit status.</param>
        /// <exception cref="InvalidOperationException">The vault rejected the operation.</exception>
        private static void EnsureSuccess(int exitCode)
        {
            if (exitCode != 0)
            {
                throw new InvalidOperationException("无法访问系统密码存储，请检查桌面密码服务是否已解锁。");
            }
        }
    }
}
