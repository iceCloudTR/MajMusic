#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;

namespace MajMusic.Presentation.ViewModels
{
    public sealed partial class MainViewModel
    {
        private int _chartGeneration, _chartRevision, _chartSeekRevision;
        private bool _chartReady, _chartPlaying, _chartDragging, _chartFullscreen, _practiceLocked;
        private double _chartPosition, _chartDuration;
        private MusicTrack? _chartTrack;
        private bool? _chartLightTheme;
        private bool _resumeAfterScrub;
        private double _scrubOrigin;
        private long _lastScrub;
        private int[]? _waveform;
        private ChartNoteTimeline? _noteTimeline;
        private readonly Dictionary<int, ChartNoteTimeline> _levelNotes = new();
        private readonly int[] _passedNotes = new int[ChartNoteTimeline.TypeCount];
        private bool _chartSeekRequested;
        private sealed record PendingSong(MusicTrack Track, double Position, double Duration);
        private PendingSong? _pendingSong;
        private bool _pendingSongDragging;
        private PendingSong? PendingMusic => !IsSongChartPreviewSelected && _playbackSnapshot.Track is null ? _pendingSong : null;
        private bool HasSkipContext => CanPlay && (_playback.Queue.Tracks.Count > 0 ||
            (IsSongChartPreviewSelected || PendingMusic is not null) && Tracks.Count > 1);
        internal MusicTrack? TransportTrack => IsSongChartPreviewSelected ? SelectedTrack : _playbackSnapshot.Track ?? PendingMusic?.Track;
        private Task _returnToMusic = Task.CompletedTask;

        /// <summary>Gets the title belonging to the active transport.</summary>
        public string TransportTitle => TransportTrack?.Title ?? PlaybackTitle;
        /// <summary>Gets the artist belonging to the active transport.</summary>
        public string TransportArtist => TransportTrack?.Artist ?? PlaybackArtist;
        /// <summary>Gets the saved chart practice settings.</summary>
        public ChartPracticeOptions ChartOptions { get; private set; } = new();
        /// <summary>Gets interleaved TAP, HOLD, SLIDE, TOUCH and BREAK counts for 128 time intervals.</summary>
        public IReadOnlyList<int>? ChartDensity => _noteTimeline?.Density;
        /// <summary>Gets peak amplitudes for the same 128 intervals of the song.</summary>
        public IReadOnlyList<int>? ChartWaveform => _waveform;
        /// <summary>Gets per-type totals in the same order as the density lanes.</summary>
        public IReadOnlyList<int> NoteTotals => _noteTimeline?.Totals ?? Array.Empty<int>();
        /// <summary>Gets the judged counts at the visible transport cursor, including an active drag.</summary>
        public IReadOnlyList<int> PassedNotes => _passedNotes;
        /// <summary>Gets the accessible, compact summary of this chart.</summary>
        public string ChartNotes { get; private set; } = "正在读取音符";
        /// <summary>Gets whether the app should consume input outside the practice lock.</summary>
        public bool IsPracticeLocked
        {
            get => _practiceLocked;
            internal set => SetField(ref _practiceLocked, value && _majViewSession is not null);
        }
        /// <summary>Gets whether the current chart owns the practice viewport.</summary>
        public bool IsChartFullscreen
        {
            get => _chartFullscreen;
            internal set
            {
                if (SetField(ref _chartFullscreen, value && _majViewSession is not null))
                {
                    _chartPlaying = false;
                    PublishChartIntent();
                    NotifyChartOwner();
                }
            }
        }

        internal Task SaveChartOptionsAsync(ChartPracticeOptions options) => RunTrackedAsync(async () =>
        {
            options = options.Normalize();
            if (_workspace is IChartPracticePreferencesWorkspace patch)
            {
                await patch.UpdateChartPracticeAsync(options, _lifetime.Token);
            }
            else
            {
                var current = await _workspace.LoadSettingsAsync(_lifetime.Token);
                await _workspace.SaveSettingsAsync(current with { ChartPractice = options }, _lifetime.Token);
            }
            _settings = _settings with { ChartPractice = options };
            ChartOptions = options;
            PublishChartIntent();
        });

