#nullable enable

using MajMusic.Core.Abstractions;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Displays installed version information and performs manual release checks.</summary>
    public sealed partial class MainViewModel
    {
        private static readonly Version InstalledVersion = Version.Parse(typeof(MainViewModel).Assembly.GetName().Version!.ToString(3));
        private string _appUpdateStatus = string.Empty;
        private Uri? _availableReleasePage;

        /// <summary>Gets the installed version compiled into the application.</summary>
        public string AppVersionText => $"版本 {InstalledVersion}";

        /// <summary>Gets the status of the current release check.</summary>
        public string AppUpdateStatus
        {
            get => _appUpdateStatus;
            private set => SetField(ref _appUpdateStatus, value);
        }

        /// <summary>Gets the public release page when a newer version is available.</summary>
        public Uri? AvailableReleasePage
        {
            get => _availableReleasePage;
            private set => SetField(ref _availableReleasePage, value);
        }

        /// <summary>Checks for an application release when explicitly requested.</summary>
        public AsyncCommand CheckAppUpdateCommand { get; }

        /// <summary>Compares the published version with the one installed on the device.</summary>
        /// <param name="source">The public release source.</param>
        private async Task CheckAppUpdateAsync(IAppReleaseSource source)
        {
            AvailableReleasePage = null;
            AppUpdateStatus = "正在检查更新…";

            var cancellationToken = _lifetime.Token;
            try
            {
                var release = await source.GetLatestAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (release is { } published && published.Version > InstalledVersion)
                {
                    AppUpdateStatus = $"发现新版本 {published.Version}";
                    AvailableReleasePage = published.Page;
                }
                else
                {
                    AppUpdateStatus = release is null ? "暂无正式版本" : "已是最新版本";
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AppUpdateStatus = "检查更新超时，请重试";
            }
        }
    }
}
