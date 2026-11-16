#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Offers explicit age-based cleanup without scheduling or automatic eviction.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Stores the three finite ranges supported by the local-file cleanup action.</summary>
        private static readonly LocalMediaCleanupOption[] CleanupRanges =
        [
            new("30天前", 30),
            new("7天前", 7),
            new("全部本地音乐", null)
        ];
        /// <summary>Defaults each application session to the conservative thirty-day range.</summary>
        private LocalMediaCleanupOption _selectedLocalMediaCleanup = CleanupRanges[0];

        /// <summary>Gets the command running exactly one manually requested cleanup.</summary>
        public AsyncCommand CleanupLocalMediaCommand { get; private set; } = null!;
        /// <summary>Gets the available manual cleanup ranges.</summary>
        public IReadOnlyList<LocalMediaCleanupOption> LocalMediaCleanupChoices => CleanupRanges;
        /// <summary>Gets or selects the range used only after the person presses the cleanup button.</summary>
        public LocalMediaCleanupOption SelectedLocalMediaCleanup
        {
            get => _selectedLocalMediaCleanup;
            set
            {
                if (value is not null && CleanupRanges.Contains(value))
                {
                    SetField(ref _selectedLocalMediaCleanup, value);
                }
            }
        }

        /// <summary>Registers a manual command without starting a scan, timer or file operation.</summary>
        private void InitializeLocalMediaCleanupCommands()
        {
            CleanupLocalMediaCommand = CreateCommand(() => CleanupLocalMediaCoreAsync(SelectedLocalMediaCleanup.Days),
                () => _workspace is ITrackMediaRemovalWorkspace);
        }

        /// <summary>Reads one catalog, selects real local audio by save time and refreshes presentation once after removal.</summary>
        /// <param name="days">The age in days, or null for every stored managed song.</param>
        /// <returns>A task representing the explicitly requested finite cleanup.</returns>
        private async Task CleanupLocalMediaCoreAsync(int? days)
        {
            if (_workspace is not ITrackMediaRemovalWorkspace removal)
            {
                return;
            }

            var token = _lifetime.Token;
            var library = await _workspace.SearchAsync(null, token);
            var cutoff = days is { } age ? DateTimeOffset.UtcNow.AddDays(-age) : (DateTimeOffset?)null;
            var tracks = await Task.Run(() =>
            {
                var selected = new List<MusicTrack>();
                foreach (var track in library)
                {
                    token.ThrowIfCancellationRequested();
                    if (track.RemoteId is null || track.Audio.LocalPath is not { Length: > 0 } path)
                    {
                        continue;
                    }

                    var document = DocumentReference.IsContentUri(path);
                    if (!document && !File.Exists(path))
                    {
                        continue;
                    }

                    DateTimeOffset? saved = track.LocalMediaSavedAt;
                    if (saved is null && !document)
                    {
                        try
                        {
                            saved = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            continue;
                        }
                    }

                    if (cutoff is null || saved is { } known && known <= cutoff.Value)
                    {
                        selected.Add(track);
                    }
                }
                return selected;
            }, token);
            if (tracks.Count == 0)
            {
                Status = "没有符合范围的本地音乐";
                return;
            }

            PrepareLocalMediaRemoval();
            foreach (var track in tracks)
            {
                await PrepareTrackMediaRemovalAsync(track);
            }

            var results = new List<TrackMediaRemovalResult>();
            var completed = false;
            try
            {
                if (_workspace is ITrackMediaRemovalBatchWorkspace batch)
                {
                    results.AddRange(await batch.RemoveTrackMediaBatchAsync(tracks, token));
                }
                else
                {
                    foreach (var track in tracks)
                    {
                        token.ThrowIfCancellationRequested();
                        results.Add(await removal.RemoveTrackMediaAsync(track, token));
                    }
                }
                completed = true;
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    if (!completed)
                    {
                        await ReconcileInterruptedLocalMediaCleanupAsync(tracks, results, token);
                    }
                    var changed = results.Where(HasRemovedLocalMedia).ToArray();
                    if (changed.Length > 0)
                    {
                        InvalidateRemovedMediaPresentations(changed);
                    }
                    await ReloadPlaylistsAsync(false);
                    await ReloadLibraryCoreAsync(token, preservePage: true);
                    if (!completed)
                    {
                        await ResetCardDownloadProgress();
                    }
                }
            }

            Status = $"已清理 {results.Count(HasRemovedLocalMedia)} 首本地音乐";
        }

        /// <summary>Recovers committed removals when a later batch item fails before the result list can return.</summary>
        /// <param name="tracks">The finite set originally selected for this cleanup.</param>
        /// <param name="results">The successful results augmented with committed changes discovered in one catalog read.</param>
        /// <param name="token">Cancels recovery when the presentation closes.</param>
        /// <returns>A task representing one failure-only catalog reconciliation.</returns>
        private async Task ReconcileInterruptedLocalMediaCleanupAsync(IReadOnlyList<MusicTrack> tracks,
            List<TrackMediaRemovalResult> results, CancellationToken token)
        {
            var library = await _workspace.SearchAsync(null, token);
            var byOwner = library.ToDictionary(DownloadOwner);
            var reported = results.Select(result => DownloadOwner(result.UpdatedTrack)).ToHashSet();
            foreach (var original in tracks)
            {
                var owner = DownloadOwner(original);
                if (reported.Contains(owner) || !byOwner.TryGetValue(owner, out var current))
                {
                    continue;
                }

                var audioRemoved = original.Audio.LocalPath is not null && current.Audio.LocalPath is null;
                var coverRemoved = original.Cover?.LocalPath is not null && current.Cover?.LocalPath is null;
                if (audioRemoved || coverRemoved)
                {
                    results.Add(new TrackMediaRemovalResult(current, audioRemoved, coverRemoved, 0));
                }
            }
        }
    }

    /// <summary>Describes one manual cleanup range without creating a persistent automatic task.</summary>
    /// <param name="Name">The range shown in storage settings.</param>
    /// <param name="Days">The age in days, or null to include all locally stored music.</param>
    public sealed record LocalMediaCleanupOption(string Name, int? Days);
}
