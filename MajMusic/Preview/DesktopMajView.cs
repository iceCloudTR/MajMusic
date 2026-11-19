#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using MajMusic.Presentation.Views;

namespace MajMusic.Preview
{
    /// <summary>Embeds the session in the desktop window using its system web renderer.</summary>
    internal sealed partial class DesktopMajView : NativeWebView, IMajViewNavigator, IPracticeDisc
    {
        private readonly Action<string> _reportFailure;
        private readonly Uri _sessionAddress;
        private readonly DispatcherTimer _startupTimeout;
        private bool _attached;
        private bool _documentLoaded;
        private bool _failureQueued;

        internal DesktopMajView(Uri address, Action<string> reportFailure, string dataDirectory)
        {
            _sessionAddress = address;
            _reportFailure = reportFailure;
            _startupTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _startupTimeout.Tick += (_, _) => Fail("内嵌谱面加载超时");
            Background = Brushes.Transparent;
            if (OperatingSystem.IsWindows())
            {
                AdapterPreference = [WebViewAdapterType.WebView2];
            }

            EnvironmentRequested += (_, options) =>
            {
                options.EnableDevTools = false;
                if (options is WindowsWebView2EnvironmentRequestedEventArgs windows)
                {
                    windows.UserDataFolder = Path.Combine(dataDirectory, "Runtime", "MajViewWebView");
                    windows.AllowSingleSignOnUsingOSPrimaryAccount = false;
                    // Playback is explicitly requested on an app button outside the trusted chart document.
                    windows.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
                }
                else if (options is AppleWKWebViewEnvironmentRequestedEventArgs apple)
                {
                    apple.NonPersistentDataStore = true;
                }
                else if (options is GtkWebViewEnvironmentRequestedEventArgs gtk)
                {
                    gtk.BaseDataDirectory = Path.Combine(dataDirectory, "Runtime", "MajViewWebView", "Data");
                    gtk.BaseCacheDirectory = Path.Combine(dataDirectory, "Runtime", "MajViewWebView", "Cache");
                }
                else if (options is LinuxWpeWebViewEnvironmentRequestedEventArgs wpe)
                {
                    wpe.DataDirectory = Path.Combine(dataDirectory, "Runtime", "MajViewWebView", "Data");
                    wpe.CacheDirectory = Path.Combine(dataDirectory, "Runtime", "MajViewWebView", "Cache");
                }
            };
            NavigationStarted += (_, args) => args.Cancel = !IsSessionDocument(args.Request);
            NewWindowRequested += (_, args) => args.Handled = true;
            NavigationCompleted += (_, args) =>
            {
                if (!IsSessionDocument(args.Request))
                {
                    return;
                }

                if (args.IsSuccess)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        AttachWindowsDisc(this, EventArgs.Empty);
                    }
                    _documentLoaded = true;
                    _startupTimeout.Stop();
                }
                else if (!_documentLoaded)
                {
                    Fail("内嵌谱面加载失败");
                }
            };
            ProcessTerminated += (_, _) => Fail("内嵌谱面已停止");
            Source = address;
        }

        public void NavigatePreview(Uri address)
        {
            if (IsSessionDocument(address))
            {
                Source = address;
            }
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = true;
            _documentLoaded = false;
            _startupTimeout.Start();
            base.OnAttachedToVisualTree(e);
            if (OperatingSystem.IsWindows())
            {
                LayoutUpdated += AttachWindowsDisc;
                AttachWindowsDisc(this, EventArgs.Empty);
            }
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            LayoutUpdated -= AttachWindowsDisc;
            ReleaseWindowsDisc();
            _attached = false;
            _startupTimeout.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == AdapterInfoProperty && AdapterInfo is DetailedWebViewAdapterInfo adapter)
            {
                if (!adapter.IsInstalled)
                {
                    Fail(OperatingSystem.IsWindows() ? "需要安装 Microsoft Edge WebView2 运行时"
                        : OperatingSystem.IsLinux() ? "需要安装 WPE WebKit 或 WebKitGTK"
                        : "当前系统不支持内嵌预览");
                }
                else if (!string.IsNullOrWhiteSpace(adapter.UnavailableReason))
                {
                    Fail("无法初始化内嵌预览");
                }
            }
        }

        private bool IsSessionDocument(Uri? uri) => uri is { IsAbsoluteUri: true }
            && uri.UserInfo.Length == 0
            && uri.Scheme == _sessionAddress.Scheme
            && uri.Host == _sessionAddress.Host
            && uri.Port == _sessionAddress.Port
            && uri.AbsolutePath == _sessionAddress.AbsolutePath;

        private void Fail(string reason)
        {
            if (!_attached || _failureQueued)
            {
                return;
            }

            _failureQueued = true;
            _startupTimeout.Stop();
            Dispatcher.UIThread.Post(() =>
            {
                if (_attached)
                {
                    _reportFailure(reason);
                }
            });
        }
    }
}
