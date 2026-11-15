#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Provides a shared music view with platform file selection at its boundary.</summary>
    public partial class MainView : UserControl
    {
        /// <summary>Initializes the shared view.</summary>
        public MainView()
        {
            InitializeComponent();
            var timeline = this.FindControl<TKCode.Arc.Slider>("PlaybackTimeline")!;
            timeline.AddHandler(PointerPressedEvent, OnTimelinePointerPressed, RoutingStrategies.Tunnel, true);
            timeline.AddHandler(PointerReleasedEvent, OnTimelinePointerReleased, RoutingStrategies.Bubble, true);
            timeline.AddHandler(KeyDownEvent, OnTimelineKeyPressed, RoutingStrategies.Tunnel, true);
            timeline.AddHandler(KeyUpEvent, OnTimelineKeyReleased, RoutingStrategies.Bubble, true);
            timeline.LostFocus += OnTimelineLostFocus;
            this.FindControl<TextBox>("LibrarySearch")!.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel, true);
            DataContextChanged += OnViewDataContextChanged;
            InitializeResponsiveShell();
        }

        /// <summary>Returns to the library after explicitly opening the selected playlist.</summary>
        /// <param name="sender">The playlist open button.</param>
        /// <param name="e">The routed click event.</param>
        private void OnOpenPlaylistClicked(object? sender, RoutedEventArgs e)
        {
            NavigateToSection(0);
        }

        /// <summary>Synchronizes page capacities when an existing view receives its view model.</summary>
        /// <param name="sender">The shared view.</param>
        /// <param name="e">The data-context notification.</param>
        private void OnViewDataContextChanged(object? sender, EventArgs e)
        {
            NotifyPageViewports();
        }

        /// <summary>Reports available page space without moving or hiding any layout region.</summary>
        private void NotifyPageViewports()
        {
            if (IsMobileLayout || DataContext is not MainViewModel viewModel)
            {
                return;
            }

            var playlistHeight = this.FindControl<ListBox>("PlaylistList")!.Bounds.Height;
            var directoryHeight = this.FindControl<ListBox>("DirectoryList")!.Bounds.Height;
            if (playlistHeight > 0)
            {
                viewModel.SetPlaylistViewportHeight(playlistHeight);
            }

            if (directoryHeight > 0)
            {
                viewModel.SetDirectoryViewportHeight(directoryHeight);
            }
        }

        /// <summary>Fits the current playlist page to the available list area.</summary>
        /// <param name="sender">The playlist list.</param>
        /// <param name="e">The updated available size.</param>
        private void OnPlaylistViewportSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Height > 0 && DataContext is MainViewModel viewModel)
            {
                viewModel.SetPlaylistViewportHeight(e.NewSize.Height);
            }
        }

        /// <summary>Fits the current directory page to the available list area.</summary>
        /// <param name="sender">The directory list.</param>
        /// <param name="e">The updated available size.</param>
        private void OnDirectoryViewportSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Height > 0 && DataContext is MainViewModel viewModel)
            {
                viewModel.SetDirectoryViewportHeight(e.NewSize.Height);
            }
        }

        /// <summary>Focuses search after the existing command reveals the input in its reserved toolbar area.</summary>
        /// <param name="sender">The search icon.</param>
        /// <param name="e">The routed click notification.</param>
        private void OnBeginSearchClicked(object? sender, RoutedEventArgs e)
        {
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("LibrarySearch")!.Focus(), DispatcherPriority.Input);
        }

        /// <summary>Closes search through its existing command while preserving focus for the toolbar's return transition.</summary>
        /// <param name="sender">The close icon whose focus will return to the search trigger.</param>
        /// <param name="e">The routed click notification.</param>
        private void OnCancelSearchClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel && viewModel.CancelSearchCommand.CanExecute(null))
            {
                viewModel.CancelSearchCommand.Execute(null);
            }
        }

        /// <summary>Runs the existing observed search command when Enter is pressed in the search box.</summary>
        /// <param name="sender">The search field.</param>
        /// <param name="e">The keyboard event.</param>
        private void OnSearchKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataContext is MainViewModel viewModel && viewModel.SearchCommand.CanExecute(null))
            {
                viewModel.SearchCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is MainViewModel current && current.CancelSearchCommand.CanExecute(null))
            {
                current.CancelSearchCommand.Execute(null);
                e.Handled = true;
            }
        }

        /// <summary>Chooses a path for the currently edited source role.</summary>
        /// <param name="sender">The browse button.</param>
        /// <param name="e">The routed click event.</param>
        private async void OnDirectoryBrowseClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            try
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择目录", AllowMultiple = false });
                if (folders.Count > 0)
                {
                    using var folder = folders[0];
                    viewModel.DirectoryPath = folder.TryGetLocalPath() ?? throw new NotSupportedException("请选择可直接访问的本地目录。");
                }
            }
            catch (Exception exception)
            {
                viewModel.ReportError(exception);
            }
        }

        /// <summary>Persists a user-selected saved account or Guest through the observed view lifetime.</summary>
        /// <param name="sender">The saved-account selector.</param>
        /// <param name="e">The selection event.</param>
        private async void OnSavedAccountSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                await viewModel.CommitSavedAccountSelectionAsync();
            }
        }

        /// <summary>Chooses a Maj playlist or favorites document without changing its contents.</summary>
        /// <param name="sender">The import button.</param>
        /// <param name="e">The routed event.</param>
        private async void OnCollectionImportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            try
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择 Maj 播放列表或 MyFavorites.json",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Maj JSON") { Patterns = new[] { "*.json" } } }
                });
                if (files.Count > 0)
                {
                    using var file = files[0];
                    var path = file.TryGetLocalPath() ?? throw new NotSupportedException("请先将列表文件保存到本地。");
                    await viewModel.ImportCollectionAsync(path);
                }
            }
            catch (Exception exception)
            {
                viewModel.ReportError(exception);
            }
        }

        /// <summary>Chooses an explicit new export destination; the application protects read-only source roots.</summary>
        /// <param name="sender">The export button.</param>
        /// <param name="e">The routed event.</param>
        private async void OnCollectionExportClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            try
            {
                using var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "导出播放列表",
                    SuggestedFileName = viewModel.ExportFileName,
                    SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(viewModel.DataDirectory)),
                    DefaultExtension = "json",
                    FileTypeChoices = new[] { new FilePickerFileType("Maj JSON") { Patterns = new[] { "*.json" } } }
                });
                if (file is not null)
                {
                    var path = file.TryGetLocalPath() ?? throw new NotSupportedException("请选择本地导出位置。");
                    await viewModel.ExportCollectionAsync(path);
                }
            }
            catch (Exception exception)
            {
                viewModel.ReportError(exception);
            }
        }

        /// <summary>Starts previewing before the slider moves its thumb or track.</summary>
        /// <param name="sender">The timeline slider.</param>
        /// <param name="e">The pointer press.</param>
        private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel
                && (e.Pointer.Type is PointerType.Touch or PointerType.Pen
                    || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed))
            {
                viewModel.BeginSeek();
            }
        }

        /// <summary>Commits one seek after the slider has handled a pointer release.</summary>
        /// <param name="sender">The timeline slider.</param>
        /// <param name="e">The pointer release.</param>
        private async void OnTimelinePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel && (e.InitialPressMouseButton == MouseButton.Left
                || e.Pointer.Type is PointerType.Touch or PointerType.Pen))
            {
                await viewModel.CommitSeekAsync();
            }
        }

        /// <summary>Starts a keyboard preview before the slider changes its value.</summary>
        /// <param name="sender">The timeline slider.</param>
        /// <param name="e">The key press.</param>
        private void OnTimelineKeyPressed(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                viewModel.CancelSeek();
                e.Handled = true;
            }
            else if (IsSeekKey(e.Key) && e.KeyModifiers == KeyModifiers.None)
            {
                if (viewModel.BeginSeek())
                {
                    var target = e.Key switch
                    {
                        Key.Home => 0,
                        Key.End => viewModel.DurationSeconds,
                        Key.Left or Key.Down => viewModel.PositionSeconds - 5,
                        Key.Right or Key.Up => viewModel.PositionSeconds + 5,
                        Key.PageDown => viewModel.PositionSeconds - 15,
                        _ => viewModel.PositionSeconds + 15
                    };
                    viewModel.PositionSeconds = Math.Clamp(target, 0, viewModel.DurationSeconds);
                    if (sender is RangeBase slider)
                    {
                        slider.SetCurrentValue(RangeBase.ValueProperty, viewModel.PositionSeconds);
                    }
                }

                e.Handled = true;
            }
        }

        /// <summary>Commits one seek when the corresponding keyboard gesture ends.</summary>
        /// <param name="sender">The timeline slider.</param>
        /// <param name="e">The key release.</param>
        private async void OnTimelineKeyReleased(object? sender, KeyEventArgs e)
        {
            if (IsSeekKey(e.Key) && DataContext is MainViewModel viewModel)
            {
                await viewModel.CommitSeekAsync();
            }
        }

        /// <summary>Discards an unfinished keyboard preview when focus moves away.</summary>
        /// <param name="sender">The timeline slider.</param>
        /// <param name="e">The focus event.</param>
        private void OnTimelineLostFocus(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel { IsSeeking: true } viewModel)
            {
                viewModel.CancelSeek();
            }
        }

        /// <summary>Identifies slider navigation keys that may change the media position.</summary>
        /// <param name="key">The input key.</param>
        /// <returns>Whether the key participates in a seek gesture.</returns>
        private static bool IsSeekKey(Key key)
        {
            return key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;
        }

    }
}
