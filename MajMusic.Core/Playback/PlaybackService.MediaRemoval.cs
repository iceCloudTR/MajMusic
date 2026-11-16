#nullable enable

using MajMusic.Core.Models;

namespace MajMusic.Core.Playback
{
    /// <summary>Invalidates a removed local resource while preserving the current queue for a fresh future resolution.</summary>
    public sealed partial class PlaybackService
    {
        /// <summary>Stops a matching loaded song and clears its native-owner identity before its managed file is removed.</summary>
        /// <param name="track">The exact song and revision whose local file is being removed.</param>
        /// <param name="cancellationToken">Cancels the serialized native command.</param>
        /// <returns>A task representing the invalidated loaded owner with the queue retained.</returns>
        public Task ForgetLocalMediaAsync(MusicTrack track, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(track);
            var generation = CancelPreparation(cancellationToken);
            return ExecuteAsync(async token =>
            {
                if (!IsCurrentIntent(generation))
                {
                    return;
                }

                var loaded = _loadedTrack;
                if (loaded is null || loaded.Id != track.Id || loaded.SourceId != track.SourceId
                    || loaded.EndpointId != track.EndpointId || loaded.Revision != track.Revision)
                {
                    return;
                }

                var previous = Snapshot;
                await _backend.StopAsync(token).ConfigureAwait(false);
                lock (_transitionGate)
                {
                    if (IsCurrentIntent(generation))
                    {
                        Volatile.Write(ref _loadedTrack, null);
                        _stateOverride = previous with
                        {
                            State = PlaybackState.Stopped, Track = null, Position = TimeSpan.Zero,
                            Duration = null, AudioInfo = null, Error = null
                        };
                    }
                }
            }, cancellationToken);
        }
    }
}
