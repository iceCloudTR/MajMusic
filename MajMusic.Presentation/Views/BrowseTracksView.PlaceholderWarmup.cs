#nullable enable

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Prepares static loading templates after real content has already reached the native frame loop.</summary>
    public partial class BrowseTracksView
    {
        /// <summary>Rejects callbacks queued for an old owner, viewport, attachment or loading state.</summary>
        private long _placeholderWarmupVersion;
        /// <summary>Coalesces repeated layout notifications for the same preparation target.</summary>
        private bool _placeholderWarmupPending;
        /// <summary>Retains the native lane selected by the queued preparation.</summary>
        private Size _placeholderWarmupViewport;
        /// <summary>Retains the columns selected by the queued preparation.</summary>
        private int _placeholderWarmupColumns;
        /// <summary>Retains the density selected by the queued preparation.</summary>
        private SongCardLayout _placeholderWarmupLayout;
        /// <summary>Records optional preparation failures without interfering with actual navigation.</summary>
        private int _placeholderWarmupFailures;

        /// <summary>Gets optional static-template preparation failures observed by an isolated rendering diagnostic.</summary>
        public int PlaceholderWarmupFailures => _placeholderWarmupFailures;

        /// <summary>Schedules only the latest bounded static target after an actual frame and behind user input.</summary>
        private void QueuePlaceholderWarmup()
        {
            if (_browseOwner is not { IsBrowsePlaceholderVisible: false } owner || owner.CardTracks.Count == 0
                || !IsEffectivelyVisible || _browseViewport.Width <= 0 || _browseViewport.Height <= 0
                || TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }
            var skeleton = this.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!;
            var viewport = _browseViewport;
            var columns = owner.BrowseColumns;
            var layout = CardLayout;
            if (skeleton.IsPreparedFor(viewport, columns, layout)
                || _placeholderWarmupPending && _placeholderWarmupViewport == viewport
                    && _placeholderWarmupColumns == columns && _placeholderWarmupLayout == layout)
            {
                return;
            }
            var version = ++_placeholderWarmupVersion;
            _placeholderWarmupPending = true;
            _placeholderWarmupViewport = viewport;
            _placeholderWarmupColumns = columns;
            _placeholderWarmupLayout = layout;
            topLevel.RequestAnimationFrame(_ => Dispatcher.UIThread.Post(() => WarmPlaceholders(version, owner, viewport, columns, layout), DispatcherPriority.Background));
        }

        /// <summary>Prepares only hidden static controls while the original real-content viewport is still current.</summary>
        /// <param name="version">The callback generation.</param>
        /// <param name="owner">The workspace captured when scheduling.</param>
        /// <param name="viewport">The captured actual content lane.</param>
        /// <param name="columns">The captured responsive column count.</param>
        /// <param name="layout">The captured card density.</param>
        private void WarmPlaceholders(long version, MainViewModel owner, Size viewport, int columns, SongCardLayout layout)
        {
            if (version != _placeholderWarmupVersion)
            {
                return;
            }
            _placeholderWarmupPending = false;
            if (!ReferenceEquals(_browseOwner, owner) || owner.IsBrowsePlaceholderVisible || owner.CardTracks.Count == 0
                || _browseViewport != viewport || owner.BrowseColumns != columns || CardLayout != layout
                || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is null)
            {
                return;
            }
            var moving = this.GetVisualAncestors().OfType<MusicSurfaceHost>()
                .Where(host => host.IsEffectivelyVisible)
                .Select(host => host.TransitionUpdate)
                .Append(_surface.TransitionUpdate)
                .Append(ResidentTransitionUpdate)
                .Where(update => !update.IsCompleted)
                .ToArray();
            if (moving.Length > 0)
            {
                _placeholderWarmupPending = true;
                _ = Task.WhenAll(moving).ContinueWith(task =>
                {
                    _ = task.Exception;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (version == _placeholderWarmupVersion)
                        {
                            _placeholderWarmupPending = false;
                            QueuePlaceholderWarmup();
                        }
                    }, DispatcherPriority.Background);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }
            try
            {
                this.FindControl<BrowseSkeletonView>("BrowseSkeletonSurface")!.PrepareViewport(viewport, columns, layout);
            }
            catch (Exception exception)
            {
                _placeholderWarmupFailures++;
                Trace.WriteLine("Static music placeholder preparation skipped: " + exception.GetType().Name);
            }
        }

        /// <summary>Retires callbacks before changing ownership or starting a visible loading presentation.</summary>
        private void CancelPlaceholderWarmup()
        {
            _placeholderWarmupVersion++;
            _placeholderWarmupPending = false;
        }
    }
}
