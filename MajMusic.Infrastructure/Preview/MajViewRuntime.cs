#nullable enable

using System.Buffers;
using System.Security.Cryptography;
using MajMusic.Infrastructure.Network;

namespace MajMusic.Infrastructure.Preview
{
    /// <summary>Installs and verifies the published MajdataNet WebGL player on demand.</summary>
    public sealed class MajViewRuntime
    {
        /// <summary>Identifies the upstream revision that published this WebGL build.</summary>
        public const string Revision = "e9db7b4b431954f2e9fefa7745d3650b93fc4671";

        private static readonly Uri Source = new($"https://raw.githubusercontent.com/TeamMajdata/MajdataNet/{Revision}/public/WebGLBuild/");
        private static readonly RuntimeFile[] Files =
        [
            new("Build.loader.js", 26_982, "CA1F0B74F13D467C479CA9E7F33E9A624E54F68420DB6AC7EE4FB0673C795E8C"),
            new("Build.framework.js", 442_933, "3270D993549F8B577760731A51B00A1481A883BF2935F20E1EE8C84694A119EC"),
            new("Build.data", 10_461_899, "785970F410BC53AC18DA1AD6AAF20EDFEE205212A1431F19A5E3C7369E3B9F1C"),
            new("Build.wasm", 38_081_292, "5C3AB6EF92B29A46BECD9B863CA789AEE1BDF0AC18754E96013DFDFFB0A3CD10")
        ];
        private static readonly Lazy<HttpClient> DownloadClient = new(() => new HttpClient(SourceHttpTransportFactory.CreateAnonymousHandler()));
        private readonly HttpClient? _httpClient;
        private readonly string _installDirectory;
        private readonly SemaphoreSlim _installGate = new(1, 1);

        /// <summary>Points at a versioned player cache without creating files or starting a request.</summary>
        /// <param name="dataDirectory">The application's private data directory.</param>
        /// <param name="httpClient">An optional caller-owned anonymous transport.</param>
        public MajViewRuntime(string dataDirectory, HttpClient? httpClient = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
            _installDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "Runtime", "MajView", Revision);
            _httpClient = httpClient;
        }

        /// <summary>Gets whether all expected local files have their expected lengths. Opening the runtime also verifies their digests.</summary>
        public bool IsInstalled => Files.All(file => HasExpectedLength(Path.Combine(_installDirectory, file.Name), file.Length));

        /// <summary>Returns a verified local player, downloading it only when permitted and needed.</summary>
        /// <param name="allowDownload">Whether this request may fetch missing or damaged player files.</param>
        /// <param name="cancellationToken">Cancels verification or download before publication.</param>
        /// <returns>The absolute directory containing the four verified runtime files.</returns>
        /// <exception cref="InvalidOperationException">The runtime is missing or damaged and downloading is disabled.</exception>
        /// <exception cref="InvalidDataException">An upstream file failed verification.</exception>
        public async Task<string> EnsureAsync(bool allowDownload, CancellationToken cancellationToken = default)
        {
            await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsInstalled && await VerifyAsync(_installDirectory, cancellationToken).ConfigureAwait(false))
                {
                    return _installDirectory;
                }
                if (!allowDownload)
                {
                    throw new InvalidOperationException("MajView 预览组件尚未下载或文件损坏。请开启在线模式后重试。");
                }

                await InstallAsync(cancellationToken).ConfigureAwait(false);
                return _installDirectory;
            }
            finally
            {
                _installGate.Release();
            }
        }

        private async Task InstallAsync(CancellationToken cancellationToken)
        {
            var staging = _installDirectory + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(staging);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                foreach (var file in Files)
                {
                    await DownloadAsync(file, Path.Combine(staging, file.Name), deadline.Token).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(_installDirectory))
                {
                    foreach (var file in Files)
                    {
                        File.Move(Path.Combine(staging, file.Name), Path.Combine(_installDirectory, file.Name), overwrite: true);
                    }
                }
                else
                {
                    try
                    {
                        Directory.Move(staging, _installDirectory);
                    }
                    catch (IOException) when (Directory.Exists(_installDirectory))
                    {
                        if (!await VerifyAsync(_installDirectory, cancellationToken).ConfigureAwait(false))
                        {
                            throw;
                        }
                    }
                }
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    foreach (var file in Files)
                    {
                        File.Delete(Path.Combine(staging, file.Name));
                    }
                    Directory.Delete(staging);
                }
            }
        }

        private async Task DownloadAsync(RuntimeFile file, string outputPath, CancellationToken cancellationToken)
        {
            using var response = await (_httpClient ?? DownloadClient.Value)
                .GetAsync(new Uri(Source, file.Name), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != file.Length)
            {
                throw InvalidRuntimeFile(file);
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long downloaded = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    downloaded += read;
                    if (downloaded > file.Length)
                    {
                        throw InvalidRuntimeFile(file);
                    }
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (downloaded != file.Length || Convert.ToHexString(hash.GetHashAndReset()) != file.Sha256)
                {
                    throw InvalidRuntimeFile(file);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task<bool> VerifyAsync(string directory, CancellationToken cancellationToken)
        {
            foreach (var file in Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var stream = new FileStream(Path.Combine(directory, file.Name), FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length != file.Length
                        || Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) != file.Sha256)
                    {
                        return false;
                    }
                }
                catch (FileNotFoundException)
                {
                    return false;
                }
                catch (DirectoryNotFoundException)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasExpectedLength(string path, long length)
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == length;
        }

        private static InvalidDataException InvalidRuntimeFile(RuntimeFile file) =>
            new($"MajView 预览组件文件校验失败：{file.Name}");

        private sealed record RuntimeFile(string Name, long Length, string Sha256);
    }
}
