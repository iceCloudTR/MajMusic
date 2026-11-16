#nullable enable

using System.Collections.ObjectModel;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Manages independently configured music-library and playlist-index directories.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Scans exactly the configured role without inferring another directory source.</summary>
        private readonly ILocalDirectoryService? _directories;
        /// <summary>Stores the selected configured source.</summary>
        private LocalDirectorySource? _selectedDirectory;
        /// <summary>Stores the edited absolute directory location.</summary>
        private string _directoryPath = string.Empty;
        /// <summary>Stores the role chosen for the edited directory.</summary>
        private DirectoryKindOption _selectedDirectoryKind = new(LocalDirectoryKind.MusicLibrary, "曲库");
        /// <summary>Retains the native picker's display name without decoding opaque document IDs.</summary>
        private (string Reference, string Name)? _pickedDirectory;

        /// <summary>Applies an explicit native folder selection without interpreting its URI as a filesystem path.</summary>
        /// <param name="reference">The ordinary local path or granted document-tree URI.</param>
        /// <param name="name">The provider's display name.</param>
        public void SetPickedDirectory(string reference, string name)
        {
            _pickedDirectory = (reference, name);
            DirectoryPath = reference;
        }

        /// <summary>Gets independently configured local sources.</summary>
        public ObservableCollection<LocalDirectorySource> DirectorySources { get; } = new();
        /// <summary>Gets the available directory roles.</summary>
        public IReadOnlyList<DirectoryKindOption> DirectoryKinds { get; } =
        [
            new(LocalDirectoryKind.MusicLibrary, "曲库"),
            new(LocalDirectoryKind.CollectionIndex, "收藏夹索引")
        ];
        /// <summary>Gets the command beginning a new directory configuration.</summary>
        public AsyncCommand NewDirectoryCommand { get; private set; } = null!;
        /// <summary>Gets the command saving and reading the directory according to its selected role.</summary>
        public AsyncCommand SaveDirectoryCommand { get; private set; } = null!;
        /// <summary>Gets the command rereading the selected configured source.</summary>
        public AsyncCommand ScanDirectoryCommand { get; private set; } = null!;
        /// <summary>Gets the command removing a source configuration.</summary>
        public AsyncCommand RemoveDirectoryCommand { get; private set; } = null!;

        /// <summary>Gets or sets the selected directory and its editor values.</summary>
        public LocalDirectorySource? SelectedDirectory
        {
            get => _selectedDirectory;
            set
            {
                if (!ReferenceEquals(_selectedDirectory, value))
                {
                    _selectedDirectory = value;
                    OnPropertyChanged();
                    DirectoryPath = value?.Path ?? string.Empty;
                    SelectedDirectoryKind = DirectoryKinds.First(item => item.Kind == (value?.Kind ?? LocalDirectoryKind.MusicLibrary));
                    RefreshCommands();
                }
            }
        }

        /// <summary>Gets or sets the directory path being edited.</summary>
        public string DirectoryPath
        {
            get => _directoryPath;
            set
            {
                if (SetField(ref _directoryPath, value))
                {
                    SaveDirectoryCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>Gets or sets the independently chosen source role.</summary>
        public DirectoryKindOption SelectedDirectoryKind
        {
            get => _selectedDirectoryKind;
            set => SetField(ref _selectedDirectoryKind, value);
        }

        /// <summary>Creates directory actions without performing a scan.</summary>
        private void InitializeDirectoryCommands()
        {
            NewDirectoryCommand = CreateCommand(() =>
            {
                SelectedDirectory = null;
                DirectoryPath = string.Empty;
                SelectedDirectoryKind = DirectoryKinds[0];
                return Task.CompletedTask;
            });
            SaveDirectoryCommand = CreateCommand(SaveDirectoryAsync, () => !string.IsNullOrWhiteSpace(DirectoryPath));
            ScanDirectoryCommand = CreateCommand(ScanSelectedDirectoryAsync, () => SelectedDirectory is not null);
            RemoveDirectoryCommand = CreateCommand(RemoveDirectoryAsync, () => SelectedDirectory is not null);
        }

        /// <summary>Refreshes configured source rows after normalization or persistence.</summary>
        /// <param name="selectedId">The preferred selected configuration identity.</param>
        private void ReloadDirectorySources(string? selectedId = null)
        {
            selectedId ??= SelectedDirectory?.Id;
            DirectorySources.Clear();
            foreach (var source in _settings.LocalDirectorySources)
            {
                DirectorySources.Add(source);
            }

            SelectedDirectory = DirectorySources.FirstOrDefault(item => item.Id == selectedId) ?? DirectorySources.FirstOrDefault();
            RefreshDirectoryPages();
            OnPropertyChanged(nameof(LocalDirectoriesText));
            RefreshCommands();
        }

        /// <summary>Saves the editor's role and path before explicitly reading that source.</summary>
        /// <returns>A task representing configuration and one source scan.</returns>
        private async Task SaveDirectoryAsync()
        {
            var path = LocalDirectorySettings.NormalizePath(DirectoryPath.Trim());
            var source = new LocalDirectorySource
            {
                Id = SelectedDirectory?.Id ?? Guid.NewGuid().ToString("N"),
                Path = path,
                Kind = SelectedDirectoryKind.Kind,
                Name = _pickedDirectory is { } picked && picked.Reference == path ? picked.Name : Path.GetFileName(path)
            };
            await PersistDirectorySourceAsync(source);
            await ScanAndRefreshDirectoryAsync(source);
        }

        /// <summary>Rereads the selected saved configuration instead of unsaved editor values.</summary>
        /// <returns>A task representing one configured source read.</returns>
        private Task ScanSelectedDirectoryAsync()
        {
            return SelectedDirectory is { } source ? ScanAndRefreshDirectoryAsync(source) : Task.CompletedTask;
        }

        /// <summary>Removes a configuration while retaining indexed music and locally edited lists.</summary>
        /// <returns>A task representing the saved configuration update.</returns>
        private async Task RemoveDirectoryAsync()
        {
            if (SelectedDirectory is not { } selected)
            {
                return;
            }

            var current = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
            await _workspace.SaveSettingsAsync(current with
            {
                LocalMusicDirectories = Array.Empty<string>(),
                LocalDirectorySources = current.LocalDirectorySources.Where(item => item.Id != selected.Id).ToArray(),
                OnlineEnabled = OnlineEnabled
            }, _lifetime.Token);
            _settings = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
            ReloadDirectorySources();
            Status = "目录已移除";
        }

        /// <summary>Persists one explicit role/path pair without touching the source tree.</summary>
        /// <param name="source">The source configuration to insert or replace.</param>
        /// <returns>A task representing local settings persistence.</returns>
        private async Task PersistDirectorySourceAsync(LocalDirectorySource source)
        {
            var current = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (current.LocalDirectorySources.Any(item => item.Id != source.Id && item.Kind == source.Kind && comparer.Equals(item.Path, source.Path)))
            {
                throw new InvalidOperationException("这个目录已配置。");
            }

            var sources = current.LocalDirectorySources.Where(item => item.Id != source.Id).Append(source).ToArray();
            await _workspace.SaveSettingsAsync(current with
            {
                LocalMusicDirectories = Array.Empty<string>(),
                LocalDirectorySources = sources,
                OnlineEnabled = OnlineEnabled
            }, _lifetime.Token);
            _settings = LocalDirectorySettings.Normalize(await _workspace.LoadSettingsAsync(_lifetime.Token));
            ReloadDirectorySources(source.Id);
        }

        /// <summary>Supports the existing host import action as an explicit music-library source only.</summary>
        /// <param name="directory">The selected music directory.</param>
        /// <returns>A task representing one music-only read.</returns>
        private async Task ImportMusicDirectoryCoreAsync(string directory)
        {
            LocalLibraryScanResult? indexed = null;
            if (_directories is null)
            {
                indexed = await _workspace.IndexDirectoryAsync(directory, _lifetime.Token);
                directory = indexed.RootDirectory;
            }

            var path = LocalDirectorySettings.NormalizePath(directory);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var source = _settings.LocalDirectorySources.FirstOrDefault(item => item.Kind == LocalDirectoryKind.MusicLibrary && comparer.Equals(item.Path, path))
                ?? new LocalDirectorySource { Id = Guid.NewGuid().ToString("N"), Path = path, Kind = LocalDirectoryKind.MusicLibrary, Name = Path.GetFileName(path) };
            await PersistDirectorySourceAsync(source);
            DirectorySourceScanResult result;
            if (indexed is null)
            {
                result = await ScanDirectorySourceAsync(source);
            }
            else
            {
                if (_collections is not null)
                {
                    await _collections.ReconcileUnavailableEntriesAsync(_lifetime.Token);
                }

                result = new DirectorySourceScanResult { Source = source, TrackCount = indexed.Tracks.Count, Issues = indexed.Issues };
            }

            UseHomeMode(HomeBrowseMode.Library);
            _activePlaylistId = null;
            SearchText = string.Empty;
            await ReloadPlaylistsAsync();
            await ReloadLibraryAsync();
            SelectedTrack = indexed?.Tracks.FirstOrDefault() is { } first
                ? Tracks.FirstOrDefault(item => item.Id == first.Id)
                : Tracks.FirstOrDefault(item => item.Audio.LocalPath is { } local && IsWithinDirectory(local, path));
            Status = DescribeDirectoryResult(result);
        }

        /// <summary>Reads exactly one configured source role using the host's directory service.</summary>
        /// <param name="source">The saved source to read.</param>
        /// <returns>The independent scan result.</returns>
        private async Task<DirectorySourceScanResult> ScanDirectorySourceAsync(LocalDirectorySource source)
        {
            if (_directories is not null)
            {
                return await _directories.ScanAsync(source, _lifetime.Token);
            }

            if (source.Kind == LocalDirectoryKind.CollectionIndex)
            {
                var exchange = _collections ?? throw new NotSupportedException("当前宿主不支持收藏夹索引。");
                var lists = await exchange.ImportIndexDirectoryAsync(source.Path, true, _lifetime.Token);
                return new DirectorySourceScanResult
                {
                    Source = source,
                    PlaylistCount = lists.Playlists.Count,
                    Issues = lists.Issues.Select(item => new LocalLibraryScanIssue { Path = item.Path, Message = item.Message }).ToArray()
                };
            }

            var result = await _workspace.IndexDirectoryAsync(source.Path, _lifetime.Token);
            if (_collections is not null)
            {
                await _collections.ReconcileUnavailableEntriesAsync(_lifetime.Token);
            }

            return new DirectorySourceScanResult { Source = source, TrackCount = result.Tracks.Count, Issues = result.Issues };
        }

        /// <summary>Reads one source and updates the affected library and list views.</summary>
        /// <param name="source">The configured source to read.</param>
        /// <returns>A task representing scanning and rebinding.</returns>
        private async Task ScanAndRefreshDirectoryAsync(LocalDirectorySource source)
        {
            var result = await ScanDirectorySourceAsync(source);
            await ReloadPlaylistsAsync();
            await ReloadLibraryAsync();
            Status = DescribeDirectoryResult(result);
        }

        /// <summary>Rereads all configured roles with music indexed before portable list matching.</summary>
        /// <returns>A task representing all independent source reads.</returns>
        private async Task RescanAsync()
        {
            var tracks = 0;
            var lists = 0;
            var issues = 0;
            foreach (var source in _settings.LocalDirectorySources.OrderBy(item => item.Kind).ToArray())
            {
                try
                {
                    var result = await ScanDirectorySourceAsync(source);
                    tracks += result.TrackCount;
                    lists += result.PlaylistCount;
                    issues += result.Issues.Count;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    issues++;
                }
            }

            await ReloadPlaylistsAsync();
            await ReloadLibraryAsync();
            Status = $"已更新 {tracks} 首音乐、{lists} 个列表" + (issues > 0 ? $"，跳过 {issues} 项" : string.Empty);
        }

        /// <summary>Formats an operation result without exposing implementation policy in the interface.</summary>
        /// <param name="result">The completed source scan.</param>
        /// <returns>The short result text.</returns>
        private static string DescribeDirectoryResult(DirectorySourceScanResult result)
        {
            var summary = result.Source.Kind == LocalDirectoryKind.MusicLibrary ? $"已读取 {result.TrackCount} 首音乐" : $"已读取 {result.PlaylistCount} 个列表";
            return summary + (result.Issues.Count > 0 ? $"，跳过 {result.Issues.Count} 项" : string.Empty);
        }

        /// <summary>Checks a referenced audio path against a selected root without filesystem I/O.</summary>
        /// <param name="path">The absolute local media path.</param>
        /// <param name="directory">The normalized directory root.</param>
        /// <returns>Whether the path is inside that directory.</returns>
        private static bool IsWithinDirectory(string path, string directory)
        {
            var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    /// <summary>Provides a readable selector label for one directory role.</summary>
    /// <param name="Kind">The configured directory responsibility.</param>
    /// <param name="Name">The user-visible role name.</param>
    public sealed record DirectoryKindOption(LocalDirectoryKind Kind, string Name);
}