        internal void SetChartTheme(bool light)
        {
            if (_chartLightTheme != light)
            {
                _chartLightTheme = light;
                PublishChartIntent();
            }
        }

        /// <summary>Captures the chosen song clock before any binding switches to the chart transport.</summary>
        private void PrimeChartClock(MusicTrack track)
        {
            _chartTrack = track;
            _chartSeekRequested = false;
            _chartPosition = 0;
            _chartDuration = track.Duration?.TotalSeconds ?? 0;
            var playback = _playback.Snapshot;
            if (SamePreviewTrack(track, playback.Track))
            {
                _chartPosition = playback.Position.TotalSeconds;
                if (playback.Duration is { TotalSeconds: > 0 } duration)
                {
                    _chartDuration = duration.TotalSeconds;
                }
            }
            else if (_pendingSong is { } parked && SamePreviewTrack(track, parked.Track))
            {
                _chartPosition = parked.Position;
                _chartDuration = parked.Duration;
            }
        }

        private void PublishChartIntent()
        {
            _majViewSession?.SetIntent(new(_chartGeneration, ++_chartRevision, _chartSeekRevision, _chartPosition,
                _chartPlaying, ChartOptions, IsMuted ? 0 : Volume / 100, IsChartFullscreen && _waveform is null, _chartLightTheme));
        }

        private void OnChartFrame(ChartFrame frame) => _dispatch(() =>
        {
            if (_isDisposing || _majViewSession is null || _chartGeneration != frame.Generation)
            {
                return;
            }
            if (frame.Failed && _majViewAddress is { } address)
            {
                ReportMajViewFailure(address, "无法读取谱面或音轨，请重试");
                return;
            }
            if (frame.Notes is { } notes)
            {
                _levelNotes[SelectedMajViewDifficulty] = _noteTimeline = notes;
                _chartDuration = frame.Duration;
                _chartPosition = Math.Min(_chartPosition, _chartDuration);
                NotifyChartAnalysis();
                NotifyTimeline();
                return;
            }
            if (frame.Waveform is { } peaks)
            {
                _waveform = peaks;
                OnPropertyChanged(nameof(ChartWaveform));
                return;
            }
            if (frame.Revision != _chartRevision || frame.Duration <= 0)
            {
                return;
            }
            var changed = !_chartReady || _chartPlaying != frame.Playing;
            _chartReady = true;
            _chartPlaying = frame.Playing;
            _chartPosition = frame.Position;
            _chartDuration = frame.Duration;
            if (changed)
            {
                NotifyChartOwner();
            }
            else
            {
                NotifyTimeline();
            }
        });

        private void NotifyChartAnalysis()
        {
            UpdatePassedNotes(true);
            OnPropertyChanged(nameof(ChartDensity));
            OnPropertyChanged(nameof(ChartWaveform));
        }

        private void UpdatePassedNotes(bool force = false)
        {
            if (_noteTimeline is null)
            {
                if (!force) return;
                Array.Clear(_passedNotes);
                ChartNotes = "正在读取音符";
            }
            else
            {
                var changed = force;
                var position = PositionSeconds;
                for (var i = 0; i < _passedNotes.Length; i++)
                {
                    var passed = _noteTimeline.CountAt(i, position);
                    changed |= _passedNotes[i] != passed;
                    _passedNotes[i] = passed;
                }
                if (!changed) return;
                var totals = _noteTimeline.Totals;
                ChartNotes = $"TAP {_passedNotes[0]}/{totals[0]}   HOLD {_passedNotes[1]}/{totals[1]}   SLIDE {_passedNotes[2]}/{totals[2]}   TOUCH {_passedNotes[3]}/{totals[3]}   BREAK {_passedNotes[4]}/{totals[4]}";
            }
            OnPropertyChanged(nameof(ChartNotes));
        }

