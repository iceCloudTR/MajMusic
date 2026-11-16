#nullable enable

using System.Globalization;
using Android.Content;
using Android.Media;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using AudioMetadata = MajMusic.Core.Models.AudioMetadata;

namespace MajMusic.Android.Audio
{
    /// <summary>Reads complete local audio metadata using Android decoders without opening a playback device.</summary>
    public sealed class AndroidAudioMetadataReader : IAudioMetadataReader
    {
        /// <summary>Retains the application context for persisted read-only document references.</summary>
        private readonly Context _context;

        /// <summary>Creates a native parser without retaining an activity.</summary>
        /// <param name="context">The optional context used to access persisted document-provider grants.</param>
        public AndroidAudioMetadataReader(Context? context = null)
        {
            _context = context?.ApplicationContext ?? global::Android.App.Application.Context;
        }

        /// <inheritdoc />
        public Task<AudioMetadata> ReadAsync(string localPath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
            if (!IsContentUri(localPath) && !Path.IsPathFullyQualified(localPath))
            {
                throw new ArgumentException("A complete local audio path is required.", nameof(localPath));
            }

            return Task.Run(() => ReadCore(localPath, cancellationToken), cancellationToken);
        }

        /// <summary>Uses independently owned native parsers on a worker and releases them before publishing results.</summary>
        /// <param name="path">The complete local audio path.</param>
        /// <param name="token">Cancels parsing before or after a native file operation.</param>
        /// <returns>The known audio metadata, with optional fields left absent rather than inferred.</returns>
        /// <exception cref="IOException">The file is unreadable or unsupported by Android media parsers.</exception>
        private AudioMetadata ReadCore(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var content = IsContentUri(path);
            var file = content ? null : new FileInfo(path);
            if (file is { Exists: false })
            {
                throw new FileNotFoundException("The local audio file is unavailable.", path);
            }

            try
            {
                using var retriever = new MediaMetadataRetriever();
                using var uri = content ? global::Android.Net.Uri.Parse(path) : null;
                if (uri is not null)
                {
                    retriever.SetDataSource(_context, uri);
                }
                else
                {
                    retriever.SetDataSource(path);
                }
                token.ThrowIfCancellationRequested();
                var milliseconds = ParseNumber(retriever.ExtractMetadata(MetadataKey.Duration));
                var bitrate = ParseNumber(retriever.ExtractMetadata(MetadataKey.Bitrate));
                var mime = retriever.ExtractMetadata(MetadataKey.Mimetype);
                int? sampleRate = null;
                int? channels = null;
                using var extractor = new MediaExtractor();
                using var descriptor = uri is not null ? _context.ContentResolver?.OpenAssetFileDescriptor(uri, "r") : null;
                if (uri is not null)
                {
                    if (descriptor?.FileDescriptor is not { } fileDescriptor)
                    {
                        throw new IOException("The selected audio document is unavailable.");
                    }

                    if (descriptor.DeclaredLength >= 0)
                    {
                        extractor.SetDataSource(fileDescriptor, descriptor.StartOffset, descriptor.DeclaredLength);
                    }
                    else
                    {
                        extractor.SetDataSource(fileDescriptor);
                    }
                }
                else
                {
                    extractor.SetDataSource(path);
                }
                for (var index = 0; index < extractor.TrackCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    using var format = extractor.GetTrackFormat(index);
                    var trackMime = format.GetString(MediaFormat.KeyMime);
                    if (trackMime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true)
                    {
                        continue;
                    }

                    mime = trackMime;
                    sampleRate = format.ContainsKey(MediaFormat.KeySampleRate) ? format.GetInteger(MediaFormat.KeySampleRate) : null;
                    channels = format.ContainsKey(MediaFormat.KeyChannelCount) ? format.GetInteger(MediaFormat.KeyChannelCount) : null;
                    bitrate ??= format.ContainsKey(MediaFormat.KeyBitRate) ? format.GetInteger(MediaFormat.KeyBitRate) : null;
                    break;
                }

                token.ThrowIfCancellationRequested();
                return new AudioMetadata
                {
                    Duration = milliseconds is > 0 ? TimeSpan.FromMilliseconds(milliseconds.Value) : null,
                    AudioInfo = new AudioStreamInfo
                    {
                        CodecName = mime,
                        BitrateBitsPerSecond = bitrate is > 0 ? bitrate : null,
                        SampleRateHz = sampleRate is > 0 ? sampleRate : null,
                        Channels = channels is > 0 ? channels : null,
                        FileSizeBytes = file?.Length ?? (descriptor?.DeclaredLength is >= 0 ? descriptor.DeclaredLength : null)
                    }
                };
            }
            catch (Java.Lang.RuntimeException exception)
            {
                throw new IOException("Android could not read this audio file.", exception);
            }
        }

        /// <summary>Reads optional nonnegative native integer metadata without culture-specific formatting.</summary>
        /// <param name="value">The native metadata text.</param>
        /// <returns>The parsed positive integer, otherwise null.</returns>
        private static long? ParseNumber(string? value)
        {
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0 ? result : null;
        }

        /// <summary>Recognizes a persisted local document-provider reference.</summary>
        /// <param name="path">The resolved local resource reference.</param>
        /// <returns>Whether the resource belongs to an Android content provider.</returns>
        private static bool IsContentUri(string path)
        {
            return path.StartsWith("content://", StringComparison.OrdinalIgnoreCase);
        }
    }
}
