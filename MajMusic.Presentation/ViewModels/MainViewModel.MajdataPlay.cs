#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Edits independent and shared media locations without relocating private application preferences.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Retains which of the two independently remembered media roots is currently edited.</summary>
        private bool _useMajdataPlayStorage;
        /// <summary>Stores the edited shared MajdataPlay root.</summary>
        private string _majdataPlayDirectory = string.Empty;
        /// <summary>Stores the edited standalone media root.</summary>
        private string _mediaStorageDirectory = string.Empty;

        /// <summary>Gets or sets the host's explicit authorization step for the requested ordinary path or document tree.</summary>
        public Func<string, Task<bool>>? EnsureFileAccessAsync { get; set; }
        /// <summary>Gets or sets the host's explicit global file-permission action, independently of a valid SAF grant.</summary>
        public Func<Task<bool>>? RequestAllFilesAccessAsync { get; set; }
        /// <summary>Gets or sets the host's permission-aware conversion of a supported document tree to an ordinary path.</summary>
        public Func<string, string>? ResolveFileSystemPath { get; set; }

        /// <summary>Gets the supported storage choices presented in both directory pages.</summary>
        public IReadOnlyList<MediaStorageModeOption> MediaStorageModes { get; } =
        [
            new(false, "独立储存"),
            new(true, "共享 MajdataPlay")
        ];

        /// <summary>Gets or selects the media mode while retaining each mode's edited location.</summary>
        public MediaStorageModeOption? SelectedMediaStorageMode
        {
            get => MediaStorageModes.First(item => item.IsShared == _useMajdataPlayStorage);
            set
            {
                if (value is not null && SetField(ref _useMajdataPlayStorage, value.IsShared, nameof(UseMajdataPlayStorage)))
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MediaStoragePath));
                    SaveMediaStorageCommand?.NotifyCanExecuteChanged();
                    RequestFileAccessCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>Gets whether the edited location shares MajdataPlay's media tree.</summary>
        public bool UseMajdataPlayStorage => _useMajdataPlayStorage;

        /// <summary>Gets the saved media root currently used by the workspace, independently of unsaved editor values.</summary>
        public string AppliedMediaStoragePath => _settings.UseMajdataPlayStorage
            ? _settings.MajdataPlayDirectory ?? (_workspace as IMajdataPlayWorkspace)?.DefaultMajdataPlayDirectory ?? _workspace.DataDirectory
            : _settings.MediaStorageDirectory ?? _workspace.DataDirectory;

        /// <summary>Gets or sets the current mode's editable ordinary path or native document-tree reference.</summary>
        public string MediaStoragePath
        {
            get => _useMajdataPlayStorage ? _majdataPlayDirectory : _mediaStorageDirectory;
            set
            {
                var changed = _useMajdataPlayStorage
                    ? SetField(ref _majdataPlayDirectory, value, nameof(MediaStoragePath))
                    : SetField(ref _mediaStorageDirectory, value, nameof(MediaStoragePath));
                if (changed)
                {
                    SaveMediaStorageCommand?.NotifyCanExecuteChanged();
                    RequestFileAccessCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>Gets the action that saves and reads the explicitly selected media root.</summary>
        public AsyncCommand SaveMediaStorageCommand { get; private set; } = null!;
        /// <summary>Gets the explicit permission action that applies the current directory after authorization succeeds.</summary>
        public AsyncCommand RequestFileAccessCommand { get; private set; } = null!;

        /// <summary>Restores only the media editor and its command after local application preferences have loaded.</summary>
        private void InitializeMajdataPlaySettings()
        {
            _useMajdataPlayStorage = _settings.UseMajdataPlayStorage;
            _majdataPlayDirectory = _settings.MajdataPlayDirectory
                ?? (_workspace as IMajdataPlayWorkspace)?.DefaultMajdataPlayDirectory ?? string.Empty;
            _mediaStorageDirectory = _settings.MediaStorageDirectory ?? _workspace.DataDirectory;
            SaveMediaStorageCommand = CreateCommand(SaveMediaStorageAsync, () => !string.IsNullOrWhiteSpace(MediaStoragePath));
            RequestFileAccessCommand = CreateCommand(async () =>
            {
                if (RequestAllFilesAccessAsync is { } request && !await request())
                {
                    return;
                }
                await SaveMediaStorageAsync();
            }, () => !string.IsNullOrWhiteSpace(MediaStoragePath));
            OnPropertyChanged(nameof(SaveMediaStorageCommand));
            OnPropertyChanged(nameof(RequestFileAccessCommand));
            OnPropertyChanged(nameof(MediaStoragePath));
            OnPropertyChanged(nameof(SelectedMediaStorageMode));
            OnPropertyChanged(nameof(UseMajdataPlayStorage));
            OnPropertyChanged(nameof(AppliedMediaStoragePath));
        }

        /// <summary>Restores ordinary paths from earlier document grants only when the host already has broad file access.</summary>
        /// <returns>A task representing noninteractive preference migration without opening permission UI.</returns>
        private async Task PrepareMediaStorageSettingsAsync()
        {
            if (ResolveFileSystemPath is not { } resolve)
            {
                return;
            }
            var shared = _settings.MajdataPlayDirectory is { } sharedRoot ? resolve(sharedRoot) : null;
            var independent = _settings.MediaStorageDirectory is { } independentRoot ? resolve(independentRoot) : null;
            if (shared == _settings.MajdataPlayDirectory && independent == _settings.MediaStorageDirectory)
            {
                return;
            }
            await _workspace.SaveSettingsAsync(_settings with { MajdataPlayDirectory = shared, MediaStorageDirectory = independent }, _lifetime.Token);
            _settings = await _workspace.LoadSettingsAsync(_lifetime.Token);
        }

        /// <summary>Retains a native writable tree selection without converting it into an ordinary filesystem path.</summary>
        /// <param name="reference">The explicitly granted tree URI or ordinary selected local path.</param>
        public void SetPickedMediaStorage(string reference)
        {
            MediaStoragePath = LocalDirectorySettings.NormalizePath(reference);
        }

        /// <summary>Applies a completed native directory selection immediately instead of leaving an unsaved draft.</summary>
        /// <param name="reference">The explicitly selected ordinary directory or granted document tree.</param>
        /// <returns>A task representing authorization, persistence and one library refresh.</returns>
        public Task ApplyPickedMediaStorageAsync(string reference)
        {
            return RunBusyAsync(async () =>
            {
                SetPickedMediaStorage(reference);
                await SaveMediaStorageAsync();
            });
        }

        /// <summary>Merges only media preferences into the latest snapshot and reads the selected root.</summary>
        /// <returns>A task representing settings publication and refreshed local music.</returns>
        private async Task SaveMediaStorageAsync()
        {
            var requested = LocalDirectorySettings.NormalizePath(MediaStoragePath.Trim());
            if (EnsureFileAccessAsync is { } authorize && !await authorize(requested))
            {
                return;
            }
            _lifetime.Token.ThrowIfCancellationRequested();
            MediaStoragePath = LocalDirectorySettings.NormalizePath(ResolveFileSystemPath?.Invoke(requested) ?? requested);
            var shared = _useMajdataPlayStorage;
            var current = await _workspace.LoadSettingsAsync(_lifetime.Token);
            var previousRoot = AppliedMediaStoragePath;
            await _workspace.SaveSettingsAsync(current with
            {
                UseMajdataPlayStorage = shared,
                MajdataPlayDirectory = string.IsNullOrWhiteSpace(_majdataPlayDirectory) ? null : LocalDirectorySettings.NormalizePath(_majdataPlayDirectory.Trim()),
                MediaStorageDirectory = string.IsNullOrWhiteSpace(_mediaStorageDirectory) ? null : LocalDirectorySettings.NormalizePath(_mediaStorageDirectory.Trim())
            }, _lifetime.Token);
            _settings = await _workspace.LoadSettingsAsync(_lifetime.Token);
            OnPropertyChanged(nameof(AppliedMediaStoragePath));
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            InvalidateMediaStorageBrowse(current.UseMajdataPlayStorage != shared || !comparer.Equals(previousRoot, AppliedMediaStoragePath));
            LocalLibraryScanResult? result = null;
            if (_workspace is IMajdataPlayWorkspace media)
            {
                try
                {
                    result = await media.RefreshMajdataPlayAsync(_lifetime.Token);
                }
                finally
                {
                    if (!_lifetime.IsCancellationRequested)
                    {
                        await ReloadPlaylistsAsync();
                        await ReloadLibraryAsync();
                    }
                }
            }
            else
            {
                await ReloadPlaylistsAsync();
                await ReloadLibraryAsync();
            }
            Status = result is null ? "媒体目录已应用" : result.Issues.Count > 0
                ? $"已读取 {result.Tracks.Count} 首音乐 · {result.Issues[0].Message}"
                : $"已读取 {result.Tracks.Count} 首音乐";
        }

        /// <summary>Rejects in-flight reads and retained paths from a previous media root before publishing the new catalog.</summary>
        /// <param name="rootChanged">Whether account feeds and completed download overlays also contain obsolete local attachments.</param>
        private void InvalidateMediaStorageBrowse(bool rootChanged)
        {
            CancelMetadataRefresh();
            _homeUpdateCancellation?.Cancel();
            _previewWarmCancellation?.Cancel();
            Interlocked.Increment(ref _browseReadVersion);
            _homeVersion++;
            _publishedPreviewVersion = -1;
            DisposeBrowsePreviews();
            _previewLibrary = [];
            _previewLibraryById = new(StringComparer.Ordinal);
            if (rootChanged)
            {
                CancelDownloadedMetadata();
                _remoteMetadataChecked.Clear();
                _cardDownloadStates.Clear();
                _recommendationRequestCancellation?.Cancel();
                _recommendationRequestOwner = null;
                _homeSnapshots.Clear();
                _recommendationAttemptVersions.Clear();
                AttachRecommendationSnapshot();
                SelectedTrack = null;
                CancelCoverLoad();
                var previous = Cover;
                Cover = null;
                _loadedCoverIdentity = null;
                previous?.Dispose();
            }
            UseHomeMode(HomeBrowseMode.Library);
        }
    }

    /// <summary>Labels an independent or MajdataPlay-shared media location.</summary>
    /// <param name="IsShared">Whether the choice uses the shared MajdataPlay root.</param>
    /// <param name="Name">The visible storage-mode label.</param>
    public sealed record MediaStorageModeOption(bool IsShared, string Name);
}
