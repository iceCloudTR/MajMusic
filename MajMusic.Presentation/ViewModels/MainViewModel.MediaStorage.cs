#nullable enable

using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Edits the application's media location without relocating private preferences or importing directory contents.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Stores the edited media root used by the application's own completed files.</summary>
        private string _mediaStorageDirectory = string.Empty;

        /// <summary>Gets or sets the host's explicit authorization step for the requested ordinary path or document tree.</summary>
        public Func<string, Task<bool>>? EnsureFileAccessAsync { get; set; }
        /// <summary>Gets or sets the host's explicit global file-permission action, independently of a valid SAF grant.</summary>
        public Func<Task<bool>>? RequestAllFilesAccessAsync { get; set; }
        /// <summary>Gets or sets the host's permission-aware conversion of a supported document tree to an ordinary path.</summary>
        public Func<string, string>? ResolveFileSystemPath { get; set; }

        /// <summary>Gets the saved media root currently used by the workspace, independently of unsaved editor values.</summary>
        public string AppliedMediaStoragePath => _settings.MediaStorageDirectory ?? _workspace.DataDirectory;

        /// <summary>Gets or sets the editable ordinary path or native document-tree reference.</summary>
        public string MediaStoragePath
        {
            get => _mediaStorageDirectory;
            set
            {
                if (SetField(ref _mediaStorageDirectory, value))
                {
                    SaveMediaStorageCommand?.NotifyCanExecuteChanged();
                    RequestFileAccessCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>Gets the action that applies the explicitly selected media root.</summary>
        public AsyncCommand SaveMediaStorageCommand { get; private set; } = null!;
        /// <summary>Gets the explicit permission action that applies the current directory after authorization succeeds.</summary>
        public AsyncCommand RequestFileAccessCommand { get; private set; } = null!;

        /// <summary>Restores only the media editor and its command after local application preferences have loaded.</summary>
        private void InitializeMediaStorageSettings()
        {
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
            var directory = _settings.MediaStorageDirectory is { } saved ? resolve(saved) : null;
            if (directory == _settings.MediaStorageDirectory)
            {
                return;
            }
            await _workspace.SaveSettingsAsync(_settings with { MediaStorageDirectory = directory }, _lifetime.Token);
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
        /// <returns>A task representing authorization, persistence and refreshed existing media availability.</returns>
        public Task ApplyPickedMediaStorageAsync(string reference)
        {
            return RunBusyAsync(async () =>
            {
                SetPickedMediaStorage(reference);
                await SaveMediaStorageAsync();
            });
        }

        /// <summary>Merges only media preferences into the latest snapshot without scanning the destination as an imported library.</summary>
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
            var current = await _workspace.LoadSettingsAsync(_lifetime.Token);
            var previousRoot = AppliedMediaStoragePath;
            await _workspace.SaveSettingsAsync(current with
            {
                MediaStorageDirectory = string.IsNullOrWhiteSpace(_mediaStorageDirectory) ? null : LocalDirectorySettings.NormalizePath(_mediaStorageDirectory.Trim())
            }, _lifetime.Token);
            _settings = await _workspace.LoadSettingsAsync(_lifetime.Token);
            OnPropertyChanged(nameof(AppliedMediaStoragePath));
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            InvalidateMediaStorageBrowse(!comparer.Equals(previousRoot, AppliedMediaStoragePath));
            await ReloadPlaylistsAsync();
            await ReloadLibraryAsync();
            Status = "媒体目录已应用";
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
            _residentBrowseVersion = -1;
            DisposeBrowsePreviews();
            foreach (var page in _browsePages)
            {
                InvalidateBrowsePage(page);
            }
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
}
