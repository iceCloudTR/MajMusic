#nullable enable

namespace MajMusic.Core.Models
{
    /// <summary>Identifies the lifecycle of one explicit audio-resolution request.</summary>
    public enum AudioDownloadState
    {
        /// <summary>Resolves metadata and checks available local resources.</summary>
        Preparing,
        /// <summary>Receives actual audio response bytes.</summary>
        Downloading,
        /// <summary>Provides a complete validated local audio file.</summary>
        Completed,
        /// <summary>Ends the operation after its owner cancels it.</summary>
        Cancelled,
        /// <summary>Ends the operation without a complete usable audio resource.</summary>
        Failed
    }

    /// <summary>Reports one track-scoped audio operation without exposing addresses, credentials or exception details.</summary>
    public sealed record AudioDownloadProgress
    {
        /// <summary>Gets the unique identity distinguishing overlapping requests for the same track.</summary>
        public required string OperationId { get; init; }
        /// <summary>Gets the requested source-scoped music identity.</summary>
        public required string TrackId { get; init; }
        /// <summary>Gets the music title retained by the requesting playback or download action.</summary>
        public required string Title { get; init; }
        /// <summary>Gets the indexed server or local source owning this operation, when supplied by the host.</summary>
        public string? SourceId { get; init; }
        /// <summary>Gets the independently authenticated endpoint owning this operation.</summary>
        public string? EndpointId { get; init; }
        /// <summary>Gets the source revision requested by this operation.</summary>
        public string? Revision { get; init; }
        /// <summary>Gets the operation lifecycle state.</summary>
        public AudioDownloadState State { get; init; }
        /// <summary>Gets actual transfer properties, or null while resolving or using an existing local resource.</summary>
        public MediaTransferProgress? Transfer { get; init; }
        /// <summary>Gets the validated local audio path only after successful completion.</summary>
        public string? LocalAudioPath { get; init; }
    }
}