        private void SeekChart(double seconds)
        {
            if (_chartDuration <= 0)
            {
                return;
            }
            _chartPosition = Math.Clamp(seconds, 0, _chartDuration);
            _chartSeekRequested = true;
            _chartSeekRevision++;
            PublishChartIntent();
            NotifyTimeline();
        }

        internal void PauseChart()
        {
            if (!_chartPlaying)
            {
                return;
            }

            _chartPlaying = false;
            PublishChartIntent();
            NotifyChartOwner();
        }

        private void ToggleChartPlayback()
        {
            if (!_chartReady || IsPracticeLocked)
            {
                return;
            }

            if (!_chartPlaying && _chartPosition >= _chartDuration - 0.05)
            {
                _chartPosition = 0;
                _chartSeekRevision++;
            }
            _chartPlaying = !_chartPlaying;
            PublishChartIntent();
            NotifyChartOwner();
        }

        private MusicTrack[] PracticeQueue(MusicTrack track, out int index)
        {
            var entries = IsRecommendationsMode && _recommendationTracks is { } recommendations
                ? recommendations.ToArray() : Tracks.ToArray();
            index = Array.FindIndex(entries, item => SamePreviewTrack(track, item));
            if (index >= 0)
            {
                return entries;
            }

            index = 0;
            return [track];
        }

        private async Task PlayPendingMusicAsync(PendingSong pending)
        {
            var entries = PracticeQueue(pending.Track, out var index);
            await _playback.SetQueueAsync(entries, index, _lifetime.Token);
            if (pending.Position > 0 && pending.Position < pending.Duration)
            {
                await _playback.SeekAsync(TimeSpan.FromSeconds(pending.Position), pending.Track.Id, _lifetime.Token);
            }
        }

        private async Task SkipFromTransportAsync(bool previous)
        {
            if (previous && PositionSeconds >= 3)
            {
                if (IsSongChartPreviewSelected)
                {
                    _chartPlaying = true;
                    SeekChart(0);
                    NotifyChartOwner();
                }
                else if (PendingMusic is { } pending)
                {
                    await PlayPendingMusicAsync(pending with { Position = 0 });
                }
                else
                {
                    await _playback.SeekAsync(TimeSpan.Zero, _lifetime.Token);
                    if (_playback.Snapshot.State == PlaybackState.Paused)
                    {
                        await _playback.ResumeAsync(_lifetime.Token);
                    }
                }
                return;
            }
            var anchor = IsSongChartPreviewSelected ? SelectedTrack : PendingMusic?.Track;
            if (anchor is not null && !SamePreviewTrack(anchor, _playback.Queue.Current))
            {
                var entries = PracticeQueue(anchor, out var index);
                var target = index + (previous ? -1 : 1);
                if (target < 0 || target >= entries.Length)
                {
                    return;
                }

                CloseMajView();
                await _returnToMusic;
                await _playback.SetQueueAsync(entries, target, _lifetime.Token);
                return;
            }
            if (_playback.Queue.Tracks.Count == 0)
            {
                return;
            }

            CloseMajView();
            await _returnToMusic;
            if (previous)
            {
                await _playback.PreviousAsync(_lifetime.Token);
            }
            else
            {
                await _playback.NextAsync(_lifetime.Token);
            }
        }

        private void NotifyChartOwner()
        {
            OnPropertyChanged(nameof(TransportTitle));
            OnPropertyChanged(nameof(TransportArtist));
            OnPropertyChanged(nameof(TransportButtonText));
            OnPropertyChanged(nameof(IsPlaying));
            NotifyTimeline();
            TogglePlayPauseCommand?.NotifyCanExecuteChanged();
            PreviousCommand?.NotifyCanExecuteChanged();
            NextCommand?.NotifyCanExecuteChanged();
        }
    }
}
