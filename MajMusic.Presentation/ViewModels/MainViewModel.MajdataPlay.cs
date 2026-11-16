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
                }
            }
        }

        /// <summary>Gets whether the edited location shares MajdataPlay's media tree.</summary>
        public bool UseMajdataPlayStorage => _useMajdataPlayStorage;

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
                }
            }
        }

        /// <summary>Gets the action that saves and reads the explicitly selected media root.</summary>
        public AsyncCommand SaveMediaStorageCommand { get; private set; } = null!;

        /// <summary>Restores only the media editor and its command after local application preferences have loaded.</summary>
        private void InitializeMajdataPlaySettings()
        {
            _useMajdataPlayStorage = _settings.UseMajdataPlayStorage;
            _majdataPlayDirectory = _settings.MajdataPlayDirectory
                ?? (_workspace as IMajdataPlayWorkspace)?.DefaultMajdataPlayDirectory ?? string.Empty;
            _mediaStorageDirectory = _settings.MediaStorageDirectory ?? _workspace.DataDirectory;
            SaveMediaStorageCommand = CreateCommand(SaveMediaStorageAsync, () => !string.IsNullOrWhiteSpace(MediaStoragePath));
            OnPropertyChanged(nameof(SaveMediaStorageCommand));
            OnPropertyChanged(nameof(MediaStoragePath));
            OnPropertyChanged(nameof(SelectedMediaStorageMode));
            OnPropertyChanged(nameof(UseMajdataPlayStorage));
        }

        /// <summary>Retains a native writable tree selection without converting it into an ordinary filesystem path.</summary>
        /// <param name="reference">The explicitly granted tree URI or ordinary selected local path.</param>
        public void SetPickedMediaStorage(string reference)
        {
            MediaStoragePath = LocalDirectorySettings.NormalizePath(reference);
        }

        /// <summary>Merges only media preferences into the latest snapshot and reads the selected root.</summary>
        /// <returns>A task representing settings publication and refreshed local music.</returns>
        private async Task SaveMediaStorageAsync()
        {
            MediaStoragePath = LocalDirectorySettings.NormalizePath(MediaStoragePath.Trim());
            var shared = _useMajdataPlayStorage;
            var current = await _workspace.LoadSettingsAsync(_lifetime.Token);
            await _workspace.SaveSettingsAsync(current with
            {
                UseMajdataPlayStorage = shared,
                MajdataPlayDirectory = string.IsNullOrWhiteSpace(_majdataPlayDirectory) ? null : LocalDirectorySettings.NormalizePath(_majdataPlayDirectory.Trim()),
                MediaStorageDirectory = string.IsNullOrWhiteSpace(_mediaStorageDirectory) ? null : LocalDirectorySettings.NormalizePath(_mediaStorageDirectory.Trim())
            }, _lifetime.Token);
            _settings = await _workspace.LoadSettingsAsync(_lifetime.Token);
            if (_workspace is IMajdataPlayWorkspace media)
            {
                await media.RefreshMajdataPlayAsync(_lifetime.Token);
                await ReloadPlaylistsAsync();
                await ReloadLibraryAsync();
            }
            Status = "媒体目录已读取";
        }
    }

    /// <summary>Labels an independent or MajdataPlay-shared media location.</summary>
    /// <param name="IsShared">Whether the choice uses the shared MajdataPlay root.</param>
    /// <param name="Name">The visible storage-mode label.</param>
    public sealed record MediaStorageModeOption(bool IsShared, string Name);
}
