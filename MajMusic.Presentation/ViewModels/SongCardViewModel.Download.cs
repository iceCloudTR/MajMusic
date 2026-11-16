#nullable enable

using System.Globalization;
using Avalonia.Controls;
using MajMusic.Core.Models;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Retains one source-revision download state independently of query and control lifetimes.</summary>
    internal sealed class SongDownloadState
    {
        /// <summary>Gets or sets the newest accepted audio operation.</summary>
        public AudioDownloadProgress? Progress { get; set; }
        /// <summary>Gets or sets whether a validated or visibly checked local file is available.</summary>
        public bool IsAvailable { get; set; }
        /// <summary>Gets or sets the known validated local path even before row metadata refreshes.</summary>
        public string? LocalPath { get; set; }
        /// <summary>Gets or sets the generation rejecting older filesystem checks.</summary>
        public long Version { get; set; }
    }

    /// <summary>Exposes per-song transfer fill and checks downloaded audio only while the card is visible.</summary>
    public sealed partial class SongCardViewModel
    {
        /// <summary>Shares cached availability with later cards for the same source revision.</summary>
        private SongDownloadState _downloadState = new();

        /// <summary>Gets the percentage used by the card's bottom-to-top download fill.</summary>
        public double DownloadPercent => IsDownloaded ? 100 : _downloadState.Progress is
        {
            State: AudioDownloadState.Downloading,
            Transfer: { TotalBytes: > 0 } transfer
        } ? Math.Clamp(transfer.ReceivedBytes * 100d / transfer.TotalBytes.Value, 0, 100) : 0;
        /// <summary>Gets whether the online song is preparing or receiving audio bytes.</summary>
        public bool IsDownloading => _track.RemoteId is not null && !_downloadState.IsAvailable
            && _downloadState.Progress?.State is AudioDownloadState.Preparing or AudioDownloadState.Downloading;
        /// <summary>Gets whether this online song has a complete available audio file.</summary>
        public bool IsDownloaded => _track.RemoteId is not null && _downloadState.IsAvailable;
        /// <summary>Gets whether an active transfer has no usable total length.</summary>
        public bool IsDownloadIndeterminate => IsDownloading && _downloadState.Progress?.Transfer?.TotalBytes is not > 0;
        /// <summary>Gets whether the information half displays a transfer or completed-download label.</summary>
        public bool HasDownloadStatus => IsDownloading || IsDownloaded;
        /// <summary>Gets a short card-local download label.</summary>
        public string DownloadStatus => IsDownloaded ? "已在本地" : IsDownloading
            ? _downloadState.Progress?.State == AudioDownloadState.Preparing ? "准备下载" : "正在下载" : string.Empty;
        /// <summary>Gets measured byte counts and receive speed without querying the filesystem.</summary>
        public string DownloadDetails
        {
            get
            {
                if (!HasDownloadStatus || _downloadState.Progress?.Transfer is not { } transfer)
                {
                    return string.Empty;
                }

                var size = transfer.TotalBytes is > 0
                    ? $"{DownloadPercent:0}% · {FormatCardBytes(transfer.ReceivedBytes)} / {FormatCardBytes(transfer.TotalBytes.Value)}"
                    : FormatCardBytes(transfer.ReceivedBytes);
                return !IsDownloaded && transfer.BytesPerSecond is > 0
                    ? size + " · " + FormatCardBytes(transfer.BytesPerSecond.Value) + "/s" : size;
            }
        }
        /// <summary>Gets the unfilled portion above the bottom-up download background.</summary>
        public GridLength DownloadEmptyHeight => new(100 - DownloadPercent, GridUnitType.Star);
        /// <summary>Gets the downloaded portion at the bottom of the information background.</summary>
        public GridLength DownloadFillHeight => new(DownloadPercent, GridUnitType.Star);

        /// <summary>Attaches source-scoped availability retained by the owning music view.</summary>
        /// <param name="state">The shared current-revision state.</param>
        internal void RestoreDownloadState(SongDownloadState state)
        {
            if (ReferenceEquals(_downloadState, state))
            {
                return;
            }

            _downloadState = state;
            _downloadState.LocalPath ??= _track.Audio.LocalPath;
            NotifyDownloadState();
            if (_attachments > 0)
            {
                StartThumbnail();
            }
        }

        /// <summary>Refreshes visible transfer bindings after the owner updates the shared state.</summary>
        internal void NotifyDownloadState()
        {
            foreach (var property in new[] { nameof(DownloadPercent), nameof(IsDownloading), nameof(IsDownloaded),
                nameof(IsDownloadIndeterminate), nameof(HasDownloadStatus), nameof(DownloadStatus), nameof(DownloadDetails),
                nameof(DownloadEmptyHeight), nameof(DownloadFillHeight) })
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));
            }
        }

        /// <summary>Rechecks a currently displayed card after an explicit cache cleanup.</summary>
        /// <returns>The resources observed by the current visible activation.</returns>
        internal Task RecheckDownloadAvailabilityAsync()
        {
            if (!_disposed && _attachments > 0)
            {
                StartThumbnail();
                return _activationTask;
            }

            return Task.CompletedTask;
        }

        /// <summary>Invalidates download presentation only when the underlying song revision or local resource changes.</summary>
        /// <param name="previous">The preceding immutable descriptor.</param>
        /// <param name="updated">The current immutable descriptor.</param>
        /// <returns>Whether an attached card should recheck its local availability.</returns>
        private bool UpdateDownloadTrack(MusicTrack previous, MusicTrack updated)
        {
            if (previous.Id != updated.Id || previous.SourceId != updated.SourceId || previous.EndpointId != updated.EndpointId
                || previous.Revision != updated.Revision || previous.RemoteId != updated.RemoteId)
            {
                _downloadState = new SongDownloadState { LocalPath = updated.Audio.LocalPath };
                NotifyDownloadState();
                return true;
            }

            if (previous.Audio.LocalPath != updated.Audio.LocalPath)
            {
                _downloadState.LocalPath = updated.Audio.LocalPath ?? _downloadState.LocalPath;
                _downloadState.Version++;
                return true;
            }

            return false;
        }

        /// <summary>Checks only a visible online song's known local audio path off the presentation thread.</summary>
        /// <param name="track">The descriptor captured by the visible activation.</param>
        /// <param name="generation">The attachment generation that owns the result.</param>
        /// <param name="token">Cancels work after the card leaves its viewport.</param>
        /// <returns>A task representing the observed existence check.</returns>
        private async Task CheckDownloadAvailabilityAsync(MusicTrack track, int generation, CancellationToken token)
        {
            if (track.RemoteId is null)
            {
                return;
            }

            var state = _downloadState;
            var version = state.Version;
            var located = await _cache.ResolveLocalTrackAsync(track, token);
            var path = state.LocalPath ?? track.Audio.LocalPath;
            path = located.Audio.LocalPath ?? path;
            var available = !string.IsNullOrWhiteSpace(path)
                && (DocumentReference.IsContentUri(path) || await Task.Run(() => File.Exists(path), token));
            token.ThrowIfCancellationRequested();
            if (!_disposed && _attachments > 0 && generation == _generation && ReferenceEquals(state, _downloadState)
                && version == state.Version && state.Progress?.State != AudioDownloadState.Downloading)
            {
                if (located.Audio != track.Audio || located.Cover != track.Cover)
                {
                    _track = _track with { Audio = located.Audio, Cover = located.Cover, Chart = located.Chart };
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Track)));
                }
                state.IsAvailable = available;
                state.LocalPath = path;
                state.Version++;
                NotifyDownloadState();
            }
        }

        /// <summary>Formats measured byte sizes without requiring a completed resource path.</summary>
        /// <param name="bytes">The nonnegative byte count or receive rate.</param>
        /// <returns>A compact decimal byte-unit string.</returns>
        private static string FormatCardBytes(double bytes)
        {
            return bytes >= 1_000_000 ? (bytes / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
                : bytes >= 1_000 ? (bytes / 1_000).ToString("0.0", CultureInfo.InvariantCulture) + " KB"
                : Math.Max(0, bytes).ToString("0", CultureInfo.InvariantCulture) + " B";
        }
    }
}
