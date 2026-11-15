#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Tests
{
    /// <summary>Checks per-card transfer fill, source ownership and visible-only download availability.</summary>
    internal static class CardDownloadPresentationTests
    {
        /// <summary>Runs operation-generation, revision, query and cache-removal regressions.</summary>
        /// <returns>A task representing completed card-state validation.</returns>
        public static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MajMusic-card-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                await ValidateVisibleAvailabilityAsync(root);
                await ValidateOperationOwnershipAsync(root);
                await ValidateCacheRemovalAsync(root);
            }
            finally
            {
                var resolved = Path.GetFullPath(root);
                var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("MajMusic-card-download-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to clean outside the generated card-download fixture.");
                }

                Directory.Delete(resolved, true);
            }
        }

        /// <summary>Checks online audio files only on visible activation and never labels local songs as downloads.</summary>
        /// <param name="root">The generated media fixture directory.</param>
        /// <returns>A task representing visible-only availability.</returns>
        private static async Task ValidateVisibleAvailabilityAsync(string root)
        {
            var path = Path.Combine(root, "visible.mp3");
            await File.WriteAllTextAsync(path, "available cached bytes");
            var workspace = new DownloadWorkspace();
            var online = Track("visible", path);
            workspace.Inner.Library.AddRange([online, Track("local", path) with { RemoteId = null }]);
            await using var view = await CreateViewAsync(workspace);
            var card = view.CardTracks.Single(item => item.Track.Id == online.Id);
            var local = view.CardTracks.Single(item => item.Track.Id == "local");
            TestAssert.True(!card.IsDownloaded && card.DownloadPercent == 0 && !local.IsDownloaded,
                "Constructing an indexed library cannot scan all audio files or prelabel local music as downloaded.");
            await card.ActivateAsync();
            await local.ActivateAsync();
            TestAssert.True(card.IsDownloaded && card.DownloadPercent == 100 && card.DownloadStatus == "下载完成",
                "An attached online card must restore a completed file from its existing local audio path.");
            TestAssert.True(!local.IsDownloaded && !local.HasDownloadStatus,
                "An original local song cannot display the online-download completion label.");
            card.Deactivate();
            File.Delete(path);
            TestAssert.True(card.IsDownloaded, "A download property getter cannot perform filesystem work after the card detaches.");
            await card.ActivateAsync();
            TestAssert.True(!card.IsDownloaded && card.DownloadPercent == 0,
                "Reattaching an online card must clear completion after its audio file was actually removed.");
        }

        /// <summary>Retains completion across queries and rejects older operations, endpoints and source revisions.</summary>
        /// <param name="root">The generated completed-audio fixture directory.</param>
        /// <returns>A task representing progress ownership and query retention.</returns>
        private static async Task ValidateOperationOwnershipAsync(string root)
        {
            var path = Path.Combine(root, "completed.mp3");
            await File.WriteAllTextAsync(path, "completed bytes");
            var workspace = new DownloadWorkspace();
            var track = Track("owned", null);
            workspace.Inner.Library.Add(track);
            await using var view = await CreateViewAsync(workspace);
            var card = view.CardTracks.Single();
            workspace.Emit(Progress(track, "first", AudioDownloadState.Preparing));
            workspace.Emit(Progress(track, "first", AudioDownloadState.Downloading, 25));
            TestAssert.True(card.IsDownloading && !card.IsDownloaded && card.DownloadPercent == 25
                && card.DownloadFillHeight.IsStar && card.DownloadFillHeight.Value == 25 && card.DownloadEmptyHeight.Value == 75,
                "A card must expose an exact bottom-up fill and its unfilled complement.");
            workspace.Emit(Progress(track, "second", AudioDownloadState.Preparing));
            workspace.Emit(Progress(track, "second", AudioDownloadState.Downloading, 40));
            workspace.Emit(Progress(track, "first", AudioDownloadState.Cancelled));
            workspace.Emit(Progress(track, "first", AudioDownloadState.Completed, 100, path));
            TestAssert.True(card.IsDownloading && card.DownloadPercent == 40,
                "An older operation cannot cancel or complete a newer operation on the same song.");
            workspace.Emit(Progress(track, "other-source", AudioDownloadState.Preparing) with { SourceId = "other" });
            workspace.Emit(Progress(track, "other-source", AudioDownloadState.Downloading, 99) with { SourceId = "other" });
            workspace.Emit(Progress(track, "other-endpoint", AudioDownloadState.Preparing) with { EndpointId = "other" });
            workspace.Emit(Progress(track, "other-endpoint", AudioDownloadState.Downloading, 99) with { EndpointId = "other" });
            TestAssert.Equal(40d, card.DownloadPercent, "Another source or independently authenticated endpoint cannot paint this card's background.");
            var revision = track with { Revision = "v2" };
            workspace.Emit(Progress(revision, "revision-two", AudioDownloadState.Preparing));
            workspace.Emit(Progress(revision, "revision-two", AudioDownloadState.Downloading, 60));
            TestAssert.Equal(40d, card.DownloadPercent, "A different source revision cannot overwrite the currently browsed revision.");
            workspace.Inner.Library[0] = revision;
            await ShowLibraryAsync(view);
            card = view.CardTracks.Single();
            TestAssert.Equal(60d, card.DownloadPercent, "A newly browsed revision must restore its own already observed transfer.");
            workspace.Emit(Progress(track, "second", AudioDownloadState.Cancelled));
            TestAssert.Equal(60d, card.DownloadPercent, "A previous revision's cancellation cannot clear the replacement revision.");
            workspace.Emit(Progress(revision, "revision-two", AudioDownloadState.Completed, 100, path));
            TestAssert.True(card.IsDownloaded && card.DownloadPercent == 100 && card.Track.Audio.LocalPath is null,
                "Completion must fill the card immediately before the row's audio descriptor has refreshed.");
            await card.ActivateAsync();
            TestAssert.True(card.IsDownloaded, "Visible checking must use the validated completion path when row metadata still has no local path.");
            view.BeginSearchCommand.Execute(null);
            await view.HomeUpdate;
            TestAssert.Equal(0, view.CardTracks.Count, "The fixture must remove the completed song from the current query.");
            await ShowLibraryAsync(view);
            card = view.CardTracks.Single();
            TestAssert.True(card.IsDownloaded && card.DownloadPercent == 100,
                "A completed song must retain its fill after leaving and reentering the current query.");
            workspace.Emit(Progress(revision, "cache-check", AudioDownloadState.Preparing));
            TestAssert.True(card.IsDownloaded && card.DownloadPercent == 100,
                "Preparing another cache hit cannot erase a still-available completed fill.");
            workspace.Emit(Progress(revision, "cache-check", AudioDownloadState.Cancelled));
            TestAssert.True(card.IsDownloaded, "Canceling a cache check cannot erase an already validated completed file.");
        }

        /// <summary>Rechecks actual completed paths after cleanup and retains downloaded files that were not deleted.</summary>
        /// <param name="root">The generated application-managed audio fixture directory.</param>
        /// <returns>A task representing cache deletion and retained-download state.</returns>
        private static async Task ValidateCacheRemovalAsync(string root)
        {
            var evictable = Path.Combine(root, "evictable.mp3");
            var pinned = Path.Combine(root, "pinned.mp3");
            await File.WriteAllTextAsync(evictable, "evictable bytes");
            await File.WriteAllTextAsync(pinned, "pinned bytes");
            var workspace = new DownloadWorkspace { ClearCacheAction = () => File.Delete(evictable) };
            var first = Track("evictable", null);
            var second = Track("pinned", null);
            workspace.Inner.Library.AddRange([first, second]);
            await using var view = await CreateViewAsync(workspace);
            var firstCard = view.CardTracks.Single(item => item.Track.Id == first.Id);
            var secondCard = view.CardTracks.Single(item => item.Track.Id == second.Id);
            workspace.Emit(Progress(first, "first", AudioDownloadState.Preparing));
            workspace.Emit(Progress(first, "first", AudioDownloadState.Completed, 100, evictable));
            workspace.Emit(Progress(second, "second", AudioDownloadState.Preparing));
            workspace.Emit(Progress(second, "second", AudioDownloadState.Completed, 100, pinned));
            await Task.WhenAll(firstCard.ActivateAsync(), secondCard.ActivateAsync());
            var pinnedEmptyTransitions = 0;
            secondCard.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SongCardViewModel.IsDownloaded) && !secondCard.IsDownloaded)
                {
                    pinnedEmptyTransitions++;
                }
            };
            view.ClearCacheCommand.Execute(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (view.IsBusy)
            {
                await Task.Delay(5, timeout.Token);
            }

            TestAssert.True(!firstCard.IsDownloaded && firstCard.DownloadPercent == 0,
                "A removed cached audio file must return its visible card to the undownloaded background.");
            TestAssert.True(secondCard.IsDownloaded && secondCard.DownloadPercent == 100 && pinnedEmptyTransitions == 0,
                "Cache cleanup cannot clear or flash the completed fill of a retained downloaded file.");
        }

        /// <summary>Creates an online descriptor without starting a source connection.</summary>
        /// <param name="id">The stable music identity.</param>
        /// <param name="path">The optional known local cache path.</param>
        /// <returns>The generated source-owned song.</returns>
        internal static MusicTrack Track(string id, string? path)
        {
            return new MusicTrack { Id = id, Title = "Song " + id, SourceId = "server", EndpointId = "endpoint", RemoteId = id, Revision = "v1", Audio = new MediaResource { LocalPath = path } };
        }

        /// <summary>Creates a source-scoped operation observation for state and layout regressions.</summary>
        /// <param name="track">The song owning this operation.</param>
        /// <param name="operation">The deterministic operation identity.</param>
        /// <param name="state">The audio resolution phase.</param>
        /// <param name="percent">The generated percentage out of one hundred bytes.</param>
        /// <param name="path">The validated path supplied only after completion.</param>
        /// <returns>The generated immutable observation.</returns>
        internal static AudioDownloadProgress Progress(MusicTrack track, string operation, AudioDownloadState state, int percent = 0, string? path = null)
        {
            return new AudioDownloadProgress
            {
                OperationId = operation,
                TrackId = track.Id,
                Title = track.Title,
                SourceId = track.SourceId,
                EndpointId = track.EndpointId,
                Revision = track.Revision,
                State = state,
                Transfer = state is AudioDownloadState.Downloading or AudioDownloadState.Completed
                    ? new MediaTransferProgress { ReceivedBytes = percent, TotalBytes = 100, BytesPerSecond = 1000, IsCompleted = state == AudioDownloadState.Completed } : null,
                LocalAudioPath = state == AudioDownloadState.Completed ? path : null
            };
        }

        /// <summary>Initializes a plain presentation without native audio, HTTP or a credential vault.</summary>
        /// <param name="workspace">The generated local index and progress source.</param>
        /// <returns>The view model owned by the caller.</returns>
        internal static async Task<MainViewModel> CreateViewAsync(DownloadWorkspace workspace)
        {
            var view = new MainViewModel(workspace, new PlaybackService(new LibraryExperienceTests.ExperienceBackend()), static action => action());
            await view.InitializeAsync();
            await ShowLibraryAsync(view);
            return view;
        }

        /// <summary>Reconciles the current browse collection through its normal public action.</summary>
        /// <param name="view">The presentation owner.</param>
        /// <returns>A task representing completed library browsing.</returns>
        private static async Task ShowLibraryAsync(MainViewModel view)
        {
            view.ShowLibraryModeCommand.Execute(null);
            await view.HomeUpdate;
        }

        /// <summary>Exposes deterministic progress events while delegating ordinary local state to an isolated index.</summary>
        internal sealed class DownloadWorkspace : IMusicWorkspace, IAudioDownloadProgressSource
        {
            /// <summary>Gets the isolated music and playlist data used by this fixture.</summary>
            public LibraryExperienceTests.ExperienceWorkspace Inner { get; } = new();
            /// <summary>Gets or sets a controlled cover request used by entry-preparation regressions.</summary>
            public Func<MusicTrack, CancellationToken, Task<string?>>? ResolveCoverOverride { get; set; }
            /// <summary>Gets the source-resolution count for each independently described song.</summary>
            public System.Collections.Concurrent.ConcurrentDictionary<string, int> CoverRequests { get; } = new();
            /// <summary>Gets or sets an explicit fixture action removing only its generated evictable audio.</summary>
            public Action? ClearCacheAction { get; init; }
            /// <inheritdoc />
            public AudioDownloadProgress? CurrentAudioDownloadProgress { get; private set; }
            /// <inheritdoc />
            public event EventHandler<AudioDownloadProgress>? AudioDownloadProgressChanged;
            /// <summary>Emits one deterministic transfer state without network activity.</summary>
            /// <param name="progress">The source-owned observation.</param>
            public void Emit(AudioDownloadProgress progress)
            {
                CurrentAudioDownloadProgress = progress;
                AudioDownloadProgressChanged?.Invoke(this, progress);
            }
            /// <inheritdoc />
            public bool IsOnline => Inner.IsOnline;
            /// <inheritdoc />
            public string DataDirectory => Inner.DataDirectory;
            /// <inheritdoc />
            public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
            {
                return Inner.LoadSettingsAsync(cancellationToken);
            }
            /// <inheritdoc />
            public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            {
                return Inner.SaveSettingsAsync(settings, cancellationToken);
            }
            /// <inheritdoc />
            public void SetOnlineEnabled(bool enabled)
            {
                Inner.SetOnlineEnabled(enabled);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicTrack>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            {
                return Inner.SearchAsync(query, cancellationToken);
            }
            /// <inheritdoc />
            public Task<MusicTrack> ImportDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                return Inner.ImportDirectoryAsync(directory, cancellationToken);
            }
            /// <inheritdoc />
            public Task<LocalLibraryScanResult> IndexDirectoryAsync(string directory, CancellationToken cancellationToken = default)
            {
                return Inner.IndexDirectoryAsync(directory, cancellationToken);
            }
            /// <inheritdoc />
            public Task<IReadOnlyList<MusicPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
            {
                return Inner.GetPlaylistsAsync(cancellationToken);
            }
            /// <inheritdoc />
            public Task SavePlaylistAsync(MusicPlaylist playlist, CancellationToken cancellationToken = default)
            {
                return Inner.SavePlaylistAsync(playlist, cancellationToken);
            }
            /// <inheritdoc />
            public Task<int> SynchronizeAsync(EndpointConfiguration endpoint, CancellationToken cancellationToken = default)
            {
                return Inner.SynchronizeAsync(endpoint, cancellationToken);
            }
            /// <inheritdoc />
            public Task<string?> ResolveCoverAsync(MusicTrack track, CancellationToken cancellationToken = default)
            {
                CoverRequests.AddOrUpdate(track.Id, 1, static (_, count) => count + 1);
                return ResolveCoverOverride?.Invoke(track, cancellationToken) ?? Task.FromResult(track.Cover?.LocalPath);
            }
            /// <inheritdoc />
            public Task<string> ResolveAudioAsync(MusicTrack track, bool keepOffline, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Card state and layout fixtures cannot request audio.");
            }
            /// <inheritdoc />
            public Task ClearCacheAsync(CancellationToken cancellationToken = default)
            {
                ClearCacheAction?.Invoke();
                return Task.CompletedTask;
            }
        }
    }
}
