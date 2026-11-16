#nullable enable

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using ArcPage = TKCode.Arc.Mobile.Page;

namespace MajMusic.Presentation.Views
{
    /// <summary>Lets an explicitly opting-in host apply normalized platform insets exactly once to its mobile page.</summary>
    public sealed class PageSafeAreaOwner : IDisposable
    {
        /// <summary>Reuses one registration per live host without retaining detached activity views.</summary>
        private static readonly ConditionalWeakTable<Control, PageSafeAreaOwner> Owners = new();
        /// <summary>Provides authoritative normalized safe-area values and scaling notifications.</summary>
        private readonly TopLevel _topLevel;
        /// <summary>Owns the root's attachment lifetime.</summary>
        private readonly Control _root;
        /// <summary>Applies the single inner safe-area padding and preserves Arc keyboard avoidance.</summary>
        private readonly ArcPage _page;
        /// <summary>Reports platform safe-area values already expressed in logical pixels.</summary>
        private readonly ISafeAreaSource _insets;
        /// <summary>Rejects callbacks after the root's presentation lifetime ends.</summary>
        private bool _disposed;

        /// <summary>Connects normalized platform insets to one page while keeping safe areas out of the outer root.</summary>
        /// <param name="topLevel">The actual host window owning scale and system insets.</param>
        /// <param name="root">The single root whose automatic outer padding would otherwise duplicate the page.</param>
        /// <param name="page">The mobile page that owns the one visible safe area.</param>
        /// <param name="insets">The current platform source reporting already normalized padding.</param>
        internal PageSafeAreaOwner(TopLevel topLevel, Control root, ArcPage page, ISafeAreaSource insets)
        {
            _topLevel = topLevel;
            _root = root;
            _page = page;
            _insets = insets;
            TopLevel.SetAutoSafeAreaPadding(root, false);
            page.UsePlatformSafeArea = false;
            _insets.Changed += OnSafeAreaChanged;
            _insets.EnableEdgeToEdge();

            _topLevel.ScalingChanged += OnScalingChanged;
            _root.DetachedFromVisualTree += OnRootDetached;
            Refresh();
        }

        /// <summary>Registers a host explicitly; other desktop, Apple and unregistered mobile providers remain unchanged.</summary>
        /// <param name="topLevel">The actual platform presentation root.</param>
        /// <param name="root">The activity's shared music view.</param>
        /// <param name="page">The page receiving one normalized safe-area padding.</param>
        /// <returns>The live root-owned registration.</returns>
        public static PageSafeAreaOwner Attach(TopLevel topLevel, Control root, ArcPage page)
        {
            if (Owners.TryGetValue(root, out var current) && !current._disposed)
            {
                return current;
            }

            Owners.Remove(root);
            var owner = new PageSafeAreaOwner(topLevel, root, page, new NativeInsetsSource(topLevel.InsetsManager));
            Owners.Add(root, owner);
            return owner;
        }

        /// <summary>Reads normalized values again after a system-bar, cutout or safe-area update.</summary>
        /// <param name="sender">The actual platform inset manager.</param>
        /// <param name="args">The safe-area event; authoritative current values are read after scaling.</param>
        private void OnSafeAreaChanged(object? sender, EventArgs args)
        {
            Refresh();
        }

        /// <summary>Refreshes cached page padding after the native platform changes its logical-to-physical scale.</summary>
        /// <param name="sender">The host reporting its new scale.</param>
        /// <param name="args">The scaling notification.</param>
        private void OnScalingChanged(object? sender, EventArgs args)
        {
            Refresh();
        }

        /// <summary>Applies the platform's logical-pixel padding without dividing it a second time.</summary>
        private void Refresh()
        {
            if (!_disposed)
            {
                _page.SafeArea = _insets.Padding;
            }
        }

        /// <summary>Ends native subscriptions when the activity view leaves its presentation tree.</summary>
        /// <param name="sender">The activity root being detached.</param>
        /// <param name="args">The root-detachment notification.</param>
        private void OnRootDetached(object? sender, VisualTreeAttachmentEventArgs args)
        {
            Dispose();
        }

        /// <summary>Releases only this root's native observers without changing another platform's provider.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _insets.Changed -= OnSafeAreaChanged;
            _insets.Dispose();

            _topLevel.ScalingChanged -= OnScalingChanged;
            _root.DetachedFromVisualTree -= OnRootDetached;
            Owners.Remove(_root);
        }

        /// <summary>Provides normalized inset values without asking hosts to implement Avalonia's restricted native interface.</summary>
        internal interface ISafeAreaSource : IDisposable
        {
            /// <summary>Gets current padding already expressed in the host's logical pixels.</summary>
            Thickness Padding { get; }
            /// <summary>Occurs after native system bars or cutouts change the current padding.</summary>
            event EventHandler? Changed;
            /// <summary>Asks the native host to draw behind system bars while preserving their safe area.</summary>
            void EnableEdgeToEdge();
        }

        /// <summary>Adapts the real platform-owned Avalonia insets manager without changing its normalization contract.</summary>
        private sealed class NativeInsetsSource : ISafeAreaSource
        {
            /// <summary>Provides platform values and native event subscriptions.</summary>
            private readonly IInsetsManager? _manager;

            /// <summary>Observes the native manager for the root's presentation lifetime.</summary>
            /// <param name="manager">The actual platform manager, or null on a host without insets.</param>
            public NativeInsetsSource(IInsetsManager? manager)
            {
                _manager = manager;
                if (_manager is not null)
                {
                    _manager.SafeAreaChanged += OnChanged;
                }
            }

            /// <inheritdoc />
            public Thickness Padding => _manager?.SafeAreaPadding ?? default;
            /// <inheritdoc />
            public event EventHandler? Changed;

            /// <inheritdoc />
            public void EnableEdgeToEdge()
            {
                if (_manager is not null)
                {
                    _manager.DisplayEdgeToEdgePreference = true;
                }
            }

            /// <summary>Forwards only notification of a native safe-area change; consumers reread current normalized values.</summary>
            /// <param name="sender">The platform manager.</param>
            /// <param name="args">The native safe-area event.</param>
            private void OnChanged(object? sender, SafeAreaChangedArgs args)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            /// <inheritdoc />
            public void Dispose()
            {
                if (_manager is not null)
                {
                    _manager.SafeAreaChanged -= OnChanged;
                }
            }
        }
    }
}
