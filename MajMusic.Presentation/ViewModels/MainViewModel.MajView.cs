#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Playback;

namespace MajMusic.Presentation.ViewModels
{
    public sealed partial class MainViewModel
    {
        private CancellationTokenSource? _majViewOpening;
        private IMajViewPreviewSession? _majViewSession;
        private Uri? _majViewAddress;
        private bool _showPreviewVideo = true;
        private string _majViewStatus = string.Empty;

        /// <summary>Gets or sets whether a new chart preview should include its background video.</summary>
        public bool ShowPreviewVideo
        {
            get => _showPreviewVideo;
            set => SetField(ref _showPreviewVideo, value);
        }

        /// <summary>Gets whether the current song can start preparing a preview.</summary>
        public bool CanOpenMajView => !_isDisposing && _majViewOpening is null && SelectedTrack is not null
            && _majViewAddress is null && _workspace is IMajViewPreviewHost;

        /// <summary>Gets the action label, including whether the optional viewer needs to be downloaded.</summary>
        public string MajViewOpenLabel => !_isDisposing && _workspace is IMajViewPreviewHost { IsMajViewInstalled: false }
            ? "加载谱面预览（首次下载 49 MB）" : "加载谱面预览";

        /// <summary>Gets the local address owned by the current inline preview.</summary>
        public Uri? MajViewAddress => _majViewAddress;

        /// <summary>Gets the latest preview preparation status.</summary>
        public string MajViewStatus
        {
            get => _majViewStatus;
            private set => SetField(ref _majViewStatus, value);
        }

        /// <summary>Prepares the selected chart and publishes it to the in-app preview pane.</summary>
        /// <returns>A task representing preparation and the playback handoff.</returns>
        public Task OpenMajViewAsync()
        {
            return RunTrackedAsync(async () =>
            {
                if (!CanOpenMajView || SelectedTrack is not { } track || _workspace is not IMajViewPreviewHost host)
                {
                    return;
                }
                using var opening = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _majViewOpening = opening;
                OnPropertyChanged(nameof(CanOpenMajView));
                MajViewStatus = "正在准备预览";
                IMajViewPreviewSession? session = null;
                try
                {
                    await ReleaseMajViewSessionAsync();
                    session = await host.CreateMajViewPreviewAsync(track, ShowPreviewVideo, true, opening.Token);
                    opening.Token.ThrowIfCancellationRequested();
                    if (_playback.Snapshot.State is PlaybackState.Playing or PlaybackState.Loading)
                    {
                        await _playback.PauseAsync(opening.Token);
                    }
                    opening.Token.ThrowIfCancellationRequested();
                    _majViewSession = session;
                    session = null;
                    MajViewStatus = string.Empty;
                    SetField(ref _majViewAddress, _majViewSession.Address, nameof(MajViewAddress));
                }
                catch (OperationCanceledException)
                {
                    MajViewStatus = string.Empty;
                }
                catch (Exception exception)
                {
                    MajViewStatus = exception is IOException or InvalidOperationException or NotSupportedException
                        ? exception.Message : "谱面预览加载失败";
                }
                finally
                {
                    _majViewOpening = null;
                    if (session is not null)
                    {
                        await session.DisposeAsync();
                    }
                    OnPropertyChanged(nameof(CanOpenMajView));
                    OnPropertyChanged(nameof(MajViewOpenLabel));
                }
            });
        }

        /// <summary>Closes the current preview without changing the main music queue.</summary>
        public void CloseMajView() => CancelMajView();

        internal void ReportMajViewFailure(Uri address, string message)
        {
            if (address == _majViewAddress)
            {
                CancelMajView();
                MajViewStatus = message;
            }
        }

        private void CancelMajView()
        {
            _majViewOpening?.Cancel();
            if (_majViewSession is not null)
            {
                _ = RunTrackedAsync(ReleaseMajViewSessionAsync);
            }
        }

        private Task ReleaseMajViewSessionAsync()
        {
            var previous = Interlocked.Exchange(ref _majViewSession, null);
            if (SetField(ref _majViewAddress, null, nameof(MajViewAddress)))
            {
                OnPropertyChanged(nameof(CanOpenMajView));
            }
            return previous?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        }
    }
}
