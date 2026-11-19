#nullable enable

using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.Views;
using global::Android.Content;
using global::Android.Views;
using global::Android.Webkit;
using global::Android.Widget;

namespace MajMusic.Android.Preview
{
    /// <summary>Hosts the system WebView in the assigned Android Avalonia preview region.</summary>
    internal sealed class AndroidMajViewHost : NativeControlHost, IMajViewNavigator, IPracticeDisc
    {
        private Uri _address;
        private readonly Action<string> _failed;
        private readonly List<TranslateTransform> _sheetTransforms = [];
        private PreviewClient? _client;
        private WebView? _view;
        private PreviewContainer? _container;
        private InterruptibleSongSheet? _sheet;
        private TopLevel? _root;
        private double _aperture = 1;
        private bool _attached;
        private bool _reported;
        private int _attachmentVersion;

        /// <summary>Creates the view that will navigate to one session when attached.</summary>
        /// <param name="address">The current preview's loopback address.</param>
        /// <param name="failed">Receives an initialization or navigation error on the UI thread.</param>
        public AndroidMajViewHost(Uri address, Action<string> failed)
        {
            _address = address;
            _failed = failed;
        }

        /// <inheritdoc />
        public void SetAperture(double fraction)
        {
            _aperture = fraction;
            _container?.SetAperture(fraction);
        }

