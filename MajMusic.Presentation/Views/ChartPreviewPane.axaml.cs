#nullable enable

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MajMusic.Presentation.Theme;
using MajMusic.Presentation.ViewModels;
using TKCode.Arc.Motion;

namespace MajMusic.Presentation.Views
{
    /// <summary>Navigates an already embedded preview without removing its platform control.</summary>
    public interface IMajViewNavigator
    {
        /// <summary>Navigates within the current preview session.</summary>
        /// <param name="address">The session address with the new difficulty and direction.</param>
        void NavigatePreview(Uri address);
    }

    /// <summary>Opens the selected song in the official MajView renderer.</summary>
    public partial class ChartPreviewPane : UserControl
    {
        private readonly ContentControl _surface;
        private readonly StackPanel _setup;
        private readonly List<Visual> _ancestors = new();
        private MainViewModel? _owner;
        private Uri? _displayedAddress;
        private long _mountVersion;

        /// <summary>Creates an inline platform control which owns its renderer through visual attachment.</summary>
        public static Func<Uri, Action<string>, Control>? CreateEmbeddedView { get; set; }

        /// <summary>Initializes the shared desktop and mobile preview entry.</summary>
        public ChartPreviewPane()
        {
            InitializeComponent();
            _surface = this.FindControl<ContentControl>("PreviewSurface")!;
            _setup = this.FindControl<StackPanel>("PreviewSetup")!;
            DataContextChanged += (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not null)
                {
                    ObserveOwner(DataContext as MainViewModel);
                }
            };
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _ancestors.AddRange(this.GetVisualAncestors());
            foreach (var ancestor in _ancestors)
            {
                ancestor.PropertyChanged += OnVisibilityChanged;
            }
            ObserveOwner(DataContext as MainViewModel);
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ObserveOwner(null);
            foreach (var ancestor in _ancestors)
            {
                ancestor.PropertyChanged -= OnVisibilityChanged;
            }
            _ancestors.Clear();
            base.OnDetachedFromVisualTree(e);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && _owner is not null)
            {
                UpdatePreview();
            }
        }

        private async void OnRetryMajView(object? sender, RoutedEventArgs args)
        {
            if (_owner is { } owner && IsPaneActive(owner))
            {
                await owner.OpenDefaultMajViewAsync();
            }
        }

        private void ObserveOwner(MainViewModel? owner)
        {
            if (ReferenceEquals(_owner, owner))
            {
                return;
            }
            if (_owner is not null)
            {
                _owner.PropertyChanged -= OnOwnerChanged;
                CloseOwnedPreview();
            }
            _owner = owner;
            if (owner is not null)
            {
                owner.PropertyChanged += OnOwnerChanged;
            }
            UpdatePreview();
        }

        private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(MainViewModel.MajViewAddress) or nameof(MainViewModel.IsSongChartPreviewSelected)
                or nameof(MainViewModel.IsSongDetailsExpanded))
            {
                UpdatePreview();
            }
        }

        private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == IsVisibleProperty && !IsEffectivelyVisible)
            {
                CloseOwnedPreview();
            }
        }

        private bool IsPaneActive(MainViewModel owner) =>
            IsEffectivelyVisible && owner.IsSongChartPreviewSelected && owner.IsSongDetailsExpanded;

        private void UpdatePreview()
        {
            if (_owner is not { } owner || !IsPaneActive(owner))
            {
                CloseOwnedPreview();
                return;
            }
            if (owner.MajViewAddress is not { } address)
            {
                ClearSurface();
                return;
            }
            if (_displayedAddress == address)
            {
                return;
            }
            if (_displayedAddress is { } current && SameSession(current, address)
                && _surface.Content is IMajViewNavigator navigator)
            {
                _displayedAddress = address;
                navigator.NavigatePreview(WithViewOptions(address));
                return;
            }

            ClearSurface();
            _displayedAddress = address;
            _surface.IsVisible = true;
            _setup.IsVisible = false;
            var version = _mountVersion;
            Dispatcher.UIThread.Post(() => MountWhenSettled(owner, address, version), DispatcherPriority.Loaded);
        }

        private async void MountWhenSettled(MainViewModel owner, Uri address, long version)
        {
            try
            {
                if (!CanMount(owner, address, version))
                {
                    return;
                }
                if (_ancestors.OfType<SongDetailsOverlay>().FirstOrDefault() is { } overlay)
                {
                    await overlay.OverlayUpdate;
                }
                else if (_ancestors.OfType<InterruptibleSongSheet>().FirstOrDefault() is { } sheet)
                {
                    await sheet.SheetUpdate;
                }
                if (!CanMount(owner, address, version))
                {
                    return;
                }
                if (CreateEmbeddedView is not { } create)
                {
                    owner.ReportMajViewFailure(address, "当前平台不支持内嵌谱面预览");
                    return;
                }

                var embedded = create(WithViewOptions(address), message =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (CanMount(owner, address, version))
                        {
                            owner.ReportMajViewFailure(address, message);
                        }
                    }));
                _surface.Content = embedded;
            }
            catch (Exception)
            {
                if (CanMount(owner, address, version))
                {
                    owner.ReportMajViewFailure(address, "无法启动谱面预览");
                }
            }
        }

        private bool CanMount(MainViewModel owner, Uri address, long version) => version == _mountVersion
            && ReferenceEquals(_owner, owner) && owner.MajViewAddress is { } active && SameSession(active, address) && IsPaneActive(owner)
            && TopLevel.GetTopLevel(this) is not null;

        private Uri WithViewOptions(Uri address)
        {
            var theme = ActualThemeVariant == ThemeVariant.Light ? "light" : "dark";
            var query = address.Query.TrimStart('?') + "&theme=" + theme;
            if (ArcMotion.GetEffectiveLevel(this) != ArcMotionLevel.Full)
            {
                query += "&motion=off";
            }
            return new UriBuilder(address) { Query = query.TrimStart('&') }.Uri;
        }

        private static bool SameSession(Uri first, Uri second) =>
            first.GetLeftPart(UriPartial.Path) == second.GetLeftPart(UriPartial.Path);

        private void CloseOwnedPreview()
        {
            var owned = _displayedAddress is not null;
            ClearSurface();
            if (owned)
            {
                _owner?.CloseMajView();
            }
        }

        private void ClearSurface()
        {
            _mountVersion++;
            _displayedAddress = null;
            _surface.Content = null;
            _surface.IsVisible = false;
            _setup.IsVisible = true;
        }
    }
}
