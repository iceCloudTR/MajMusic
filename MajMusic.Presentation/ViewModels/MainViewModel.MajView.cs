#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Playback;

namespace MajMusic.Presentation.ViewModels
{
    public sealed partial class MainViewModel
    {
        private CancellationTokenSource? _majViewOpening;
        private IMajViewPreviewSession? _majViewSession;
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
            && _workspace is IMajViewPreviewHost;

        /// <summary>Gets the action label, including whether the optional viewer needs to be downloaded.</summary>
        public string MajViewOpenLabel => !_isDisposing && _workspace is IMajViewPreviewHost { IsMajViewInstalled: false }
            ? "安装并打开 MajView（49 MB）" : "打开 MajView";

        /// <summary>Gets the latest preview preparation status.</summary>
        public string MajViewStatus
        {
            get => _majViewStatus;
            private set => SetField(ref _majViewStatus, value);
        }

        /// <summary>Prepares the selected chart, parks music playback and opens the independent viewer.</summary>
        /// <param name="launch">The current window's browser launcher.</param>
        /// <returns>A task representing preparation and launch.</returns>
        public Task OpenMajViewAsync(Func<Uri, Task<bool>> launch)
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
                    if (!await launch(session.Address))
                    {
                        MajViewStatus = "无法打开浏览器";
                        return;
                    }
                    opening.Token.ThrowIfCancellationRequested();
                    _majViewSession = session;
                    session = null;
                    MajViewStatus = string.Empty;
                }
                catch (OperationCanceledException)
                {
                    MajViewStatus = string.Empty;
                }
                catch (Exception exception)
                {
                    MajViewStatus = exception is IOException or InvalidOperationException or NotSupportedException
                        ? exception.Message : "无法打开 MajView";
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
            return previous?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        }
    }
}
