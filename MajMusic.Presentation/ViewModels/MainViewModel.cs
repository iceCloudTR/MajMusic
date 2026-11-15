#nullable enable

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Exposes local music management and explicitly configured optional sources.</summary>
    public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
    {
        /// <summary>Coordinates storage and optional sources.</summary>
        private readonly IMusicWorkspace _workspace;
        /// <summary>Exchanges Maj-compatible files without allowing writes to music sources.</summary>
        private readonly IMajCollectionExchange? _collections;
        /// <summary>Identifies the fixed local favorites collection.</summary>
        private const string FavoritesId = "local:favorites";
        /// <summary>Stores the latest local favorites metadata.</summary>
        private MusicPlaylist? _favorites;
        /// <summary>Indexes portable favorite identities without doing database work in property getters.</summary>
        private readonly HashSet<string> _favoriteHashes = new(StringComparer.Ordinal);
        /// <summary>Coordinates the platform playback adapter.</summary>
        private readonly PlaybackService _playback;
        /// <summary>Marshals audio notifications to the host presentation thread.</summary>
        private readonly Action<Action> _dispatch;
        /// <summary>Protects operation registration against shutdown.</summary>
        private readonly object _operationGate = new();
        /// <summary>Tracks completions that shutdown must await before releasing resources.</summary>
        private readonly HashSet<Task> _operations = new();
        /// <summary>Protects cover cancellation ownership across selection changes and shutdown.</summary>
        private readonly object _coverGate = new();
        /// <summary>Shares one shutdown task across repeated close notifications.</summary>
        private Task? _disposal;
        /// <summary>Rejects newly requested operations after shutdown begins.</summary>
        private bool _isDisposing;
        /// <summary>Stores the last state observed from the actual audio owner.</summary>
        private PlaybackSnapshot _playbackSnapshot;
        /// <summary>Protects a seek gesture from backend notifications on another thread.</summary>
        private readonly object _seekGate = new();
        /// <summary>Cancels a committed seek if its media changes before execution.</summary>
        private CancellationTokenSource? _seekCancellation;
        /// <summary>Identifies the media captured when a seek gesture starts.</summary>
        private string? _seekTrackId;
        /// <summary>Rejects commits superseded by a media transition or canceled gesture.</summary>
        private long _seekVersion;
        /// <summary>Stores the proposed position while the user is interacting with the timeline.</summary>
        private double _seekPreviewSeconds;
        /// <summary>Tracks a user-owned preview independently of the backend media clock.</summary>
        private bool _isSeeking;
        /// <summary>Records a media transition before its notification reaches the UI thread.</summary>
        private bool _seekInvalidated;
        /// <summary>Cancels operations when the owning view closes.</summary>
        private readonly CancellationTokenSource _lifetime = new();
        /// <summary>Cancels obsolete cover loads.</summary>
        private CancellationTokenSource? _coverCancellation;
        /// <summary>Identifies the cover request currently shared by selection notifications.</summary>
        private CoverIdentity? _pendingCoverIdentity;
        /// <summary>Completes after the shared cover request and its cleanup have finished.</summary>
        private TaskCompletionSource? _coverCompletion;
        /// <summary>Identifies the bitmap currently displayed without depending on audio metadata.</summary>
        private CoverIdentity? _loadedCoverIdentity;
        /// <summary>Stores preferences not currently edited by the view.</summary>
        private AppSettings _settings = new();
        /// <summary>Stores the selected music entry.</summary>
        private MusicTrack? _selectedTrack;
        /// <summary>Stores the playlist targeted by the add operation.</summary>
        private MusicPlaylist? _selectedPlaylist;
        /// <summary>Stores the explicitly opened playlist independently of the add target.</summary>
        private string? _activePlaylistId;
        /// <summary>Owns the decoded selected cover.</summary>
        private Bitmap? _cover;
        /// <summary>Stores the library search keyword.</summary>
        private string _searchText = string.Empty;
        /// <summary>Stores the proposed local playlist name.</summary>
        private string _newPlaylistName = string.Empty;
        /// <summary>Stores the edited optional source address.</summary>
        private string _endpointUrl = string.Empty;
        /// <summary>Stores the edited optional source name.</summary>
        private string _endpointName = string.Empty;
        /// <summary>Stores the visible operation status.</summary>
        private string _status = "正在打开本地音乐库…";
        /// <summary>Tracks exclusive application operations.</summary>
        private bool _isBusy;
        /// <summary>Tracks explicit network permission.</summary>
        private bool _onlineEnabled;

        /// <summary>Creates a view model without performing network requests.</summary>
        /// <param name="workspace">The platform-independent application services.</param>
        /// <param name="playback">The playback service owned by this view lifetime.</param>
        /// <param name="dispatcher">An optional host dispatcher; tests may run notifications immediately.</param>
        /// <param name="collections">The optional platform-independent Maj collection exchange.</param>
        /// <param name="metadata">The optional local audio metadata provider owned by this view.</param>
        /// <param name="directories">The optional independent local-directory scanning service.</param>
        public MainViewModel(IMusicWorkspace workspace, PlaybackService playback, Action<Action>? dispatcher = null,
            IMajCollectionExchange? collections = null, ITrackMetadataProvider? metadata = null, ILocalDirectoryService? directories = null)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            ArgumentNullException.ThrowIfNull(playback);
            _workspace = workspace;
            _collections = collections;
            _metadata = metadata;
            _onlineWorkspace = workspace as IOnlineMusicWorkspace;
            _accountPreferences = workspace as IAccountPreferencesWorkspace;
            _directories = directories;
            _playback = playback;
            _dispatch = dispatcher ?? DispatchToUi;
            _playbackSnapshot = playback.Snapshot;
            SearchCommand = CreateCommand(ExecuteSearchAsync);
            CreatePlaylistCommand = CreateCommand(CreatePlaylistAsync);
            AddToPlaylistCommand = CreateCommand(AddToPlaylistAsync, () => SelectedTrack is not null && SelectedPlaylist is { IsCloud: false });
            OpenPlaylistCommand = CreateCommand(OpenPlaylistAsync, () => SelectedPlaylist is not null);
            ShowAllCommand = CreateCommand(ShowAllAsync);
            SaveSettingsCommand = CreateCommand(SaveSettingsAsync);
            SynchronizeCommand = CreateCommand(SynchronizeAsync, () => OnlineEnabled);
            DownloadCommand = CreateCommand(DownloadAsync, () => OnlineEnabled && SelectedTrack is not null);
            ClearCacheCommand = CreateCommand(ClearCacheAsync);
            PlayCommand = CreateCommand(PlayAsync, () => CanPlay && (SelectedTrack is not null || CanResume));
            PauseCommand = CreateCommand(() => _playback.PauseAsync(_lifetime.Token), () => CanPlay && _playbackSnapshot.State == PlaybackState.Playing);
            NextCommand = CreateCommand(() => _playback.NextAsync(_lifetime.Token), () => CanPlay && _playback.Queue.Tracks.Count > 0);
            PreviousCommand = CreateCommand(() => _playback.PreviousAsync(_lifetime.Token), () => CanPlay && _playback.Queue.Tracks.Count > 0);
            ToggleFavoriteCommand = CreateCommand(ToggleFavoriteAsync, () => SelectedTrack is not null);
            OpenFavoritesCommand = CreateCommand(OpenFavoritesAsync);
            RescanCommand = CreateCommand(RescanAsync, () => _settings.LocalDirectorySources.Count > 0);
            InitializeSourceCommands();
            InitializeMetadataCommands();
            InitializeAccountCommands();
            InitializeDirectoryCommands();
            InitializeSettingsCommands();
            InitializeHomeCommands();
            InitializePanelPagingCommands();
            InitializePlaybackModeCommands();
            _playback.StateChanged += OnPlaybackStateChanged;
        }

        /// <summary>Occurs when a bound property changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>Gets the visible search results.</summary>
        public ObservableCollection<MusicTrack> Tracks { get; } = new();
        /// <summary>Gets the local playlists.</summary>
        public ObservableCollection<MusicPlaylist> Playlists { get; } = new();
        /// <summary>Gets the search command.</summary>
        public AsyncCommand SearchCommand { get; }
        /// <summary>Gets the playlist creation command.</summary>
        public AsyncCommand CreatePlaylistCommand { get; }
        /// <summary>Gets the selected track's playlist insertion command.</summary>
        public AsyncCommand AddToPlaylistCommand { get; }
        /// <summary>Gets the command explicitly opening the chosen playlist.</summary>
        public AsyncCommand OpenPlaylistCommand { get; }
        /// <summary>Gets the command returning to the entire music library.</summary>
        public AsyncCommand ShowAllCommand { get; }
        /// <summary>Gets the non-secret settings save command.</summary>
        public AsyncCommand SaveSettingsCommand { get; }
        /// <summary>Gets the explicit source synchronization command.</summary>
        public AsyncCommand SynchronizeCommand { get; }
        /// <summary>Gets the selected music's permanent download command.</summary>
        public AsyncCommand DownloadCommand { get; }
        /// <summary>Gets the evictable cache cleanup command.</summary>
        public AsyncCommand ClearCacheCommand { get; }
        /// <summary>Gets the playback command.</summary>
        public AsyncCommand PlayCommand { get; }
        /// <summary>Gets the pause command.</summary>
        public AsyncCommand PauseCommand { get; }
        /// <summary>Gets the next-track command.</summary>
        public AsyncCommand NextCommand { get; }
        /// <summary>Gets the previous-track command.</summary>
        public AsyncCommand PreviousCommand { get; }
        /// <summary>Gets the action that adds or removes the selected music from favorites.</summary>
        public AsyncCommand ToggleFavoriteCommand { get; }
        /// <summary>Gets the action opening the local favorites list.</summary>
        public AsyncCommand OpenFavoritesCommand { get; }
        /// <summary>Gets the action rescanning the saved read-only roots.</summary>
        public AsyncCommand RescanCommand { get; }
        /// <summary>Gets whether the host provides compatible file exchange.</summary>
        public bool CanExchangeCollections => _collections is not null;
        /// <summary>Gets the selected playlist's compatible export filename.</summary>
        public string ExportFileName => SelectedPlaylist?.IsFavorite == true ? "MyFavorites.json" : "MajMusic-playlist.json";
        /// <summary>Gets saved read-only root locations for settings.</summary>
        public string LocalDirectoriesText => _settings.LocalDirectorySources.Count == 0
            ? "尚未添加目录" : string.Join(Environment.NewLine, _settings.LocalDirectorySources.Select(item => item.Path));
        /// <summary>Gets the selected music's favorite action text.</summary>
        public string FavoriteButtonText => IsSelectedFavorite ? "♥ 已喜欢" : "♡ 我喜欢";
        /// <summary>Gets whether the selected entry or its portable Maj identity is favorited.</summary>
        public bool IsSelectedFavorite => SelectedTrack is { } track && _favorites is { } favorites
            && (favorites.TrackIds.Contains(track.Id, StringComparer.Ordinal)
                || TrackMajHash(track) is { } hash && _favoriteHashes.Contains(hash));
        /// <summary>Gets readable properties of the actual loaded audio stream.</summary>
        public string AudioDetails => FormatAudioDetails(_playbackSnapshot.AudioInfo);
        /// <summary>Gets whether actual loaded audio properties are available.</summary>
        public bool HasAudioDetails => _playbackSnapshot.AudioInfo is not null;
        /// <summary>Gets whether the playback owner has an actionable availability message.</summary>
        public bool HasPlaybackHint => !string.IsNullOrEmpty(PlaybackHint);
        /// <summary>Gets whether the latest operation has a visible result.</summary>
        public bool HasStatus => !string.IsNullOrEmpty(Status);
        /// <summary>Gets whether the platform has a working audio adapter.</summary>
        public bool CanPlay => _playback.IsAvailable;
        /// <summary>Gets the application data directory.</summary>
        public string DataDirectory => _workspace.DataDirectory;
        /// <summary>Gets the current number of displayed entries.</summary>
        public string LibrarySummary => Tracks.Count > LibraryPageSize
            ? $"{Tracks.Count} 首 · 第 {_libraryPageIndex + 1}/{Math.Max(1, (Tracks.Count + LibraryPageSize - 1) / LibraryPageSize)} 页"
            : $"{Tracks.Count} 首音乐";
        /// <summary>Gets the selected track title or the empty-selection text.</summary>
        public string SelectedTitle => SelectedTrack?.Title ?? "选择一首音乐";
        /// <summary>Gets the selected artist or the empty-library hint.</summary>
        public string SelectedArtist => SelectedTrack?.Artist ?? string.Empty;
        /// <summary>Gets the loaded music title independently of the browsing selection.</summary>
        public string PlaybackTitle => _playbackSnapshot.Track?.Title ?? "尚未播放";
        /// <summary>Gets the loaded music artist independently of the browsing selection.</summary>
        public string PlaybackArtist => _playbackSnapshot.Track?.Artist ?? string.Empty;
        /// <summary>Gets the action label for starting or resuming audio.</summary>
        public string PlayButtonText => CanResume ? "继续" : "播放";
        /// <summary>Gets a truthful playback availability hint.</summary>
        public string PlaybackHint => _playbackSnapshot.Error ?? (CanPlay ? string.Empty : "音频播放组件不可用");
        /// <summary>Gets whether the current known media timeline supports user seeking.</summary>
        public bool CanSeek => IsIdle && HasSeekableMedia;
        /// <summary>Gets whether the user currently owns a timeline preview.</summary>
        public bool IsSeeking => _isSeeking;
        /// <summary>Gets a nonzero slider maximum while unknown durations remain disabled.</summary>
        public double DurationSeconds => _playbackSnapshot.Duration is { TotalSeconds: > 0 } duration ? duration.TotalSeconds : 1;
        /// <summary>Gets the backend position or updates only an active user preview.</summary>
        public double PositionSeconds
        {
            get => _isSeeking ? _seekPreviewSeconds : ClampPosition(_playbackSnapshot.Position.TotalSeconds);
            set
            {
                if (!_isSeeking || !double.IsFinite(value))
                {
                    return;
                }

                _seekPreviewSeconds = ClampPosition(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(PositionText));
            }
        }
        /// <summary>Gets the formatted backend position or current user preview.</summary>
        public string PositionText => FormatTime(TimeSpan.FromSeconds(PositionSeconds));
        /// <summary>Gets the formatted media duration or the unknown-duration marker.</summary>
        public string DurationText => _playbackSnapshot.Duration is { } duration ? FormatTime(duration) : "--:--";
        /// <summary>Gets whether a prepared resource provides an authoritative seekable duration.</summary>
        private bool HasSeekableMedia => CanPlay && _playback.Capabilities.HasFlag(PlaybackCapabilities.Seeking)
            && _playbackSnapshot.Track is not null && _playbackSnapshot.Duration > TimeSpan.Zero
            && _playbackSnapshot.State is PlaybackState.Playing or PlaybackState.Paused;
        /// <summary>Gets whether local operations are available.</summary>
        public bool IsIdle => !_isBusy && !Volatile.Read(ref _isDisposing);
        /// <summary>Gets whether an exclusive workspace operation is active.</summary>
        public bool IsBusy => _isBusy;
        /// <summary>Gets whether a prepared paused resource can resume without reopening.</summary>
        private bool CanResume => _playback.Snapshot is { State: PlaybackState.Paused, Track: not null };

        /// <summary>Gets or sets the literal search keyword.</summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetField(ref _searchText, value))
                {
                    OnHomeSearchChanged();
                }
            }
        }
        /// <summary>Gets or sets the proposed local playlist name.</summary>
        public string NewPlaylistName
        {
            get => _newPlaylistName;
            set => SetField(ref _newPlaylistName, value);
        }
        /// <summary>Gets or sets the optional source address.</summary>
        public string EndpointUrl
        {
            get => _endpointUrl;
            set
            {
                if (SetField(ref _endpointUrl, value))
                {
                    LoginCommand?.NotifyCanExecuteChanged();
                    SynchronizeCommand?.NotifyCanExecuteChanged();
                }
            }
        }
        /// <summary>Gets or sets the optional source name.</summary>
        public string EndpointName
        {
            get => _endpointName;
            set => SetField(ref _endpointName, value);
        }
        /// <summary>Gets the current operation status.</summary>
        public string Status
        {
            get => _status;
            private set
            {
                if (SetField(ref _status, value))
                {
                    OnPropertyChanged(nameof(HasStatus));
                }
            }
        }
        /// <summary>Gets the selected cover bitmap.</summary>
        public Bitmap? Cover
        {
            get => _cover;
            private set => SetField(ref _cover, value);
        }

        /// <summary>Gets or sets explicit network permission; disabling it cancels source requests immediately.</summary>
        public bool OnlineEnabled
        {
            get => _onlineEnabled;
            set
            {
                if (!Volatile.Read(ref _isDisposing) && SetField(ref _onlineEnabled, value))
                {
                    _workspace.SetOnlineEnabled(value);
                    if (value)
                    {
                        BeginAutomaticLogin();
                    }
                    else
                    {
                        CancelAutomaticLogin();
                    }

                    RefreshCommands();
                    OnPropertyChanged(nameof(HasMoreRecommendations));
                }
            }
        }

        /// <summary>Gets or sets the selected music metadata.</summary>
        public MusicTrack? SelectedTrack
        {
            get => _selectedTrack;
            set
            {
                if (SetField(ref _selectedTrack, value))
                {
                    OnPropertyChanged(nameof(SelectedTitle));
                    OnPropertyChanged(nameof(SelectedArtist));
                    OnPropertyChanged(nameof(SelectedVisibleTrack));
                    OnPropertyChanged(nameof(SelectedBrowseTrack));
                    OnPropertyChanged(nameof(SelectedBrowseCard));
                    RefreshCardSelection();
                    OnPropertyChanged(nameof(PlayButtonText));
                    OnPropertyChanged(nameof(FavoriteButtonText));
                    OnPropertyChanged(nameof(IsSelectedFavorite));
                    RefreshCommands();
                }
            }
        }

        /// <summary>Gets or sets the add target without implicitly filtering the library.</summary>
        public MusicPlaylist? SelectedPlaylist
        {
            get => _selectedPlaylist;
            set
            {
                if (!ReferenceEquals(_selectedPlaylist, value))
                {
                    _selectedPlaylist = value;
                    OnPropertyChanged();
                    RefreshCommands();
                    OnPropertyChanged(nameof(ExportFileName));
                }
            }
        }

        /// <summary>Reads local state without synchronizing optional sources.</summary>
        /// <returns>A task representing initialization.</returns>
        public Task InitializeAsync()
        {
            return RunBusyAsync(async () =>
            {
                CancelAutomaticLogin();
                _accountSettingsLoaded = false;
                _settings = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
                _lifetime.Token.ThrowIfCancellationRequested();
                OnlineEnabled = _settings.OnlineEnabled;
                var endpoint = _settings.Endpoints.FirstOrDefault(item => item.Id == _settings.ActiveEndpointId)
                    ?? _settings.Endpoints.FirstOrDefault();
                ReloadEndpointProfiles(endpoint?.Id);
                ReloadDirectorySources();
                OnPropertyChanged(nameof(LocalDirectoriesText));
                await ReloadPlaylistsAsync();
                InitializeHomeState();
                await ReloadLibraryAsync();
                await InitializeAudioSettingsAsync();
                Status = string.Empty;
                _accountSettingsLoaded = true;
                if (OnlineEnabled)
                {
                    BeginAutomaticLogin();
                }
            });
        }

        /// <summary>Recursively indexes a selected read-only music root without copying its files.</summary>
        /// <param name="directory">The selected root, including any nested song directories.</param>
        /// <returns>A task representing indexing and optional compatible-list reads.</returns>
        public Task ImportDirectoryAsync(string directory)
        {
            return RunBusyAsync(() => ImportMusicDirectoryCoreAsync(directory));
        }

        /// <summary>Reads one Maj-compatible collection chosen through the platform file picker.</summary>
        /// <param name="filePath">The source JSON file, which remains unchanged.</param>
        /// <returns>A task representing local collection import.</returns>
        public Task ImportCollectionAsync(string filePath)
        {
            return RunBusyAsync(async () =>
            {
                var exchange = _collections ?? throw new NotSupportedException("当前宿主没有配置列表文件交换。");
                var playlist = await exchange.ImportFileAsync(filePath, _lifetime.Token);
                await ReloadPlaylistsAsync();
                SelectedPlaylist = Playlists.FirstOrDefault(item => item.Id == playlist.Id);
                var missing = playlist.MajCollection?.Entries.Count(item => item.TrackId is null) ?? 0;
                Status = $"已导入 {playlist.Name} · {playlist.TrackIds.Count} 首" + (missing > 0 ? $" · {missing} 首待匹配" : string.Empty);
            });
        }

        /// <summary>Writes a selected list to a new compatible file outside read-only sources.</summary>
        /// <param name="filePath">The explicitly selected new export destination.</param>
        /// <returns>A task representing the export.</returns>
        public Task ExportCollectionAsync(string filePath)
        {
            return RunBusyAsync(async () =>
            {
                var exchange = _collections ?? throw new NotSupportedException("当前宿主没有配置列表文件交换。");
                var playlist = SelectedPlaylist ?? throw new InvalidOperationException("请先选择要导出的播放列表或我喜欢。");
                await exchange.ExportAsync(filePath, playlist, _lifetime.Token);
                Status = "列表已导出";
            });
        }

        /// <summary>Starts or resumes the selected music through the observed view lifetime.</summary>
        /// <returns>A task representing the playback command.</returns>
        public Task PlaySelectedAsync()
        {
            return RunBusyAsync(StartSelectedTrackAsync);
        }

        /// <summary>Captures the current media and starts a user-owned timeline preview.</summary>
        /// <returns>Whether the current media can accept the gesture.</returns>
        public bool BeginSeek()
        {
            if (!CanSeek)
            {
                return false;
            }

            lock (_seekGate)
            {
                if (_isSeeking)
                {
                    return true;
                }

                _seekVersion++;
                _seekTrackId = _playbackSnapshot.Track!.Id;
                _seekPreviewSeconds = ClampPosition(_playbackSnapshot.Position.TotalSeconds);
                _seekInvalidated = false;
                _isSeeking = true;
            }

            NotifyTimeline();
            return true;
        }

        /// <summary>Discards a preview and cancels any pending seek for that interaction.</summary>
        public void CancelSeek()
        {
            lock (_seekGate)
            {
                _seekVersion++;
                _isSeeking = false;
                _seekInvalidated = false;
                _seekTrackId = null;
                _seekCancellation?.Cancel();
            }

            NotifyTimeline();
        }

        /// <summary>Submits one position when a pointer or keyboard gesture completes.</summary>
        /// <returns>A task representing the observed seek, or no action for a canceled gesture.</returns>
        public Task CommitSeekAsync()
        {
            string? trackId;
            double seconds;
            long version;
            lock (_seekGate)
            {
                if (!_isSeeking)
                {
                    return Task.CompletedTask;
                }

                _isSeeking = false;
                trackId = _seekTrackId;
                seconds = _seekPreviewSeconds;
                version = _seekVersion;
            }

            NotifyTimeline();
            return RunBusyAsync(async () =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                lock (_seekGate)
                {
                    if (_seekInvalidated || version != _seekVersion || trackId is null
                        || _playback.Snapshot.Track?.Id != trackId || !HasSeekableMedia)
                    {
                        return;
                    }

                    _seekCancellation = cancellation;
                }

                try
                {
                    await _playback.SeekAsync(TimeSpan.FromSeconds(seconds), trackId, cancellation.Token);
                }
                finally
                {
                    lock (_seekGate)
                    {
                        if (ReferenceEquals(_seekCancellation, cancellation))
                        {
                            _seekCancellation = null;
                        }
                    }

                    NotifyTimeline();
                }
            });
        }

        /// <summary>Loads only the selected cover and cancels stale selection requests.</summary>
        /// <returns>A task representing the selected cover load.</returns>
        public Task LoadCoverAsync()
        {
            return RunTrackedAsync(LoadCoverCoreAsync);
        }

        /// <summary>Loads a cover while its registered operation keeps the lifetime resources alive.</summary>
        /// <returns>A task representing the observed cover load.</returns>
        private async Task LoadCoverCoreAsync()
        {
            var selected = SelectedTrack;
            var identity = GetCoverIdentity(selected);
            CancellationTokenSource? obsolete = null;
            CancellationTokenSource? cancellation = null;
            TaskCompletionSource? completion = null;
            Task? shared = null;
            Bitmap? previous = null;
            lock (_coverGate)
            {
                if (identity is null)
                {
                    obsolete = _coverCancellation;
                    _coverCancellation = null;
                    _pendingCoverIdentity = null;
                    _coverCompletion = null;
                    _loadedCoverIdentity = null;
                    previous = Cover;
                    Cover = null;
                }
                else if (identity == _loadedCoverIdentity && Cover is not null)
                {
                    if (_pendingCoverIdentity != identity)
                    {
                        obsolete = _coverCancellation;
                        _coverCancellation = null;
                        _pendingCoverIdentity = null;
                        _coverCompletion = null;
                    }
                }
                else if (identity == _pendingCoverIdentity && _coverCompletion is not null)
                {
                    shared = _coverCompletion.Task;
                }
                else
                {
                    obsolete = _coverCancellation;
                    cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _coverCancellation = cancellation;
                    _pendingCoverIdentity = identity;
                    _coverCompletion = completion;
                    if (!IsSameCoverTrack(_loadedCoverIdentity, identity))
                    {
                        _loadedCoverIdentity = null;
                        previous = Cover;
                        Cover = null;
                    }
                }
            }

            CancelCoverRequest(obsolete);
            previous?.Dispose();
            previous = null;
            if (shared is not null)
            {
                await shared;
                return;
            }

            if (selected is null || identity is null || cancellation is null || completion is null)
            {
                return;
            }

            Bitmap? candidate = null;
            try
            {
                var token = cancellation.Token;
                token.ThrowIfCancellationRequested();
                var path = await _workspace.ResolveCoverAsync(selected, token);
                token.ThrowIfCancellationRequested();
                if (path is not null)
                {
                    candidate = new Bitmap(path);
                }

                lock (_coverGate)
                {
                    if (!Volatile.Read(ref _isDisposing) && ReferenceEquals(_coverCompletion, completion)
                        && identity == GetCoverIdentity(SelectedTrack) && !token.IsCancellationRequested)
                    {
                        previous = Cover;
                        Cover = candidate;
                        _loadedCoverIdentity = candidate is null ? null : identity;
                        candidate = null;
                    }
                }

                previous?.Dispose();
            }
            catch (OperationCanceledException)
            {
                // A newer selection or the view lifetime superseded this cover.
            }
            catch (Exception exception)
            {
                if (!cancellation.IsCancellationRequested && identity == GetCoverIdentity(SelectedTrack))
                {
                    ReportError(exception);
                }
            }
            finally
            {
                lock (_coverGate)
                {
                    if (ReferenceEquals(_coverCompletion, completion))
                    {
                        _coverCancellation = null;
                        _pendingCoverIdentity = null;
                        _coverCompletion = null;
                    }
                }

                candidate?.Dispose();
                cancellation.Dispose();
                completion.TrySetResult();
            }
        }

        /// <summary>Captures only the selected song and cover descriptor, independently of audio properties.</summary>
        /// <param name="track">The selected music entry, or null for an empty inspector.</param>
        /// <returns>The immutable cover identity, or null when no song is selected.</returns>
        private static CoverIdentity? GetCoverIdentity(MusicTrack? track)
        {
            return track is null ? null : new CoverIdentity(track.Id, track.SourceId, track.EndpointId, track.Cover);
        }

        /// <summary>Checks whether a version update still belongs to the song already being inspected.</summary>
        /// <param name="displayed">The currently displayed bitmap's identity.</param>
        /// <param name="requested">The requested cover identity.</param>
        /// <returns>Whether the old bitmap can remain visible until its replacement is ready.</returns>
        private static bool IsSameCoverTrack(CoverIdentity? displayed, CoverIdentity requested)
        {
            return displayed is not null && displayed.TrackId == requested.TrackId
                && displayed.SourceId == requested.SourceId && displayed.EndpointId == requested.EndpointId;
        }

        /// <summary>Cancels obsolete cover work after releasing its state lock.</summary>
        /// <param name="cancellation">The retired request lifetime, or null.</param>
        private static void CancelCoverRequest(CancellationTokenSource? cancellation)
        {
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A request that completed concurrently has already released its cancellation source.
            }
        }

        /// <summary>Detaches and cancels the current shared cover request during shutdown.</summary>
        private void CancelCoverLoad()
        {
            CancellationTokenSource? cancellation;
            lock (_coverGate)
            {
                cancellation = _coverCancellation;
                _coverCancellation = null;
                _pendingCoverIdentity = null;
                _coverCompletion = null;
            }

            CancelCoverRequest(cancellation);
        }

        /// <summary>Identifies one song's cover without including frequently refreshed audio metadata.</summary>
        /// <param name="TrackId">The stable song identity.</param>
        /// <param name="SourceId">The server or local source identity.</param>
        /// <param name="EndpointId">The owning account slot for remote entries.</param>
        /// <param name="Resource">The cover descriptor and its source version.</param>
        private sealed record CoverIdentity(string TrackId, string SourceId, string? EndpointId, MediaResource? Resource);

        /// <summary>Reports an observed user action failure.</summary>
        /// <param name="exception">The observed failure.</param>
        public void ReportError(Exception exception)
        {
            if (!Volatile.Read(ref _isDisposing))
            {
                Status = exception is HttpRequestException request ? request.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "请重新登录",
                    System.Net.HttpStatusCode.Forbidden => "当前账号无法访问",
                    System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotImplemented => "此接入点未提供这项功能",
                    _ => $"连接未完成：{request.Message}"
                } : $"操作未完成：{exception.Message}";
            }
        }

        /// <summary>Cancels view operations and releases playback and image resources.</summary>
        /// <returns>A task representing completed playback disposal.</returns>
        public ValueTask DisposeAsync()
        {
            lock (_operationGate)
            {
                if (_disposal is null)
                {
                    _isDisposing = true;
                    _playback.StateChanged -= OnPlaybackStateChanged;
                    _disposal = DisposeCoreAsync(_operations.ToArray());
                    OnPropertyChanged(nameof(IsIdle));
                    RefreshCommands();
                }

                return new ValueTask(_disposal);
            }
        }

        /// <summary>Waits for canceled work before releasing resources used by that work.</summary>
        /// <param name="operations">The operations registered before close began.</param>
        /// <returns>A task representing completed resource disposal.</returns>
        private async Task DisposeCoreAsync(Task[] operations)
        {
            _lifetime.Cancel();
            CancelAutomaticLogin();
            CancelMetadataRefresh();
            CancelSeek();
            CancelCoverLoad();
            await DisposeCardsAsync();

            await Task.WhenAll(operations);
            var previous = Cover;
            Cover = null;
            previous?.Dispose();
            try
            {
                await _playback.DisposeAsync();
                if (_metadata is IAsyncDisposable asyncMetadata)
                {
                    await asyncMetadata.DisposeAsync();
                }
                else if (_metadata is IDisposable metadata)
                {
                    metadata.Dispose();
                }
            }
            finally
            {
                _lifetime.Dispose();
            }
        }

        /// <summary>Registers asynchronous work before shutdown can snapshot outstanding operations.</summary>
        /// <param name="action">The action to observe.</param>
        /// <returns>A task representing the action, or no work after shutdown begins.</returns>
        private async Task RunTrackedAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_operationGate)
            {
                if (_isDisposing)
                {
                    return;
                }

                _operations.Add(completion.Task);
            }

            try
            {
                await action();
            }
            finally
            {
                lock (_operationGate)
                {
                    _operations.Remove(completion.Task);
                }

                completion.TrySetResult();
            }
        }

        /// <summary>Builds a command bound to the view lifetime and busy state.</summary>
        /// <param name="action">The operation to perform.</param>
        /// <param name="canExecute">An optional additional availability check.</param>
        /// <returns>The observed asynchronous command.</returns>
        private AsyncCommand CreateCommand(Func<Task> action, Func<bool>? canExecute = null)
        {
            return new AsyncCommand(() => RunBusyAsync(action), ReportError, () => IsIdle && (canExecute?.Invoke() ?? true));
        }

        /// <summary>Serializes view operations and observes failures.</summary>
        /// <param name="action">The operation to perform.</param>
        /// <returns>A task representing the observed operation.</returns>
        private Task RunBusyAsync(Func<Task> action)
        {
            return RunTrackedAsync(() => RunBusyCoreAsync(action));
        }

        /// <summary>Executes a registered workspace operation and observes its cancellation or failure.</summary>
        /// <param name="action">The exclusive operation to perform.</param>
        /// <returns>A task representing the observed operation.</returns>
        private async Task RunBusyCoreAsync(Func<Task> action)
        {
            if (_isBusy || _lifetime.IsCancellationRequested || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            _isBusy = true;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanSeek));
            RefreshCommands();
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                if (!Volatile.Read(ref _isDisposing))
                {
                    Status = "操作已取消。";
                }
            }
            catch (Exception exception)
            {
                await ReconcileExpiredSessionsAsync();
                ReportError(exception);
            }
            finally
            {
                _isBusy = false;
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanSeek));
                RefreshCommands();
            }
        }

        /// <summary>Refreshes local search results and applies the optional playlist filter.</summary>
        /// <returns>A task representing the local read.</returns>
        private Task ReloadLibraryAsync()
        {
            return ReloadLibraryCoreAsync(_lifetime.Token);
        }

        /// <summary>Reads one versioned home snapshot while preserving the inspected song.</summary>
        /// <param name="cancellationToken">Cancels an obsolete merged search.</param>
        /// <param name="preservePage">Whether a feed append should retain the current page.</param>
        /// <returns>A task representing the current visible content snapshot.</returns>
        private async Task ReloadLibraryCoreAsync(CancellationToken cancellationToken, bool preservePage = false)
        {
            var version = _homeVersion;
            var searching = IsSearching;
            var mode = _homeMode;
            IReadOnlyList<MusicTrack> found = !searching && mode == HomeBrowseMode.Recommendations
                ? _recommendationTracks ?? (IReadOnlyList<MusicTrack>)Array.Empty<MusicTrack>()
                : await _workspace.SearchAsync(searching ? SearchText : null, cancellationToken);
            var sourceNames = _settings.Endpoints.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
            var tracks = found.Select(track => track.EndpointId is { } endpointId && sourceNames.TryGetValue(endpointId, out var name)
                ? track with { SourceName = name } : track).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            if (version != _homeVersion)
            {
                return;
            }

            var inspected = SelectedTrack;
            var playlist = Playlists.FirstOrDefault(item => item.Id == _activePlaylistId);
            IEnumerable<MusicTrack> visible = tracks;
            if (!searching && mode == HomeBrowseMode.Recommendations)
            {
                visible = tracks;
            }
            else if (!searching && mode == HomeBrowseMode.Playlists)
            {
                var byId = tracks.ToDictionary(item => item.Id, StringComparer.Ordinal);
                visible = playlist is null ? Enumerable.Empty<MusicTrack>() : playlist.TrackIds.Where(byId.ContainsKey).Select(id => byId[id]);
            }

            var entries = visible.ToArray();
            Tracks.Clear();
            foreach (var track in entries)
            {
                Tracks.Add(track);
            }

            SelectedTrack = Tracks.FirstOrDefault(item => item.Id == inspected?.Id) ?? inspected;
            RefreshCardTracks();
            _libraryPageIndex = preservePage ? Math.Clamp(_libraryPageIndex, 0, Math.Max(0, (Tracks.Count - 1) / LibraryPageSize)) : 0;
            RefreshVisibleTracks();
            OnPropertyChanged(nameof(LibrarySummary));
        }

        /// <summary>Refreshes local playlists while preserving the selected identity.</summary>
        /// <returns>A task representing the read.</returns>
        private async Task ReloadPlaylistsAsync()
        {
            var selectedId = SelectedPlaylist?.Id;
            var playlists = (await _workspace.GetPlaylistsAsync(_lifetime.Token)).ToList();
            playlists.AddRange(_cloudPlaylists.Values.SelectMany(items => items));
            _favorites = playlists.FirstOrDefault(item => item.IsFavorite || item.Id == FavoritesId);
            if (_favorites is null)
            {
                _favorites = new MusicPlaylist { Id = FavoritesId, Name = "我喜欢", IsFavorite = true };
                await _workspace.SavePlaylistAsync(_favorites, _lifetime.Token);
                playlists.Add(_favorites);
            }

            _favoriteHashes.Clear();
            var favoriteIds = _favorites.TrackIds.ToHashSet(StringComparer.Ordinal);
            if (favoriteIds.Count > 0)
            {
                var all = await _workspace.SearchAsync(null, _lifetime.Token);
                foreach (var track in all)
                {
                    if (favoriteIds.Contains(track.Id) && TrackMajHash(track) is { } hash)
                    {
                        _favoriteHashes.Add(hash);
                    }
                }
            }

            foreach (var entry in _favorites.MajCollection?.Entries ?? Array.Empty<MajCollectionEntry>())
            {
                if (entry.TrackId is null || favoriteIds.Contains(entry.TrackId))
                {
                    _favoriteHashes.Add(entry.Hash);
                }
            }

            _lifetime.Token.ThrowIfCancellationRequested();
            var library = (await _workspace.SearchAsync(null, _lifetime.Token)).ToDictionary(item => item.Id, StringComparer.Ordinal);
            Playlists.Clear();
            foreach (var playlist in playlists.OrderByDescending(item => item.IsFavorite))
            {
                var summary = PlaylistContentClassifier.GetSummary(playlist, library);
                Playlists.Add(playlist with
                {
                    ContentKind = summary.Kind,
                    UnresolvedCount = summary.UnresolvedCount,
                    SourceName = playlist.IsCloud ? EndpointProfiles.FirstOrDefault(item => item.Configuration.Id == playlist.EndpointId)?.DisplayName : null
                });
            }

            var favoritesId = _favorites.Id;
            _favorites = Playlists.FirstOrDefault(item => item.Id == favoritesId);
            SelectedPlaylist = Playlists.FirstOrDefault(item => item.Id == selectedId) ?? _favorites;
            RefreshPlaylistPages();
            OnPropertyChanged(nameof(SelectedHomePlaylist));
            OnPropertyChanged(nameof(FavoriteButtonText));
            OnPropertyChanged(nameof(IsSelectedFavorite));
            OnPropertyChanged(nameof(ExportFileName));
            if (!Playlists.Any(item => item.Id == _activePlaylistId))
            {
                _activePlaylistId = null;
            }
        }

        /// <summary>Toggles a favorite by portable identity when available, otherwise by local entry.</summary>
        /// <returns>A task representing the local favorite update.</returns>
        private async Task ToggleFavoriteAsync()
        {
            if (SelectedTrack is not { } track || _favorites is not { } favorites)
            {
                return;
            }

            MusicPlaylist updated;
            if (IsSelectedFavorite)
            {
                var hash = TrackMajHash(track);
                var all = await _workspace.SearchAsync(null, _lifetime.Token);
                var removedIds = all.Where(item => item.Id == track.Id || hash is not null && TrackMajHash(item) == hash)
                    .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                updated = favorites with
                {
                    TrackIds = favorites.TrackIds.Where(id => !removedIds.Contains(id)).ToArray(),
                    MajCollection = favorites.MajCollection is { } definition ? definition with
                    {
                        Entries = definition.Entries.Where(entry => entry.Hash != hash
                            && (entry.TrackId is null || !removedIds.Contains(entry.TrackId))).ToArray()
                    } : null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            else
            {
                updated = favorites with
                {
                    TrackIds = favorites.TrackIds.Append(track.Id).Distinct(StringComparer.Ordinal).ToArray(),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }

            await _workspace.SavePlaylistAsync(updated, _lifetime.Token);
            await ReloadPlaylistsAsync();
            if (_activePlaylistId == FavoritesId)
            {
                await ReloadLibraryAsync();
            }

            Status = "我喜欢已更新";
        }

        /// <summary>Opens the local favorites collection.</summary>
        /// <returns>A task representing the local list refresh.</returns>
        private Task OpenFavoritesAsync()
        {
            SelectedPlaylist = _favorites;
            UseHomeMode(HomeBrowseMode.Playlists);
            _activePlaylistId = FavoritesId;
            return ReloadLibraryAsync();
        }

        /// <summary>Reads a source-compatible favorite identity without replacing the path-based entry identity.</summary>
        /// <param name="track">The music entry.</param>
        /// <returns>The portable source hash, or null when unavailable.</returns>
        private static string? TrackMajHash(MusicTrack track)
        {
            return track.MajChartHash ?? (track.RemoteId is not null ? track.Revision : null);
        }

        /// <summary>Formats actual stream metadata without assigning a subjective quality label.</summary>
        /// <param name="info">The current parsed stream, or null before preparation.</param>
        /// <returns>A readable codec, bitrate, sample rate, channel and size summary.</returns>
        private static string FormatAudioDetails(AudioStreamInfo? info)
        {
            if (info is null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.CodecName))
            {
                parts.Add(info.CodecName);
            }

            if (info.BitrateBitsPerSecond is { } bitrate)
            {
                parts.Add($"{(info.IsBitrateEstimated ? "约 " : "")}{bitrate / 1000d:0.#} kbps{(info.IsBitrateEstimated ? "（文件平均估算）" : "")}");
            }

            if (info.SampleRateHz is { } sampleRate)
            {
                parts.Add($"{sampleRate / 1000d:0.##} kHz");
            }

            if (info.Channels is { } channels)
            {
                parts.Add($"{channels} 声道");
            }

            if (info.FileSizeBytes is { } bytes)
            {
                parts.Add($"{bytes / 1048576d:0.##} MiB");
            }

            return parts.Count == 0 ? "音频参数未知" : string.Join(" · ", parts);
        }

        /// <summary>Creates a named local playlist.</summary>
        /// <returns>A task representing persistence.</returns>
        /// <exception cref="ArgumentException">The name is blank.</exception>
        private async Task CreatePlaylistAsync()
        {
            var name = NewPlaylistName.Trim();
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            await _workspace.SavePlaylistAsync(new MusicPlaylist { Id = Guid.NewGuid().ToString("N"), Name = name }, _lifetime.Token);
            await ReloadPlaylistsAsync();
            NewPlaylistName = string.Empty;
            Status = $"已创建播放列表：{name}";
        }

        /// <summary>Adds the selected music to the selected local playlist.</summary>
        /// <returns>A task representing persistence.</returns>
        private async Task AddToPlaylistAsync()
        {
            if (SelectedTrack is not { } track || SelectedPlaylist is not { IsCloud: false } playlist)
            {
                return;
            }

            var updated = playlist with
            {
                TrackIds = playlist.TrackIds.Contains(track.Id, StringComparer.Ordinal)
                    ? playlist.TrackIds : playlist.TrackIds.Append(track.Id).ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _workspace.SavePlaylistAsync(updated, _lifetime.Token);
            await ReloadPlaylistsAsync();
            if (_activePlaylistId == playlist.Id)
            {
                await ReloadLibraryAsync();
            }

            Status = $"已加入：{playlist.Name}";
        }

        /// <summary>Explicitly opens the selected playlist in its saved order.</summary>
        /// <returns>A task representing the local refresh.</returns>
        private Task OpenPlaylistAsync()
        {
            UseHomeMode(HomeBrowseMode.Playlists);
            _activePlaylistId = SelectedPlaylist?.Id;
            return ReloadLibraryAsync();
        }

        /// <summary>Clears the active playlist filter without changing the add target.</summary>
        /// <returns>A task representing the local refresh.</returns>
        private Task ShowAllAsync()
        {
            UseHomeMode(HomeBrowseMode.Library);
            return ReloadLibraryAsync();
        }

        /// <summary>Saves one edited source while preserving additional configured sources.</summary>
        /// <returns>A task representing persistence.</returns>
        /// <exception cref="ArgumentException">The edited source address is invalid.</exception>
        private async Task SaveSettingsAsync()
        {
            _settings = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
            var endpoints = _settings.Endpoints.ToList();
            var activeId = SelectedEndpointProfile?.Configuration.Id;
            var changedSource = false;
            if (!string.IsNullOrWhiteSpace(EndpointUrl))
            {
                if (!Uri.TryCreate(EndpointUrl.Trim(), UriKind.Absolute, out var uri))
                {
                    throw new ArgumentException("请输入有效的 HTTPS 接入点地址。");
                }

                var index = endpoints.FindIndex(item => item.Id == activeId);
                var endpoint = new EndpointConfiguration
                {
                    Id = index >= 0 ? endpoints[index].Id : Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(EndpointName) ? "自定义来源" : EndpointName.Trim(),
                    BaseUri = uri,
                    ServerId = index >= 0 && endpoints[index].BaseUri == uri ? endpoints[index].ServerId : null,
                    Accounts = index >= 0 ? endpoints[index].Accounts : Array.Empty<SavedEndpointAccount>(),
                    SelectedAccountId = index >= 0 ? endpoints[index].SelectedAccountId : null
                };
                if (index >= 0)
                {
                    changedSource = endpoints[index].Name != endpoint.Name || endpoints[index].BaseUri != uri;
                    if (endpoints[index].BaseUri != uri)
                    {
                        ClearAccountSnapshots(endpoint.Id);
                    }

                    endpoints[index] = endpoint;
                }
                else
                {
                    endpoints.Add(endpoint);
                }

                activeId = endpoint.Id;
            }

            var settings = _settings with { OnlineEnabled = OnlineEnabled, ActiveEndpointId = activeId, Endpoints = endpoints };
            await _workspace.SaveSettingsAsync(settings, _lifetime.Token);
            _settings = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
            ReloadEndpointProfiles(activeId);
            if (changedSource)
            {
                await ReloadPlaylistsAsync();
                await ReloadLibraryAsync();
            }

            Status = "设置已保存";
        }

        /// <summary>Synchronizes metadata only after an explicit user action.</summary>
        /// <returns>A task representing synchronization.</returns>
        /// <exception cref="InvalidOperationException">No source is configured.</exception>
        private async Task SynchronizeAsync()
        {
            await SaveSettingsAsync();
            var endpoint = _settings.Endpoints.FirstOrDefault(item => item.Id == _settings.ActiveEndpointId)
                ?? throw new InvalidOperationException("请先填写接入点地址。");
            var count = await _workspace.SynchronizeAsync(endpoint, _lifetime.Token);
            _remoteMetadataChecked.Clear();
            _settings = await _workspace.LoadSettingsAsync(_lifetime.Token);
            ReloadEndpointProfiles(endpoint.Id);
            await ReloadLibraryAsync();
            Status = $"已同步 {count} 首音乐";
        }

        /// <summary>Protects a complete audio download from cache cleanup.</summary>
        /// <returns>A task representing the download.</returns>
        private async Task DownloadAsync()
        {
            if (SelectedTrack is { } track)
            {
                await _workspace.ResolveAudioAsync(track, true, _lifetime.Token);
                var page = _libraryPageIndex;
                await ReloadLibraryAsync();
                if (page > 0)
                {
                    _libraryPageIndex = Math.Min(page, Math.Max(0, (Tracks.Count - 1) / LibraryPageSize));
                    RefreshVisibleTracks();
                }

                Status = $"已保存离线：{track.Title}";
            }
        }

        /// <summary>Clears evictable resources without removing imports or pinned downloads.</summary>
        /// <returns>A task representing cleanup.</returns>
        private async Task ClearCacheAsync()
        {
            await _workspace.ClearCacheAsync(_lifetime.Token);
            Status = "缓存已清理";
        }

        /// <summary>Resumes the loaded resource or starts the selected track when nothing is paused.</summary>
        /// <returns>A task representing resumed or newly started playback.</returns>
        private async Task PlayAsync()
        {
            if (CanResume)
            {
                await _playback.ResumeAsync(_lifetime.Token);
                return;
            }

            await StartSelectedTrackAsync();
        }

        /// <summary>Explicitly starts the browsing selection independently of a paused queue entry.</summary>
        /// <returns>A task representing selected-track playback.</returns>
        private async Task StartSelectedTrackAsync()
        {
            if (SelectedTrack is { } track)
            {
                if (_playback.Snapshot is { State: PlaybackState.Playing } current && current.Track?.Id == track.Id)
                {
                    return;
                }

                var index = Tracks.IndexOf(track);
                if (index >= 0)
                {
                    await _playback.SetQueueAsync(Tracks.ToArray(), index, _lifetime.Token);
                }
            }
        }

        /// <summary>Observes the audio owner independently of the browsing selection.</summary>
        /// <param name="sender">The notifying playback owner.</param>
        /// <param name="eventArgs">The state notification.</param>
        private void OnPlaybackStateChanged(object? sender, EventArgs eventArgs)
        {
            if (Volatile.Read(ref _isDisposing))
            {
                return;
            }

            var snapshot = _playback.Snapshot;
            lock (_seekGate)
            {
                if ((_isSeeking || _seekCancellation is not null)
                    && (snapshot.State is PlaybackState.Loading or PlaybackState.Empty or PlaybackState.Stopped or PlaybackState.Failed or PlaybackState.Completed
                        || snapshot.Track?.Id != _seekTrackId))
                {
                    _seekVersion++;
                    _seekInvalidated = true;
                    _seekCancellation?.Cancel();
                }
            }

            _dispatch(() =>
            {
                if (!Volatile.Read(ref _isDisposing))
                {
                    var current = _playback.Snapshot;
                    var changed = _playbackSnapshot.State != current.State || _playbackSnapshot.Track?.Id != current.Track?.Id
                        || _playbackSnapshot.Error != current.Error;
                    var audioChanged = !ReferenceEquals(_playbackSnapshot.AudioInfo, current.AudioInfo);
                    _playbackSnapshot = current;
                    SyncAudioOutputState();
                    PublishPlaybackMetadata(current);
                    if (_seekInvalidated && _isSeeking)
                    {
                        CancelSeek();
                    }

                    NotifyTimeline();
                    if (audioChanged || changed)
                    {
                        OnPropertyChanged(nameof(AudioDetails));
                        OnPropertyChanged(nameof(HasAudioDetails));
                    }
                    if (changed)
                    {
                        OnPropertyChanged(nameof(PlaybackTitle));
                        OnPropertyChanged(nameof(PlaybackArtist));
                        OnPropertyChanged(nameof(PlayButtonText));
                        OnPropertyChanged(nameof(TransportButtonText));
                        OnPropertyChanged(nameof(IsPlaying));
                        OnPropertyChanged(nameof(PlaybackHint));
                        OnPropertyChanged(nameof(HasPlaybackHint));
                        RefreshCommands();
                    }
                }
            });
        }

        /// <summary>Notifies timeline bindings without submitting a playback operation.</summary>
        private void NotifyTimeline()
        {
            OnPropertyChanged(nameof(PositionSeconds));
            OnPropertyChanged(nameof(DurationSeconds));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(CanSeek));
            OnPropertyChanged(nameof(IsSeeking));
        }

        /// <summary>Clamps a proposed position to the known native media timeline.</summary>
        /// <param name="seconds">The proposed position in seconds.</param>
        /// <returns>A finite nonnegative position within the known duration.</returns>
        private double ClampPosition(double seconds)
        {
            var duration = _playbackSnapshot.Duration?.TotalSeconds;
            return Math.Clamp(seconds, 0, duration is > 0 ? duration.Value : double.MaxValue);
        }

        /// <summary>Formats media time without treating a long track as a wall-clock time.</summary>
        /// <param name="time">The nonnegative media time.</param>
        /// <returns>A minutes/seconds or hours/minutes/seconds label.</returns>
        private static string FormatTime(TimeSpan time)
        {
            return time.TotalHours >= 1
                ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{(long)time.TotalMinutes:00}:{time.Seconds:00}";
        }

        /// <summary>Refreshes all command availability checks.</summary>
        private void RefreshCommands()
        {
            foreach (var command in new[] { SearchCommand, CreatePlaylistCommand, AddToPlaylistCommand, OpenPlaylistCommand, ShowAllCommand, SaveSettingsCommand,
                SynchronizeCommand, DownloadCommand, ClearCacheCommand, PlayCommand, PauseCommand, NextCommand, PreviousCommand,
                ToggleFavoriteCommand, OpenFavoritesCommand, RescanCommand, NewEndpointCommand, RemoveEndpointCommand,
                LoginCommand, GuestCommand, SynchronizeAllCommand, RefreshCloudPlaylistsCommand, RecommendationsCommand,
                MoreRecommendationsCommand, PreviousLibraryPageCommand, NextLibraryPageCommand, NewAccountCommand,
                SaveAccountCommand, RemoveAccountCommand, NewDirectoryCommand, SaveDirectoryCommand, ScanDirectoryCommand, RemoveDirectoryCommand,
                RefreshOutputDevicesCommand, SaveAudioSettingsCommand, TogglePlayPauseCommand,
                ToggleShuffleCommand, CycleRepeatModeCommand,
                PreviousPlaylistPageCommand, NextPlaylistPageCommand, PreviousDirectoryPageCommand, NextDirectoryPageCommand,
                ShowRecommendationsModeCommand, ShowPlaylistsModeCommand, ShowLibraryModeCommand, BeginSearchCommand, CancelSearchCommand })
            {
                command?.NotifyCanExecuteChanged();
            }
        }

        /// <summary>Updates a property and notifies the view.</summary>
        /// <typeparam name="T">The property value type.</typeparam>
        /// <param name="field">The backing field.</param>
        /// <param name="value">The new value.</param>
        /// <param name="name">The property name.</param>
        /// <returns>Whether the value changed.</returns>
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(name);
            return true;
        }

        /// <summary>Notifies bindings of a changed property.</summary>
        /// <param name="name">The property name.</param>
        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Marshals audio notifications to Avalonia's presentation thread.</summary>
        /// <param name="action">The notification to execute.</param>
        private static void DispatchToUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        }
    }
}
