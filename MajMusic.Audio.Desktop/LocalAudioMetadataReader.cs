#nullable enable

using LibVLCSharp.Shared;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Audio.Desktop
{
    /// <summary>Parses local audio using an independent, lazy native engine without creating a media player.</summary>
    public sealed class LocalAudioMetadataReader : IAudioMetadataReader, IAsyncDisposable
    {
        /// <summary>Serializes native parsing and engine release.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);
        /// <summary>Protects creation of the shared disposal task.</summary>
        private readonly object _disposalGate = new();
        /// <summary>Contains an optional explicit native-runtime directory.</summary>
        private readonly string? _nativeLibraryDirectory;
        /// <summary>Owns the parser engine independently of the playing track.</summary>
        private LibVLC? _engine;
        /// <summary>Contains the single engine-release operation.</summary>
        private Task? _disposal;
        /// <summary>Rejects new parser operations during shutdown.</summary>
        private int _disposed;

        /// <summary>Creates an idle parser without loading native libraries or opening an audio device.</summary>
        /// <param name="nativeLibraryDirectory">An optional explicit LibVLC directory.</param>
        public LocalAudioMetadataReader(string? nativeLibraryDirectory = null)
        {
            _nativeLibraryDirectory = nativeLibraryDirectory;
        }

        /// <inheritdoc />
        public int CacheRevision => 1;

        /// <inheritdoc />
        /// <exception cref="ArgumentException">The path is not a fully qualified local file path.</exception>
        /// <exception cref="ObjectDisposedException">The reader is shutting down.</exception>
        public Task<AudioMetadata> ReadAsync(string localPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
            if (!Path.IsPathFullyQualified(localPath) || localPath.Contains("://", StringComparison.Ordinal)
                || localPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new ArgumentException("Metadata parsing requires a fully qualified local file path.", nameof(localPath));
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return Task.Run(() => ReadCoreAsync(Path.GetFullPath(localPath), cancellationToken), cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lock (_disposalGate)
            {
                if (_disposal is null)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    _disposal = DisposeCoreAsync();
                }

                return new ValueTask(_disposal);
            }
        }

        /// <summary>Parses one descriptor under the native gate on the managed pool.</summary>
        /// <param name="localPath">The normalized local audio path.</param>
        /// <param name="cancellationToken">Cancels native parsing.</param>
        /// <returns>Available properties of the first audio stream.</returns>
        /// <exception cref="InvalidDataException">The file does not expose supported audio metadata.</exception>
        private async Task<AudioMetadata> ReadCoreAsync(string localPath, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var file = new FileInfo(localPath);
                if (!file.Exists)
                {
                    throw new FileNotFoundException("The local audio file is missing.", localPath);
                }

                var engine = GetEngine();
                using var media = LocalAudioMedia.Create(engine, localPath);
                if (await LocalAudioMedia.ParseAudioTrackAsync(media, cancellationToken).ConfigureAwait(false) is { } track)
                {
                    return LocalAudioMedia.ReadMetadata(media, track, file.Length);
                }

                throw new InvalidDataException("The local file does not expose supported audio metadata.");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Initializes an isolated parser with network metadata and device output disabled.</summary>
        /// <returns>The parser engine owned by this reader.</returns>
        /// <exception cref="NotSupportedException">The native runtime cannot be loaded.</exception>
        private LibVLC GetEngine()
        {
            if (_engine is not null)
            {
                return _engine;
            }

            try
            {
                LibVLCSharp.Shared.Core.Initialize(_nativeLibraryDirectory);
                _engine = new LibVLC("--ignore-config", "--no-video", "--no-spu", "--no-sub-autodetect-file",
                    "--no-lua", "--no-metadata-network-access", "--no-auto-preparse", "--no-media-library",
                    "--services-discovery=", "--aout=dummy", "--vout=dummy", "--quiet");
                return _engine;
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException
                or TypeInitializationException or VLCException)
            {
                throw new NotSupportedException("Local audio metadata parsing is unavailable.", exception);
            }
        }

        /// <summary>Releases the parser after its current native read has completed.</summary>
        /// <returns>A task representing completed engine release.</returns>
        private async Task DisposeCoreAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => _engine?.Dispose()).ConfigureAwait(false);
                _engine = null;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
