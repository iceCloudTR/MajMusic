#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;

namespace MajMusic.Presentation.ViewModels
{
    public sealed partial class MainViewModel
    {
        private CancellationTokenSource? _majViewOpening;
        private CancellationTokenSource? _majViewLookupCancellation;
        private Task _majViewLookup = Task.CompletedTask;
        private bool _majViewDifficultiesRead;
        private IMajViewPreviewSession? _majViewSession;
        private Uri? _majViewAddress;
        private int _majViewDifficulty;
        private int _majViewDirection = 1;
        private bool _showPreviewVideo = true;
        private bool _canRetryMajView;
        private string _majViewStatus = string.Empty;
        private IReadOnlyList<ChartPreviewDifficulty> _majViewDifficulties = [];

        /// <summary>Gets or sets whether prepared preview sessions include background video.</summary>
        public bool ShowPreviewVideo
        {
            get => _showPreviewVideo;
            set => SetField(ref _showPreviewVideo, value);
        }

        /// <summary>Gets the original playable difficulty slots in their display order.</summary>
        public IReadOnlyList<ChartPreviewDifficulty> MajViewDifficulties
        {
            get => _majViewDifficulties;
            private set => SetField(ref _majViewDifficulties, value);
        }

        /// <summary>Gets the selected one-based slot, or zero outside the preview.</summary>
        public int SelectedMajViewDifficulty
        {
            get => _majViewDifficulty;
            private set => SetField(ref _majViewDifficulty, value);
        }

        /// <summary>Gets the current session address with the selected difficulty.</summary>
        public Uri? MajViewAddress => _majViewAddress;

        /// <summary>Gets the current inline preview status.</summary>
        public string MajViewStatus
        {
            get => _majViewStatus;
            private set => SetField(ref _majViewStatus, value);
        }

        /// <summary>Gets whether a failed, explicit preview request can be retried.</summary>
        public bool CanRetryMajView
        {
            get => _canRetryMajView;
            private set => SetField(ref _canRetryMajView, value);
        }

        /// <summary>Chooses a real chart slot and prepares it without starting its playback.</summary>
        /// <param name="number">The one-based slot from the difficulty tabs.</param>
        /// <returns>A task representing the updated inline preview.</returns>
        public Task SelectMajViewDifficultyAsync(int number)
        {
            if (_isDisposing || !MajViewDifficulties.Any(item => item.Number == number))
            {
                return Task.CompletedTask;
            }
            var previous = SelectedMajViewDifficulty;
            if (previous == number && _majViewSession is not null && IsSongChartPreviewSelected)
            {
                return Task.CompletedTask;
            }
            _majViewDifficulty = number;
            if (previous != number)
            {
                _majViewDirection = previous > number ? -1 : 1;
            }
            IsSongChartPreviewSelected = true;
            if (previous != number)
            {
                OnPropertyChanged(nameof(SelectedMajViewDifficulty));
            }
            CanRetryMajView = false;
            if (_majViewSession is not null)
            {
                PublishMajViewAddress();
                return Task.CompletedTask;
            }
            return OpenMajViewAsync();
        }

        internal Task OpenDefaultMajViewAsync()
        {
            var original = SelectedTrack;
            IsSongDetailsExpanded = true;
            IsSongChartPreviewSelected = true;
            return RunTrackedAsync(async () =>
            {
                if (SelectedMajViewDifficulty > 0)
                {
                    await SelectMajViewDifficultyAsync(SelectedMajViewDifficulty);
                    return;
                }
                CanRetryMajView = false;
                MajViewStatus = "正在读取难度";
                await EnsureMajViewDifficultiesAsync();
                if (!IsSongDetailsExpanded || !IsSongChartPreviewSelected || !SamePreviewTrack(original, SelectedTrack)
                    || SelectedMajViewDifficulty != 0 || _isDisposing)
                {
                    return;
                }
                if (MajViewDifficulties.LastOrDefault() is { } difficulty)
                {
                    await SelectMajViewDifficultyAsync(difficulty.Number);
                }
                else
                {
                    MajViewStatus = "没有可预览的难度";
                    CanRetryMajView = !_majViewDifficultiesRead && _workspace is IChartPreviewWorkspace;
                }
            });
        }

        internal Task EnsureMajViewDifficultiesAsync()
        {
            if (_majViewDifficultiesRead || !IsSongDetailsExpanded || _isDisposing
                || SelectedTrack is not { } track || _workspace is not IChartPreviewWorkspace source)
            {
                return Task.CompletedTask;
            }
            if (_majViewLookupCancellation is { IsCancellationRequested: false })
            {
                return _majViewLookup;
            }
            var lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _majViewLookupCancellation = lookup;
            _majViewLookup = RunTrackedAsync(async () =>
            {
                using (lookup)
                {
                    try
                    {
                        var levels = await source.GetChartPreviewDifficultiesAsync(track, lookup.Token);
                        lookup.Token.ThrowIfCancellationRequested();
                        _majViewDifficultiesRead = true;
                        MajViewDifficulties = levels.Where(item => item.Number is >= 1 and <= 7)
                            .DistinctBy(item => item.Number).OrderBy(item => item.Number).ToArray();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception)
                    {
                        if (!lookup.IsCancellationRequested)
                        {
                            _majViewDifficultiesRead = false;
                        }
                    }
                    finally
                    {
                        if (ReferenceEquals(_majViewLookupCancellation, lookup))
                        {
                            _majViewLookupCancellation = null;
                        }
                    }
                }
            });
            return _majViewLookup;
        }

