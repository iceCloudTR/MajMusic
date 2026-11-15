#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Infrastructure.Application
{
    /// <summary>Publishes bounded, source-scoped progress only for explicit audio operations.</summary>
    public sealed partial class MusicWorkspace : IAudioDownloadProgressSource
    {
        /// <summary>Retains the latest operation snapshot without blocking presentation readers.</summary>
        private AudioDownloadProgress? _audioDownloadProgress;

        /// <inheritdoc />
        public AudioDownloadProgress? CurrentAudioDownloadProgress => Volatile.Read(ref _audioDownloadProgress);
        /// <inheritdoc />
        public event EventHandler<AudioDownloadProgress>? AudioDownloadProgressChanged;

        /// <summary>Starts a distinct audio operation before metadata or cache resolution begins.</summary>
        /// <param name="track">The entry owned by the requesting action.</param>
        /// <returns>The progress reporter owned by this audio operation.</returns>
        private AudioDownloadOperation BeginAudioDownload(MusicTrack track)
        {
            var operation = new AudioDownloadOperation(PublishAudioDownloadProgress, new AudioDownloadProgress
            {
                OperationId = Guid.NewGuid().ToString("N"),
                TrackId = track.Id,
                Title = track.Title,
                SourceId = track.SourceId,
                EndpointId = track.EndpointId,
                Revision = track.Revision,
                State = AudioDownloadState.Preparing
            });
            operation.Start();
            return operation;
        }

        /// <summary>Notifies independent observers outside resource and progress locks.</summary>
        /// <param name="progress">The safe operation snapshot to publish.</param>
        private void PublishAudioDownloadProgress(AudioDownloadProgress progress)
        {
            Volatile.Write(ref _audioDownloadProgress, progress);
            var observers = AudioDownloadProgressChanged;
            if (observers is null)
            {
                return;
            }

            foreach (EventHandler<AudioDownloadProgress> observer in observers.GetInvocationList())
            {
                try
                {
                    observer(this, progress);
                }
                catch (Exception)
                {
                    // A presentation observer cannot change audio resolution or another observer's notifications.
                }
            }
        }

        /// <summary>Orders one operation's notifications while retaining at most one pending update.</summary>
        private sealed class AudioDownloadOperation : IProgress<MediaTransferProgress>
        {
            /// <summary>Protects notification ownership without invoking external observers under the lock.</summary>
            private readonly object _gate = new();
            /// <summary>Publishes safe snapshots to the workspace's optional observers.</summary>
            private readonly Action<AudioDownloadProgress> _publish;
            /// <summary>Retains immutable operation and music ownership.</summary>
            private readonly AudioDownloadProgress _initial;
            /// <summary>Retains the latest real transfer measurement for terminal notifications.</summary>
            private MediaTransferProgress? _lastTransfer;
            /// <summary>Coalesces observer backpressure into one newest pending snapshot.</summary>
            private AudioDownloadProgress? _pending;
            /// <summary>Indicates whether one caller owns notification delivery.</summary>
            private bool _draining;
            /// <summary>Rejects late transfer callbacks after a terminal state.</summary>
            private bool _finished;

            /// <summary>Creates an operation-local reporter without starting a media request.</summary>
            /// <param name="publish">The observer-safe workspace publisher.</param>
            /// <param name="initial">The unique requested-track ownership.</param>
            public AudioDownloadOperation(Action<AudioDownloadProgress> publish, AudioDownloadProgress initial)
            {
                _publish = publish;
                _initial = initial;
            }

            /// <summary>Publishes the initial metadata and cache preparation state.</summary>
            public void Start()
            {
                Queue(AudioDownloadState.Preparing, null, null);
            }

            /// <summary>Publishes actual audio transfer bytes without posting delayed callbacks through a UI synchronization context.</summary>
            /// <param name="value">The latest real cache transfer measurement.</param>
            public void Report(MediaTransferProgress value)
            {
                Queue(AudioDownloadState.Downloading, value, null);
            }

            /// <summary>Ends the operation exactly once and rejects any subsequent transfer callbacks.</summary>
            /// <param name="state">The completed, canceled or failed terminal state.</param>
            /// <param name="localAudioPath">The validated local path supplied only after completion.</param>
            public void Finish(AudioDownloadState state, string? localAudioPath = null)
            {
                Queue(state, null, localAudioPath);
            }

            /// <summary>Commits one state while leaving all observer callbacks outside the operation gate.</summary>
            /// <param name="state">The lifecycle state to publish.</param>
            /// <param name="transfer">The newest real transfer measurement, when receiving bytes.</param>
            /// <param name="localAudioPath">The completed validated local path, when available.</param>
            private void Queue(AudioDownloadState state, MediaTransferProgress? transfer, string? localAudioPath)
            {
                lock (_gate)
                {
                    if (_finished)
                    {
                        return;
                    }

                    if (state == AudioDownloadState.Downloading)
                    {
                        _lastTransfer = transfer;
                    }
                    else if (state is AudioDownloadState.Completed or AudioDownloadState.Cancelled or AudioDownloadState.Failed)
                    {
                        _finished = true;
                    }

                    _pending = _initial with
                    {
                        State = state,
                        Transfer = _lastTransfer,
                        LocalAudioPath = state == AudioDownloadState.Completed ? localAudioPath : null
                    };
                    if (_draining)
                    {
                        return;
                    }

                    _draining = true;
                }

                while (true)
                {
                    AudioDownloadProgress next;
                    lock (_gate)
                    {
                        if (_pending is null)
                        {
                            _draining = false;
                            return;
                        }

                        next = _pending;
                        _pending = null;
                    }

                    _publish(next);
                }
            }
        }
    }
}
