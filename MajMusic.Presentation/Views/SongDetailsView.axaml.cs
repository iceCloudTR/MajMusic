#nullable enable

using System.Globalization;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Displays shared expanded metadata, plain-text social threads and existing song commands.</summary>
    public partial class SongDetailsView : UserControl
    {
        /// <summary>Observes only the attached detail owner's social availability.</summary>
        private MainViewModel? _owner;
        /// <summary>Defers template events until the compiled view has installed its name scope.</summary>
        private bool _initialized;
        /// <summary>Retains the bounded comment layout used when a short window or keyboard reduces available height.</summary>
        private bool _compactComments;
        /// <summary>Distinguishes the first chrome synchronization from an unchanged regular layout.</summary>
        private bool _chromeInitialized;
        private bool _chartPreview;
        private readonly SongPreviewTabs _tabs;
        /// <summary>Creates the expanded content without requesting social data or starting playback.</summary>
        public SongDetailsView()
        {
            InitializeComponent();
            _tabs = this.FindControl<SongPreviewTabs>("SongPreviewSegments")!;
            _tabs.SelectionChanged += OnDetailsTabSelectionChanged;
            _initialized = true;
            DataContextChanged += OnOwnerChanged;
            SizeChanged += OnDetailsSizeChanged;
            UpdatePaneVisibility();
        }

        /// <summary>Gives discussions and the chart area the available height while metadata retains its artwork summary.</summary>
        /// <param name="sender">The detail content tabs.</param>
        /// <param name="e">The selected detail pane.</param>
        private void OnDetailsTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ReferenceEquals(e.Source, _tabs))
            {
                UpdatePaneVisibility();
            }
        }

        private void UpdatePaneVisibility()
        {
            var slot = _tabs.SelectedValue is int selection ? selection : _owner?.IsSongChartPreviewSelected == true ? 1 : -1;
            this.FindControl<Grid>("SongDetailsSummary")!.IsVisible = slot == -1;
            this.FindControl<ScrollViewer>("SongOverviewPane")!.IsVisible = slot == -1;
            this.FindControl<Grid>("SongCommentsLayout")!.IsVisible = slot == 0;
            this.FindControl<Border>("ChartPreviewHost")!.IsVisible = slot > 0;
            UpdateViewportChrome();
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ObserveOwner(DataContext as MainViewModel);
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ObserveOwner(null);
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>Rebinds social availability when an existing content view receives another workspace.</summary>
        /// <param name="sender">The detail view.</param>
        /// <param name="e">The changed context.</param>
        private void OnOwnerChanged(object? sender, EventArgs e)
        {
            if (TopLevel.GetTopLevel(this) is not null)
            {
                ObserveOwner(DataContext as MainViewModel);
            }
        }

        /// <summary>Prevents a hidden social tab from retaining active controls for an offline or local selection.</summary>
        /// <param name="owner">The currently attached workspace, or null on detach.</param>
        private void ObserveOwner(MainViewModel? owner)
        {
            if (_owner is not null)
            {
                _owner.PropertyChanged -= OnSocialAvailabilityChanged;
            }
            _owner = owner;
            if (_owner is not null)
            {
                _owner.PropertyChanged += OnSocialAvailabilityChanged;
            }
            UpdatePaneVisibility();
        }

        /// <summary>Returns to ordinary metadata when social actions stop belonging to the selected song.</summary>
        /// <param name="sender">The current workspace.</param>
        /// <param name="e">The changed social or selection property.</param>
        private void OnSocialAvailabilityChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsSongChartPreviewSelected))
            {
                UpdatePaneVisibility();
            }
            else if (e.PropertyName is nameof(MainViewModel.HasSongReplyTarget) or nameof(MainViewModel.HasSongSocialStatus))
            {
                UpdateViewportChrome();
            }
        }

        /// <summary>Keeps the input fixed within the detail tab when the window or software keyboard leaves a short viewport.</summary>
        /// <param name="sender">The detail view.</param>
        /// <param name="e">The available detail-region size.</param>
        private void OnDetailsSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            UpdateViewportChrome();
        }

        /// <summary>Moves secondary actions into the existing title row without introducing an outer scroll surface.</summary>
        private void UpdateViewportChrome()
        {
            if (!_initialized)
            {
                return;
            }
            var threshold = 460 + (_owner?.HasSongReplyTarget == true ? 44 : 0) + (_owner?.HasSongSocialStatus == true ? 28 : 0);
            var compact = Equals(_tabs.SelectedValue, 0) && Bounds.Height > 0 && Bounds.Height < threshold;
            var chart = this.FindControl<Border>("ChartPreviewHost")!.IsVisible;
            if (_chromeInitialized && compact == _compactComments && chart == _chartPreview)
            {
                return;
            }
            _chromeInitialized = true;
            _compactComments = compact;
            _chartPreview = chart;
            var layout = this.FindControl<Grid>("SongDetailsLayout")!;
            layout.Margin = chart ? new Thickness(6, 4) : compact ? new Thickness(16, 4) : new Thickness(16);
            layout.RowSpacing = chart ? 4 : compact ? 3 : 12;
            var comments = this.FindControl<Grid>("SongCommentsLayout")!;
            comments.Margin = new Thickness(0, compact ? 3 : 8, 0, 0);
            comments.RowSpacing = compact ? 3 : 8;
            this.FindControl<Grid>("SongCommentsHeader")!.IsVisible = !compact;
            this.FindControl<Grid>("SongMoreCommentsHost")!.IsVisible = !compact;
            this.FindControl<Grid>("SongDetailsFooter")!.IsVisible = !compact && !chart;
            this.FindControl<StackPanel>("CompactSongDetailsActions")!.IsVisible = compact;
            this.FindControl<TextBlock>("SongCommentsStatus")!.MaxLines = compact ? 1 : 2;
        }

        /// <summary>Explicitly plays the inspected entry rather than resuming an unrelated paused song.</summary>
        /// <param name="sender">The fixed song-play action.</param>
        /// <param name="e">The explicit user activation.</param>
        private async void OnPlaySelectedClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel owner)
            {
                await owner.PlaySelectedAsync();
            }
        }
    }

    /// <summary>Preserves visible thread depth while bounding indentation on narrow screens.</summary>
    public sealed class CommentIndentConverter : IValueConverter
    {
        /// <inheritdoc />
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return new Thickness(value is int depth ? Math.Clamp(depth, 0, 3) * 12 : 0, 0, 0, 0);
        }

        /// <inheritdoc />
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException("Thread indentation is one-way.");
        }
    }
}
