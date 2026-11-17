#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Removes captured local media and invalidates only that song's retained presentation state.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Rejects transfer notifications already queued before an explicit local-file removal.</summary>
        private readonly HashSet<string> _removedMediaOperations = new(StringComparer.Ordinal);

        /// <summary>Checks whether the captured online song can expose the shared local-file removal action.</summary>
        /// <param name="track">The source-scoped song captured by the menu.</param>
        /// <returns>Whether a supporting idle workspace may remove its managed copies.</returns>
        public bool CanRemoveTrackMediaForMenu(MusicTrack track)
        {
            return IsIdle && _workspace is ITrackMediaRemovalWorkspace && track.RemoteId is not null
                && (!_cardDownloadStates.TryGetValue(DownloadOwner(track), out var state)
                    || state.Progress?.State is not (AudioDownloadState.Preparing or AudioDownloadState.Downloading));
        }

        /// <summary>Removes application-managed local files without deleting the source song or playlist references.</summary>
        /// <param name="track">The exact song and revision selected when the menu opened.</param>
        /// <returns>A task representing stopped matching playback, removal and refreshed availability.</returns>
        public Task RemoveTrackMediaFromMenuAsync(MusicTrack track)
        {
            ArgumentNullException.ThrowIfNull(track);
            return RunBusyAsync(async () =>
            {
                if (_workspace is not ITrackMediaRemovalWorkspace removal || track.RemoteId is null)
                {
                    return;
                }

                PrepareLocalMediaRemoval();
                await PrepareTrackMediaRemovalAsync(track);
                var result = await removal.RemoveTrackMediaAsync(track, _lifetime.Token);
                if (HasRemovedLocalMedia(result))
                {
                    InvalidateRemovedMediaPresentations([result]);
                    await ReloadPlaylistsAsync(false);
                    await ReloadLibraryCoreAsync(_lifetime.Token, preservePage: true);
                }

                Status = HasRemovedLocalMedia(result) ? $"已删除本地文件：{track.Title}" : "没有可删除的本地文件";
            });
        }

        /// <summary>Cancels old page work and releases obsolete prepared snapshots once for the whole explicit operation.</summary>
        private void PrepareLocalMediaRemoval()
        {
            CancelMetadataRefresh();
            _homeUpdateCancellation?.Cancel();
            _previewWarmCancellation?.Cancel();
            foreach (var preview in _browsePreviews.Values)
            {
                preview.Dispose();
            }
            _browsePreviews.Clear();
        }

        /// <summary>Releases only a captured song's pending metadata and playback before removing its files.</summary>
        /// <param name="track">The source revision selected for removal.</param>
        /// <returns>A task representing the retired resource users.</returns>
        private async Task PrepareTrackMediaRemovalAsync(MusicTrack track)
        {
            if (_downloadMetadata.Remove(DownloadOwner(track), out var metadata))
            {
                CancelDownloadMetadataRequest(metadata);
                await metadata.Completion.WaitAsync(_lifetime.Token);
            }

            if (_playback.Snapshot.Track is { } playing && SameMetadataOwner(playing, track))
            {
                await _playback.ForgetLocalMediaAsync(track, _lifetime.Token);
            }
        }

        /// <summary>Checks both detached references and removed supplementary managed files.</summary>
        /// <param name="result">The workspace's completed removal.</param>
        /// <returns>Whether at least one selected local media resource changed.</returns>
        private static bool HasRemovedLocalMedia(TrackMediaRemovalResult result)
        {
            return result.AudioRemoved || result.CoverRemoved || result.DeletedFileCount > 0;
        }

        /// <summary>Clears obsolete local paths across bounded snapshots without confusing other source accounts or revisions.</summary>
        /// <param name="results">The completed removals belonging to this explicit operation.</param>
        private void InvalidateRemovedMediaPresentations(IReadOnlyList<TrackMediaRemovalResult> results)
        {
            var byOwner = results.GroupBy(result => DownloadOwner(result.UpdatedTrack))
                .ToDictionary(group => group.Key, group => group.Last());
            foreach (var result in results)
            {
                var updated = result.UpdatedTrack;
                var owner = DownloadOwner(updated);
                if (_cardDownloadStates.TryGetValue(owner, out var state))
                {
                    if (state.Progress is { } progress)
                    {
                        _removedMediaOperations.Add(progress.OperationId);
                    }
                    state.Version++;
                    state.Progress = null;
                    state.IsAvailable = !result.AudioRemoved && state.IsAvailable;
                    state.LocalPath = updated.Audio.LocalPath;
                }
                if (_audioDownloadSource?.CurrentAudioDownloadProgress is { } latest && MatchesCardDownload(updated, latest))
                {
                    _removedMediaOperations.Add(latest.OperationId);
                }
            }
            foreach (var snapshot in _homeSnapshots.Values)
            {
                for (var index = 0; index < snapshot.Tracks.Count; index++)
                {
                    if (byOwner.TryGetValue(DownloadOwner(snapshot.Tracks[index]), out var result))
                    {
                        snapshot.Tracks[index] = result.UpdatedTrack;
                    }
                }
            }

            foreach (var page in _browsePages)
            {
                foreach (var result in byOwner.Values)
                {
                    var updated = result.UpdatedTrack;
                    if (page.Snapshot?.Indexes.TryGetValue((updated.Id, updated.SourceId, updated.EndpointId), out var index) == true
                        && SameMetadataOwner(page.Tracks[index], updated))
                    {
                        page.Snapshot.Tracks[index] = updated;
                        page.Tracks[index] = updated;
                    }
                }
            }
            for (var index = 0; index < VisibleTracks.Count; index++)
            {
                if (byOwner.TryGetValue(DownloadOwner(VisibleTracks[index]), out var result))
                {
                    VisibleTracks[index] = result.UpdatedTrack;
                }
            }

            foreach (var card in _cards.Values.ToArray())
            {
                if (byOwner.TryGetValue(DownloadOwner(card.Track), out var result))
                {
                    card.ApplyMediaRemoval(result);
                }
            }

            if (SelectedTrack is { } selected && byOwner.TryGetValue(DownloadOwner(selected), out var selectedResult))
            {
                CancelCoverLoad();
                if (selectedResult.CoverRemoved)
                {
                    var previous = Cover;
                    Cover = null;
                    _loadedCoverIdentity = null;
                    previous?.Dispose();
                }
                SelectedTrack = selectedResult.UpdatedTrack;
            }
        }
    }
}
