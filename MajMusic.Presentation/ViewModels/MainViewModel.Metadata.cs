#nullable enable

using System.Collections.ObjectModel;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Progressively populates visible music rows without borrowing the active playback engine.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Bounds each visible page and its background metadata work.</summary>
        private int LibraryPageSize { get; set; } = 8;
        /// <summary>Reads cached local audio properties independently of playback.</summary>
        private readonly ITrackMetadataProvider? _metadata;
        /// <summary>Protects metadata cancellation ownership across refresh and shutdown.</summary>
        private readonly object _metadataCancellationGate = new();
        /// <summary>Cancels work that belongs to an obsolete visible page.</summary>
        private CancellationTokenSource? _metadataCancellation;
        /// <summary>Exposes the latest observed refresh for deterministic validation.</summary>
        private Task _metadataRefresh = Task.CompletedTask;
        /// <summary>Avoids repeatedly requesting absent optional fields during one view lifetime.</summary>
        private readonly HashSet<string> _remoteMetadataChecked = new(StringComparer.Ordinal);
        /// <summary>Stores the currently visible zero-based library page.</summary>
        private int _libraryPageIndex;
        /// <summary>Identifies the last authoritative playback metadata published to the library.</summary>
        private (string? Id, string? Revision, TimeSpan? Duration, AudioStreamInfo? Info) _publishedPlaybackMetadata;

        /// <summary>Gets the current page of searchable entries.</summary>
        public ObservableCollection<MusicTrack> VisibleTracks { get; } = new();
        /// <summary>Gets or sets a visible row without clearing the inspector during page replacement.</summary>
        public MusicTrack? SelectedVisibleTrack
        {
            get => VisibleTracks.FirstOrDefault(item => item.Id == SelectedTrack?.Id);
            set
            {
                if (value is not null)
                {
                    SelectedTrack = value;
                }
            }
        }
        /// <summary>Gets the number of music rows that fit the current content viewport.</summary>
        public int VisiblePageSize => LibraryPageSize;

        /// <summary>Fits a complete page of fixed-height rows without changing the surrounding layout.</summary>
        /// <param name="height">The actual library-list height in logical pixels.</param>
        public void SetLibraryViewportHeight(double height)
        {
            if (!double.IsFinite(height) || height <= 0 || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            var capacity = Math.Clamp((int)Math.Floor(Math.Max(0, height - 12) / 52), 1, 100);
            if (capacity == LibraryPageSize)
            {
                return;
            }

            var first = _libraryPageIndex * LibraryPageSize;
            LibraryPageSize = capacity;
            _libraryPageIndex = Math.Clamp(first / capacity, 0, Math.Max(0, (Tracks.Count - 1) / capacity));
            OnPropertyChanged(nameof(VisiblePageSize));
            RefreshVisibleTracks();
        }
        /// <summary>Gets the latest cancellable background metadata refresh.</summary>
        public Task MetadataRefresh => _metadataRefresh;
        /// <summary>Gets whether navigation is needed for the current result set.</summary>
        public bool HasLibraryPages => Tracks.Count > LibraryPageSize;
        /// <summary>Gets the command opening the preceding result page.</summary>
        public AsyncCommand PreviousLibraryPageCommand { get; private set; } = null!;
        /// <summary>Gets the command opening the next result page.</summary>
        public AsyncCommand NextLibraryPageCommand { get; private set; } = null!;

        /// <summary>Creates result navigation bound to the current search snapshot.</summary>
        private void InitializeMetadataCommands()
        {
            PreviousLibraryPageCommand = CreateCommand(() => ChangeLibraryPageAsync(-1), () => _libraryPageIndex > 0);
            NextLibraryPageCommand = CreateCommand(() => ChangeLibraryPageAsync(1),
                () => (_libraryPageIndex + 1) * LibraryPageSize < Tracks.Count);
        }

        /// <summary>Changes the visible result page without changing playback queue ownership.</summary>
        /// <param name="offset">The signed page offset.</param>
        /// <returns>A completed task after rebinding the visible snapshot.</returns>
        private Task ChangeLibraryPageAsync(int offset)
        {
            var maximum = Math.Max(0, (Tracks.Count - 1) / LibraryPageSize);
            _libraryPageIndex = Math.Clamp(_libraryPageIndex + offset, 0, maximum);
            RefreshVisibleTracks();
            return Task.CompletedTask;
        }

        /// <summary>Shows one bounded result page and starts observed background enrichment.</summary>
        /// <param name="enrich">Whether this page owns independent metadata work.</param>
        private void RefreshVisibleTracks(bool enrich = true)
        {
            CancelMetadataRefresh();
            VisibleTracks.Clear();
            foreach (var track in Tracks.Skip(_libraryPageIndex * LibraryPageSize).Take(LibraryPageSize))
            {
                VisibleTracks.Add(track);
            }

            OnPropertyChanged(nameof(LibrarySummary));
            OnPropertyChanged(nameof(HasLibraryPages));
            OnPropertyChanged(nameof(LibraryViewTitle));
            OnPropertyChanged(nameof(IsRecommendationsView));
            OnPropertyChanged(nameof(HasMoreRecommendations));
            OnPropertyChanged(nameof(SelectedVisibleTrack));
            RefreshCommands();
            if (!enrich || _metadata is null && _onlineWorkspace is null)
            {
                return;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_metadataCancellationGate)
            {
                _metadataCancellation = cancellation;
            }

            var tracks = VisibleTracks.ToArray();
            // Publish completion before a parser can signal that work has begun.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _metadataRefresh = RunTrackedAsync(async () =>
            {
                await start.Task;
                await RefreshMetadataAsync(tracks, cancellation);
            });
            start.SetResult();
        }

        /// <summary>Enriches one bounded page and isolates unreadable media from unrelated rows.</summary>
        /// <param name="tracks">The captured visible page.</param>
        /// <param name="cancellation">The page-owned cancellation source.</param>
        /// <returns>A task representing all metadata work for that page.</returns>
        private async Task RefreshMetadataAsync(IReadOnlyList<MusicTrack> tracks, CancellationTokenSource cancellation)
        {
            try
            {
                var failedSources = new HashSet<string>(StringComparer.Ordinal);
                foreach (var track in tracks.DistinctBy(item => item.Id).OrderBy(item => item.Audio.LocalPath is null ? 1 : 0))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var updated = track;
                        if (_metadata is not null)
                        {
                            updated = await EnrichLocalMetadataAsync(updated, cancellation.Token);
                        }
                        cancellation.Token.ThrowIfCancellationRequested();
                        updated = PreferDownloadedMetadata(updated);
                        if (updated != track)
                        {
                            ApplyTrackMetadata(updated);
                        }

                        var remoteKey = track.Id + "\n" + track.Revision;
                        var sourceKey = track.EndpointId ?? track.SourceId;
                        if (track.RemoteId is not null && OnlineEnabled && _onlineWorkspace is not null
                            && (updated.Duration is null || updated.AudioInfo is null) && !_remoteMetadataChecked.Contains(remoteKey)
                            && !failedSources.Contains(sourceKey))
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(8));
                            try
                            {
                                var remote = await _onlineWorkspace.EnrichTrackMetadataAsync(updated, timeout.Token);
                                updated = updated with
                                {
                                    Duration = updated.Duration ?? remote.Duration,
                                    AudioInfo = updated.AudioInfo ?? remote.AudioInfo
                                };
                                _remoteMetadataChecked.Add(remoteKey);
                            }
                            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                            {
                                failedSources.Add(sourceKey);
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                failedSources.Add(sourceKey);
                                // Cached local audio remains useful when this endpoint cannot return metadata.
                            }
                        }

                        cancellation.Token.ThrowIfCancellationRequested();
                        updated = PreferDownloadedMetadata(updated);
                        if (updated != track)
                        {
                            if (_onlineWorkspace is not null)
                            {
                                await _onlineWorkspace.UpdateTrackMetadataAsync(updated, cancellation.Token);
                            }

                            ApplyTrackMetadata(updated);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                        // Disabling the network may cancel one source read while local rows remain usable.
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // An individual unreadable file or unavailable source keeps its existing row metadata.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A newer page, search, or shutdown superseded this bounded refresh.
            }
            finally
            {
                lock (_metadataCancellationGate)
                {
                    if (ReferenceEquals(_metadataCancellation, cancellation))
                    {
                        _metadataCancellation = null;
                    }
                }

                cancellation.Dispose();
            }
        }

        /// <summary>Validates online and archived media ownership before a local metadata provider can open its path.</summary>
        /// <param name="track">The captured source revision or completed-download candidate.</param>
        /// <param name="cancellationToken">Cancels validation and parsing with the original page, card or download lifecycle.</param>
        /// <returns>The owned or sanitized descriptor with any available local metadata; explicit imports retain their original source paths.</returns>
        private async Task<MusicTrack> EnrichLocalMetadataAsync(MusicTrack track, CancellationToken cancellationToken)
        {
            var candidate = track;
            if ((track.RemoteId is not null || track.SourceId == "local/archive") && _workspace is ILocalTrackMediaResolver resolver)
            {
                candidate = await resolver.ResolveLocalTrackAsync(track, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return _metadata is { } provider
                ? await provider.EnrichAsync(candidate, cancellationToken).ConfigureAwait(false) : candidate;
        }

        /// <summary>Replaces matching visible metadata while preserving selected and queued entry identities.</summary>
        /// <param name="updated">The immutable metadata result.</param>
        /// <param name="completedAudio">Whether the result also supplies a validated completed audio path.</param>
        private void ApplyTrackMetadata(MusicTrack updated, bool completedAudio = false)
        {
            if (Volatile.Read(ref _isDisposing))
            {
                return;
            }

            MusicTrack? selected = null;
            var key = (updated.Id, updated.SourceId, updated.EndpointId);
            foreach (var page in _browsePages)
            {
                if (page.Snapshot?.Indexes.TryGetValue(key, out var index) == true && SameMetadataOwner(page.Tracks[index], updated))
                {
                    var merged = MergeMetadataUpdate(page.Tracks[index], updated, completedAudio);
                    page.Snapshot.Tracks[index] = merged;
                    page.Tracks[index] = merged;
                    if (ReferenceEquals(page, ActiveBrowsePage) && SelectedTrack is { } selection && SameMetadataOwner(selection, updated))
                    {
                        selected = merged;
                    }
                }
            }

            for (var index = 0; index < VisibleTracks.Count; index++)
            {
                if (SameMetadataOwner(VisibleTracks[index], updated))
                {
                    VisibleTracks[index] = MergeMetadataUpdate(VisibleTracks[index], updated, completedAudio);
                }
            }

            if (_recommendationTracks is not null)
            {
                for (var index = 0; index < _recommendationTracks.Count; index++)
                {
                    if (SameMetadataOwner(_recommendationTracks[index], updated))
                    {
                        _recommendationTracks[index] = MergeMetadataUpdate(_recommendationTracks[index], updated, completedAudio);
                    }
                }
            }

            if (selected is null && SelectedTrack is { } inspected && SameMetadataOwner(inspected, updated))
            {
                selected = MergeMetadataUpdate(inspected, updated, completedAudio);
            }

            if (selected is not null)
            {
                SelectedTrack = selected;
            }

            RefreshCardTrackMetadata(updated);
            if (completedAudio && _cards.TryGetValue((updated.Id, updated.SourceId, updated.EndpointId), out var card)
                && SameMetadataOwner(card.Track, updated))
            {
                card.UpdateTrack(MergeMetadataUpdate(card.Track, updated, true));
            }
        }

        /// <summary>Checks that a metadata result still belongs to the same indexed source revision.</summary>
        /// <param name="current">The current row.</param>
        /// <param name="incoming">The captured metadata result.</param>
        /// <returns>Whether the row can accept those properties.</returns>
        private static bool SameMetadataOwner(MusicTrack current, MusicTrack incoming)
        {
            return current.Id == incoming.Id && current.SourceId == incoming.SourceId
                && current.Revision == incoming.Revision && current.EndpointId == incoming.EndpointId;
        }

        /// <summary>Retains current resource ownership while accepting a completed download's validated local path.</summary>
        /// <param name="current">The latest indexed descriptor.</param>
        /// <param name="incoming">The parsed audio properties.</param>
        /// <param name="completedAudio">Whether this update owns a completed local audio resource.</param>
        /// <returns>The descriptor enriched without changing its title, cover or source addresses.</returns>
        private static MusicTrack MergeMetadataUpdate(MusicTrack current, MusicTrack incoming, bool completedAudio)
        {
            var merged = MergeRowMetadata(current, incoming);
            return completedAudio && incoming.Audio.LocalPath is { } path
                ? merged with { Audio = current.Audio with { LocalPath = path } } : merged;
        }

        /// <summary>Merges only audio properties so an old playback object cannot replace current source descriptors.</summary>
        /// <param name="current">The row retaining current title and resource ownership.</param>
        /// <param name="incoming">The parsed metadata.</param>
        /// <returns>The current row with refreshed audio properties.</returns>
        private static MusicTrack MergeRowMetadata(MusicTrack current, MusicTrack incoming)
        {
            return current with
            {
                Duration = incoming.Duration,
                AudioInfo = incoming.AudioInfo,
                AudioMetadataLength = incoming.AudioMetadataLength ?? current.AudioMetadataLength,
                AudioMetadataLastWriteUtcTicks = incoming.AudioMetadataLastWriteUtcTicks ?? current.AudioMetadataLastWriteUtcTicks
            };
        }

        /// <summary>Cancels the previous visible page without disposing a source still used by its parser.</summary>
        private void CancelMetadataRefresh()
        {
            lock (_metadataCancellationGate)
            {
                _metadataCancellation?.Cancel();
            }
        }

        /// <summary>Copies authoritative native playback properties back into searchable music rows.</summary>
        /// <param name="snapshot">The currently owned native media state.</param>
        private void PublishPlaybackMetadata(PlaybackSnapshot snapshot)
        {
            if (snapshot.Track is not { } track || snapshot.AudioInfo is null
                || snapshot.State is PlaybackState.Loading or PlaybackState.Failed)
            {
                return;
            }

            var key = (track.Id, track.Revision, snapshot.Duration, snapshot.AudioInfo);
            if (_publishedPlaybackMetadata == key)
            {
                return;
            }

            _publishedPlaybackMetadata = key;
            var updated = track with { Duration = snapshot.Duration ?? track.Duration, AudioInfo = snapshot.AudioInfo };
            RememberDownloadedPlaybackMetadata(updated);
            ApplyTrackMetadata(updated);
            if (_onlineWorkspace is not null)
            {
                _ = RunTrackedAsync(async () =>
                {
                    try
                    {
                        await _onlineWorkspace.UpdateTrackMetadataAsync(updated, _lifetime.Token);
                    }
                    catch (Exception)
                    {
                        // The playback owner remains authoritative when persistence is unavailable or closing.
                    }
                });
            }
        }
    }
}
