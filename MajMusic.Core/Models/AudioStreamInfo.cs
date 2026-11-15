#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Describes parsed audio properties without inferring a quality or constant-bitrate classification.</summary>
    public sealed record AudioStreamInfo
    {
        /// <summary>Gets the decoder's human-readable codec description, or null when unknown.</summary>
        public string? CodecName { get; init; }

        /// <summary>Gets the stream metadata bitrate in bits per second, or the explicitly marked file average estimate.</summary>
        public long? BitrateBitsPerSecond { get; init; }

        /// <summary>Gets whether bitrate was estimated from complete file length and parsed duration.</summary>
        /// <remarks>An estimate includes container overhead and does not identify constant or variable bitrate encoding.</remarks>
        public bool IsBitrateEstimated { get; init; }

        /// <summary>Gets the parsed audio sample rate in hertz, or null when unknown.</summary>
        public int? SampleRateHz { get; init; }

        /// <summary>Gets the parsed audio channel count, or null when unknown.</summary>
        public int? Channels { get; init; }

        /// <summary>Gets the complete local file length in bytes, or null when unavailable.</summary>
        public long? FileSizeBytes { get; init; }
    }
}
