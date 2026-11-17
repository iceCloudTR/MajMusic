#nullable enable

using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using global::Android.Webkit;

namespace MajMusic.Android.Preview
{
    /// <summary>Hosts the system WebView in the assigned Android Avalonia preview region.</summary>
    internal sealed class AndroidMajViewHost : NativeControlHost
    {
        private readonly Uri _address;
        private readonly Action<string> _failed;
        private PreviewClient? _client;
        private bool _attached;
        private bool _reported;

        /// <summary>Creates the view that will navigate to one session when attached.</summary>
        /// <param name="address">The current preview's loopback address.</param>
        /// <param name="failed">Receives an initialization or navigation error on the UI thread.</param>
        public AndroidMajViewHost(Uri address, Action<string> failed)
        {
            _address = address;
            _failed = failed;
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
        {
            _attached = true;
            _reported = false;
            base.OnAttachedToVisualTree(args);
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
        {
            _attached = false;
            base.OnDetachedFromVisualTree(args);
        }

        /// <inheritdoc />
        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            WebView? view = null;
            try
            {
                if (_address.Scheme != Uri.UriSchemeHttp || _address.Host != "127.0.0.1" || _address.AbsolutePath.Length <= 1)
                {
                    throw new InvalidOperationException("MajView requires its own local preview session.");
                }

                var context = (parent as AndroidViewControlHandle)?.View.Context ?? global::Android.App.Application.Context;
                view = new WebView(context);
                view.SetBackgroundColor(global::Android.Graphics.Color.Rgb(23, 23, 27));
                view.VerticalScrollBarEnabled = false;
                view.HorizontalScrollBarEnabled = false;
                var settings = view.Settings ?? throw new InvalidOperationException("Android WebView settings are unavailable.");
                settings.JavaScriptEnabled = true;
                settings.DomStorageEnabled = true;
                settings.AllowFileAccess = false;
                settings.AllowContentAccess = false;
                settings.JavaScriptCanOpenWindowsAutomatically = false;
                settings.SetSupportMultipleWindows(false);
                settings.MediaPlaybackRequiresUserGesture = true;
                _client = new PreviewClient(_address, ReportFailure);
                view.SetWebViewClient(_client);
                view.LoadUrl(_address.AbsoluteUri);
                return new AndroidViewControlHandle(view);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Java.Lang.RuntimeException)
            {
                if (view is not null)
                {
                    DestroyWebView(view);
                }

                _client?.Dispose();
                _client = null;
                ReportFailure("无法启动谱面预览，请检查 Android 系统 WebView 是否可用。");
                return base.CreateNativeControlCore(parent);
            }
        }

        /// <inheritdoc />
        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (control is AndroidViewControlHandle { View: WebView view })
            {
                DestroyWebView(view);
                _client?.Dispose();
                _client = null;
            }
            else
            {
                base.DestroyNativeControlCore(control);
            }
        }

        private static void DestroyWebView(WebView view)
        {
            view.StopLoading();
            view.Destroy();
            view.Dispose();
        }

        private void ReportFailure(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_attached && !_reported)
                {
                    _reported = true;
                    _failed(message);
                }
            });
        }

        private sealed class PreviewClient(Uri address, Action<string> reportFailure) : WebViewClient
        {
            public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
            {
                return !BelongsToSession(request?.Url?.ToString());
            }

            public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
            {
                return BelongsToSession(request?.Url?.ToString()) ? null
                    : new WebResourceResponse("text/plain", "utf-8", 403, "Forbidden", new Dictionary<string, string>(), Stream.Null);
            }

            public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
            {
                if (request?.IsForMainFrame == true && BelongsToSession(request.Url?.ToString()))
                {
                    reportFailure("谱面预览加载失败。");
                }
            }

            public override void OnReceivedHttpError(WebView? view, IWebResourceRequest? request, WebResourceResponse? errorResponse)
            {
                if (request?.IsForMainFrame == true && BelongsToSession(request.Url?.ToString()))
                {
                    reportFailure("谱面预览加载失败。");
                }
            }

            public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
            {
                reportFailure("谱面预览已中断，请重新打开。");
                return true;
            }

            private bool BelongsToSession(string? request)
            {
                return Uri.TryCreate(request, UriKind.Absolute, out var uri)
                    && uri.Scheme == address.Scheme && uri.Authority == address.Authority
                    && uri.AbsolutePath.StartsWith(address.AbsolutePath, StringComparison.Ordinal);
            }
        }
    }
}
