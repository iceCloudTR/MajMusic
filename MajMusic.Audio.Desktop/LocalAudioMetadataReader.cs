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
                using var media = new Media(engine, localPath, FromType.FromPath,
                    ":no-video", ":no-spu", ":no-sub-autodetect-file");
                // Continue away from the native callback before accessing or releasing native descriptors.
                var status = await media.Parse(MediaParseOptions.ParseLocal, 5000, cancellationToken)
                    .ContinueWith(static task => task.GetAwaiter().GetResult(), CancellationToken.None,
                        TaskContinuationOptions.None, TaskScheduler.Default).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (status == MediaParsedStatus.Done)
                {
                    foreach (var track in media.Tracks)
                    {
                        if (track.TrackType == TrackType.Audio)
                        {
                            return ReadMetadata(media, track, file.Length);
                        }
                    }
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

        /// <summary>Copies native stream fields into immutable library metadata.</summary>
        /// <param name="media">The parsed local descriptor.</param>
        /// <param name="track">The first audio stream.</param>
        /// <param name="fileSize">The complete local file length.</param>
        /// <returns>Actual duration and known stream properties.</returns>
        private static AudioMetadata ReadMetadata(Media media, MediaTrack track, long fileSize)
        {
            var duration = media.Duration > 0 ? TimeSpan.FromMilliseconds(media.Duration) : (TimeSpan?)null;
            long? bitrate = track.Bitrate > 0 ? track.Bitrate : null;
            var estimated = false;
            if (bitrate is null && fileSize > 0 && duration is { TotalSeconds: > 0 } knownDuration)
            {
                var average = fileSize * 8d / knownDuration.TotalSeconds;
                if (double.IsFinite(average) && average is > 0 and < long.MaxValue)
                {
                    bitrate = (long)Math.Round(average, MidpointRounding.AwayFromZero);
                    estimated = true;
                }
            }

            var rate = track.Data.Audio.Rate;
            var channels = track.Data.Audio.Channels;
            var codec = media.CodecDescription(TrackType.Audio, track.Codec);
            return new AudioMetadata
            {
                Duration = duration,
                AudioInfo = new AudioStreamInfo
                {
                    CodecName = string.IsNullOrWhiteSpace(codec) ? null : codec,
                    BitrateBitsPerSecond = bitrate,
                    IsBitrateEstimated = estimated,
                    SampleRateHz = rate is > 0 and <= int.MaxValue ? (int)rate : null,
                    Channels = channels is > 0 and <= int.MaxValue ? (int)channels : null,
                    FileSizeBytes = fileSize
                }
            };
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
