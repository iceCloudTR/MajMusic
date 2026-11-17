#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Theme;

namespace MajMusic.Presentation.Views
{
    /// <summary>Hosts desktop and touch presentations around the same music workspace and commands.</summary>
    public partial class MainView
    {
        /// <summary>Identifies the shared navigation section independently of the desktop or mobile chrome.</summary>
        public static readonly StyledProperty<int> SelectedSectionIndexProperty = AvaloniaProperty.Register<MainView, int>(
            nameof(SelectedSectionIndex), defaultBindingMode: BindingMode.TwoWay);
        /// <summary>Identifies whether the active presentation uses the touch layout.</summary>
        public static readonly StyledProperty<bool> IsMobileLayoutProperty = AvaloniaProperty.Register<MainView, bool>(nameof(IsMobileLayout));
        /// <summary>Retains the desktop content and its page-local navigation while it is detached.</summary>
        private Control? _desktopContent;
        /// <summary>Creates the mobile presentation only after a narrow viewport requests it.</summary>
        private MobileMusicView? _mobileContent;

        /// <summary>Gets or sets the active home, playlists, directories, accounts or settings section.</summary>
        public int SelectedSectionIndex
        {
            get => GetValue(SelectedSectionIndexProperty);
            set => SetValue(SelectedSectionIndexProperty, Math.Clamp(value, 0, 4));
        }
        /// <summary>Gets whether the current viewport displays the touch presentation.</summary>
        public bool IsMobileLayout => GetValue(IsMobileLayoutProperty);

        /// <summary>Registers responsive layout ownership without starting a second workspace or playback service.</summary>
        private void InitializeResponsiveShell()
        {
            _desktopContent = (Control)Content!;
            SizeChanged += OnShellSizeChanged;
            var tabs = this.FindControl<TabControl>("MainTabs")!;
            tabs.SelectionChanged += (_, _) => SelectedSectionIndex = tabs.SelectedIndex;
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SelectedSectionIndexProperty)
            {
                if (DataContext is MainViewModel owner && owner.IsSongDetailsExpanded)
                {
                    owner.CloseExpandedSongDetailsCommand.Execute(null);
                }
                if (_desktopContent is not null && this.FindControl<TabControl>("MainTabs") is { } tabs
                    && tabs.SelectedIndex != SelectedSectionIndex)
                {
                    tabs.SelectedIndex = SelectedSectionIndex;
                }

                if (_mobileContent is not null && _mobileContent.SelectedSectionIndex != SelectedSectionIndex)
                {
                    _mobileContent.SelectedSectionIndex = SelectedSectionIndex;
                }
            }
        }

        /// <summary>Changes page presentation while retaining the current music, queue, search and account state.</summary>
        /// <param name="section">Zero-based home, playlists, directories, accounts or settings section.</param>
        public void NavigateToSection(int section)
        {
            SelectedSectionIndex = section;
        }

        /// <summary>Opens the touch inspector after a card selection; desktop details remain in their fixed sidebar.</summary>
        public void OpenTrackDetails()
        {
            if (IsMobileLayout)
            {
                _mobileContent?.OpenTrackDetails();
            }
        }

        /// <summary>Exchanges active presentation trees so hidden account and song controls cannot duplicate user actions.</summary>
        /// <param name="sender">The shared root view.</param>
        /// <param name="e">The current viewport dimensions.</param>
        private void OnShellSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            var mobile = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || e.NewSize.Width is > 0 and < 680;
            if (mobile == IsMobileLayout || _desktopContent is null)
            {
                return;
            }

            if (DataContext is MainViewModel { IsSongDetailsExpanded: true } owner)
            {
                owner.IsSongDetailsExpanded = false;
            }
            if (mobile)
            {
                this.FindControl<SharedSongCardFlight>("DesktopSongCardFlight")?.MoveTo(false);
            }

            if (mobile && _mobileContent is null)
            {
                _mobileContent = new MobileMusicView { SelectedSectionIndex = SelectedSectionIndex };
                _mobileContent.PropertyChanged += (_, change) =>
                {
                    if (change.Property == MobileMusicView.SelectedSectionIndexProperty)
                    {
                        SelectedSectionIndex = _mobileContent.SelectedSectionIndex;
                    }
                };
                _mobileContent.BrowseDirectoryRequested += (_, _) => OnDirectoryBrowseClicked(this, new RoutedEventArgs());
                _mobileContent.ImportCollectionRequested += (_, _) => OnCollectionImportClicked(this, new RoutedEventArgs());
                _mobileContent.ExportCollectionRequested += (_, _) => OnCollectionExportClicked(this, new RoutedEventArgs());
            }

            SetValue(IsMobileLayoutProperty, mobile);
            Content = mobile ? _mobileContent : _desktopContent;
            if (!mobile)
            {
                NotifyPageViewports();
            }
        }
    }
}
