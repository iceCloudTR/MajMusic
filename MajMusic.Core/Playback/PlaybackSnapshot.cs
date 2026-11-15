#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Playback
{
    /// <summary>Describes the externally observable audio lifecycle.</summary>
    public enum PlaybackState
    {
        /// <summary>No media is loaded.</summary>
        Empty,
        /// <summary>A media resource is being prepared.</summary>
        Loading,
        /// <summary>The backend is advancing its media clock.</summary>
        Playing,
        /// <summary>Playback is paused at the retained position.</summary>
        Paused,
        /// <summary>Playback was stopped explicitly or reached the queue boundary.</summary>
        Stopped,
        /// <summary>The backend reached the natural end of its current media.</summary>
        Completed,
        /// <summary>The latest playback operation failed.</summary>
        Failed
    }

    /// <summary>Describes audio operations supported by a platform backend.</summary>
    [Flags]
    public enum PlaybackCapabilities
    {
        /// <summary>No audio backend is available.</summary>
        None = 0,
        /// <summary>The backend can open, play, pause and stop audio.</summary>
        Playback = 1,
        /// <summary>The backend can seek within loaded audio.</summary>
        Seeking = 2
    }

    /// <summary>Provides an immutable view of backend-clock playback state.</summary>
    public sealed record PlaybackSnapshot
    {
        /// <summary>Gets the current lifecycle state.</summary>
        public PlaybackState State { get; init; }

        /// <summary>Gets the successfully loaded music entry, if any.</summary>
        public MusicTrack? Track { get; init; }

        /// <summary>Gets the media position reported by the audio backend.</summary>
        public TimeSpan Position { get; init; }

        /// <summary>Gets the duration reported by the backend, or null when not yet known.</summary>
        public TimeSpan? Duration { get; init; }

        /// <summary>Gets the parsed audio properties retained independently of timeline polling.</summary>
        public AudioStreamInfo? AudioInfo { get; init; }

        /// <summary>Gets a non-secret failure description, if an operation failed.</summary>
        public string? Error { get; init; }
    }
}
