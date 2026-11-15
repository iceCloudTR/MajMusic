#nullable enable

using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Identifies the content displayed in the player's home list.</summary>
    public enum HomeBrowseMode
    {
        /// <summary>Displays the current account's recommendation snapshot.</summary>
        Recommendations,
        /// <summary>Displays an explicitly opened playlist.</summary>
        Playlists,
        /// <summary>Displays the complete indexed music library.</summary>
        Library
    }

    /// <summary>Owns the home mode, progressive search and account-scoped recommendation snapshots.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Stores the home mode independently of the transient search field.</summary>
        private HomeBrowseMode _homeMode = HomeBrowseMode.Recommendations;
        /// <summary>Tracks whether the search field is expanded.</summary>
        private bool _isSearchExpanded;
        /// <summary>Suppresses search scheduling while committing an explicit mode change.</summary>
        private bool _changingHomeState;
        /// <summary>Defers source-change work until initial preferences have loaded.</summary>
        private bool _homeInitialized;
        /// <summary>Rejects old local queries after the visible mode or search changes.</summary>
        private long _homeVersion;
        /// <summary>Orders catalog reads even when a playlist mutation retains the same browsing mode.</summary>
        private long _browseReadVersion;
        /// <summary>Cancels the preceding merged home update.</summary>
        private CancellationTokenSource? _homeUpdateCancellation;
        /// <summary>Exposes completion of the latest merged home update.</summary>
        private Task _homeUpdate = Task.CompletedTask;
        /// <summary>Identifies the surface transition started by the latest browsing intent.</summary>
        private long _browseTransitionVersion;
        /// <summary>Enters later home modes from the right and earlier modes from the left.</summary>
        private int _browseTransitionDirection = 1;
        /// <summary>Identifies the only operation allowed to finish the current loading presentation.</summary>
        private long _browseLoadOwner;
        /// <summary>Retains the current target's loading state independently of the displayed catalog.</summary>
        private bool _isBrowseLoading;
        /// <summary>Preserves finite feeds separately for each source/account choice during this view lifetime.</summary>
        private readonly Dictionary<string, RecommendationSnapshot> _homeSnapshots = new(StringComparer.Ordinal);
        /// <summary>Identifies the account whose cached feed is currently attached.</summary>
        private string? _attachedRecommendationOwner;
        /// <summary>Identifies the owner of the current network feed request.</summary>
        private string? _recommendationRequestOwner;
        /// <summary>Cancels a recommendation request after its source account changes.</summary>
        private CancellationTokenSource? _recommendationRequestCancellation;
        /// <summary>Coalesces requests for the same recommendation owner.</summary>
        private Task _recommendationRequest = Task.CompletedTask;

        /// <summary>Gets or sets the normal home browsing mode.</summary>
        public HomeBrowseMode HomeMode
        {
            get => _homeMode;
            set
            {
                if (_homeMode != value || IsSearchExpanded)
                {
                    UseHomeMode(value);
                    QueueHomeUpdate(false);
                }
            }
        }

        /// <summary>Gets whether the recommendation segment is selected.</summary>
        public bool IsRecommendationsMode => _homeMode == HomeBrowseMode.Recommendations && !IsSearching;
        /// <summary>Gets whether the playlist segment is selected.</summary>
        public bool IsPlaylistsMode => _homeMode == HomeBrowseMode.Playlists && !IsSearching;
        /// <summary>Gets whether the library segment is selected.</summary>
        public bool IsLibraryMode => _homeMode == HomeBrowseMode.Library && !IsSearching;
        /// <summary>Gets whether the search input is expanded.</summary>
        public bool IsSearchExpanded => _isSearchExpanded;
        /// <summary>Gets whether a nonempty search currently owns the home list.</summary>
        public bool IsSearching => _isSearchExpanded && !string.IsNullOrWhiteSpace(SearchText);
        /// <summary>Gets whether another locally cached recommendation page follows the current page.</summary>
        public bool HasMoreRecommendations => IsRecommendationsMode && _recommendationPageIndex + 1 < RecommendationPageCount;
        /// <summary>Gets or opens the playlist currently browsed at home, independently of the sidebar's insertion target.</summary>
        public MusicPlaylist? SelectedHomePlaylist
        {
            get => Playlists.FirstOrDefault(item => item.Id == _activePlaylistId) ?? SelectedPlaylist;
            set
            {
                if (value is null)
                {
                    return;
                }

                SelectedPlaylist = value;
                UseHomeMode(HomeBrowseMode.Playlists);
                QueueHomeUpdate(false);
            }
        }
        /// <summary>Gets the latest observed mode or search update.</summary>
        public Task HomeUpdate => _homeUpdate;
        /// <summary>Gets whether the latest browsing target is still preparing its content.</summary>
        public bool IsBrowseLoading => _isBrowseLoading;
        /// <summary>Gets the transition generation published immediately when the user chooses new content.</summary>
        public long BrowseTransitionVersion => _browseTransitionVersion;
        /// <summary>Gets the horizontal direction of the latest home navigation, positive for forward.</summary>
        public int BrowseTransitionDirection => _browseTransitionDirection;
        /// <summary>Gets the command selecting recommendations.</summary>
        public AsyncCommand ShowRecommendationsModeCommand { get; private set; } = null!;
        /// <summary>Gets the command selecting playlists.</summary>
        public AsyncCommand ShowPlaylistsModeCommand { get; private set; } = null!;
        /// <summary>Gets the command selecting the indexed music library.</summary>
        public AsyncCommand ShowLibraryModeCommand { get; private set; } = null!;
        /// <summary>Gets the command expanding the home search field.</summary>
        public AsyncCommand BeginSearchCommand { get; private set; } = null!;
        /// <summary>Gets the command closing search while retaining the full music library.</summary>
        public AsyncCommand CancelSearchCommand { get; private set; } = null!;

        /// <summary>Creates content-navigation actions without blocking transport controls.</summary>
        private void InitializeHomeCommands()
        {
            ShowRecommendationsModeCommand = CreateHomeModeCommand(HomeBrowseMode.Recommendations);
            ShowPlaylistsModeCommand = CreateHomeModeCommand(HomeBrowseMode.Playlists);
            ShowLibraryModeCommand = CreateHomeModeCommand(HomeBrowseMode.Library);
            BeginSearchCommand = new AsyncCommand(() =>
            {
                UseHomeMode(HomeBrowseMode.Library);
                _isSearchExpanded = true;
                NotifyHomeState();
                QueueHomeUpdate(false);
                return Task.CompletedTask;
            }, ReportError, () => !Volatile.Read(ref _isDisposing));
            CancelSearchCommand = CreateHomeModeCommand(HomeBrowseMode.Library);
        }

        /// <summary>Creates a mode command that leaves asynchronous transport commands available.</summary>
        /// <param name="mode">The destination home mode.</param>
        /// <returns>The observed navigation action.</returns>
        private AsyncCommand CreateHomeModeCommand(HomeBrowseMode mode)
        {
            return new AsyncCommand(() =>
            {
                UseHomeMode(mode);
                QueueHomeUpdate(false);
                return Task.CompletedTask;
            }, ReportError, () => !Volatile.Read(ref _isDisposing));
        }

        /// <summary>Attaches the initial source account without initiating a network request.</summary>
        private void InitializeHomeState()
        {
            _homeInitialized = true;
            AttachRecommendationSnapshot();
            NotifyHomeState();
            QueueWarmBrowsePreviews();
        }

        /// <summary>Commits one stable home mode while retaining the inspected music entry.</summary>
        /// <param name="mode">The requested content mode.</param>
        internal void UseHomeMode(HomeBrowseMode mode)
        {
            var previousMode = _homeMode;
            var wasSearching = IsSearching;
            var playlistId = mode == HomeBrowseMode.Playlists ? SelectedPlaylist?.Id : null;
            var contentChanged = previousMode != mode || wasSearching || _activePlaylistId != playlistId;
            _changingHomeState = true;
            try
            {
                _homeMode = mode;
                _isSearchExpanded = false;
                SearchText = string.Empty;
                _activePlaylistId = playlistId;
                AttachRecommendationSnapshot();
            }
            finally
            {
                _changingHomeState = false;
            }

            _homeVersion++;
            _homeUpdateCancellation?.Cancel();
            var loadingOwner = BeginBrowseLoad(contentChanged, wasSearching ? -1 : Math.Sign((int)mode - (int)previousMode));
            TryPublishPreparedPreview(loadingOwner);
            NotifyHomeState();
        }

        /// <summary>Schedules progressive whole-library search only while the search field is open.</summary>
        private void OnHomeSearchChanged()
        {
            if (_changingHomeState || !_isSearchExpanded)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(SearchText))
            {
                _homeMode = HomeBrowseMode.Library;
                _activePlaylistId = null;
                AttachRecommendationSnapshot();
            }

            _homeVersion++;
            BeginBrowseLoad(false);
            NotifyHomeState();
            QueueHomeUpdate(IsSearching);
        }

        /// <summary>Runs an explicit search or refresh request from the existing host command.</summary>
        /// <returns>A task representing the resulting content read.</returns>
        private async Task ExecuteSearchAsync()
        {
            _homeUpdateCancellation?.Cancel();
            BeginBrowseLoad(true);
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                _homeMode = HomeBrowseMode.Library;
                _activePlaylistId = null;
                _isSearchExpanded = true;
                _homeVersion++;
                NotifyHomeState();
            }
            else if (IsSearchExpanded)
            {
                _homeMode = HomeBrowseMode.Library;
                _activePlaylistId = null;
                AttachRecommendationSnapshot();
                NotifyHomeState();
            }

            await ReloadLibraryAsync();
            if (IsRecommendationsMode)
            {
                await EnsureHomeRecommendationsAsync();
            }
        }

        /// <summary>Schedules one cancellable home read instead of starting a query for every keystroke.</summary>
        /// <param name="debounce">Whether to merge adjacent typing events.</param>
        private void QueueHomeUpdate(bool debounce)
        {
            _homeUpdateCancellation?.Cancel();
            if (!_homeInitialized || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _homeUpdateCancellation = cancellation;
            var loadingOwner = BeginBrowseLoad(false);
            _homeUpdate = RunTrackedAsync(async () =>
            {
                try
                {
                    if (debounce)
                    {
                        await Task.Delay(180, cancellation.Token);
                    }

                    var recommendations = IsRecommendationsMode ? EnsureHomeRecommendationsAsync() : Task.CompletedTask;
                    await ReloadLibraryCoreAsync(cancellation.Token, manageLoading: false);
                    await recommendations;
                }
                catch (OperationCanceledException)
                {
                    // A newer home state owns the content.
                }
                catch (Exception exception)
                {
                    ReportError(exception);
                }
                finally
                {
                    FinishBrowseLoad(loadingOwner);
                    if (ReferenceEquals(_homeUpdateCancellation, cancellation))
                    {
                        _homeUpdateCancellation = null;
                    }

                    cancellation.Dispose();
                }
            });
        }

        /// <summary>Starts a target presentation before storage, metadata or network work can delay its first frame.</summary>
        /// <param name="transition">Whether the visible surface should begin a new transition.</param>
        /// <param name="direction">The horizontal navigation direction; zero uses the forward direction.</param>
        /// <returns>The owner allowed to finish this loading presentation.</returns>
        private long BeginBrowseLoad(bool transition, int direction = 1)
        {
            var owner = ++_browseLoadOwner;
            if (transition)
            {
                var nextDirection = direction < 0 ? -1 : 1;
                if (_browseTransitionDirection != nextDirection)
                {
                    _browseTransitionDirection = nextDirection;
                    OnPropertyChanged(nameof(BrowseTransitionDirection));
                }

                _browseTransitionVersion++;
                OnPropertyChanged(nameof(BrowseTransitionVersion));
            }

            if (!_isBrowseLoading && _publishedPreviewVersion != _homeVersion)
            {
                _isBrowseLoading = true;
                OnPropertyChanged(nameof(IsBrowseLoading));
            }

            return owner;
        }

        /// <summary>Finishes loading only when this operation still owns the latest presentation.</summary>
        /// <param name="owner">The owner returned when the load started.</param>
        private void FinishBrowseLoad(long owner)
        {
            if (owner == _browseLoadOwner && _isBrowseLoading)
            {
                _isBrowseLoading = false;
                OnPropertyChanged(nameof(IsBrowseLoading));
            }
        }

        /// <summary>Updates the attached feed after the selected source account changes.</summary>
        private void OnHomeEndpointChanged()
        {
            if (!_homeInitialized)
            {
                return;
            }

            var owner = CurrentRecommendationOwner();
            if (_attachedRecommendationOwner != owner)
            {
                _recommendationRequestCancellation?.Cancel();
                AttachRecommendationSnapshot();
                _homeVersion++;
                if (IsRecommendationsMode)
                {
                    BeginBrowseLoad(true);
                    QueueHomeUpdate(false);
                }
            }
        }

        /// <summary>Loads recommendations after an eligible account becomes available.</summary>
        /// <returns>A task representing an existing or newly started finite feed request.</returns>
        public Task EnsureHomeRecommendationsAsync()
        {
            if (!IsRecommendationsMode || _onlineWorkspace is null || !HasSelectedAccount
                || CurrentRecommendationOwner() is not { } owner
                || _homeSnapshots.TryGetValue(owner, out var snapshot) && snapshot.IsComplete)
            {
                return Task.CompletedTask;
            }

            return RequestHomeRecommendationsAsync(false);
        }

        /// <summary>Starts or joins one request for the currently selected account.</summary>
        /// <param name="force">Whether to replace an existing recommendation snapshot.</param>
        /// <returns>The observed request.</returns>
        private Task RequestHomeRecommendationsAsync(bool force)
        {
            if (_onlineWorkspace is null || !HasSelectedAccount || CurrentRecommendationOwner() is not { } owner)
            {
                return Task.CompletedTask;
            }

            if (_recommendationRequestOwner == owner && !_recommendationRequest.IsCompleted)
            {
                return _recommendationRequest;
            }

            if (!force && (_homeSnapshots.TryGetValue(owner, out var snapshot) && snapshot.IsComplete
                || _recommendationAttemptVersions.TryGetValue(owner, out var attemptedVersion) && attemptedVersion == _homeVersion))
            {
                return Task.CompletedTask;
            }

            _recommendationRequestCancellation?.Cancel();
            _recommendationAttemptVersions[owner] = _homeVersion;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _recommendationRequestCancellation = cancellation;
            _recommendationRequestOwner = owner;
            var endpoint = RequireSelectedEndpoint();
            _recommendationRequest = RunTrackedAsync(async () =>
            {
                try
                {
                    await LoadRecommendationBatchAsync(endpoint, owner, force, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    // A changed source account or closed view superseded this feed.
                }
                catch (Exception)
                {
                    if (CurrentRecommendationOwner() == owner)
                    {
                        Status = "推荐加载失败";
                        await ReconcileExpiredSessionsAsync();
                    }
                }
                finally
                {
                    if (ReferenceEquals(_recommendationRequestCancellation, cancellation))
                    {
                        _recommendationRequestCancellation = null;
                    }

                    cancellation.Dispose();
                }
            });
            return _recommendationRequest;
        }

        /// <summary>Gets an account-scoped in-memory cache identity, including an offline remembered selection.</summary>
        /// <returns>The selected source/account identity, or null for Guest.</returns>
        private string? CurrentRecommendationOwner()
        {
            if (SelectedEndpointProfile is not { } profile)
            {
                return null;
            }

            var username = profile.IsLoggedIn ? profile.Username
                : profile.Configuration.Accounts.FirstOrDefault(item => item.Id == profile.Configuration.SelectedAccountId)?.Username;
            return string.IsNullOrEmpty(username) ? null : Uri.EscapeDataString(profile.Configuration.Id) + ":" + Uri.EscapeDataString(username);
        }

        /// <summary>Attaches only the feed belonging to the currently selected source/account.</summary>
        private void AttachRecommendationSnapshot()
        {
            _attachedRecommendationOwner = CurrentRecommendationOwner();
            if (_attachedRecommendationOwner is { } owner && _homeSnapshots.TryGetValue(owner, out var snapshot))
            {
                _recommendationTracks = snapshot.Tracks;
                _recommendationEndpointId = SelectedEndpointProfile?.Configuration.Id;
                _recommendationPageIndex = Math.Clamp(snapshot.PageIndex, 0, Math.Max(0, (snapshot.Tracks.Count - 1) / RecommendationPageSize));
            }
            else
            {
                _recommendationTracks = null;
                _recommendationEndpointId = null;
                _recommendationPageIndex = 0;
            }

            NotifyRecommendationPagination();
        }

        /// <summary>Notifies mode and search bindings without moving the surrounding layout.</summary>
        private void NotifyHomeState()
        {
            OnPropertyChanged(nameof(HomeMode));
            OnPropertyChanged(nameof(IsSearchExpanded));
            OnPropertyChanged(nameof(IsSearching));
            OnPropertyChanged(nameof(IsRecommendationsMode));
            OnPropertyChanged(nameof(IsPlaylistsMode));
            OnPropertyChanged(nameof(IsLibraryMode));
            OnPropertyChanged(nameof(IsCardBrowse));
            OnPropertyChanged(nameof(IsRecommendationsView));
            OnPropertyChanged(nameof(HasMoreRecommendations));
            OnPropertyChanged(nameof(SelectedHomePlaylist));
            OnPropertyChanged(nameof(LibraryViewTitle));
            NotifyRecommendationPagination();
            RefreshCommands();
        }

        /// <summary>Retains one bounded account recommendation batch and its selected local page.</summary>
        /// <param name="Tracks">The loaded entries in server order.</param>
        /// <param name="PageIndex">The zero-based selected page in this account's cached batch.</param>
        /// <param name="NextServerPage">The next uncommitted server page, retained when a batch is interrupted.</param>
        /// <param name="IsComplete">Whether the source ended or the three-page batch limit has been reached.</param>
        private sealed record RecommendationSnapshot(List<MusicTrack> Tracks, int PageIndex, int NextServerPage, bool IsComplete);
    }
}
