#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Reports bytes received during one actual media transfer.</summary>
    public sealed record MediaTransferProgress
    {
        /// <summary>Gets the number of response bytes already written to the temporary file.</summary>
        public long ReceivedBytes { get; init; }

        /// <summary>Gets the expected response length, or null when the source does not provide it.</summary>
        public long? TotalBytes { get; init; }

        /// <summary>Gets the average receive rate, or null before a measurable interval has elapsed.</summary>
        public double? BytesPerSecond { get; init; }

        /// <summary>Gets whether the complete response has been received.</summary>
        public bool IsCompleted { get; init; }
    }
}
