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
        /// <summary>Cancels the preceding merged home update.</summary>
        private CancellationTokenSource? _homeUpdateCancellation;
        /// <summary>Exposes completion of the latest merged home update.</summary>
        private Task _homeUpdate = Task.CompletedTask;
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
        /// <summary>Gets whether the current online account has another finite recommendation page.</summary>
        public bool HasMoreRecommendations => IsRecommendationsMode && HasSelectedAccount && _hasMoreRecommendations;
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
        /// <summary>Gets the command selecting recommendations.</summary>
        public AsyncCommand ShowRecommendationsModeCommand { get; private set; } = null!;
        /// <summary>Gets the command selecting playlists.</summary>
        public AsyncCommand ShowPlaylistsModeCommand { get; private set; } = null!;
        /// <summary>Gets the command selecting the indexed music library.</summary>
        public AsyncCommand ShowLibraryModeCommand { get; private set; } = null!;
        /// <summary>Gets the command expanding the home search field.</summary>
        public AsyncCommand BeginSearchCommand { get; private set; } = null!;
        /// <summary>Gets the command closing search and returning to recommendations.</summary>
        public AsyncCommand CancelSearchCommand { get; private set; } = null!;

        /// <summary>Creates content-navigation actions without blocking transport controls.</summary>
        private void InitializeHomeCommands()
        {
            ShowRecommendationsModeCommand = CreateHomeModeCommand(HomeBrowseMode.Recommendations);
            ShowPlaylistsModeCommand = CreateHomeModeCommand(HomeBrowseMode.Playlists);
            ShowLibraryModeCommand = CreateHomeModeCommand(HomeBrowseMode.Library);
            BeginSearchCommand = new AsyncCommand(() => _homeUpdate = RunTrackedAsync(async () =>
            {
                UseHomeMode(HomeBrowseMode.Recommendations);
                _isSearchExpanded = true;
                NotifyHomeState();
                await ReloadLibraryAsync();
                await EnsureHomeRecommendationsAsync();
            }), ReportError, () => !Volatile.Read(ref _isDisposing));
            CancelSearchCommand = CreateHomeModeCommand(HomeBrowseMode.Recommendations);
        }

        /// <summary>Creates a mode command that leaves asynchronous transport commands available.</summary>
        /// <param name="mode">The destination home mode.</param>
        /// <returns>The observed navigation action.</returns>
        private AsyncCommand CreateHomeModeCommand(HomeBrowseMode mode)
        {
            return new AsyncCommand(() => _homeUpdate = RunTrackedAsync(async () =>
            {
                UseHomeMode(mode);
                await ReloadLibraryAsync();
                if (mode == HomeBrowseMode.Recommendations)
                {
                    await EnsureHomeRecommendationsAsync();
                }
            }), ReportError, () => !Volatile.Read(ref _isDisposing));
        }

        /// <summary>Attaches the initial source account without initiating a network request.</summary>
        private void InitializeHomeState()
        {
            _homeInitialized = true;
            AttachRecommendationSnapshot();
            NotifyHomeState();
        }

        /// <summary>Commits one stable home mode while retaining the inspected music entry.</summary>
        /// <param name="mode">The requested content mode.</param>
        internal void UseHomeMode(HomeBrowseMode mode)
        {
            _changingHomeState = true;
            try
            {
                _homeMode = mode;
                _isSearchExpanded = false;
                SearchText = string.Empty;
                _activePlaylistId = mode == HomeBrowseMode.Playlists ? SelectedPlaylist?.Id : null;
                AttachRecommendationSnapshot();
            }
            finally
            {
                _changingHomeState = false;
            }

            _homeVersion++;
            _homeUpdateCancellation?.Cancel();
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
                _homeMode = HomeBrowseMode.Recommendations;
                _activePlaylistId = null;
                AttachRecommendationSnapshot();
            }

            _homeVersion++;
            NotifyHomeState();
            QueueHomeUpdate(IsSearching);
        }

        /// <summary>Runs an explicit search or refresh request from the existing host command.</summary>
        /// <returns>A task representing the resulting content read.</returns>
        private async Task ExecuteSearchAsync()
        {
            _homeUpdateCancellation?.Cancel();
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                _isSearchExpanded = true;
                _homeVersion++;
                NotifyHomeState();
            }
            else if (IsSearchExpanded)
            {
                _homeMode = HomeBrowseMode.Recommendations;
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
            _homeUpdate = RunTrackedAsync(async () =>
            {
                try
                {
                    if (debounce)
                    {
                        await Task.Delay(180, cancellation.Token);
                    }

                    await ReloadLibraryCoreAsync(cancellation.Token);
                    if (IsRecommendationsMode)
                    {
                        await EnsureHomeRecommendationsAsync();
                    }
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
                    if (ReferenceEquals(_homeUpdateCancellation, cancellation))
                    {
                        _homeUpdateCancellation = null;
                    }

                    cancellation.Dispose();
                }
            });
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
                    QueueHomeUpdate(false);
                }
            }
        }

        /// <summary>Loads recommendations after an eligible account becomes available.</summary>
        /// <returns>A task representing an existing or newly started finite feed request.</returns>
        public Task EnsureHomeRecommendationsAsync()
        {
            if (!IsRecommendationsMode || _onlineWorkspace is null || !HasSelectedAccount
                || CurrentRecommendationOwner() is not { } owner || _homeSnapshots.ContainsKey(owner))
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

            if (!force && _homeSnapshots.ContainsKey(owner))
            {
                return Task.CompletedTask;
            }

            if (_recommendationRequestOwner == owner && !_recommendationRequest.IsCompleted)
            {
                return _recommendationRequest;
            }

            _recommendationRequestCancellation?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _recommendationRequestCancellation = cancellation;
            _recommendationRequestOwner = owner;
            var endpoint = RequireSelectedEndpoint();
            _recommendationRequest = RunTrackedAsync(async () =>
            {
                try
                {
                    var page = await _onlineWorkspace.GetRecommendationsAsync(endpoint, 0, 50, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!OnlineEnabled || CurrentRecommendationOwner() != owner)
                    {
                        return;
                    }

                    _homeSnapshots[owner] = new RecommendationSnapshot(page.ToList(), 1, page.Count == 50);
                    AttachRecommendationSnapshot();
                    if (IsRecommendationsMode)
                    {
                        await ReloadLibraryCoreAsync(cancellation.Token);
                    }
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
                _recommendationNextPage = snapshot.NextPage;
                _hasMoreRecommendations = snapshot.HasMore;
            }
            else
            {
                _recommendationTracks = null;
                _recommendationEndpointId = null;
                _recommendationNextPage = 0;
                _hasMoreRecommendations = false;
            }
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
            RefreshCommands();
        }

        /// <summary>Retains one finite account recommendation snapshot and its continuation cursor.</summary>
        /// <param name="Tracks">The loaded entries in server order.</param>
        /// <param name="NextPage">The next server page to request.</param>
        /// <param name="HasMore">Whether the last page was full.</param>
        private sealed record RecommendationSnapshot(List<MusicTrack> Tracks, int NextPage, bool HasMore);
    }
}