        private Task OpenMajViewAsync()
        {
            return RunTrackedAsync(async () =>
            {
                if (_majViewOpening is { IsCancellationRequested: false } || SelectedTrack is not { } track
                    || _workspace is not IMajViewPreviewHost host)
                {
                    return;
                }
                using var opening = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _majViewOpening = opening;
                IMajViewPreviewSession? session = null;
                try
                {
                    MajViewStatus = "正在加载谱面";
                    if (_playback.Snapshot.State is PlaybackState.Playing or PlaybackState.Loading)
                    {
                        await _playback.PauseAsync(opening.Token);
                    }
                    opening.Token.ThrowIfCancellationRequested();
                    await _returnToMusic;
                    opening.Token.ThrowIfCancellationRequested();
                    session = await host.CreateMajViewPreviewAsync(track, ShowPreviewVideo, opening.Token);
                    opening.Token.ThrowIfCancellationRequested();
                    session.FrameReceived += OnChartFrame;
                    _majViewSession = session;
                    session = null;
                    MajViewStatus = string.Empty;
                    PublishMajViewAddress();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    if (!opening.IsCancellationRequested)
                    {
                        MajViewStatus = exception is IOException or InvalidOperationException or NotSupportedException
                            ? exception.Message : "谱面预览加载失败";
                        CanRetryMajView = true;
                    }
                }
                finally
                {
                    if (ReferenceEquals(_majViewOpening, opening))
                    {
                        _majViewOpening = null;
                    }
                    if (session is not null)
                    {
                        await session.DisposeAsync();
                    }
                }
            });
        }

        private void PublishMajViewAddress()
        {
            _chartGeneration++;
            _chartPlaying = false;
            _resumeAfterScrub = false;
            _chartReady = false;
            _chartSeekRevision++;
            _noteTimeline = _levelNotes.GetValueOrDefault(SelectedMajViewDifficulty);
            PublishChartIntent();
            NotifyChartOwner();
            NotifyChartAnalysis();
            var builder = new UriBuilder(_majViewSession!.Address)
            {
                Query = FormattableString.Invariant($"level={SelectedMajViewDifficulty}&direction={_majViewDirection}&g={_chartGeneration}")
            };
            SetField(ref _majViewAddress, builder.Uri, nameof(MajViewAddress));
        }

        /// <summary>Closes the inline preview without changing the main music queue.</summary>
        public void CloseMajView()
        {
            if (IsSongChartPreviewSelected)
            {
                IsSongChartPreviewSelected = false;
            }
            else
            {
                CancelMajView();
            }
        }

        internal void ReportMajViewFailure(Uri address, string message)
        {
            if (_majViewAddress == address)
            {
                _ = RunTrackedAsync(ReleaseMajViewSessionAsync);
                MajViewStatus = message;
                CanRetryMajView = true;
            }
        }

        private void ResetMajViewTrack()
        {
            _majViewLookupCancellation?.Cancel();
            _majViewDifficultiesRead = false;
            MajViewDifficulties = [];
            CloseMajView();
            if (IsSongDetailsExpanded)
            {
                _ = EnsureMajViewDifficultiesAsync();
            }
        }

        private void CancelMajView()
        {
            _majViewOpening?.Cancel();
            SelectedMajViewDifficulty = 0;
            CanRetryMajView = false;
            MajViewStatus = string.Empty;
            if (_majViewSession is not null || _chartTrack is not null)
            {
                _ = RunTrackedAsync(ReleaseMajViewSessionAsync);
            }
        }

        private Task ReleaseMajViewSessionAsync()
        {
            var previous = Interlocked.Exchange(ref _majViewSession, null);
            if (previous is not null)
            {
                previous.FrameReceived -= OnChartFrame;
            }
            var leavingChart = !IsSongChartPreviewSelected || _isDisposing;
            if (leavingChart && !_isDisposing && (_chartReady || _chartSeekRequested))
            {
                if (_chartTrack is { } inspected && _playback.Snapshot.Track is null && _playback.Queue.Current is null)
                {
                    _pendingSong = new(inspected, _chartPosition, _chartDuration);
                }
                if (_chartTrack is not null && SamePreviewTrack(_chartTrack, _playback.Snapshot.Track)
                    && _playback.Snapshot.State == PlaybackState.Paused)
                {
                    var trackId = _chartTrack!.Id;
                    var position = TimeSpan.FromSeconds(_chartPosition);
                    _returnToMusic = RunTransportAsync(() => _playback.SeekAsync(position, trackId, _lifetime.Token));
                }
            }
            _chartGeneration++;
            _chartReady = _chartPlaying = _chartDragging = false;
            if (leavingChart)
            {
                _chartTrack = null;
                _chartDuration = _chartPosition = 0;
                _chartSeekRequested = false;
            }
            _noteTimeline = null;
            _waveform = null;
            _levelNotes.Clear();
            IsChartFullscreen = false;
            IsPracticeLocked = false;
            SetField(ref _majViewAddress, null, nameof(MajViewAddress));
            NotifyChartOwner();
            NotifyChartAnalysis();
            return previous?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        }

        private static bool SamePreviewTrack(MusicTrack? first, MusicTrack? second) =>
            (first?.Id, first?.SourceId, first?.EndpointId, first?.Revision)
                == (second?.Id, second?.SourceId, second?.EndpointId, second?.Revision);
    }
}
