#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Infrastructure.Preview;

namespace MajMusic.Infrastructure.Application
{
    public sealed partial class MusicWorkspace : IMajViewPreviewHost
    {
        private MajViewRuntime? _majViewRuntime;
        private MajViewRuntime PreviewRuntime
        {
            get
            {
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return _majViewRuntime ??= new MajViewRuntime(DataDirectory);
                }
            }
        }

        /// <inheritdoc />
        public bool IsMajViewInstalled => PreviewRuntime.IsInstalled;

        /// <inheritdoc />
        public async Task<IMajViewPreviewSession> CreateMajViewPreviewAsync(MusicTrack track, bool includeVideo,
            bool allowRuntimeDownload, CancellationToken cancellationToken = default)
        {
            if (!System.Net.HttpListener.IsSupported)
            {
                throw new PlatformNotSupportedException("当前平台无法打开 MajView。");
            }
            var assets = await ResolveChartPreviewAsync(track, includeVideo, cancellationToken).ConfigureAwait(false);
            var directory = await PreviewRuntime.EnsureAsync(allowRuntimeDownload, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new MajViewPreviewSession(directory, track.Title, assets, OpenPreviewMediaAsync);
        }

        private Task<Stream> OpenPreviewMediaAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DocumentReference.IsContentUri(path))
            {
                if (_documentStorage?.CanHandle(path) == true)
                {
                    return _documentStorage.OpenReadAsync(path, cancellationToken);
                }
                if (_mediaStorageAccess?.CanHandle(path) == true)
                {
                    return _mediaStorageAccess.OpenReadAsync(path, cancellationToken);
                }
                throw new FileNotFoundException("无法读取预览资源。");
            }
            return Task.FromResult<Stream>(MajViewPreviewSession.OpenFile(path));
        }
    }
}
