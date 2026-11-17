#nullable enable

using LibVLCSharp.Shared;
using MajMusic.Core.Models;

namespace MajMusic.Audio.Desktop
{
    /// <summary>Creates consistently parsed local media for metadata reads and playback.</summary>
    internal static class LocalAudioMedia
    {
        /// <summary>Opens a local descriptor with the demuxer appropriate for its audio data.</summary>
        /// <param name="engine">The native audio engine owning the descriptor.</param>
        /// <param name="path">The normalized local audio path.</param>
        /// <returns>The native descriptor owned by the caller.</returns>
        internal static Media Create(LibVLC engine, string path)
        {
            var mp3 = IsMp3(path);
            var media = new Media(engine, path, FromType.FromPath, ":no-video", ":no-spu", ":no-sub-autodetect-file");
            if (mp3)
            {
                // The MPEG demuxer can report an Info frame's rate as the stream rate and estimate length from it.
                // Keep automatic detection available on installations without the avformat plugin.
                media.AddOption(":demux=avformat,any");
            }

            return media;
        }

        /// <summary>Parses the first audio stream using a worker continuation outside the native callback.</summary>
        /// <param name="media">The local descriptor to parse.</param>
        /// <param name="cancellationToken">Cancels the native read.</param>
        /// <returns>The first audio stream, if one was parsed.</returns>
        internal static async Task<MediaTrack?> ParseAudioTrackAsync(Media media, CancellationToken cancellationToken)
        {
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
                        return track;
                    }
                }
            }

            return null;
        }

        /// <summary>Reads the same duration and stream properties for playback and library indexing.</summary>
        /// <param name="media">The parsed local descriptor.</param>
        /// <param name="track">The parsed audio stream.</param>
        /// <param name="fileSize">The complete local file length.</param>
        /// <returns>The known duration and audio properties.</returns>
        internal static AudioMetadata ReadMetadata(Media media, MediaTrack track, long fileSize)
        {
            var milliseconds = media.Duration;
            var duration = milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : (TimeSpan?)null;
            long? bitrate = track.Bitrate > 0 ? track.Bitrate : null;
            var estimated = false;
            if (bitrate is null && fileSize > 0 && duration is { } knownDuration)
            {
                var average = fileSize * 8d / knownDuration.TotalSeconds;
                if (average is > 0 and < long.MaxValue)
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

        /// <summary>Recognizes MP3 paths and extensionless cache objects with an MPEG Layer III header.</summary>
        /// <param name="path">The local audio path.</param>
        /// <returns>Whether the file is named or begins with MP3 audio.</returns>
        private static bool IsMp3(string path)
        {
            if (string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using var stream = File.OpenRead(path);
                Span<byte> header = stackalloc byte[10];
                var length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                if (length == header.Length && header[..3].SequenceEqual("ID3"u8)
                    && ((header[6] | header[7] | header[8] | header[9]) & 0x80) == 0)
                {
                    var tagSize = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
                    var footerSize = header[3] == 4 && (header[5] & 0x10) != 0 ? 10 : 0;
                    stream.Position = header.Length + tagSize + footerSize;
                    length = stream.ReadAtLeast(header[..4], 4, throwOnEndOfStream: false);
                }

                return length >= 4 && header[0] == 0xff && (header[1] & 0xe6) == 0xe2
                    && (header[1] & 0x18) != 0x08 && (header[2] & 0xf0) != 0xf0 && (header[2] & 0x0c) != 0x0c;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
