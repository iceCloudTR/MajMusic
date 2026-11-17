#nullable enable

using System.Globalization;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Hosts the mobile overview, discussion and chart panes below the song header.</summary>
    public partial class SongDetailsView : UserControl
    {
        /// <summary>Observes only the attached detail owner's social availability.</summary>
        private MainViewModel? _owner;
        private readonly SongPreviewTabs _tabs;
        /// <summary>Creates the expanded content without requesting social data or starting playback.</summary>
        public SongDetailsView()
        {
            InitializeComponent();
            _tabs = this.FindControl<SongPreviewTabs>("SongPreviewSegments")!;
            _tabs.SelectionChanged += OnDetailsTabSelectionChanged;
            DataContextChanged += OnOwnerChanged;
            SizeChanged += (_, e) => Classes.Set("compactComposer", e.NewSize.Height is > 0 and < 280);
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
            this.FindControl<ScrollViewer>("SongOverviewPane")!.IsVisible = slot == -1;
            this.FindControl<Grid>("SongCommentsLayout")!.IsVisible = slot == 0;
            this.FindControl<Border>("ChartPreviewHost")!.IsVisible = slot > 0;
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
