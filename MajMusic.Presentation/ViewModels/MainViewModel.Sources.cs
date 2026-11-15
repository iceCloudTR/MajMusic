#nullable enable

using System.Collections.ObjectModel;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Connects independently configured accounts, cloud lists and finite recommendation pages.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Provides account-aware operations when the host supports online sources.</summary>
        private readonly IOnlineMusicWorkspace? _onlineWorkspace;
        /// <summary>Retains account-private cloud snapshots only for this view lifetime.</summary>
        private readonly Dictionary<string, IReadOnlyList<MusicPlaylist>> _cloudPlaylists = new(StringComparer.Ordinal);
        /// <summary>Identifies the source configuration being edited.</summary>
        private EndpointProfile? _selectedEndpointProfile;
        /// <summary>Stores the account name entered for the selected configuration.</summary>
        private string _loginUsername = string.Empty;
        /// <summary>Holds the current login input only until its request completes.</summary>
        private string _loginPassword = string.Empty;
        /// <summary>Retains a finite recommendation snapshot independently of library search.</summary>
        private List<MusicTrack>? _recommendationTracks;
        /// <summary>Identifies the account configuration that owns the recommendation snapshot.</summary>
        private string? _recommendationEndpointId;
        /// <summary>Stores the selected local page of the current account's bounded recommendation batch.</summary>
        private int _recommendationPageIndex;

        /// <summary>Gets saved endpoint configurations and their current account labels.</summary>
        public ObservableCollection<EndpointProfile> EndpointProfiles { get; } = new();
        /// <summary>Gets the command starting a new independently authenticated source configuration.</summary>
        public AsyncCommand NewEndpointCommand { get; private set; } = null!;
        /// <summary>Gets the command removing the selected configuration.</summary>
        public AsyncCommand RemoveEndpointCommand { get; private set; } = null!;
        /// <summary>Gets the command logging into the selected configuration.</summary>
        public AsyncCommand LoginCommand { get; private set; } = null!;
        /// <summary>Gets the command returning the selected configuration to Guest.</summary>
        public AsyncCommand GuestCommand { get; private set; } = null!;
        /// <summary>Gets the command synchronizing all saved configurations.</summary>
        public AsyncCommand SynchronizeAllCommand { get; private set; } = null!;
        /// <summary>Gets the command reading account-private cloud collections.</summary>
        public AsyncCommand RefreshCloudPlaylistsCommand { get; private set; } = null!;
        /// <summary>Gets the command opening a refreshed recommendation snapshot.</summary>
        public AsyncCommand RecommendationsCommand { get; private set; } = null!;
        /// <summary>Gets the compatibility action opening the next cached recommendation page without a source request.</summary>
        public AsyncCommand MoreRecommendationsCommand { get; private set; } = null!;
        /// <summary>Gets whether the current library view is a recommendation snapshot.</summary>
        public bool IsRecommendationsView => IsRecommendationsMode;
        /// <summary>Gets the account label of the selected endpoint.</summary>
        public string SelectedAccountLabel => SelectedEndpointProfile?.AccountLabel ?? "Guest";
        /// <summary>Gets the name of the open library or snapshot.</summary>
        public string LibraryViewTitle => IsSearching ? "搜索结果" : _homeMode == HomeBrowseMode.Recommendations ? "猜你喜欢"
            : _homeMode == HomeBrowseMode.Playlists ? Playlists.FirstOrDefault(item => item.Id == _activePlaylistId)?.Name ?? "歌单" : "曲库";
        /// <summary>Gets whether account-only source actions are available.</summary>
        private bool HasSelectedAccount => OnlineEnabled && SelectedEndpointProfile?.IsLoggedIn == true;

        /// <summary>Gets or sets the configuration selected for editing and account actions.</summary>
        public EndpointProfile? SelectedEndpointProfile
        {
            get => _selectedEndpointProfile;
            set
            {
                if (SetField(ref _selectedEndpointProfile, value))
                {
                    EndpointName = value?.Configuration.Name ?? string.Empty;
                    EndpointUrl = value?.Configuration.BaseUri.AbsoluteUri ?? string.Empty;
                    ReloadSavedAccounts(value);
                    OnPropertyChanged(nameof(SelectedAccountLabel));
                    RefreshCommands();
                    OnHomeEndpointChanged();
                }
            }
        }

        /// <summary>Gets or sets the account name for an explicit login.</summary>
        public string LoginUsername
        {
            get => _loginUsername;
            set
            {
                if (SetField(ref _loginUsername, value))
                {
                    LoginCommand?.NotifyCanExecuteChanged();
                    SaveAccountCommand?.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(HasRememberedPassword));
                }
            }
        }

        /// <summary>Gets or sets the transient password input.</summary>
        public string LoginPassword
        {
            get => _loginPassword;
            set
            {
                if (SetField(ref _loginPassword, value))
                {
                    LoginCommand?.NotifyCanExecuteChanged();
                    SaveAccountCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>Creates source actions once the main view commands exist.</summary>
        private void InitializeSourceCommands()
        {
            NewEndpointCommand = CreateCommand(() =>
            {
                SelectedEndpointProfile = null;
                EndpointName = string.Empty;
                EndpointUrl = string.Empty;
                return Task.CompletedTask;
            });
            RemoveEndpointCommand = CreateCommand(RemoveEndpointAsync, () => SelectedEndpointProfile is not null);
            LoginCommand = CreateCommand(LoginAsync, () => _onlineWorkspace is not null && OnlineEnabled
                && !string.IsNullOrWhiteSpace(EndpointUrl) && !string.IsNullOrWhiteSpace(LoginUsername)
                && (!string.IsNullOrEmpty(LoginPassword) || _accountPreferences is not null && HasRememberedPassword));
            GuestCommand = CreateCommand(UseGuestAsync, () => _onlineWorkspace is not null && SelectedEndpointProfile is not null);
            SynchronizeAllCommand = CreateCommand(SynchronizeAllAsync, () => OnlineEnabled && _settings.Endpoints.Count > 0);
            RefreshCloudPlaylistsCommand = CreateCommand(RefreshCloudPlaylistsAsync, () => _onlineWorkspace is not null && HasSelectedAccount);
            RecommendationsCommand = CreateCommand(RefreshRecommendationsAsync, () => _onlineWorkspace is not null && HasSelectedAccount);
            InitializeRecommendationPagingCommands();
            MoreRecommendationsCommand = NextRecommendationPageCommand;
        }

        /// <summary>Rebuilds source labels without contacting a server or changing account ownership.</summary>
        /// <param name="selectedId">The preferred selected configuration identity.</param>
        private void ReloadEndpointProfiles(string? selectedId = null)
        {
            selectedId ??= SelectedEndpointProfile?.Configuration.Id ?? _settings.ActiveEndpointId;
            var sessions = (_onlineWorkspace?.GetEndpointSessions() ?? Array.Empty<EndpointSession>())
                .ToDictionary(item => item.EndpointId, StringComparer.Ordinal);
            EndpointProfiles.Clear();
            foreach (var endpoint in _settings.Endpoints)
            {
                sessions.TryGetValue(endpoint.Id, out var session);
                EndpointProfiles.Add(new EndpointProfile(endpoint, session));
            }

            SelectedEndpointProfile = EndpointProfiles.FirstOrDefault(item => item.Configuration.Id == selectedId)
                ?? EndpointProfiles.FirstOrDefault();
            RefreshEndpointCards();
            RefreshCommands();
        }

        /// <summary>Removes a saved source and its in-memory account-private snapshots.</summary>
        /// <returns>A task representing the saved configuration update.</returns>
        private async Task RemoveEndpointAsync()
        {
            if (SelectedEndpointProfile is not { } profile)
            {
                return;
            }

            var remaining = _settings.Endpoints.Where(item => item.Id != profile.Configuration.Id).ToArray();
            var settings = _settings with
            {
                Endpoints = remaining,
                ActiveEndpointId = remaining.FirstOrDefault()?.Id,
                OnlineEnabled = OnlineEnabled
            };
            await _workspace.SaveSettingsAsync(settings, _lifetime.Token);
            _settings = settings;
            ClearAccountSnapshots(profile.Configuration.Id);
            ReloadEndpointProfiles(settings.ActiveEndpointId);
            await ReloadPlaylistsAsync();
            await ReloadLibraryAsync();
            Status = "接入点已移除";
        }

        /// <summary>Logs into the selected independently configured source.</summary>
        /// <returns>A task representing the explicit login.</returns>
        private async Task LoginAsync()
        {
            var online = _onlineWorkspace ?? throw new NotSupportedException("当前宿主不支持账号登录。");
            var username = LoginUsername.Trim();
            var password = LoginPassword;
            var remember = RememberPassword;
            var automatic = AutoLogin;
            try
            {
                if (string.IsNullOrWhiteSpace(EndpointUrl))
                {
                    throw new InvalidOperationException("请填写接入点地址。");
                }

                await SaveSettingsAsync();
                var endpoint = RequireSelectedEndpoint();
                if (string.IsNullOrEmpty(password) && _accountPreferences is not null && endpoint.SelectedAccountId is { } accountId)
                {
                    await _accountPreferences.LoginSavedAccountAsync(endpoint, accountId, _lifetime.Token);
                }
                else
                {
                    await online.LoginAsync(endpoint, username, password, _lifetime.Token);
                }

                ClearAccountSnapshots(endpoint.Id);
                try
                {
                    if (_accountPreferences is not null)
                    {
                        await _accountPreferences.SaveAccountAsync(endpoint, username, string.IsNullOrEmpty(password) ? null : password,
                            remember, automatic, _lifetime.Token);
                    }
                }
                finally
                {
                    await ReloadAccountStateAsync(endpoint.Id);
                }

                Status = $"{endpoint.Name} · {username}";
                await EnsureHomeRecommendationsAsync();
            }
            finally
            {
                LoginPassword = string.Empty;
            }
        }

        /// <summary>Returns only the selected source to Guest.</summary>
        /// <returns>A task representing the local session replacement.</returns>
        private async Task UseGuestAsync()
        {
            var endpoint = RequireSelectedEndpoint();
            var online = _onlineWorkspace ?? throw new NotSupportedException("当前宿主不支持账号登录。");
            await online.UseGuestAsync(endpoint, _lifetime.Token);
            if (_accountPreferences is not null)
            {
                _settings = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
            }

            ClearAccountSnapshots(endpoint.Id);
            ReloadEndpointProfiles(endpoint.Id);
            await ReloadPlaylistsAsync();
            await ReloadLibraryAsync();
            Status = $"{endpoint.Name} · Guest";
        }

        /// <summary>Reads cloud lists for the selected account.</summary>
        /// <returns>A task representing one account's finite snapshot.</returns>
        private async Task RefreshCloudPlaylistsAsync()
        {
            var endpoint = RequireSelectedEndpoint();
            var online = _onlineWorkspace ?? throw new NotSupportedException("当前宿主不支持云端列表。");
            var lists = await online.GetCloudPlaylistsAsync(endpoint, _lifetime.Token);
            _cloudPlaylists[endpoint.Id] = lists;
            await ReloadPlaylistsAsync();
            await ReloadLibraryAsync();
            Status = $"{endpoint.Name} · {lists.Count} 个云端列表";
        }

        /// <summary>Refreshes the first recommendation page for the selected account.</summary>
        /// <returns>A task representing the finite feed refresh.</returns>
        private async Task RefreshRecommendationsAsync()
        {
            UseHomeMode(HomeBrowseMode.Recommendations);
            await ReloadLibraryAsync();
            await RequestHomeRecommendationsAsync(true);
        }

        /// <summary>Synchronizes saved configurations independently so one unavailable server does not hide others.</summary>
        /// <returns>A task representing the explicitly requested multi-source refresh.</returns>
        private async Task SynchronizeAllAsync()
        {
            var count = 0;
            var failures = 0;
            foreach (var endpoint in _settings.Endpoints)
            {
                try
                {
                    count += await _workspace.SynchronizeAsync(endpoint, _lifetime.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                }
            }

            _settings = await _workspace.LoadSettingsAsync(_lifetime.Token);
            _remoteMetadataChecked.Clear();
            ReloadEndpointProfiles();
            await ReloadLibraryAsync();
            Status = $"已同步 {count} 首音乐" + (failures > 0 ? $"，{failures} 个接入点未完成" : string.Empty);
        }

        /// <summary>Removes private snapshots when their account changes.</summary>
        /// <param name="endpointId">The changed configuration identity.</param>
        private void ClearAccountSnapshots(string endpointId)
        {
            CancelMetadataRefresh();
            _remoteMetadataChecked.Clear();
            _cloudPlaylists.Remove(endpointId);
            var ownerPrefix = Uri.EscapeDataString(endpointId) + ":";
            foreach (var key in _homeSnapshots.Keys.Where(key => key.StartsWith(ownerPrefix, StringComparison.Ordinal)).ToArray())
            {
                _homeSnapshots.Remove(key);
            }

            foreach (var key in _recommendationAttemptVersions.Keys.Where(key => key.StartsWith(ownerPrefix, StringComparison.Ordinal)).ToArray())
            {
                _recommendationAttemptVersions.Remove(key);
            }

            if (_recommendationRequestOwner?.StartsWith(ownerPrefix, StringComparison.Ordinal) == true)
            {
                _recommendationRequestCancellation?.Cancel();
            }
            var removed = Playlists.Where(item => item.IsCloud && item.EndpointId == endpointId).ToArray();
            var clearView = _recommendationEndpointId == endpointId || removed.Any(item => item.Id == _activePlaylistId);
            foreach (var playlist in removed)
            {
                Playlists.Remove(playlist);
            }

            if (SelectedPlaylist is { IsCloud: true } selected && selected.EndpointId == endpointId)
            {
                SelectedPlaylist = _favorites is { } favorites ? VisiblePlaylistForLocal(favorites) : null;
            }

            if (_recommendationEndpointId == endpointId)
            {
                _recommendationTracks = null;
                _recommendationEndpointId = null;
                _recommendationPageIndex = 0;
            }

            if (clearView)
            {
                _activePlaylistId = null;
                Tracks.Clear();
                VisibleTracks.Clear();
                SelectedTrack = null;
                _libraryPageIndex = 0;
                OnPropertyChanged(nameof(LibrarySummary));
                OnPropertyChanged(nameof(LibraryViewTitle));
                OnPropertyChanged(nameof(IsRecommendationsView));
                OnPropertyChanged(nameof(HasLibraryPages));
                RefreshCommands();
            }

            NotifyRecommendationPagination();
        }

        /// <summary>Removes private snapshots when an authenticated request invalidates one source session.</summary>
        /// <returns>A task representing any required list and library refresh.</returns>
        private async Task ReconcileExpiredSessionsAsync()
        {
            if (_onlineWorkspace is null || Volatile.Read(ref _isDisposing))
            {
                return;
            }

            var sessions = _onlineWorkspace.GetEndpointSessions().ToDictionary(item => item.EndpointId, StringComparer.Ordinal);
            var expired = EndpointProfiles.Where(profile => profile.IsLoggedIn
                && (!sessions.TryGetValue(profile.Configuration.Id, out var session) || !session.IsLoggedIn))
                .Select(profile => profile.Configuration.Id).ToArray();
            if (expired.Length == 0)
            {
                return;
            }

            foreach (var endpointId in expired)
            {
                ClearAccountSnapshots(endpointId);
            }

            ReloadEndpointProfiles();
            await ReloadPlaylistsAsync();
            await ReloadLibraryAsync();
        }

        /// <summary>Gets the currently saved source configuration.</summary>
        /// <returns>The selected saved configuration.</returns>
        /// <exception cref="InvalidOperationException">No configuration has been selected.</exception>
        private EndpointConfiguration RequireSelectedEndpoint()
        {
            return SelectedEndpointProfile?.Configuration ?? throw new InvalidOperationException("请选择接入点。");
        }

        /// <summary>Checks the local search keyword against a finite feed snapshot.</summary>
        /// <param name="track">The entry to inspect.</param>
        /// <param name="query">The optional literal search keyword.</param>
        /// <returns>Whether the entry matches.</returns>
        private static bool MatchesQuery(MusicTrack track, string? query)
        {
            return string.IsNullOrWhiteSpace(query) || track.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
                || track.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Pairs one saved source configuration with its current independent account label.</summary>
    public sealed class EndpointProfile
    {
        /// <summary>Creates a display entry without retaining authentication secrets.</summary>
        /// <param name="configuration">The saved source configuration.</param>
        /// <param name="session">The current in-memory account state.</param>
        public EndpointProfile(EndpointConfiguration configuration, EndpointSession? session)
        {
            Configuration = configuration;
            Username = session?.Username;
            IsLoggedIn = session?.IsLoggedIn == true;
        }

        /// <summary>Gets the configured API source.</summary>
        public EndpointConfiguration Configuration { get; }
        /// <summary>Gets whether this configuration has an active login.</summary>
        public bool IsLoggedIn { get; }
        /// <summary>Gets the current account name.</summary>
        public string? Username { get; }
        /// <summary>Gets a short account label.</summary>
        public string AccountLabel => IsLoggedIn ? Username ?? "已登录"
            : Configuration.Accounts.FirstOrDefault(item => item.Id == Configuration.SelectedAccountId) is { } account
                ? account.Username + " · 未登录" : "Guest";
        /// <summary>Gets the source name and account shown together in selectors.</summary>
        public string DisplayName => $"{Configuration.Name} · {AccountLabel}";
    }
}
