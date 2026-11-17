#nullable enable

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Presentation.Theme;

namespace MajMusic.Presentation.Views
{
    /// <summary>Prepares existing inactive desktop page templates during separate idle frames without attaching them visually.</summary>
    public partial class MainView
    {
        /// <summary>Retains only this root's existing inactive page controls.</summary>
        private readonly Queue<Control> _desktopPageWarmupQueue = new();
        /// <summary>Rejects pending work after attachment, owner or viewport changes.</summary>
        private long _desktopPageWarmupVersion;
        /// <summary>Records whether one frame and its subsequent background action are pending.</summary>
        private bool _desktopPageWarmupPending;
        /// <summary>Tracks the one-shot wait for valid initial page bounds.</summary>
        private bool _desktopPageWarmupWaitingForLayout;
        /// <summary>Prevents duplicate root event subscriptions.</summary>
        private bool _desktopPageWarmupInitialized;
        /// <summary>Counts successfully prepared inactive roots in the current owner and viewport generation.</summary>
        private int _desktopPageWarmupCount;
        /// <summary>Records total synchronous idle preparation work, excluding the frame waits.</summary>
        private TimeSpan _desktopPageWarmupElapsed;
        /// <summary>Counts optional pages whose detached template cannot be prepared safely.</summary>
        private int _desktopPageWarmupFailures;

        /// <summary>Gets the number of existing inactive pages prepared for the current owner and viewport.</summary>
        public int DesktopPageWarmupCount => _desktopPageWarmupCount;
        /// <summary>Gets total detached-template and layout preparation time without idle frame waits.</summary>
        public TimeSpan DesktopPageWarmupElapsed => _desktopPageWarmupElapsed;
        /// <summary>Gets the number of pages which fell back to normal on-demand initialization.</summary>
        public int DesktopPageWarmupFailures => _desktopPageWarmupFailures;

        /// <summary>Installs lifecycle-bound idle preparation after the root's XAML has been constructed.</summary>
        private void InitializeDesktopPageWarmup()
        {
            if (_desktopPageWarmupInitialized)
            {
                return;
            }
            _desktopPageWarmupInitialized = true;
            AttachedToVisualTree += OnDesktopPageWarmupAttached;
            DetachedFromVisualTree += OnDesktopPageWarmupDetached;
            DataContextChanged += OnDesktopPageWarmupContextChanged;
            SizeChanged += OnDesktopPageWarmupSizeChanged;
        }

        /// <summary>Starts a fresh queue only after this root belongs to a desktop visual tree.</summary>
        /// <param name="sender">The attached root.</param>
        /// <param name="args">The new visual attachment.</param>
        private void OnDesktopPageWarmupAttached(object? sender, VisualTreeAttachmentEventArgs args) => RestartDesktopPageWarmup();

        /// <summary>Invalidates all queued work without retaining a detached owner through event subscriptions.</summary>
        /// <param name="sender">The detached root.</param>
        /// <param name="args">The retired visual attachment.</param>
        private void OnDesktopPageWarmupDetached(object? sender, VisualTreeAttachmentEventArgs args) => CancelDesktopPageWarmup();

        /// <summary>Retires the previous workspace's queued pages before preparing templates with the new bindings.</summary>
        /// <param name="sender">The root whose owner changed.</param>
        /// <param name="args">The data-context notification.</param>
        private void OnDesktopPageWarmupContextChanged(object? sender, EventArgs args) => RestartDesktopPageWarmup();

        /// <summary>Measures future idle work against the current desktop viewport and skips responsive mobile roots.</summary>
        /// <param name="sender">The resized root.</param>
        /// <param name="args">The new available size.</param>
        private void OnDesktopPageWarmupSizeChanged(object? sender, SizeChangedEventArgs args) => RestartDesktopPageWarmup();

        /// <summary>Invalidates old frame callbacks and removes a pending one-shot layout subscription.</summary>
        private void CancelDesktopPageWarmup()
        {
            _desktopPageWarmupVersion++;
            _desktopPageWarmupPending = false;
            _desktopPageWarmupQueue.Clear();
            if (_desktopPageWarmupWaitingForLayout)
            {
                LayoutUpdated -= OnDesktopPageWarmupLayoutReady;
                _desktopPageWarmupWaitingForLayout = false;
            }
        }

        /// <summary>Collects original inactive contents without replacing their logical parent or constructing another page.</summary>
        private void RestartDesktopPageWarmup()
        {
            CancelDesktopPageWarmup();
            _desktopPageWarmupCount = 0;
            _desktopPageWarmupElapsed = TimeSpan.Zero;
            _desktopPageWarmupFailures = 0;
            if (TopLevel.GetTopLevel(this) is null || DataContext is null || IsMobileLayout)
            {
                return;
            }
            var tabs = this.FindControl<TabControl>("MainTabs");
            if (tabs is null)
            {
                return;
            }
            if (tabs.Bounds.Width <= 0 || tabs.Bounds.Height <= 0)
            {
                _desktopPageWarmupWaitingForLayout = true;
                LayoutUpdated += OnDesktopPageWarmupLayoutReady;
                return;
            }
            foreach (var item in tabs.Items.OfType<TabItem>())
            {
                if (item.Content is Control page && !ReferenceEquals(page, tabs.SelectedContent) && TopLevel.GetTopLevel(page) is null)
                {
                    _desktopPageWarmupQueue.Enqueue(page);
                }
            }
            ScheduleDesktopPageWarmup(_desktopPageWarmupVersion);
        }

