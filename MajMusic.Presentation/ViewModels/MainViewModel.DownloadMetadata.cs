#nullable enable

using MajMusic.Core.Models;
using MajMusic.Core.Playback;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Parses completed local audio immediately without waiting for another viewport or playback action.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Bounds retained parsed download overlays independently of catalog size.</summary>
        private const int DownloadMetadataCapacity = 64;
        /// <summary>Coalesces only the latest completed resource for each source revision.</summary>
        private readonly Dictionary<CardDownloadOwner, DownloadMetadataRequest> _downloadMetadata = new();
        /// <summary>Orders retained overlays for bounded eviction.</summary>
        private long _downloadMetadataStamp;
        /// <summary>Exposes the latest observed local metadata operation.</summary>
        private Task _downloadMetadataRefresh = Task.CompletedTask;

        /// <summary>Gets completion of the latest metadata probe triggered by a completed audio resource.</summary>
        public Task DownloadMetadataRefresh => _downloadMetadataRefresh;
        /// <summary>Gets the bounded number of local-read owners retained for catalog publication.</summary>
        internal int RetainedDownloadMetadataCount => _downloadMetadata.Count;

        /// <summary>Starts local parsing after an accepted terminal transfer event and rejects superseded operations.</summary>
        /// <param name="owner">The exact source, endpoint and revision receiving the event.</param>
        /// <param name="progress">The accepted operation's current state.</param>
        private void ObserveDownloadedMetadata(CardDownloadOwner owner, AudioDownloadProgress progress)
        {
            if (_metadata is null)
            {
                return;
            }

            _downloadMetadata.TryGetValue(owner, out var previous);
            if (progress.State != AudioDownloadState.Completed || progress.LocalAudioPath is not { Length: > 0 } path)
            {
                if (progress.State == AudioDownloadState.Preparing && previous is { Result: null }
                    && previous.OperationId != progress.OperationId)
                {
                    CancelDownloadMetadataRequest(previous);
                    _downloadMetadata.Remove(owner);
                }
                return;
            }
            if (previous is not null && previous.Path == path
                && (previous.Result is not null || previous.OperationId == progress.OperationId))
            {
                previous.Stamp = ++_downloadMetadataStamp;
                _downloadMetadataRefresh = previous.Completion;
                return;
            }

            if (previous is not null)
            {
                CancelDownloadMetadataRequest(previous);
                _downloadMetadata.Remove(owner);
            }
            while (_downloadMetadata.Count >= DownloadMetadataCapacity)
            {
                var playingOwner = _playback.Snapshot.Track is { } playing ? DownloadOwner(playing) : null;
                var oldest = _downloadMetadata.Where(item => item.Key != playingOwner).MinBy(item => item.Value.Stamp);
                CancelDownloadMetadataRequest(oldest.Value);
                _downloadMetadata.Remove(oldest.Key);
            }

            MusicTrack? track = null;
            if (_cardIndexes.TryGetValue((owner.TrackId, owner.SourceId, owner.EndpointId), out var index)
                && DownloadOwner(Tracks[index]) == owner)
            {
                track = Tracks[index];
            }
            track ??= SelectedTrack is { } selected && DownloadOwner(selected) == owner ? selected : null;
            track ??= _playback.Queue.Tracks.FirstOrDefault(item => DownloadOwner(item) == owner);
            track ??= new MusicTrack
            {
                Id = owner.TrackId,
                Title = progress.Title,
                SourceId = owner.SourceId,
                EndpointId = owner.EndpointId,
                Revision = owner.Revision,
                Audio = new MediaResource()
            };
            track = track with { Audio = track.Audio with { LocalPath = path } };
            var request = new DownloadMetadataRequest(progress.OperationId, path, ++_downloadMetadataStamp,
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _downloadMetadata.Add(owner, request);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            request.Completion = _downloadMetadataRefresh = RunTrackedAsync(async () =>
            {
                await start.Task;
                await EnrichDownloadedMetadataAsync(owner, track, request);
            });
            start.SetResult();
        }

        /// <summary>Reads the completed file through the host's local metadata provider and publishes only its current owner.</summary>
        /// <param name="owner">The captured source revision.</param>
        /// <param name="track">The descriptor containing the validated completed audio path.</param>
        /// <param name="request">The bounded operation whose replacement cancels publication.</param>
        /// <returns>A task representing parsing, immediate UI publication and local persistence.</returns>
        private async Task EnrichDownloadedMetadataAsync(CardDownloadOwner owner, MusicTrack track, DownloadMetadataRequest request)
        {
            var token = request.Cancellation.Token;
            try
            {
                var updated = await EnrichLocalMetadataAsync(track, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!SameMetadataOwner(track, updated))
                {
                    return;
                }
                var publication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _dispatch(() =>
                {
                    try
                    {
                        if (Volatile.Read(ref _isDisposing) || token.IsCancellationRequested
                            || !_downloadMetadata.TryGetValue(owner, out var current) || !ReferenceEquals(current, request))
                        {
                            publication.TrySetResult(false);
                            return;
                        }
                        request.Result = updated;
                        ApplyTrackMetadata(updated, true);
                        if (_playbackSnapshot.Track is { } playing && SameMetadataOwner(playing, updated))
                        {
                            _playbackSnapshot = WithDownloadedPlaybackMetadata(_playback.Snapshot);
                            NotifyTimeline();
                            OnPropertyChanged(nameof(AudioDetails));
                            OnPropertyChanged(nameof(HasAudioDetails));
                        }
                        publication.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        publication.TrySetException(exception);
                    }
                });
                if (await publication.Task.WaitAsync(token).ConfigureAwait(false) && _onlineWorkspace is not null)
                {
                    await _onlineWorkspace.UpdateTrackMetadataAsync(updated, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // A newer completed resource, bounded eviction or shutdown superseded this local read.
            }
            catch (Exception)
            {
                // An unreadable optional audio tag cannot turn a completed download into a failed transfer.
            }
            finally
            {
                request.Cancellation.Dispose();
            }
        }

        /// <summary>Keeps an in-flight catalog snapshot from restoring metadata captured before download completion.</summary>
        /// <param name="snapshot">The about-to-be-published catalog and its source-scoped identity index.</param>
        private void OverlayDownloadedMetadata(BrowseSnapshot snapshot)
        {
            foreach (var request in _downloadMetadata.Values)
            {
                if (request.Result is { } updated
                    && snapshot.Indexes.TryGetValue((updated.Id, updated.SourceId, updated.EndpointId), out var index)
                    && SameMetadataOwner(snapshot.Tracks[index], updated))
                {
                    snapshot.Tracks[index] = MergeMetadataUpdate(snapshot.Tracks[index], updated, true);
                }
            }
        }

        /// <summary>Prevents an older uncached-row read from replacing properties parsed from the completed file.</summary>
        /// <param name="incoming">The optional page or viewport result captured before file completion.</param>
        /// <returns>The completed local result when this older descriptor still lacks its current path.</returns>
        private MusicTrack PreferDownloadedMetadata(MusicTrack incoming)
        {
            return _downloadMetadata.TryGetValue(DownloadOwner(incoming), out var request)
                && request.Result is { } completed
                && (incoming.Audio.LocalPath != request.Path || incoming.AudioMetadataLastWriteUtcTicks is null
                    || completed.AudioMetadataLastWriteUtcTicks is { } completedWrite
                        && incoming.AudioMetadataLastWriteUtcTicks < completedWrite)
                ? MergeMetadataUpdate(incoming, completed, true) : incoming;
        }

        /// <summary>Keeps later authoritative native properties in the bounded completed-file overlay.</summary>
        /// <param name="updated">The current playback resource and its actual decoder properties.</param>
        private void RememberDownloadedPlaybackMetadata(MusicTrack updated)
        {
            if (_downloadMetadata.TryGetValue(DownloadOwner(updated), out var request)
                && request.Result is { } completed && updated.Audio.LocalPath == request.Path)
            {
                request.Result = MergeMetadataUpdate(completed, updated, false);
            }
        }

        /// <summary>Fills missing playback metadata without changing the backend, queue or native playback position.</summary>
        /// <param name="snapshot">The latest authoritative backend snapshot.</param>
        /// <returns>The same playback ownership with available parsed properties and preserved native values.</returns>
        private PlaybackSnapshot WithDownloadedPlaybackMetadata(PlaybackSnapshot snapshot)
        {
            if (snapshot.Track is not { } track || !_downloadMetadata.TryGetValue(DownloadOwner(track), out var request)
                || request.Result is not { } metadata || track.Audio.LocalPath is { } loadedPath && loadedPath != metadata.Audio.LocalPath)
            {
                return snapshot;
            }
            var native = snapshot.AudioInfo;
            var parsed = metadata.AudioInfo;
            var info = native is null ? parsed : parsed is null ? native : native with
            {
                CodecName = native.CodecName ?? parsed.CodecName,
                BitrateBitsPerSecond = native.BitrateBitsPerSecond ?? parsed.BitrateBitsPerSecond,
                IsBitrateEstimated = native.BitrateBitsPerSecond is not null ? native.IsBitrateEstimated : parsed.IsBitrateEstimated,
                SampleRateHz = native.SampleRateHz ?? parsed.SampleRateHz,
                Channels = native.Channels ?? parsed.Channels,
                FileSizeBytes = native.FileSizeBytes ?? parsed.FileSizeBytes
            };
            if (info == _playbackSnapshot.AudioInfo)
            {
                info = _playbackSnapshot.AudioInfo;
            }
            return snapshot with
            {
                Track = MergeMetadataUpdate(track, metadata, true),
                Duration = snapshot.Duration > TimeSpan.Zero ? snapshot.Duration : metadata.Duration,
                AudioInfo = info
            };
        }

        /// <summary>Cancels pending local probes before the metadata provider is drained and disposed.</summary>
        private void CancelDownloadedMetadata()
        {
            foreach (var request in _downloadMetadata.Values)
            {
                CancelDownloadMetadataRequest(request);
            }
            _downloadMetadata.Clear();
        }

        /// <summary>Cancels a request that may already have released its scoped token source.</summary>
        /// <param name="request">The superseded or closing metadata owner.</param>
        private static void CancelDownloadMetadataRequest(DownloadMetadataRequest request)
        {
            try
            {
                request.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The completed overlay can outlive its local read's cancellation source.
            }
        }

        /// <summary>Retains one bounded completed-file overlay and its observed local read.</summary>
        /// <param name="OperationId">The audio operation owning the local path.</param>
        /// <param name="Path">The validated completed audio resource.</param>
        /// <param name="Stamp">The bounded retention order.</param>
        /// <param name="Cancellation">The owner canceled by replacement or shutdown.</param>
        private sealed record DownloadMetadataRequest(string OperationId, string Path, long Stamp, CancellationTokenSource Cancellation)
        {
            /// <summary>Gets or sets the recent-use order independently of the immutable request identity.</summary>
            public long Stamp { get; set; } = Stamp;
            /// <summary>Gets or sets the latest accepted local metadata result.</summary>
            public MusicTrack? Result { get; set; }
            /// <summary>Gets or sets completion of parsing and persistence.</summary>
            public Task Completion { get; set; } = Task.CompletedTask;
        }
    }
}