        public void NavigatePreview(Uri target)
        {
            if (target.GetLeftPart(UriPartial.Path) == _address.GetLeftPart(UriPartial.Path))
            {
                _address = target;
                _view?.LoadUrl(target.AbsoluteUri);
            }
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
        {
            _attachmentVersion++;
            _attached = true;
            _reported = false;
            base.OnAttachedToVisualTree(args);
            PracticeStage.ReportNativeClip(this, true);
            _client?.SetFailureHandler(ReportFailure);
            _root = TopLevel.GetTopLevel(this);
            _sheet = this.FindAncestorOfType<InterruptibleSongSheet>();
            if (_sheet is null)
            {
                return;
            }

            // NativeControlHost reacts to layout and visibility. The sheet's render transform instead moves
            // the existing native surface without remeasuring the WebView or resetting its canvas.
            foreach (var ancestor in this.GetVisualAncestors().TakeWhile(ancestor => !ReferenceEquals(ancestor, _sheet)))
            {
                if (ancestor.RenderTransform is TranslateTransform transform)
                {
                    _sheetTransforms.Add(transform);
                    transform.Changed += OnSheetTransformChanged;
                }
            }
            _sheet.SizeChanged += OnSheetSizeChanged;
            LayoutUpdated += OnSheetTransformChanged;
            UpdateSheetPlacement();
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
        {
            _attached = false;
            _attachmentVersion++;
            _client?.SetFailureHandler(null);
            foreach (var transform in _sheetTransforms)
            {
                transform.Changed -= OnSheetTransformChanged;
            }
            _sheetTransforms.Clear();
            LayoutUpdated -= OnSheetTransformChanged;
            if (_sheet is not null)
            {
                _sheet.SizeChanged -= OnSheetSizeChanged;
            }
            _sheet = null;
            _root = null;
            base.OnDetachedFromVisualTree(args);
        }

        private void OnSheetTransformChanged(object? sender, EventArgs e) => UpdateSheetPlacement();

        private void OnSheetSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateSheetPlacement();

        /// <summary>Keeps the native margins, translated disc and drawer clip in the AvaloniaView's client coordinates.</summary>
        private void UpdateSheetPlacement()
        {
            if (!_attached || _container is not { Width: > 0, Height: > 0 } container || _sheet is not { } sheet ||
                _root is not { } root || this.TranslatePoint(default, root) is not { } origin ||
                sheet.TranslatePoint(default, root) is not { } viewport)
            {
                return;
            }
            // Android's attachment is a direct child of the AvaloniaView. System bars can move the entire
            // view on screen. Subtracting a screen origin here would apply that movement a second time.
            var scale = root.RenderScaling;
            var left = (int)Math.Round(origin.X * scale);
            var top = (int)Math.Round(origin.Y * scale);
            var x = left - container.Left;
            var y = top - container.Top;
            if (container.TranslationX != x)
            {
                container.TranslationX = x;
            }
            if (container.TranslationY != y)
            {
                container.TranslationY = y;
            }
            container.ClipToViewport(
                (int)Math.Ceiling(viewport.X * scale) - left,
                (int)Math.Ceiling(viewport.Y * scale) - top,
                (int)Math.Floor((viewport.X + sheet.Bounds.Width) * scale) - left,
                (int)Math.Floor((viewport.Y + sheet.Bounds.Height) * scale) - top);
        }

        /// <inheritdoc />
        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            WebView? view = null;
            PreviewContainer? container = null;
            try
            {
                if (_address.Scheme != Uri.UriSchemeHttp || _address.Host != "127.0.0.1" || _address.AbsolutePath.Length <= 1)
                {
                    throw new InvalidOperationException("MajView requires its own local preview session.");
                }

                var context = (parent as AndroidViewControlHandle)?.View.Context ?? global::Android.App.Application.Context;
                view = new WebView(context);
                view.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
                view.VerticalScrollBarEnabled = false;
                view.HorizontalScrollBarEnabled = false;
                var settings = view.Settings ?? throw new InvalidOperationException("Android WebView settings are unavailable.");
                settings.JavaScriptEnabled = true;
                settings.DomStorageEnabled = true;
                settings.AllowFileAccess = false;
                settings.AllowContentAccess = false;
                settings.JavaScriptCanOpenWindowsAutomatically = false;
                settings.SetSupportMultipleWindows(false);
                // Only the trusted loopback chart is loaded. Its user gesture originates on the native transport.
                settings.MediaPlaybackRequiresUserGesture = false;
                view.Focusable = false;
                view.Clickable = false;
                _client = new PreviewClient(_address, ReportFailure);
                view.SetWebViewClient(_client);
                // Avalonia disposes the attached native view before calling DestroyNativeControlCore.
                // Give it the container so the WebView can be destroyed after it has been detached.
                container = new PreviewContainer(context, UpdateSheetPlacement) { OutlineProvider = DiscOutline.Instance, ClipToOutline = true };
                container.SetAperture(_aperture);
                container.AttachWebView(view);
                view.LoadUrl(_address.AbsoluteUri);
                var handle = new AndroidViewControlHandle(container);
                _view = view;
                _container = container;
                return handle;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Java.Lang.RuntimeException)
            {
                _client?.SetFailureHandler(null);
                _client = null;
                container?.Dispose();
                if (view is not null)
                {
                    DestroyWebView(view);
                }

                ReportFailure("无法启动谱面预览，请检查 Android 系统 WebView 是否可用。");
                return base.CreateNativeControlCore(parent);
            }
        }

        /// <inheritdoc />
        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            var view = _view;
            var container = _container;
            _view = null;
            _container = null;
            // Chromium can still invoke callbacks already in flight. Let the Java peer retain the client
            // until those callbacks are finished, without allowing them to retain or update this host.
            _client?.SetFailureHandler(null);
            _client = null;
            try
            {
                container?.DetachWebView();
                if (view is not null)
                {
                    DestroyWebView(view);
                }
            }
            finally
            {
                base.DestroyNativeControlCore(control);
            }
        }

        /// <summary>Destroys the already detached WebView on its owning UI thread.</summary>
        /// <param name="view">The WebView owned by this host.</param>
        private static void DestroyWebView(WebView view)
        {
            try
            {
                view.Destroy();
            }
            finally
            {
                view.Dispose();
            }
        }

        private void ReportFailure(string message)
        {
            var attachment = Volatile.Read(ref _attachmentVersion);
            Dispatcher.UIThread.Post(() =>
            {
                if (_attached && attachment == _attachmentVersion && !_reported)
                {
                    _reported = true;
                    _failed(message);
                }
            });
        }

        /// <summary>Lets the app draw native controls in the WebView's four transparent corners.</summary>
        private sealed class DiscOutline : ViewOutlineProvider
        {
            internal static readonly DiscOutline Instance = new();

            /// <inheritdoc />
            public override void GetOutline(View? view, global::Android.Graphics.Outline? outline)
            {
                if (view is PreviewContainer container && outline is not null)
                {
                    // A round rect is supported by outline clipping on every supported Android version.
                    var clip = container.DiscClip;
                    outline.SetRoundRect(clip.X, clip.Y, clip.Right, clip.Bottom, clip.Width / 2f);
                }
            }
        }