        /// <summary>Uses the next valid layout rather than polling an unarranged page host.</summary>
        /// <param name="sender">The newly arranged root.</param>
        /// <param name="args">The layout notification.</param>
        private void OnDesktopPageWarmupLayoutReady(object? sender, EventArgs args)
        {
            if (this.FindControl<TabControl>("MainTabs") is { Bounds.Width: > 0, Bounds.Height: > 0 })
            {
                RestartDesktopPageWarmup();
            }
        }

        /// <summary>Waits for a separate actual frame before scheduling one low-priority page preparation.</summary>
        /// <param name="version">The root generation which owns the queue.</param>
        private void ScheduleDesktopPageWarmup(long version)
        {
            if (version != _desktopPageWarmupVersion || _desktopPageWarmupPending || _desktopPageWarmupQueue.Count == 0
                || IsMobileLayout || TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }
            _desktopPageWarmupPending = true;
            topLevel.RequestAnimationFrame(_ => Dispatcher.UIThread.Post(() => WarmOneDesktopPage(version), DispatcherPriority.Background));
        }

        /// <summary>Applies and lays out one detached existing content root, leaving Loaded and native view attachment to normal navigation.</summary>
        /// <param name="version">The root generation captured before the frame wait.</param>
        private void WarmOneDesktopPage(long version)
        {
            if (version != _desktopPageWarmupVersion)
            {
                return;
            }
            _desktopPageWarmupPending = false;
            if (IsMobileLayout || TopLevel.GetTopLevel(this) is null || this.FindControl<TabControl>("MainTabs") is not { } tabs)
            {
                return;
            }
            var pageHost = tabs.GetVisualDescendants().OfType<MusicSurfaceHost>()
                .FirstOrDefault(host => host.Name == "PageSurfaceTransition");
            var browser = this.FindControl<BrowseTracksView>("SongBrowser");
            var browseHost = browser?.FindControl<MusicSurfaceHost>("BrowseSurfaceTransition");
            if (IsSurfaceMoving(pageHost) || IsSurfaceMoving(browseHost)
                || browser is { IsEffectivelyVisible: true, ResidentTransitionUpdate.IsCompleted: false }
                || this.FindControl<SharedSongCardFlight>("DesktopSongCardFlight") is { IsEffectivelyVisible: true, MoveUpdate.IsCompleted: false })
            {
                ScheduleDesktopPageWarmup(version);
                return;
            }
            if (_desktopPageWarmupQueue.TryDequeue(out var page)
                && !ReferenceEquals(page, tabs.SelectedContent) && TopLevel.GetTopLevel(page) is null)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    ApplyDetachedPageTemplates(page);
                    page.Measure(tabs.Bounds.Size);
                    page.Arrange(new Rect(tabs.Bounds.Size));
                    // Measurement can realize virtualized item containers whose own templates need the same preparation.
                    ApplyDetachedPageTemplates(page);
                    page.Measure(tabs.Bounds.Size);
                    page.Arrange(new Rect(tabs.Bounds.Size));
                    _desktopPageWarmupCount++;
                }
                catch (Exception exception)
                {
                    _desktopPageWarmupFailures++;
                    Trace.WriteLine("Detached desktop page warmup skipped: " + exception.GetType().Name);
                }
                finally
                {
                    _desktopPageWarmupElapsed += Stopwatch.GetElapsedTime(started);
                }
            }
            ScheduleDesktopPageWarmup(version);
        }

        /// <summary>Keeps optional first-use layout work out of an active visible navigation animation.</summary>
        /// <param name="host">A page or browsing surface which may currently be visible.</param>
        /// <returns>Whether this surface is still painting its transition.</returns>
        private static bool IsSurfaceMoving(MusicSurfaceHost? host)
        {
            return host is { IsEffectivelyVisible: true }
                && (host.TransitionProgress < 1 || !host.TransitionUpdate.IsCompleted);
        }

        /// <summary>Prepares existing visual descendants without assigning another visual or logical parent.</summary>
        /// <param name="control">The original detached page or one of its realized children.</param>
        private static void ApplyDetachedPageTemplates(Control control)
        {
            if (control is TemplatedControl templated)
            {
                templated.ApplyTemplate();
            }
            foreach (var child in control.GetVisualChildren().OfType<Control>())
            {
                ApplyDetachedPageTemplates(child);
            }
        }
    }
}