        /// <summary>Keeps native painting and hit testing inside the same visible sheet rectangle.</summary>
        private sealed class PreviewContainer(Context context, Action layoutChanged) : FrameLayout(context)
        {
            private WebView? _webView;
            private (int Left, int Top, int Right, int Bottom)? _viewport;
            private global::Android.Graphics.Rect? _clip;
            private bool _isClipped;

            private double _aperture = 1;
            internal PixelRect DiscClip => PracticeStage.GetDiscClip(Width, Height, _aperture);

            internal void SetAperture(double fraction)
            {
                if (_aperture != fraction)
                {
                    _aperture = fraction;
                    InvalidateOutline();
                }
            }

            protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
            {
                base.OnLayout(changed, left, top, right, bottom);
                if (changed)
                {
                    layoutChanged();
                }
            }

            /// <summary>Parent the WebView while keeping its disposal with the preview host.</summary>
            /// <param name="view">The WebView rendered inside this container.</param>
            public void AttachWebView(WebView view)
            {
                _webView = view;
                AddView(view, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            }

            /// <summary>Removes the child before Avalonia releases the container's Java peer.</summary>
            public void DetachWebView()
            {
                if (_webView is { } view)
                {
                    _webView = null;
                    RemoveView(view);
                }
            }

            /// <inheritdoc />
            protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
            {
                base.OnSizeChanged(width, height, oldWidth, oldHeight);
                InvalidateOutline();
            }

            /// <summary>Clips the viewport without measuring or laying out the embedded engine again.</summary>
            public void ClipToViewport(int left, int top, int right, int bottom)
            {
                left = Math.Clamp(left, 0, Width);
                top = Math.Clamp(top, 0, Height);
                right = Math.Clamp(right, left, Width);
                bottom = Math.Clamp(bottom, top, Height);
                var viewport = (left, top, right, bottom);
                var isClipped = left != 0 || top != 0 || right != Width || bottom != Height;
                if (_viewport == viewport && _isClipped == isClipped)
                {
                    return;
                }

                _viewport = viewport;
                _isClipped = isClipped;
                if (!isClipped)
                {
                    ClipBounds = null;
                }
                else
                {
                    (_clip ??= new global::Android.Graphics.Rect()).Set(left, top, right, bottom);
                    ClipBounds = _clip;
                }
            }

            /// <inheritdoc />
            public override bool DispatchTouchEvent(MotionEvent? e)
            {
                if (e?.ActionMasked == MotionEventActions.Down)
                {
                    var x = e.GetX();
                    var y = e.GetY();
                    var radius = DiscClip.Width / 2f;
                    var dx = x - Width / 2f;
                    var dy = y - Height / 2f;
                    if (dx * dx + dy * dy > radius * radius || _viewport is { } viewport &&
                        (x < viewport.Left || x >= viewport.Right || y < viewport.Top || y >= viewport.Bottom))
                    {
                        return false;
                    }
                }
                return base.DispatchTouchEvent(e);
            }

            /// <inheritdoc />
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    DetachWebView();
                    _clip?.Dispose();
                    _clip = null;
                }
                base.Dispose(disposing);
            }
        }

        private sealed class PreviewClient(Uri address, Action<string> reportFailure) : WebViewClient
        {
            private Action<string>? _reportFailure = reportFailure;

            /// <summary>Attaches the active UI callback or releases it while native requests drain.</summary>
            /// <param name="handler">The callback for the active attachment, or null after detach.</param>
            public void SetFailureHandler(Action<string>? handler) => Volatile.Write(ref _reportFailure, handler);

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
                    Volatile.Read(ref _reportFailure)?.Invoke("谱面预览加载失败。");
                }
            }

            public override void OnReceivedHttpError(WebView? view, IWebResourceRequest? request, WebResourceResponse? errorResponse)
            {
                if (request?.IsForMainFrame == true && BelongsToSession(request.Url?.ToString()))
                {
                    Volatile.Read(ref _reportFailure)?.Invoke("谱面预览加载失败。");
                }
            }

            public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
            {
                Volatile.Read(ref _reportFailure)?.Invoke("谱面预览已中断，请重新打开。");
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
